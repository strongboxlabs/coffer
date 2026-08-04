using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// End-to-end checks for <c>GET /api/auth/me</c>. The SPA hits this
/// at every protected-route load to gate the redirect to /login,
/// so the 200 / 401 split is critical contract — locking it down
/// here so a future refactor can't drift it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthMeEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AuthMeEndpointTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Get_returns_current_users_identity()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthEndpoints.CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(alice.UserId, body!.Id);
        Assert.Equal(alice.Username, body.Username);
        // SyntheticLedger uses the same string for display name and
        // username — that's a setup detail, not a contract; this
        // assertion just confirms the field comes through.
        Assert.Equal(alice.Username, body.DisplayName);
        // A synthetic user is not an admin (ADR-0060).
        Assert.False(body.IsAdmin);
    }

    [Fact]
    public async Task Get_reflects_the_admin_flag()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        // Only the service role may set is_admin (migration 138 column grant).
        await using (var serviceDb = _fixture.NewDbContext())
        {
            await serviceDb.Database.ExecuteSqlRawAsync(
                "UPDATE users SET is_admin = true WHERE id = {0}", alice.UserId);
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var body = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<AuthEndpoints.CurrentUserResponse>())!;
        Assert.True(body.IsAdmin);
    }

    [Fact]
    public async Task Request_role_cannot_self_promote_is_admin()
    {
        // Privilege boundary (ADR-0060 / migration 138): the users_self RLS
        // policy is FOR ALL, but the column grant denies the request-time
        // coffer_app role any UPDATE of is_admin — so a user can't self-promote
        // even on their own row. Only the service role (setup-complete,
        // migrations) sets it.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var appDb = _fixture.NewAppDbContextAsUser(alice.UserId);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            appDb.Database.ExecuteSqlRawAsync(
                "UPDATE users SET is_admin = true WHERE id = {0}", alice.UserId));
        // 42501 = insufficient_privilege (the column grant denies it).
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Get_returns_401_when_no_session_cookie_present()
    {
        // No auth header, no dev-auth → the default policy rejects.
        // The SPA's auth-check loader treats this 401 as "redirect
        // to /login," which is the whole reason this endpoint exists.
        await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_returns_callers_own_row_not_another_users()
    {
        // Belt-and-suspenders test of RLS at the endpoint layer: if
        // /api/auth/me ever looked up users by something other than
        // the authenticated user (regression risk in a future
        // refactor), this would surface it.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var aliceClient = await AuthedClientAsync(factory, alice);

        var response = await aliceClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<AuthEndpoints.CurrentUserResponse>())!;
        Assert.Equal(alice.UserId, body.Id);
        Assert.NotEqual(bob.UserId, body.Id);
    }
}
