using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Db.Services;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MultiUser;

/// <summary>
/// ADR-0083 slice B — invite links. Repo-level coverage of the token lifecycle
/// (mint / validate / expiry / consume / list / revoke), plus the security-critical
/// redeem ceremony end-to-end (the WebAuthn seam mocked, mirroring
/// <c>SetupEndpointsTests</c>): a valid invite → a new user with the invite's
/// PRE-SCOPED grant + the invite consumed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvitesTests
{
    private readonly PostgresFixture _fixture;

    public InvitesTests(PostgresFixture fixture) => _fixture = fixture;

    private InvitesRepository Repo() => new(_fixture.NewServiceFactory());

    [Fact]
    public async Task Create_then_valid_scope_returns_ledger_role_and_admin_flag()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = Repo();
        var token = await repo.CreateAsync(ledger.UserId, ledger.LedgerId, "editor", grantsAdmin: false);

        var scope = await repo.GetValidScopeAsync(token);
        Assert.NotNull(scope);
        Assert.Equal(ledger.LedgerId, scope!.LedgerId);
        Assert.Equal("editor", scope.Role);
        Assert.False(scope.GrantsAdmin);

        // A malformed token is "not found", not an exception.
        Assert.Null(await repo.GetValidScopeAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Expired_and_consumed_invites_are_not_valid()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var (expiredToken, expiredHash) = BootstrapTokenService.GenerateToken();
        var (usedToken, usedHash) = BootstrapTokenService.GenerateToken();

        await using (var db = _fixture.NewDbContext())
        {
            db.Invites.Add(new InviteRow
            {
                TokenHash = expiredHash, Id = Guid.NewGuid(), IssuedByUserId = ledger.UserId,
                LedgerId = ledger.LedgerId, Role = "viewer", ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            });
            db.Invites.Add(new InviteRow
            {
                TokenHash = usedHash, Id = Guid.NewGuid(), IssuedByUserId = ledger.UserId,
                LedgerId = ledger.LedgerId, Role = "viewer", ExpiresAt = DateTime.UtcNow.AddDays(7),
                ConsumedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var repo = Repo();
        Assert.Null(await repo.GetValidScopeAsync(expiredToken));
        Assert.Null(await repo.GetValidScopeAsync(usedToken));
    }

    [Fact]
    public async Task List_and_revoke_are_scoped_to_the_ledger_and_by_id_for_admin()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = Repo();
        await repo.CreateAsync(ledger.UserId, ledger.LedgerId, "viewer", false);
        await repo.CreateAsync(ledger.UserId, ledger.LedgerId, "editor", false);

        var pending = await repo.ListPendingForLedgerAsync(ledger.LedgerId);
        Assert.Equal(2, pending.Count);

        // Owner revoke is scoped: a wrong ledger id can't touch it; the right one removes it.
        Assert.False(await repo.RevokeForLedgerAsync(Guid.NewGuid(), pending[0].Id));
        Assert.True(await repo.RevokeForLedgerAsync(ledger.LedgerId, pending[0].Id));
        Assert.Single(await repo.ListPendingForLedgerAsync(ledger.LedgerId));

        // Admin revoke by id removes the rest.
        Assert.True(await repo.RevokeAsync(pending[1].Id));
        Assert.Empty(await repo.ListPendingForLedgerAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task Redeem_creates_a_user_with_the_pre_scoped_grant_and_consumes_the_invite()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var token = await Repo().CreateAsync(ledger.UserId, ledger.LedgerId, "editor", grantsAdmin: false);

        var credentialId = RandomBytes(64);
        await using var factory = new ApiFactory(_fixture).WithService(_ => NewWebAuthnSubstitute(credentialId));
        using var client = factory.CreateClient();

        var username = $"invitee-{Guid.NewGuid():N}";
        var beginResp = await client.PostAsJsonAsync(
            $"/api/auth/invite/{token}/begin",
            new InviteBeginRequest { Username = username, DisplayName = "Invitee" });
        Assert.True(beginResp.StatusCode == HttpStatusCode.OK, await beginResp.Content.ReadAsStringAsync());
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var completeResp = await client.PostAsJsonAsync(
            $"/api/auth/invite/{token}/complete",
            new InviteCompleteRequest
            {
                ChallengeId = challengeId,
                AttestationResponse = FakeAttestation(credentialId),
                CredentialNickname = "Key",
            });
        Assert.True(completeResp.StatusCode == HttpStatusCode.OK,
            await completeResp.Content.ReadAsStringAsync());

        // A session cookie is issued (the invitee lands signed-in).
        Assert.Contains(completeResp.Headers,
            h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
              && h.Value.Any(v => v.StartsWith("coffer.session=", StringComparison.Ordinal)));

        var doc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("userId").GetString(), out var newUserId));
        Assert.Equal(10, doc.RootElement.GetProperty("recoveryCodes").EnumerateArray().Count());

        await using var db = _fixture.NewDbContext();
        // The pre-scoped grant landed at the invite's role...
        var grant = await db.UserLedgerGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.LedgerId == ledger.LedgerId && g.UserId == newUserId);
        Assert.NotNull(grant);
        Assert.Equal("editor", grant!.Role);
        // ...the new user is not an admin (grantsAdmin was false)...
        Assert.False((await db.Users.AsNoTracking().FirstAsync(u => u.Id == newUserId)).IsAdmin);
        // ...and the invite is spent.
        Assert.Null(await Repo().GetValidScopeAsync(token));
    }

    // ── WebAuthn seam mock (mirrors SetupEndpointsTests) ──────────────────────

    private static IWebAuthnService NewWebAuthnSubstitute(byte[] credentialId)
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginRegistration(Arg.Any<Fido2User>(), Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => new CredentialCreateOptions
           {
               Challenge = RandomBytes(32),
               Rp = new PublicKeyCredentialRpEntity("localhost", "Coffer-test", null),
               User = new Fido2User { Id = Guid.NewGuid().ToByteArray(), Name = "invitee", DisplayName = "Invitee" },
               PubKeyCredParams = new List<PubKeyCredParam> { new(COSE.Algorithm.ES256) },
               Timeout = 60000,
               ExcludeCredentials = new List<PublicKeyCredentialDescriptor>(),
           });
        sub.CompleteRegistrationAsync(
            Arg.Any<AuthenticatorAttestationRawResponse>(), Arg.Any<CredentialCreateOptions>(),
            Arg.Any<IsCredentialIdUniqueToUserAsyncDelegate>(), Arg.Any<CancellationToken>())
           .Returns(_ => Task.FromResult(new WebAuthnRegistrationOutcome(
               CredentialId: credentialId, PublicKey: RandomBytes(77), SignatureCounter: 0,
               Aaguid: Guid.NewGuid(), Transports: new[] { "usb" })));
        return sub;
    }

    private static AuthenticatorAttestationRawResponse FakeAttestation(byte[] credentialId) =>
        new()
        {
            Id = Convert.ToBase64String(credentialId).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = RandomBytes(64),
                ClientDataJson = RandomBytes(128),
            },
        };

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
