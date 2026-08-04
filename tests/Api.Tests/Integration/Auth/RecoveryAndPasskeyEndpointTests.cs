using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// The account-recovery + passkey-management surface (ADR-0013
/// follow-through): recovery-code sign-in (anonymous), adding/listing/
/// removing passkeys, and regenerating recovery codes (all authenticated,
/// current-user-scoped). The Fido2 layer is substituted so these exercise
/// endpoint plumbing — persistence, scoping, the keep-one-passkey guard,
/// code consumption, the rate limiter — without forging real ceremonies.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecoveryAndPasskeyEndpointTests
{
    private readonly PostgresFixture _fixture;

    public RecoveryAndPasskeyEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // --- WebAuthn substitute (registration) ------------------------------

    private static IWebAuthnService NewRegistrationSubstitute(byte[] credentialId)
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginRegistration(Arg.Any<Fido2User>(), Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => BuildFakeCreateOptions());
        sub.CompleteRegistrationAsync(
                Arg.Any<AuthenticatorAttestationRawResponse>(),
                Arg.Any<CredentialCreateOptions>(),
                Arg.Any<IsCredentialIdUniqueToUserAsyncDelegate>(),
                Arg.Any<CancellationToken>())
           .Returns(_ => Task.FromResult(new WebAuthnRegistrationOutcome(
               CredentialId: credentialId,
               PublicKey: RandomBytes(77),
               SignatureCounter: 0,
               Aaguid: Guid.NewGuid(),
               Transports: new[] { "usb" })));
        return sub;
    }

    private static CredentialCreateOptions BuildFakeCreateOptions() =>
        new()
        {
            Challenge = RandomBytes(32),
            Rp = new PublicKeyCredentialRpEntity("localhost", "Coffer-test", null),
            User = new Fido2User { Id = Guid.NewGuid().ToByteArray(), Name = "alice", DisplayName = "Alice" },
            PubKeyCredParams = new List<PubKeyCredParam> { new(COSE.Algorithm.ES256) },
            Timeout = 60000,
            ExcludeCredentials = new List<PublicKeyCredentialDescriptor>(),
        };

    private static AuthenticatorAttestationRawResponse FakeAttestation(byte[] credentialId) =>
        new()
        {
            Id = Base64Url(credentialId),
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = RandomBytes(64),
                ClientDataJson = RandomBytes(128),
            },
        };

    // --- Seeding helpers -------------------------------------------------

    /// <summary>Insert a fresh recovery-code set for the user; return the plaintext codes.</summary>
    private async Task<IReadOnlyList<string>> SeedRecoveryCodesAsync(Guid userId)
    {
        var (plaintext, hashes) = RecoveryCodes.Generate();
        await using var db = _fixture.NewDbContext();
        foreach (var hash in hashes)
            db.RecoveryCodes.Add(new RecoveryCodeRow { Id = Guid.NewGuid(), UserId = userId, CodeHash = hash });
        await db.SaveChangesAsync();
        return plaintext;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private async Task<int> UnusedCodeCountAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.RecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null);
    }

    /// <summary>Insert a credential with a chosen RP id (for the exclude-scoping test).</summary>
    private async Task<byte[]> SeedCredentialAsync(Guid userId, string? rpId, string nickname)
    {
        var credId = RandomBytes(64);
        await using var db = _fixture.NewDbContext();
        db.WebAuthnCredentials.Add(new WebAuthnCredentialRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = credId,
            PublicKey = RandomBytes(77),
            SignatureCounter = 0,
            Nickname = nickname,
            RpId = rpId,
        });
        await db.SaveChangesAsync();
        return credId;
    }

    // =====================================================================
    // A. Recovery-code sign-in (POST /api/auth/login/recovery)
    // =====================================================================

    [Fact]
    public async Task Recovery_login_with_a_valid_code_issues_a_cookie_and_consumes_only_that_code()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var codes = await SeedRecoveryCodesAsync(ledger.UserId);

        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = codes[0] });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(resp.Headers,
            h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
              && h.Value.Any(v => v.StartsWith("coffer.session=", StringComparison.Ordinal)));

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(ledger.UserId, doc.RootElement.GetProperty("userId").GetGuid());

        // Exactly one code consumed (9 of 10 remain).
        Assert.Equal(RecoveryCodes.CodesPerSet - 1, await UnusedCodeCountAsync(ledger.UserId));
    }

    [Fact]
    public async Task Recovery_login_rejects_reuse_of_a_consumed_code()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var codes = await SeedRecoveryCodesAsync(ledger.UserId);

        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = codes[0] });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Recovery_login_rejects_a_wrong_code_without_consuming_anything()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedRecoveryCodesAsync(ledger.UserId);

        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = "ZZZZZ-ZZZZZ" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(RecoveryCodes.CodesPerSet, await UnusedCodeCountAsync(ledger.UserId));
    }

    [Fact]
    public async Task Recovery_login_rejects_an_unknown_username_with_401()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = $"nobody-{Guid.NewGuid():N}", RecoveryCode = "ABCDE-FGHJK" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Recovery_login_rejects_a_missing_code_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("recovery-code-required", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Recovery_login_is_rate_limited_per_window()
    {
        // Limit of 2/min: requests 1+2 pass through to the handler (401 for an
        // unknown user), request 3 is rejected by the limiter (429).
        await using var factory = new ApiFactory(_fixture)
            .WithConfig("Api:Auth:RecoveryRateLimitPerMinute", "2");
        using var client = factory.CreateClient();

        var body = new RecoveryLoginRequest
        {
            Username = $"nobody-{Guid.NewGuid():N}",
            RecoveryCode = "ABCDE-FGHJK",
        };

        var r1 = await client.PostAsJsonAsync("/api/auth/login/recovery", body);
        var r2 = await client.PostAsJsonAsync("/api/auth/login/recovery", body);
        var r3 = await client.PostAsJsonAsync("/api/auth/login/recovery", body);

        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    // =====================================================================
    // B. Passkey management (register / list / delete)
    // =====================================================================

    [Fact]
    public async Task Register_begin_persists_a_register_challenge_bound_to_the_user()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sub = NewRegistrationSubstitute(RandomBytes(64));
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth().WithService(_ => sub);
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync("/api/auth/register/begin", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var challengeId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        await using var db = _fixture.NewDbContext();
        var row = await db.PendingChallenges.AsNoTracking()
            .Where(c => c.Id == challengeId)
            .Select(c => new { c.Flow, c.UserId })
            .SingleAsync();
        Assert.Equal(ChallengeStore.RegisterFlow, row.Flow);
        Assert.Equal(ledger.UserId, row.UserId);
    }

    [Fact]
    public async Task Register_complete_adds_a_passkey_that_then_appears_in_the_list()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var newCredentialId = RandomBytes(64);
        var sub = NewRegistrationSubstitute(newCredentialId);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth().WithService(_ => sub);
        using var client = await AuthedClientAsync(factory, ledger);

        var begin = await client.PostAsync("/api/auth/register/begin", content: null);
        var challengeId = JsonDocument.Parse(await begin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var complete = await client.PostAsJsonAsync("/api/auth/register/complete",
            new RegisterCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(newCredentialId),
                CredentialNickname = "My new YubiKey",
            });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var list = await client.GetFromJsonAsync<List<CredentialSummary>>("/api/auth/credentials");
        Assert.NotNull(list);
        Assert.Contains(list!, c => c.Nickname == "My new YubiKey");
    }

    [Fact]
    public async Task Register_complete_rejects_a_challenge_minted_for_a_different_user()
    {
        // Alice begins a register ceremony; Bob (different cookie) tries to
        // complete it. The user-bound challenge must reject Bob.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var credId = RandomBytes(64);
        var sub = NewRegistrationSubstitute(credId);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth().WithService(_ => sub);

        using var aliceClient = await AuthedClientAsync(factory, alice);
        var begin = await aliceClient.PostAsync("/api/auth/register/begin", content: null);
        var challengeId = JsonDocument.Parse(await begin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        using var bobClient = await AuthedClientAsync(factory, bob);
        var complete = await bobClient.PostAsJsonAsync("/api/auth/register/complete",
            new RegisterCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(credId),
                CredentialNickname = "stolen",
            });
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Fact]
    public async Task Credentials_list_is_scoped_to_the_current_user()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceCred = await alice.AddCredentialAsync(nickname: "alice-key");
        await bob.AddCredentialAsync(nickname: "bob-key");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var list = await client.GetFromJsonAsync<List<CredentialSummary>>("/api/auth/credentials");
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal(aliceCred.Id, list![0].Id);
        Assert.Equal("alice-key", list[0].Nickname);
    }

    [Fact]
    public async Task Delete_removes_one_of_several_passkeys()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var keep = await ledger.AddCredentialAsync(nickname: "keep");
        var drop = await ledger.AddCredentialAsync(nickname: "drop");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/auth/credentials/{drop.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var remaining = await db.WebAuthnCredentials
            .Where(c => c.UserId == ledger.UserId).Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { keep.Id }, remaining);
    }

    [Fact]
    public async Task Delete_refuses_to_remove_the_last_passkey()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var only = await ledger.AddCredentialAsync(nickname: "only");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/auth/credentials/{only.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("credential-last-remaining", doc.RootElement.GetProperty("code").GetString());

        await using var db = _fixture.NewDbContext();
        Assert.True(await db.WebAuthnCredentials.AnyAsync(c => c.Id == only.Id));
    }

    [Fact]
    public async Task Delete_of_another_users_credential_reports_not_found()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddCredentialAsync(nickname: "alice-only");
        var bobCred = await bob.AddCredentialAsync(nickname: "bob-only");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var resp = await client.DeleteAsync($"/api/auth/credentials/{bobCred.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("credential-not-found", doc.RootElement.GetProperty("code").GetString());

        // Bob's credential untouched.
        await using var db = _fixture.NewDbContext();
        Assert.True(await db.WebAuthnCredentials.AnyAsync(c => c.Id == bobCred.Id));
    }

    [Fact]
    public async Task Register_begin_excludes_only_current_rp_credentials()
    {
        // A credential registered under the CURRENT RP must be excluded (a key
        // can't enrol twice for one account); one left over from a PREVIOUS RP
        // (domain rename / ADR-0061 restore) must NOT be — else the same
        // authenticator wrongly refuses re-enrolment. Test RpId is "localhost".
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var currentRpCred = await SeedCredentialAsync(ledger.UserId, "localhost", "current-rp");
        var staleRpCred = await SeedCredentialAsync(ledger.UserId, "old.example.com", "stale-rp");

        IReadOnlyList<PublicKeyCredentialDescriptor>? captured = null;
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginRegistration(
                Arg.Any<Fido2User>(),
                Arg.Do<IReadOnlyList<PublicKeyCredentialDescriptor>>(x => captured = x))
           .Returns(_ => BuildFakeCreateOptions());

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth().WithService(_ => sub);
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync("/api/auth/register/begin", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.NotNull(captured);
        Assert.Contains(captured!, d => d.Id.SequenceEqual(currentRpCred));
        Assert.DoesNotContain(captured!, d => d.Id.SequenceEqual(staleRpCred));
    }

    [Fact]
    public async Task Delete_removes_the_last_passkey_when_recovery_codes_exist()
    {
        // The last passkey CAN be removed when a fallback login exists (unused
        // recovery codes) — this is what lets a user clear a now-dead passkey
        // (e.g. left from a previous RP) without locking themselves out.
        // Contrast Delete_refuses_to_remove_the_last_passkey (no recovery codes).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var only = await ledger.AddCredentialAsync(nickname: "only");
        await SeedRecoveryCodesAsync(ledger.UserId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/auth/credentials/{only.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        Assert.False(await db.WebAuthnCredentials.AnyAsync(c => c.Id == only.Id));
    }

    [Fact]
    public async Task Account_endpoints_require_authentication()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(); // no cookie

        var list = await client.GetAsync("/api/auth/credentials");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        var begin = await client.PostAsync("/api/auth/register/begin", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, begin.StatusCode);

        var status = await client.GetAsync("/api/auth/recovery-codes");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    // =====================================================================
    // C. Recovery-code status + regeneration
    // =====================================================================

    [Fact]
    public async Task Recovery_codes_status_reports_remaining_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedRecoveryCodesAsync(ledger.UserId);
        // Consume one directly so remaining != total.
        await using (var db = _fixture.NewDbContext())
        {
            var one = await db.RecoveryCodes.FirstAsync(c => c.UserId == ledger.UserId);
            await db.RecoveryCodes.Where(c => c.Id == one.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, _ => (DateTime?)DateTime.UtcNow));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var status = await client.GetFromJsonAsync<RecoveryCodesStatusResponse>("/api/auth/recovery-codes");
        Assert.NotNull(status);
        Assert.Equal(RecoveryCodes.CodesPerSet - 1, status!.Remaining);
        Assert.Equal(RecoveryCodes.CodesPerSet, status.Total);
    }

    [Fact]
    public async Task Regenerate_replaces_the_set_and_invalidates_old_codes()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var oldCodes = await SeedRecoveryCodesAsync(ledger.UserId);
        // Burn one old code so we can prove regenerate wipes used + unused alike.
        await using (var db = _fixture.NewDbContext())
        {
            var one = await db.RecoveryCodes.FirstAsync(c => c.UserId == ledger.UserId);
            await db.RecoveryCodes.Where(c => c.Id == one.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, _ => (DateTime?)DateTime.UtcNow));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var regen = await client.PostAsync("/api/auth/recovery-codes/regenerate", content: null);
        Assert.Equal(HttpStatusCode.OK, regen.StatusCode);
        var fresh = JsonDocument.Parse(await regen.Content.ReadAsStringAsync())
            .RootElement.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(RecoveryCodes.CodesPerSet, fresh.Count);

        // Old set fully replaced: exactly CodesPerSet rows, all unused.
        Assert.Equal(RecoveryCodes.CodesPerSet, await UnusedCodeCountAsync(ledger.UserId));

        // An old code no longer logs in; a new one does.
        using var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var withOld = await anon.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = oldCodes[1] });
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

        var withNew = await anon.PostAsJsonAsync("/api/auth/login/recovery",
            new RecoveryLoginRequest { Username = ledger.Username, RecoveryCode = fresh[0] });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }
}
