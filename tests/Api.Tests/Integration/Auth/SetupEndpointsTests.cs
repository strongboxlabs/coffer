using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// End-to-end checks for the bootstrap setup ceremony. The Fido2 layer is
/// substituted with NSubstitute — we test endpoint plumbing (route
/// mapping, persistence, ProblemDetails on failure, transactional
/// atomicity, cookie issuance), not WebAuthn cryptographic verification.
/// The actual library integration is exercised manually with a YubiKey
/// after deploy; the seam <see cref="IWebAuthnService"/> is the safe
/// boundary.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SetupEndpointsTests : DatabaseTest
{
    public SetupEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static IWebAuthnService NewWebAuthnSubstitute(byte[]? credentialId = null)
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginRegistration(Arg.Any<Fido2User>(), Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => BuildFakeOptions());
        sub.CompleteRegistrationAsync(
            Arg.Any<AuthenticatorAttestationRawResponse>(),
            Arg.Any<CredentialCreateOptions>(),
            Arg.Any<IsCredentialIdUniqueToUserAsyncDelegate>(),
            Arg.Any<CancellationToken>())
           .Returns(_ => Task.FromResult(new WebAuthnRegistrationOutcome(
               CredentialId: credentialId ?? RandomBytes(64),
               PublicKey: RandomBytes(77),
               SignatureCounter: 0,
               Aaguid: Guid.NewGuid(),
               Transports: new[] { "usb" })));
        return sub;
    }

    private static CredentialCreateOptions BuildFakeOptions() =>
        // Construct a minimal options object via the parameterless ctor +
        // init for the fields the code reads. Round-tripping ToJson /
        // FromJson exercises the same wire shape the real library uses.
        new()
        {
            Challenge = RandomBytes(32),
            Rp = new PublicKeyCredentialRpEntity("localhost", "Coffer-test", null),
            User = new Fido2User { Id = Guid.NewGuid().ToByteArray(), Name = "alice", DisplayName = "Alice" },
            PubKeyCredParams = new List<PubKeyCredParam> { new(COSE.Algorithm.ES256) },
            Timeout = 60000,
            ExcludeCredentials = new List<PublicKeyCredentialDescriptor>(),
        };

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    private static AuthenticatorAttestationRawResponse FakeAttestation(byte[] credentialId) =>
        new()
        {
            Id = Base64Url(credentialId),    // Fido2NetLib v4 typed Id as the base64url-encoded string
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = RandomBytes(64),
                ClientDataJson = RandomBytes(128),
            },
        };

    /// <summary>
    /// Insert a fresh, unconsumed bootstrap token directly into the DB
    /// and return its plaintext so the test can pass it to /setup. The
    /// production path is "service mints + logs," but the test wants the
    /// raw value, which is never returned by the service.
    /// </summary>
    private async Task<string> SeedBootstrapTokenAsync()
    {
        await using var db = Db.NewDbContext();
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        db.BootstrapTokens.Add(new BootstrapTokenRow
        {
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        return plaintext;
    }

    [Fact]
    public async Task Begin_returns_options_and_persists_a_pending_challenge()
    {
        var token = await SeedBootstrapTokenAsync();
        var sub = NewWebAuthnSubstitute();

        await using var factory = new ApiFactory(Db)
            .WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice Z." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();
        Assert.NotEqual(Guid.Empty, challengeId);

        // The challenge row exists, in setup flow, unconsumed.
        await using var db = Db.NewDbContext();
        var row = await db.PendingChallenges.AsNoTracking()
            .Where(c => c.Id == challengeId)
            .Select(c => new { c.Flow, c.ConsumedAt })
            .SingleAsync();
        Assert.Equal(ChallengeStore.SetupFlow, row.Flow);
        Assert.Null(row.ConsumedAt);
    }

    [Fact]
    public async Task Begin_rejects_an_invalid_token_with_401()
    {
        await SeedBootstrapTokenAsync();        // valid token persisted, but the test sends a different one

        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/setup/totally-not-the-token/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Begin_rejects_a_taken_username_with_422_and_code()
    {
        var token = await SeedBootstrapTokenAsync();

        await using (var db = Db.NewDbContext())
        {
            db.Users.Add(new UserRow
            {
                Id = Guid.NewGuid(),
                DisplayName = "Existing",
                Username = "alice",
                CreatedBy = "test",
            });
            await db.SaveChangesAsync();
        }

        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("setup-username-taken",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("", "Alice", "setup-username-required")]
    [InlineData("alice", "", "setup-display-name-required")]
    [InlineData("   ", "Alice", "setup-username-required")]
    public async Task Begin_rejects_empty_username_or_displayname_with_422_and_code(
        string username, string displayName, string expectedCode)
    {
        var token = await SeedBootstrapTokenAsync();
        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = username, DisplayName = displayName });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Complete_persists_user_credential_recovery_codes_and_issues_a_cookie()
    {
        var token = await SeedBootstrapTokenAsync();

        var credentialId = RandomBytes(64);
        var sub = NewWebAuthnSubstitute(credentialId);
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        // /begin
        var beginResp = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice" });
        Assert.Equal(HttpStatusCode.OK, beginResp.StatusCode);
        var beginDoc = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync());
        var challengeId = beginDoc.RootElement.GetProperty("challengeId").GetGuid();

        // /complete — no ledger is created (ADR-0088); the user lands on the hub.
        var completeResp = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/complete",
            new SetupCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(credentialId),
                CredentialNickname = "YubiKey 5C",
            });
        Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        // Cookie issued.
        Assert.Contains(completeResp.Headers,
            h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
              && h.Value.Any(v => v.StartsWith("__Host-coffer.session=", StringComparison.Ordinal)));

        // Body shape.
        var completeDoc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParse(completeDoc.RootElement.GetProperty("userId").GetString(), out var userId));
        Assert.Equal("alice", completeDoc.RootElement.GetProperty("username").GetString());
        var codes = completeDoc.RootElement.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(10, codes.Length);
        // Setup creates no ledger, so the response names none (ADR-0088).
        Assert.False(completeDoc.RootElement.TryGetProperty("ledgerId", out _));
        Assert.False(completeDoc.RootElement.TryGetProperty("ledgerName", out _));

        // The master key comes back too (ADR-0092 D2): startup minted it on this
        // install and setup is the one moment the operator is present, verified, and
        // paying attention. Returned under the same one-time bootstrap-token gate as
        // the recovery codes above, and it is the LESS sensitive of the two.
        Assert.Equal(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",   // ApiFactory's fixture key
            completeDoc.RootElement.GetProperty("masterKeyBase64").GetString());

        // …and the disclosure is audited, so "who saw the key" has an answer even
        // for the unavoidable bootstrap one.
        await using (var auditDb = Db.NewDbContext())
        {
            Assert.Contains(
                await auditDb.AdminAuditEvents.AsNoTracking()
                    .Where(e => e.Action == AdminAuditActions.MasterKeyShownAtSetup)
                    .ToListAsync(),
                e => e.ActorUserId == userId);
        }

        // DB state: user, credential, recovery codes, bootstrap token
        // consumed, and NO ledger grant.
        await using var db = Db.NewDbContext();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Username, u.CreatedBy, u.IsAdmin })
            .SingleAsync();
        Assert.Equal("alice", user.Username);
        Assert.Equal("bootstrap-token", user.CreatedBy);
        // First human user via setup is the operator → admin (ADR-0060).
        Assert.True(user.IsAdmin);

        Assert.Equal(1, await db.WebAuthnCredentials.CountAsync(c => c.UserId == userId));
        Assert.Equal(10, await db.RecoveryCodes.CountAsync(r => r.UserId == userId));
        Assert.Equal(1, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt != null));
        Assert.Equal(0, await db.UserLedgerGrants.CountAsync(g => g.UserId == userId));
    }

    [Fact]
    public async Task Complete_rejects_unknown_challenge_id()
    {
        var token = await SeedBootstrapTokenAsync();
        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/complete",
            new SetupCompleteRequest
            {
                ChallengeId = Guid.NewGuid(),
                AttestationResponse = FakeAttestation(RandomBytes(64)),
                CredentialNickname = "key",
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Complete_returning_a_failed_attestation_leaves_no_DB_side_effects()
    {
        var token = await SeedBootstrapTokenAsync();

        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginRegistration(Arg.Any<Fido2User>(), Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => BuildFakeOptions());
        sub.CompleteRegistrationAsync(
                Arg.Any<AuthenticatorAttestationRawResponse>(),
                Arg.Any<CredentialCreateOptions>(),
                Arg.Any<IsCredentialIdUniqueToUserAsyncDelegate>(),
                Arg.Any<CancellationToken>())
           .Returns<Task<WebAuthnRegistrationOutcome>>(_ =>
               throw new Fido2VerificationException("forged signature"));

        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var beginResp = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice" });
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var completeResp = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/complete",
            new SetupCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(RandomBytes(64)),
                CredentialNickname = "key",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completeResp.StatusCode);

        using (var doc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync()))
            Assert.Equal("setup-attestation-failed",
                doc.RootElement.GetProperty("code").GetString());

        // No user, no credential, no recovery codes, token still alive.
        await using var db = Db.NewDbContext();
        Assert.Equal(0, await db.Users.CountAsync(u => u.Username == "alice"));
        Assert.Equal(0, await db.WebAuthnCredentials.CountAsync());
        Assert.Equal(1, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
    }

    [Fact]
    public async Task Info_accepts_a_valid_token_and_offers_no_ledger_list()
    {
        var token = await SeedBootstrapTokenAsync();
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/setup/{token}/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ADR-0088: /info exists to validate the token. It must NOT advertise
        // ledgers — offering placeholder rows makes the first-run choice
        // misleading.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("availableLedgers", out _));
    }

    [Fact]
    public async Task Info_rejects_invalid_token_with_401()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/setup/not-a-real-token/info");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Setup creates no ledger, whatever the client asks (ADR-0088). A client built when
    /// setup could add a Demo still sends <c>includeDemo</c>: the field is ignored, setup
    /// completes, and the user owns nothing, so the hub's Demo button is the only way to
    /// a Demo.
    /// </summary>
    [Fact]
    public async Task Complete_creates_no_ledger()
    {
        var token = await SeedBootstrapTokenAsync();
        var credentialId = RandomBytes(64);
        var sub = NewWebAuthnSubstitute(credentialId);
        await using var factory = new ApiFactory(Db).WithService(_ => sub);
        using var client = factory.CreateClient();

        var beginResp = await client.PostAsJsonAsync(
            $"/api/auth/setup/{token}/begin",
            new SetupBeginRequest { Username = "alice", DisplayName = "Alice" });
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        // The body an older client sends: the request plus includeDemo, which the
        // request type no longer declares.
        var body = JsonSerializer.SerializeToNode(
            new SetupCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(credentialId),
                CredentialNickname = "YubiKey",
            },
            JsonSerializerOptions.Web)!.AsObject();
        body["includeDemo"] = true;

        await using (var before = Db.NewDbContext())
            Assert.Equal(0, await before.Ledgers.CountAsync());

        var completeResp = await client.PostAsJsonAsync($"/api/auth/setup/{token}/complete", body);
        Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        using var doc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        var userId = doc.RootElement.GetProperty("userId").GetGuid();
        Assert.False(doc.RootElement.TryGetProperty("ledgerId", out _));
        Assert.False(doc.RootElement.TryGetProperty("ledgerName", out _));

        await using var db = Db.NewDbContext();
        // The user exists, so the setup did run; it created nothing to own.
        Assert.Equal(1, await db.Users.CountAsync(u => u.Id == userId));
        Assert.Equal(0, await db.UserLedgerGrants.CountAsync(g => g.UserId == userId));
        Assert.Equal(0, await db.Ledgers.CountAsync());
    }
}
