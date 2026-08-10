using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Fido2NetLib;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

using static Coffer.Api.Contracts.BackupContracts;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The admin backup surface (ADR-0060, <c>/api/admin/backups</c>): the
/// RequireAdmin gate, passphrase set/validate, and the schedule's
/// passphrase-gated enable. The actual pg_dump create path is verified by the
/// slice-② live docker round-trip, not here (the test host has no
/// postgresql-client), so these exercise everything UP TO the engine.
///
/// The backup row in global_scheduled_jobs is a deployment singleton, so each
/// test resets it first (ApiCollection runs sequentially).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminBackupsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public AdminBackupsEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    // Dev-auth is stamped admin (ADR-0060 ③a), so the default factory's client
    // is an admin client — the simplest way to reach the gated routes.
    private HttpClient AdminClient(ApiFactory factory) => factory.CreateClient();

    private static async Task<HttpClient> CookieClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    private async Task ResetBackupRowAsync()
    {
        await using var db = _fixture.NewDbContext();   // service role
        await db.Database.ExecuteSqlRawAsync("DELETE FROM global_scheduled_jobs;");
    }

    private async Task PromoteToAdminAsync(SyntheticLedger ledger)
    {
        await using var db = _fixture.NewDbContext();   // only the service role may set is_admin
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE users SET is_admin = true WHERE id = {0}", ledger.UserId);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    [Fact]
    public async Task Non_admin_cookie_is_forbidden()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);

        var resp = await client.GetAsync("/api/admin/backups");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await client.GetAsync("/api/admin/backups");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_cookie_can_list()
    {
        await ResetBackupRowAsync();
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(alice);          // real cookie + is_admin claim path
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);

        var resp = await client.GetAsync("/api/admin/backups");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<BackupSummary>>();
        Assert.NotNull(list);   // empty (none created), but a 200 list
    }

    // --- passphrase reveal (ADR-0092 D7) -----------------------------------

    [Fact]
    public async Task Passphrase_reveal_is_admin_gated()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/backups/passphrase/reveal/begin", null)).StatusCode);

        using var anonClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonClient.PostAsync("/api/admin/backups/passphrase/reveal/begin", null)).StatusCode);
    }

    [Fact]
    public async Task Passphrase_reveal_refuses_before_the_ceremony_when_none_is_set()
    {
        // Fail early rather than making the operator tap their authenticator only to
        // be told there was nothing to show.
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.PostAsync("/api/admin/backups/passphrase/reveal/begin", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-passphrase-not-set", await CodeOf(resp));
    }

    [Fact]
    public async Task Passphrase_reveal_needs_an_assertion_and_is_not_reachable_by_GET()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);
        await client.PutAsJsonAsync("/api/admin/backups/passphrase",
            new SetBackupPassphraseRequest("correct-horse"));

        // No assertion in the body.
        var missing = await client.PostAsJsonAsync("/api/admin/backups/passphrase/reveal",
            new { challengeId = Guid.NewGuid(), assertionResponse = (object?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("master-key-assertion-required", await CodeOf(missing));

        // The secret must never sit in a URL, a referrer, or history.
        var viaGet = await client.GetAsync("/api/admin/backups/passphrase/reveal");
        Assert.Equal(HttpStatusCode.NotFound, viaGet.StatusCode);
        Assert.DoesNotContain("correct-horse", await viaGet.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_masterkey_reveal_challenge_cannot_be_redeemed_for_the_passphrase()
    {
        // The two step-ups have separate flows so a challenge is good for exactly the
        // ceremony it was minted for — even though cross-redemption between two admin
        // step-ups would gain an attacker nothing.
        await ResetBackupRowAsync();
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(alice);
        var credential = await alice.AddCredentialAsync();

        var challenges = new ChallengeStore(_fixture.NewServiceFactory());
        var masterKeyChallenge = await challenges.SaveAsync(
            ChallengeStore.MasterKeyRevealFlow, alice.UserId,
            new AssertionOptions { Challenge = new byte[32] }.ToJson(),
            null, TimeSpan.FromMinutes(2));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);
        await client.PutAsJsonAsync("/api/admin/backups/passphrase",
            new SetBackupPassphraseRequest("correct-horse"));

        var resp = await client.PostAsJsonAsync("/api/admin/backups/passphrase/reveal",
            new MasterKeyContracts.RevealRequest(
                masterKeyChallenge,
                new AuthenticatorAssertionRawResponse
                {
                    // Base64URL, not base64 — Fido2NetLib's converter rejects the
                    // latter outright, which is a binding failure, not a 401.
                    Id = Convert.ToBase64String(credential.CredentialId)
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    RawId = credential.CredentialId,
                    Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
                    Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                    {
                        AuthenticatorData = new byte[] { 0 },
                        ClientDataJson = new byte[] { 0 },
                        Signature = new byte[] { 0 },
                    },
                }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain("correct-horse", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Set_passphrase_too_short_is_422()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/admin/backups/passphrase", new SetBackupPassphraseRequest("short1"));   // 6 chars
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-passphrase-invalid", await CodeOf(resp));
    }

    [Fact]
    public async Task Set_passphrase_then_schedule_reports_configured()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var set = await client.PutAsJsonAsync(
            "/api/admin/backups/passphrase", new SetBackupPassphraseRequest("a-good-passphrase"));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var sched = await (await client.GetAsync("/api/admin/backups/schedule"))
            .Content.ReadFromJsonAsync<BackupScheduleResponse>();
        Assert.NotNull(sched);
        Assert.True(sched!.PassphraseConfigured);
        Assert.False(sched.Enabled);   // passphrase set ≠ schedule enabled
    }

    [Fact]
    public async Task Create_without_passphrase_is_422()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        // No passphrase configured → rejected BEFORE any pg_dump runs.
        var resp = await client.PostAsync("/api/admin/backups", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-passphrase-not-set", await CodeOf(resp));
    }

    [Fact]
    public async Task Enable_schedule_without_passphrase_is_422()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/admin/backups/schedule",
            new SetBackupScheduleRequest(Enabled: true, HourLocal: 3, MinuteLocal: 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-passphrase-not-set", await CodeOf(resp));
    }

    [Fact]
    public async Task Enable_schedule_with_passphrase_succeeds()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        await client.PutAsJsonAsync(
            "/api/admin/backups/passphrase", new SetBackupPassphraseRequest("a-good-passphrase"));

        var put = await client.PutAsJsonAsync(
            "/api/admin/backups/schedule",
            new SetBackupScheduleRequest(Enabled: true, HourLocal: 3, MinuteLocal: 0, Timezone: "UTC"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<BackupScheduleResponse>())!;
        Assert.True(saved.Enabled);
        Assert.True(saved.PassphraseConfigured);
        Assert.NotNull(saved.NextRunAt);
        Assert.True(saved.NextRunAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Schedule_rejects_out_of_range_time()
    {
        await ResetBackupRowAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/admin/backups/schedule",
            new SetBackupScheduleRequest(Enabled: false, HourLocal: 99, MinuteLocal: 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("schedule-invalid", await CodeOf(resp));
    }

    [Fact]
    public async Task Download_unknown_id_is_404()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.GetAsync("/api/admin/backups/coffer-20260623T031500000Z-0a1b2c3d");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Pin_unknown_id_is_404()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        // Valid id shape but no such artifact on disk → 404 (no orphan pin).
        var resp = await client.PostAsync(
            "/api/admin/backups/coffer-20260623T031500000Z-0a1b2c3d/pin", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Retention_roundtrips_via_get_and_put()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var put = await client.PutAsJsonAsync(
            "/api/admin/backups/retention",
            new SetBackupRetentionRequest(RetentionDaily: 14, RetentionWeekly: 6, RetentionMonthly: 24));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await (await client.GetAsync("/api/admin/backups/retention"))
            .Content.ReadFromJsonAsync<BackupRetentionResponse>();
        Assert.NotNull(got);
        Assert.Equal(14, got!.RetentionDaily);
        Assert.Equal(6, got.RetentionWeekly);
        Assert.Equal(24, got.RetentionMonthly);
    }

    [Fact]
    public async Task Retention_out_of_range_is_422()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/admin/backups/retention",
            new SetBackupRetentionRequest(RetentionDaily: 5000, RetentionWeekly: 8, RetentionMonthly: 12));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-retention-invalid", await CodeOf(resp));
    }

    [Fact]
    public async Task Validate_reports_compatible_for_this_installs_kek()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        // ApiFactory pins the host Master KEK to 32 zero bytes.
        var archive = await MakeBackupHeaderAsync(
            Coffer.Api.Backup.KekFingerprint.Compute(new byte[32]));

        using var resp = await PostValidateAsync(client, archive);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BackupKekCheckResponse>();
        Assert.NotNull(body);
        Assert.True(body!.HasFingerprint);
        Assert.True(body.Compatible);
    }

    [Fact]
    public async Task Validate_reports_incompatible_for_a_different_kek()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = AdminClient(factory);

        var archive = await MakeBackupHeaderAsync(
            Coffer.Api.Backup.KekFingerprint.Compute(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        using var resp = await PostValidateAsync(client, archive);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BackupKekCheckResponse>();
        Assert.NotNull(body);
        Assert.True(body!.HasFingerprint);
        Assert.False(body.Compatible);
    }

    // A minimal valid v2 .cofferbak carrying the given KEK fingerprint — enough
    // for the header-only pre-flight check.
    private static async Task<byte[]> MakeBackupHeaderAsync(byte[] fingerprint)
    {
        using var ms = new MemoryStream();
        await Coffer.Api.Backup.BackupCrypto.EncryptAsync(
            new MemoryStream([1, 2, 3]), "pw", ms, fingerprint);
        return ms.ToArray();
    }

    private static Task<HttpResponseMessage> PostValidateAsync(HttpClient client, byte[] archive)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(archive), "archive", "backup.cofferbak" },
        };
        return client.PostAsync("/api/admin/backups/restore/validate", form);
    }
}
