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
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// The fresh passkey tap that sensitive actions need (2026-10-12 security audit, M2).
/// </summary>
/// <remarks>
/// Before, a session cookie alone could enrol a passkey of its own and then pass every
/// step-up with it; and adding passkeys, new recovery codes, MCP tokens, granting admin,
/// restoring, changing the backup passphrase, deleting backups and turning MCP writes
/// on needed no tap at all. These pin both halves: every gated endpoint refuses without
/// a tap, and the gate refuses a tap that is too new, unverified, minted for another
/// action, or replayed. Fido2's signature check is substituted (StepUpTestHelper).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StepUpTests
{
    private readonly PostgresFixture _fixture;

    public StepUpTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> CookieClientAsync(
        ApiFactory factory, SyntheticLedger user, string authMethod = SessionAuthMethods.Passkey,
        DateTime? startedAt = null)
    {
        var cookie = await user.IssueSessionCookieAsync(authMethod, startedAt);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private async Task MakeAdminAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdmin, _ => true));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static HttpRequestMessage RegenerateRecoveryCodes() =>
        new(HttpMethod.Post, "/api/auth/recovery-codes/regenerate");

    // --- every gated action, refused without a tap ---------------------------

    public static TheoryData<string> GatedActions() => new()
    {
        StepUpActions.PasskeyAdd,
        StepUpActions.PasskeyDelete,
        StepUpActions.RecoveryCodes,
        StepUpActions.McpToken,
        StepUpActions.GrantAdmin + ":user",
        StepUpActions.GrantAdmin + ":invite",
        StepUpActions.Restore,
        StepUpActions.BackupPassphrase,
        StepUpActions.BackupDelete,
        StepUpActions.McpWrites,
    };

    [Theory]
    [MemberData(nameof(GatedActions))]
    public async Task Every_gated_action_is_refused_without_a_tap(string action)
    {
        // An admin with a passkey, signed in with it: everything except the tap.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);
        var key = await admin.AddCredentialAsync(nickname: "key");
        var other = await admin.AddMemberAsync("viewer");

        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, admin);

        HttpRequestMessage request = action switch
        {
            StepUpActions.PasskeyAdd => new(HttpMethod.Post, "/api/auth/register/begin"),
            StepUpActions.PasskeyDelete => new(HttpMethod.Delete, $"/api/auth/credentials/{key.Id}"),
            StepUpActions.RecoveryCodes => RegenerateRecoveryCodes(),
            StepUpActions.McpToken => new(HttpMethod.Post, "/api/account/mcp-tokens")
                { Content = JsonContent.Create(new { name = "app" }) },
            "grant-admin:user" => new(HttpMethod.Put, $"/api/admin/users/{other}/admin")
                { Content = JsonContent.Create(new { isAdmin = true }) },
            "grant-admin:invite" => new(HttpMethod.Post, "/api/admin/invites")
                { Content = JsonContent.Create(new { grantsAdmin = true }) },
            StepUpActions.Restore => new(HttpMethod.Post, "/api/admin/backups/restore")
                { Content = new MultipartFormDataContent { { new StringContent("x"), "passphrase" } } },
            StepUpActions.BackupPassphrase => new(HttpMethod.Put, "/api/admin/backups/passphrase")
                { Content = JsonContent.Create(new { passphrase = "correct-horse-battery" }) },
            StepUpActions.BackupDelete => new(HttpMethod.Delete,
                "/api/admin/backups/coffer-20260101T000000000Z-00000000"),
            StepUpActions.McpWrites => new(HttpMethod.Put, "/api/admin/system-settings/mcp")
                { Content = JsonContent.Create(new { enabled = true, writesEnabled = true }) },
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-required", await CodeOf(response));
    }

    // --- what the gate refuses ------------------------------------------------

    [Fact]
    public async Task A_passkey_enrolled_after_the_session_began_cannot_approve_anything()
    {
        // The audit's bypass: a stolen cookie enrolled its own key, then used it. A key
        // added during this session is refused even though it IS the caller's.
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await user.AddCredentialAsync(nickname: "before");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, user);
        await Task.Delay(20);                       // timestamps strictly after the session's
        var after = await user.AddCredentialAsync(nickname: "after");

        var response = await StepUpTestHelper.SendWithStepUpAsync(
            client, RegenerateRecoveryCodes(), StepUpActions.RecoveryCodes, after.CredentialId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-no-eligible-passkey", await CodeOf(response));
    }

    [Fact]
    public async Task A_tap_without_user_verification_is_refused()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var key = await user.AddCredentialAsync(nickname: "key");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, user);

        var request = RegenerateRecoveryCodes();
        request.Headers.Add(StepUpGuard.HeaderName, await StepUpTestHelper.HeaderValueAsync(
            client, StepUpActions.RecoveryCodes, key.CredentialId, userVerified: false));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-user-not-verified", await CodeOf(response));
    }

    [Fact]
    public async Task A_tap_for_one_action_cannot_approve_another()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var key = await user.AddCredentialAsync(nickname: "key");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, user);

        var request = RegenerateRecoveryCodes();
        request.Headers.Add(StepUpGuard.HeaderName, await StepUpTestHelper.HeaderValueAsync(
            client, StepUpActions.PasskeyDelete, key.CredentialId));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tap_approves_one_request_only()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var key = await user.AddCredentialAsync(nickname: "key");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => StepUpTestHelper.AcceptingWebAuthn());
        using var client = await CookieClientAsync(factory, user);
        var header = await StepUpTestHelper.HeaderValueAsync(client, StepUpActions.RecoveryCodes, key.CredentialId);

        var first = RegenerateRecoveryCodes();
        first.Headers.Add(StepUpGuard.HeaderName, header);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first)).StatusCode);

        var replay = RegenerateRecoveryCodes();
        replay.Headers.Add(StepUpGuard.HeaderName, header);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replay)).StatusCode);
    }

    [Fact]
    public async Task Begin_refuses_an_unknown_action()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await CookieClientAsync(factory, user);

        var response = await client.PostAsJsonAsync("/api/auth/step-up/begin", new { action = "reveal-everything" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-unknown-action", await CodeOf(response));
    }

    // --- the recovery-code allowance -------------------------------------------

    private static IWebAuthnService RegistrationAndAssertions(Func<byte[]> nextCredentialId)
    {
        var sub = StepUpTestHelper.AcceptingWebAuthn();
        sub.BeginRegistration(Arg.Any<Fido2User>(), Arg.Any<IReadOnlyList<PublicKeyCredentialDescriptor>>())
           .Returns(_ => new CredentialCreateOptions
           {
               Challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
               Rp = new PublicKeyCredentialRpEntity("localhost", "Coffer-test", null),
               User = new Fido2User { Id = Guid.NewGuid().ToByteArray(), Name = "u", DisplayName = "U" },
               PubKeyCredParams = new List<PubKeyCredParam> { new(COSE.Algorithm.ES256) },
               Timeout = 60000,
               ExcludeCredentials = new List<PublicKeyCredentialDescriptor>(),
           });
        sub.CompleteRegistrationAsync(
                Arg.Any<AuthenticatorAttestationRawResponse>(), Arg.Any<CredentialCreateOptions>(),
                Arg.Any<IsCredentialIdUniqueToUserAsyncDelegate>(), Arg.Any<CancellationToken>())
           .Returns(_ => Task.FromResult(new WebAuthnRegistrationOutcome(
               CredentialId: nextCredentialId(), PublicKey: new byte[77], SignatureCounter: 0,
               Aaguid: Guid.NewGuid(), Transports: new[] { "usb" })));
        return sub;
    }

    private static async Task<HttpResponseMessage> AddPasskeyUntappedAsync(HttpClient client)
    {
        var begin = await client.PostAsync("/api/auth/register/begin", content: null);
        if (begin.StatusCode != HttpStatusCode.OK) return begin;
        var challengeId = JsonDocument.Parse(await begin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("challengeId").GetGuid();
        var credentialId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        return await client.PostAsJsonAsync("/api/auth/register/complete", new RegisterCompleteRequest
        {
            ChallengeId = challengeId,
            CredentialNickname = "new key",
            AttestationResponse = new AuthenticatorAttestationRawResponse
            {
                Id = Convert.ToBase64String(credentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                RawId = credentialId,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                {
                    AttestationObject = new byte[64],
                    ClientDataJson = new byte[128],
                },
            },
        });
    }

    [Fact]
    public async Task A_recovery_code_session_may_add_one_passkey_without_a_tap()
    {
        // It may have no usable passkey at all — that is why it used a recovery code —
        // so without this it could never get one. One, though, not a stream of them.
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => RegistrationAndAssertions(
                () => System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)));
        using var client = await CookieClientAsync(factory, user, SessionAuthMethods.RecoveryCode);

        Assert.Equal(HttpStatusCode.OK, (await AddPasskeyUntappedAsync(client)).StatusCode);

        var second = await AddPasskeyUntappedAsync(client);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("step-up-required", await CodeOf(second));

        await using var db = _fixture.NewDbContext();
        Assert.Equal(1, await db.WebAuthnCredentials.CountAsync(c => c.UserId == user.UserId));
    }

    [Fact]
    public async Task The_recovery_allowance_ends_fifteen_minutes_after_signing_in()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => RegistrationAndAssertions(
                () => System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)));
        using var client = await CookieClientAsync(
            factory, user, SessionAuthMethods.RecoveryCode, startedAt: DateTime.UtcNow.AddMinutes(-16));

        var response = await AddPasskeyUntappedAsync(client);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-required", await CodeOf(response));
    }

    [Fact]
    public async Task A_passkey_session_gets_no_allowance_to_add_a_passkey_untapped()
    {
        // The allowance is for recovery-code sessions only; a passkey session has a
        // passkey to tap with.
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await user.AddCredentialAsync(nickname: "key");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth()
            .WithService(_ => RegistrationAndAssertions(
                () => System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)));
        using var client = await CookieClientAsync(factory, user);

        var response = await AddPasskeyUntappedAsync(client);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("step-up-required", await CodeOf(response));
    }
}
