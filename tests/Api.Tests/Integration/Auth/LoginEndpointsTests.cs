using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// End-to-end checks for the login (assertion) ceremony. The Fido2 layer
/// is substituted with NSubstitute so the test exercises endpoint
/// plumbing — route mapping, persistence side effects, signature-counter
/// updates, cookie issuance, ProblemDetails on failure — without forging
/// real WebAuthn assertions. Real-library verification is a manual
/// YubiKey smoke test post-deploy.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoginEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public LoginEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    private static AssertionOptions BuildFakeAssertionOptions() =>
        new()
        {
            Challenge = RandomBytes(32),
            RpId = "localhost",
            Timeout = 60000,
            AllowCredentials = new List<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Preferred,
        };

    private static AuthenticatorAssertionRawResponse FakeAssertion(byte[] credentialId) =>
        new()
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

    /// <summary>
    /// Build a substitute that reports a successful assertion against
    /// any input. <paramref name="newSignatureCounter"/> is what the
    /// endpoint must persist via <c>UpdateAfterAssertionAsync</c>.
    /// </summary>
    private static IWebAuthnService NewSuccessfulSubstitute(uint newSignatureCounter)
    {
        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginAssertion(Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => BuildFakeAssertionOptions());
        sub.CompleteAssertionAsync(
                Arg.Any<AuthenticatorAssertionRawResponse>(),
                Arg.Any<AssertionOptions>(),
                Arg.Any<byte[]>(),
                Arg.Any<uint>(),
                Arg.Any<IsUserHandleOwnerOfCredentialIdAsync>(),
                Arg.Any<CancellationToken>())
           .Returns(call => Task.FromResult(new WebAuthnAssertionOutcome(
               CredentialId: ((AuthenticatorAssertionRawResponse)call[0]).RawId,
               NewSignatureCounter: newSignatureCounter)));
        return sub;
    }

    /// <summary>
    /// Seed a user + a credential under a fresh synthetic ledger so
    /// tests have an attestation target to authenticate against. Returns
    /// the credential row so tests can echo its id at /complete.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, WebAuthnCredentialRow Credential)>
        SeedUserWithCredentialAsync(string username = "alice")
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await OverrideUsernameAsync(ledger.UserId, username);
        var credential = await ledger.AddCredentialAsync();
        return (ledger, credential);
    }

    /// <summary>
    /// Override the auto-generated random username from
    /// <see cref="SyntheticLedger"/> with a known one so the
    /// <c>/login/begin</c> lookup is deterministic.
    /// </summary>
    private async Task OverrideUsernameAsync(Guid userId, string username)
    {
        await using var db = _fixture.NewDbContext();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Username, _ => username));
    }

    [Fact]
    public async Task Begin_returns_options_and_persists_a_pending_login_challenge()
    {
        var (_, credential) = await SeedUserWithCredentialAsync(username: $"alice-{Guid.NewGuid():N}");

        var sub = NewSuccessfulSubstitute(newSignatureCounter: 1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var username = await GetUsernameAsync(credential.UserId);
        var response = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var challengeId = doc.RootElement.GetProperty("challengeId").GetGuid();
        Assert.NotEqual(Guid.Empty, challengeId);

        await using var db = _fixture.NewDbContext();
        var row = await db.PendingChallenges.AsNoTracking()
            .Where(c => c.Id == challengeId)
            .Select(c => new { c.Flow, c.UserId })
            .SingleAsync();
        Assert.Equal(ChallengeStore.LoginFlow, row.Flow);
        Assert.Equal(credential.UserId, row.UserId);
    }

    [Fact]
    public async Task Begin_rejects_unknown_username_with_401()
    {
        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login/begin",
            new LoginBeginRequest { Username = $"never-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Begin_rejects_user_with_no_credentials_with_401()
    {
        // Synthetic ledger creates a user but no credentials — login
        // can't proceed without something for the authenticator picker
        // to choose from.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var username = $"alice-{Guid.NewGuid():N}";
        await OverrideUsernameAsync(ledger.UserId, username);

        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Begin_rejects_disabled_user_with_401()
    {
        var (ledger, _) = await SeedUserWithCredentialAsync(username: $"alice-{Guid.NewGuid():N}");
        await using (var db = _fixture.NewDbContext())
        {
            await db.Users
                .Where(u => u.Id == ledger.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, _ => true));
        }

        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var username = await GetUsernameAsync(ledger.UserId);
        var response = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Begin_rejects_empty_username_with_422_and_code(string username)
    {
        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("login-username-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Complete_verifies_assertion_bumps_counter_and_issues_a_cookie()
    {
        var (_, credential) = await SeedUserWithCredentialAsync(username: $"alice-{Guid.NewGuid():N}");

        var sub = NewSuccessfulSubstitute(newSignatureCounter: 7);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var username = await GetUsernameAsync(credential.UserId);
        var beginResp = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        Assert.Equal(HttpStatusCode.OK, beginResp.StatusCode);
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var completeResp = await client.PostAsJsonAsync(
            "/api/auth/login/complete",
            new LoginCompleteRequest
            {
                ChallengeId = challengeId,
                AssertionResponse = FakeAssertion(credential.CredentialId),
            });
        Assert.Equal(HttpStatusCode.OK, completeResp.StatusCode);

        Assert.Contains(completeResp.Headers,
            h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
              && h.Value.Any(v => v.StartsWith("coffer.session=", StringComparison.Ordinal)));

        var doc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        Assert.Equal(credential.UserId, doc.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(username, doc.RootElement.GetProperty("username").GetString());

        // Counter must have been bumped to the value the substitute returned.
        Assert.Equal(7, await GetCounterAsync(credential.Id));
    }

    [Fact]
    public async Task Complete_rejects_unknown_challenge_id_with_401()
    {
        var (_, credential) = await SeedUserWithCredentialAsync(username: $"alice-{Guid.NewGuid():N}");

        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login/complete",
            new LoginCompleteRequest
            {
                ChallengeId = Guid.NewGuid(),
                AssertionResponse = FakeAssertion(credential.CredentialId),
            });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Complete_rejects_credential_owned_by_a_different_user_with_401()
    {
        // /begin resolves user A → persists challenge with user_id=A.
        // /complete sends an assertion whose credential belongs to user B.
        // Auth must fail; otherwise an attacker who knows user B's credential
        // id could log in as A.
        var (_, credentialA) = await SeedUserWithCredentialAsync(username: $"a-{Guid.NewGuid():N}");
        var (_, credentialB) = await SeedUserWithCredentialAsync(username: $"b-{Guid.NewGuid():N}");

        var sub = NewSuccessfulSubstitute(1);
        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var usernameA = await GetUsernameAsync(credentialA.UserId);
        var beginResp = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = usernameA });
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var completeResp = await client.PostAsJsonAsync(
            "/api/auth/login/complete",
            new LoginCompleteRequest
            {
                ChallengeId = challengeId,
                AssertionResponse = FakeAssertion(credentialB.CredentialId),
            });
        Assert.Equal(HttpStatusCode.Unauthorized, completeResp.StatusCode);

        // No counter bumps anywhere.
        Assert.Equal(0, await GetCounterAsync(credentialA.Id));
        Assert.Equal(0, await GetCounterAsync(credentialB.Id));
    }

    [Fact]
    public async Task Complete_returning_a_failed_assertion_leaves_no_DB_side_effects()
    {
        var (_, credential) = await SeedUserWithCredentialAsync(username: $"alice-{Guid.NewGuid():N}");

        var sub = Substitute.For<IWebAuthnService>();
        sub.BeginAssertion(Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => BuildFakeAssertionOptions());
        sub.CompleteAssertionAsync(
                Arg.Any<AuthenticatorAssertionRawResponse>(),
                Arg.Any<AssertionOptions>(),
                Arg.Any<byte[]>(),
                Arg.Any<uint>(),
                Arg.Any<IsUserHandleOwnerOfCredentialIdAsync>(),
                Arg.Any<CancellationToken>())
           .Returns<Task<WebAuthnAssertionOutcome>>(_ =>
               throw new Fido2VerificationException("forged signature"));

        await using var factory = new ApiFactory(_fixture).WithService(_ => sub);
        using var client = factory.CreateClient();

        var username = await GetUsernameAsync(credential.UserId);
        var beginResp = await client.PostAsJsonAsync(
            "/api/auth/login/begin", new LoginBeginRequest { Username = username });
        var challengeId = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();

        var completeResp = await client.PostAsJsonAsync(
            "/api/auth/login/complete",
            new LoginCompleteRequest
            {
                ChallengeId = challengeId,
                AssertionResponse = FakeAssertion(credential.CredentialId),
            });
        Assert.Equal(HttpStatusCode.Unauthorized, completeResp.StatusCode);

        // Counter still 0; no auth_sessions row created.
        Assert.Equal(0, await GetCounterAsync(credential.Id));

        await using var db = _fixture.NewDbContext();
        var sessionCount = await db.AuthSessions
            .CountAsync(s => s.UserId == credential.UserId && s.RevokedAt == null);
        Assert.Equal(0, sessionCount);
    }

    private async Task<long> GetCounterAsync(Guid credentialId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.WebAuthnCredentials
            .Where(c => c.Id == credentialId)
            .Select(c => c.SignatureCounter)
            .SingleAsync();
    }

    private async Task<string> GetUsernameAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Username)
            .SingleAsync()
            ?? throw new InvalidOperationException($"No username found for user {userId}.");
    }
}
