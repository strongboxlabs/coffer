using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

using static Coffer.Api.Contracts.MasterKeyContracts;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The admin master-KEK surface (ADR-0092 D2, <c>/api/admin/master-key</c>).
/// Mostly about what must NOT return key material: admin-only, fresh assertion
/// required, flow-scoped so a login challenge is worthless, and the asserting
/// credential must belong to the caller.
/// </summary>
/// <remarks>
/// The happy path uses an <see cref="IWebAuthnService"/> substitute — the same
/// pattern <c>LoginEndpointsTests</c> uses — because a genuine signature needs a
/// real authenticator. That stub verifies everything except the signature maths
/// itself, which is Fido2NetLib's and is shared with the login ceremony.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdminMasterKeyEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public AdminMasterKeyEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The fixture KEK ApiFactory pins — 32 zero bytes.</summary>
    private const string FixtureKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<HttpClient> CookieClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    private async Task PromoteToAdminAsync(SyntheticLedger ledger)
    {
        await using var db = _fixture.NewDbContext();
        await db.Users.Where(u => u.Id == ledger.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, _ => true));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    /// <summary>
    /// Clear the two deployment singletons that carry KEK-wrapped blobs. Rotation
    /// is all-or-nothing over EVERY wrapped value, so one unopenable row anywhere
    /// aborts it — and neighbouring tests legitimately leave placeholder ciphertext
    /// (an endpoint test only needs the row to exist, not to be a real sealed blob).
    /// Same convention <c>AdminBackupsEndpointsTests</c> uses for the backup row.
    /// </summary>
    private async Task ResetWrappedSingletonsAsync()
    {
        await using var db = _fixture.NewDbContext();       // service role
        await db.DriveSync.ExecuteDeleteAsync();
        await db.GlobalScheduledJobs.ExecuteDeleteAsync();
    }

    private static AssertionOptions FakeAssertionOptions() => new()
    {
        Challenge = RandomBytes(32),
        Timeout = 60000,
        AllowCredentials = new List<PublicKeyCredentialDescriptor>(),
        UserVerification = UserVerificationRequirement.Preferred,
    };

    private static AuthenticatorAssertionRawResponse FakeAssertion(byte[] credentialId) => new()
    {
        Id = Base64Url(credentialId),
        RawId = credentialId,
        Type = PublicKeyCredentialType.PublicKey,
        Response = new AuthenticatorAssertionRawResponse.AssertionResponse
        {
            AuthenticatorData = RandomBytes(64),
            ClientDataJson = RandomBytes(128),
            Signature = RandomBytes(64),
        },
    };

    /// <summary>Substitute that accepts any assertion — isolates the endpoint's
    /// own gating from Fido2NetLib's signature verification.</summary>
    private static IWebAuthnService AcceptingWebAuthn()
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginAssertion(Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => FakeAssertionOptions());
        sub.CompleteAssertionAsync(
                Arg.Any<AuthenticatorAssertionRawResponse>(),
                Arg.Any<AssertionOptions>(),
                Arg.Any<byte[]>(),
                Arg.Any<uint>(),
                Arg.Any<IsUserHandleOwnerOfCredentialIdAsync>(),
                Arg.Any<CancellationToken>())
           .Returns(call => Task.FromResult(new WebAuthnAssertionOutcome(
               CredentialId: ((AuthenticatorAssertionRawResponse)call[0]).RawId,
               NewSignatureCounter: 7)));
        return sub;
    }

    /// <summary>Substitute that rejects every assertion.</summary>
    private static IWebAuthnService RejectingWebAuthn()
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginAssertion(Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => FakeAssertionOptions());
        sub.CompleteAssertionAsync(
                Arg.Any<AuthenticatorAssertionRawResponse>(),
                Arg.Any<AssertionOptions>(),
                Arg.Any<byte[]>(),
                Arg.Any<uint>(),
                Arg.Any<IsUserHandleOwnerOfCredentialIdAsync>(),
                Arg.Any<CancellationToken>())
           .Returns<Task<WebAuthnAssertionOutcome>>(_ =>
               throw new Fido2VerificationException("signature mismatch"));
        return sub;
    }

    // --- the gate -----------------------------------------------------------

    [Fact]
    public async Task Anonymous_cannot_read_status_or_reveal()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/admin/master-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/admin/master-key/reveal/begin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/admin/master-key/reveal",
                new RevealRequest(Guid.NewGuid(), null))).StatusCode);
    }

    [Fact]
    public async Task Non_admin_cookie_is_forbidden()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/admin/master-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/master-key/reveal/begin", null)).StatusCode);
    }

    [Fact]
    public async Task Reveal_is_not_reachable_by_GET()
    {
        // Key material must never sit in a URL, a referrer header, or history.
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/admin/master-key/reveal");

        // 404 (no GET route) rather than 405 — routing never matches the path for
        // GET at all. Either is fine; what matters is that no key comes back.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain(FixtureKeyBase64, await resp.Content.ReadAsStringAsync());
    }

    // --- status is metadata only -------------------------------------------

    [Fact]
    public async Task Status_returns_metadata_and_never_the_key()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();          // dev-auth is admin

        var resp = await client.GetAsync("/api/admin/master-key");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize<MasterKeyStatusResponse>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(status);
        Assert.Equal("v1", status!.KekId);
        Assert.False(string.IsNullOrWhiteSpace(status.Path));
        Assert.Equal(32, status.Fingerprint.Length);              // 16 bytes, hex
        Assert.DoesNotContain(FixtureKeyBase64, body);            // the whole point
    }

    // --- reveal: negative paths --------------------------------------------

    [Fact]
    public async Task Reveal_without_an_assertion_is_422_not_a_key()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("master-key-assertion-required", await CodeOf(resp));
    }

    [Fact]
    public async Task Reveal_with_an_unguessed_challenge_is_401()
    {
        // The "skip /begin and post a random id" shape.
        await using var factory = new ApiFactory(_fixture).WithService(_ => AcceptingWebAuthn());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(Guid.NewGuid(), FakeAssertion(RandomBytes(64))));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_login_flow_challenge_cannot_be_redeemed_for_a_reveal()
    {
        // Why MasterKeyRevealFlow is its own flow: otherwise "log in" would be
        // equivalent to "hand me the master key". Written directly to the store so
        // the test doesn't depend on the login endpoint's own preconditions.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(alice);
        var credential = await alice.AddCredentialAsync();

        var challenges = new ChallengeStore(_fixture.NewServiceFactory());
        var loginChallengeId = await challenges.SaveAsync(
            ChallengeStore.LoginFlow, alice.UserId,
            FakeAssertionOptions().ToJson(), null, TimeSpan.FromMinutes(2));

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, alice);

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(loginChallengeId, FakeAssertion(credential.CredentialId)));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Another_users_credential_cannot_unlock_the_key()
    {
        // Without the caller-ownership check, any enrolled user's authenticator
        // would satisfy an admin's reveal.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        await admin.AddCredentialAsync();

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var bobsCredential = await bob.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(bobsCredential.CredentialId)));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain(FixtureKeyBase64, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_signature_verification_returns_401_and_no_key()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => RejectingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain(FixtureKeyBase64, await resp.Content.ReadAsStringAsync());
    }

    // --- reveal: happy path -------------------------------------------------

    [Fact]
    public async Task A_verified_assertion_returns_the_key_with_no_store()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var beginDoc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = beginDoc.RootElement.GetProperty("challengeId").GetGuid();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RevealResponse>();
        Assert.Equal(FixtureKeyBase64, body!.KeyBase64);
        Assert.Equal("v1", body.KekId);

        // The one response in the API carrying key material must not be cacheable.
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString() ?? "");

        // Durable audit (ADR-0092 D2): who saw the key, and when. Written before the
        // key goes out, so a reveal can't happen unaudited.
        await using var db = _fixture.NewDbContext();
        var audited = await db.AdminAuditEvents.AsNoTracking()
            .Where(e => e.Action == AdminAuditActions.MasterKeyRevealed
                     && e.ActorUserId == admin.UserId)
            .ToListAsync();
        Assert.NotEmpty(audited);
    }

    [Fact]
    public async Task A_refused_reveal_writes_no_audit_row()
    {
        // The audit must record reveals, not attempts — otherwise a failed ceremony
        // reads later as though the key was handed over.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => RejectingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        Assert.Empty(await db.AdminAuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == admin.UserId)
            .ToListAsync());
    }

    [Fact]
    public async Task A_challenge_is_single_use_even_after_a_successful_reveal()
    {
        // A captured challenge id must not be a standing licence to re-read the key.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

        var first = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
            new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.DoesNotContain(FixtureKeyBase64, await second.Content.ReadAsStringAsync());
    }

    // --- rotation (D4) ------------------------------------------------------

    [Fact]
    public async Task Rotate_is_admin_gated_and_needs_an_assertion()
    {
        await using var anon = new ApiFactory(_fixture).WithoutDevAuth();
        using var anonClient = anon.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonClient.PostAsJsonAsync("/api/admin/master-key/rotate",
                new RotateRequest(Guid.NewGuid(), null))).StatusCode);

        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        using var nonAdmin = await CookieClientAsync(anon, alice);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await nonAdmin.PostAsJsonAsync("/api/admin/master-key/rotate",
                new RotateRequest(Guid.NewGuid(), null))).StatusCode);

        await using var factory = new ApiFactory(_fixture);
        using var admin = factory.CreateClient();                  // dev-auth is admin
        var resp = await admin.PostAsJsonAsync("/api/admin/master-key/rotate",
            new RotateRequest(Guid.NewGuid(), null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("master-key-assertion-required", await CodeOf(resp));
    }

    [Fact]
    public async Task The_rotate_preview_endpoint_is_gone()
    {
        // Removed with the "Check first" button (ADR-0092 D4): rotation runs the dry run
        // itself as its first step and refuses before touching anything, so a preview
        // only produced a list that didn't change the operator's decision — while
        // implying the check was opt-in. An endpoint with no caller is surface nobody
        // exercises and everybody maintains.
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/admin/master-key/rotate/preview", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Rotate_with_a_bogus_assertion_does_not_touch_the_key()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => RejectingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
        using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

        var resp = await client.PostAsJsonAsync("/api/admin/master-key/rotate",
            new RotateRequest(challengeId, FakeAssertion(credential.CredentialId)));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        // The key file is shared process state in tests, so the important assertion
        // is that a rejected ceremony wrote nothing: the install still reports v1.
        var status = await (await client.GetAsync("/api/admin/master-key"))
            .Content.ReadFromJsonAsync<MasterKeyStatusResponse>();
        Assert.Equal("v1", status!.KekId);
    }

    [Fact]
    public async Task Reveal_is_repeatable_with_a_fresh_ceremony()
    {
        // Not show-once (ADR-0092 D2): re-display costs nothing against an admin who
        // can already read every ledger, while show-once strands an operator whose
        // browser died before they wrote the key down.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await PromoteToAdminAsync(admin);
        var credential = await admin.AddCredentialAsync();

        await using var factory = new ApiFactory(_fixture)
            .WithoutDevAuth().WithService(_ => AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var begin = await client.PostAsync("/api/admin/master-key/reveal/begin", null);
            using var doc = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
            var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();

            var resp = await client.PostAsJsonAsync("/api/admin/master-key/reveal",
                new RevealRequest(challengeId, FakeAssertion(credential.CredentialId)));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(FixtureKeyBase64,
                (await resp.Content.ReadFromJsonAsync<RevealResponse>())!.KeyBase64);
        }
    }
}
