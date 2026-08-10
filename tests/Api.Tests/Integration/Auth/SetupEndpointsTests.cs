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
public sealed class SetupEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public SetupEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
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
        await using var db = _fixture.NewDbContext();
        // TRUNCATE is the cleanest way to wipe global state between
        // setup tests; EF doesn't have a native equivalent so we route
        // the SQL through the DbContext's connection.
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE bootstrap_tokens, webauthn_credentials, webauthn_pending_challenges CASCADE;");

        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        db.BootstrapTokens.Add(new BootstrapTokenRow
        {
            TokenHash = hash,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        return plaintext;
    }

    /// <summary>
    /// Wipe every user except the seeded system user, plus any
    /// "new-ledger" rows those users created via the setup ceremony.
    /// Setup tests assert on counts and on freshly-created rows.
    /// </summary>
    /// <remarks>
    /// Order matters because of the ≥1-owner deferred trigger on
    /// user_ledger_grants. The user-cascade through grants would leave
    /// any setup-created new ledger orphaned, and the trigger fires at
    /// COMMIT. So we delete those ledgers first.
    ///
    /// We restrict the ledger delete to rows without a grant for the system
    /// user — that's the discriminator between "created by a SetupEndpoints
    /// test" (no system-user grant) and "created by SyntheticLedger" for some
    /// other test class (which DOES grant the system user, so it survives this
    /// reset). Other classes' synthetic ledgers also have accounts hanging off
    /// them under <c>accounts_ledger_id_fkey ON DELETE RESTRICT</c>; a blanket
    /// delete here would 23503-fail at the FK. Keeping them out of scope dodges
    /// that without making the reset schema-aware of every dependent table.
    ///
    /// (The "not the seeded Default ledger" clause is gone — ADR-0088 / migration
    /// 186 removed that row, so there is nothing to exclude.)
    /// </remarks>
    private async Task ResetUsersAsync()
    {
        await using var db = _fixture.NewDbContext();

        // The ledgers this reset removes: setup-created, i.e. not system-owned.
        var targetLedgerIds = await db.Ledgers
            .Where(l => !db.UserLedgerGrants.Any(g =>
                    g.LedgerId == l.Id && g.UserId == UserRow.SystemUserId))
            .Select(l => l.Id)
            .ToListAsync();

        // Tear each one down with the PRODUCT's ledger delete (migration 141's
        // fn_ledger_delete, the same routine DELETE /api/ledgers/{id} uses)
        // rather than hand-rolled per-table deletes.
        //
        // Thirteen tables are ON DELETE RESTRICT against `ledgers` (accounts,
        // securities, holdings, lots, txn_headers, txn_legs, security_prices,
        // security_splits, tags, feed_connections, ledger_operations,
        // account_external_ids, txn_header_account_balances), so a correct
        // teardown has to know the full order. Duplicating that here means the
        // reset silently rots the next time a RESTRICT table is added — and when
        // it rots it doesn't fail the test that caused it, it poisons whichever
        // sibling test resets next. Since ADR-0088 a setup-created ledger can be
        // a full Demo import rather than a bare category tree, so this is no
        // longer hypothetical.
        foreach (var ledgerId in targetLedgerIds)
        {
            await db.LedgerDelete(ledgerId).Select(r => r.LedgerId).FirstAsync();
        }
        await db.Users
            .Where(u => u.Id != UserRow.SystemUserId)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Begin_returns_options_and_persists_a_pending_challenge()
    {
        await ResetUsersAsync();
        var token = await SeedBootstrapTokenAsync();
        var sub = NewWebAuthnSubstitute();

        await using var factory = new ApiFactory(_fixture)
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
        await using var db = _fixture.NewDbContext();
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
        await ResetUsersAsync();
        await SeedBootstrapTokenAsync();        // valid token persisted, but the test sends a different one

        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
        await ResetUsersAsync();
        var token = await SeedBootstrapTokenAsync();

        await using (var db = _fixture.NewDbContext())
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
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
        await ResetUsersAsync();
        var token = await SeedBootstrapTokenAsync();

        var credentialId = RandomBytes(64);
        var sub = NewWebAuthnSubstitute(credentialId);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
              && h.Value.Any(v => v.StartsWith("coffer.session=", StringComparison.Ordinal)));

        // Body shape.
        var completeDoc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParse(completeDoc.RootElement.GetProperty("userId").GetString(), out var userId));
        Assert.Equal("alice", completeDoc.RootElement.GetProperty("username").GetString());
        var codes = completeDoc.RootElement.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(10, codes.Length);
        // No ledger without the demo opt-in (ADR-0088).
        Assert.Equal(JsonValueKind.Null, completeDoc.RootElement.GetProperty("ledgerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, completeDoc.RootElement.GetProperty("ledgerName").ValueKind);

        // The master key comes back too (ADR-0092 D2): startup minted it on this
        // install and setup is the one moment the operator is present, verified, and
        // paying attention. Returned under the same one-time bootstrap-token gate as
        // the recovery codes above, and it is the LESS sensitive of the two.
        Assert.Equal(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",   // ApiFactory's fixture key
            completeDoc.RootElement.GetProperty("masterKeyBase64").GetString());

        // …and the disclosure is audited, so "who saw the key" has an answer even
        // for the unavoidable bootstrap one.
        await using (var auditDb = _fixture.NewDbContext())
        {
            Assert.Contains(
                await auditDb.AdminAuditEvents.AsNoTracking()
                    .Where(e => e.Action == AdminAuditActions.MasterKeyShownAtSetup)
                    .ToListAsync(),
                e => e.ActorUserId == userId);
        }

        // DB state: user, credential, recovery codes, bootstrap token
        // consumed, and NO ledger grant.
        await using var db = _fixture.NewDbContext();
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
        await ResetUsersAsync();
        var token = await SeedBootstrapTokenAsync();
        var sub = NewWebAuthnSubstitute();
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
        await ResetUsersAsync();
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

        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
        await using var db = _fixture.NewDbContext();
        Assert.Equal(0, await db.Users.CountAsync(u => u.Username == "alice"));
        Assert.Equal(0, await db.WebAuthnCredentials.CountAsync());
        Assert.Equal(1, await db.BootstrapTokens.CountAsync(t => t.ConsumedAt == null));
    }

    [Fact]
    public async Task Info_accepts_a_valid_token_and_offers_no_ledger_list()
    {
        var token = await SeedBootstrapTokenAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/setup/{token}/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ADR-0088: /info exists to validate the token. It must NOT advertise
        // ledgers — the rows it used to list were empty placeholders, and
        // offering them is what made the first-run choice misleading.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("availableLedgers", out _));
    }

    [Fact]
    public async Task Info_rejects_invalid_token_with_401()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/setup/not-a-real-token/info");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The demo opt-in (ADR-0088) seeds a real ledger through the import
    /// pipeline and grants the new user ownership. This is the only path by
    /// which setup produces a ledger, and it must produce a POPULATED one —
    /// an empty ledger called "Demo" is the exact bug this ADR removed.
    /// </summary>
    [Fact]
    public async Task Complete_with_IncludeDemo_seeds_a_populated_Demo_ledger()
    {
        await ResetUsersAsync();
        var token = await SeedBootstrapTokenAsync();
        var credentialId = RandomBytes(64);
        var sub = NewWebAuthnSubstitute(credentialId);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
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
                AttestationResponse = FakeAttestation(credentialId),
                CredentialNickname = "YubiKey",
                IncludeDemo = true,
            });
        Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        using var doc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        var userId = doc.RootElement.GetProperty("userId").GetGuid();
        // The seed is best-effort, so a null ledger here means it threw —
        // assert explicitly rather than letting the null slide through.
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("ledgerId").ValueKind);
        var ledgerId = doc.RootElement.GetProperty("ledgerId").GetGuid();
        Assert.Equal("Demo", doc.RootElement.GetProperty("ledgerName").GetString());

        await using var db = _fixture.NewDbContext();
        Assert.Equal(1, await db.Ledgers.CountAsync(l => l.Id == ledgerId));
        Assert.Equal(1, await db.UserLedgerGrants.CountAsync(
            g => g.UserId == userId && g.LedgerId == ledgerId && g.Role == "owner"));
        // Populated, not a shell — the whole point.
        Assert.True(await db.Accounts.CountAsync(a => a.LedgerId == ledgerId) > 0,
            "Demo ledger should carry the sample dataset's accounts.");
    }
}
