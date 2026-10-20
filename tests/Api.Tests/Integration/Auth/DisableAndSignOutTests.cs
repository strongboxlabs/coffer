using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Disabling a user ends their access; signing out everywhere ends it without disabling
/// (2026-10-12 security audit, H2, S1).
/// </summary>
/// <remarks>
/// Disabling used to block only new logins: an open session kept working for up to 30
/// days, an MCP token for 90 or forever, and a disabled admin could re-enable
/// themselves. Re-enabling must not bring any of it back.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DisableAndSignOutTests
{
    private readonly PostgresFixture _fixture;

    public DisableAndSignOutTests(PostgresFixture fixture) => _fixture = fixture;

    private static HttpClient CookieClient(ApiFactory factory, string cookie)
    {
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

    private static Task<HttpResponseMessage> SetDisabledAsync(HttpClient admin, Guid userId, bool disabled) =>
        admin.PutAsJsonAsync($"/api/admin/users/{userId}/disabled", new { disabled });

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_and_tokens_and_re_enabling_restores_none()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var userCookie = await user.IssueSessionCookieAsync();
        var tokens = new McpTokensRepository(_fixture.NewServiceFactory(), TimeProvider.System);
        var (_, tokenHash) = McpTokenService.Generate();
        await tokens.IssueAsync(user.UserId, "app", tokenHash, "coffer.read", expiresAt: null);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var adminClient = CookieClient(factory, await admin.IssueSessionCookieAsync());
        using var userClient = CookieClient(factory, userCookie);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotNull(await tokens.ValidateAsync(tokenHash));

        Assert.Equal(HttpStatusCode.NoContent, (await SetDisabledAsync(adminClient, user.UserId, true)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Null(await tokens.ValidateAsync(tokenHash));

        // Re-enabling restores nothing: they sign in again and reconnect their apps.
        Assert.Equal(HttpStatusCode.NoContent, (await SetDisabledAsync(adminClient, user.UserId, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Null(await tokens.ValidateAsync(tokenHash));
    }

    [Fact]
    public async Task A_disabled_user_is_refused_even_by_a_session_the_disable_did_not_revoke()
    {
        // Check at use, not only revoke at disable: a session minted in the same
        // instant, or a future path that flips the flag without revoking.
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var cookie = await user.IssueSessionCookieAsync();
        await using (var db = _fixture.NewDbContext())
            await db.Users.Where(u => u.Id == user.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, _ => true));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = CookieClient(factory, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_admin_cannot_re_enable_themselves()
    {
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(first.UserId);
        await MakeAdminAsync(second.UserId);
        var secondCookie = await second.IssueSessionCookieAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var firstClient = CookieClient(factory, await first.IssueSessionCookieAsync());
        using var secondClient = CookieClient(factory, secondCookie);
        Assert.Equal(HttpStatusCode.NoContent, (await SetDisabledAsync(firstClient, second.UserId, true)).StatusCode);

        var selfRescue = await SetDisabledAsync(secondClient, second.UserId, false);

        Assert.Equal(HttpStatusCode.Unauthorized, selfRescue.StatusCode);
        await using var db = _fixture.NewDbContext();
        Assert.True(await db.Users.Where(u => u.Id == second.UserId).Select(u => u.IsDisabled).SingleAsync());
    }

    [Fact]
    public async Task The_database_no_longer_counts_a_disabled_admin_as_an_admin()
    {
        // current_app_user_is_admin() guards system_events and the admin notification
        // targets through RLS (mig 213); it ignored is_disabled until mig 248.
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);

        async Task<bool> IsAdminInDatabaseAsync()
        {
            await using var db = _fixture.NewAppDbContextAsUser(admin.UserId);
            return await db.Database.SqlQueryRaw<bool>("SELECT current_app_user_is_admin() AS \"Value\"")
                                    .SingleAsync();
        }

        Assert.True(await IsAdminInDatabaseAsync());
        await using (var db = _fixture.NewDbContext())
            await db.Users.Where(u => u.Id == admin.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, _ => true));
        Assert.False(await IsAdminInDatabaseAsync());
    }

    [Fact]
    public async Task Sign_out_other_sessions_keeps_the_one_asking()
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var otherCookie = await user.IssueSessionCookieAsync();
        var thisCookie = await user.IssueSessionCookieAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var thisClient = CookieClient(factory, thisCookie);
        using var otherClient = CookieClient(factory, otherCookie);

        var response = await thisClient.PostAsync("/api/auth/sessions/sign-out-others", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<Coffer.Api.Auth.Webauthn.SignOutOthersResponse>())!.SessionsEnded);
        Assert.Equal(HttpStatusCode.OK, (await thisClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherClient.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_admin_can_sign_a_user_out_everywhere_without_disabling_them()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);
        var user = await SyntheticLedger.CreateAsync(_fixture);
        var userCookie = await user.IssueSessionCookieAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var adminClient = CookieClient(factory, await admin.IssueSessionCookieAsync());
        using var userClient = CookieClient(factory, userCookie);

        var response = await adminClient.PostAsync($"/api/admin/users/{user.UserId}/sign-out", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).StatusCode);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.Users.Where(u => u.Id == user.UserId).Select(u => u.IsDisabled).SingleAsync());
    }

    [Fact]
    public async Task Signing_out_an_unknown_user_is_not_found()
    {
        var admin = await SyntheticLedger.CreateAsync(_fixture);
        await MakeAdminAsync(admin.UserId);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var adminClient = CookieClient(factory, await admin.IssueSessionCookieAsync());

        var response = await adminClient.PostAsync($"/api/admin/users/{Guid.NewGuid()}/sign-out", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
