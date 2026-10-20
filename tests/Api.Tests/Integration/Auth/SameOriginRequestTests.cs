using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// The cross-site request check (2026-10-12 security audit, M1): a state-changing
/// request carrying the session cookie must come from Coffer's own origin.
/// </summary>
/// <remarks>
/// SameSite=Strict stops other SITES; a sibling subdomain is the same site. The browser
/// says where a request came from in <c>Sec-Fetch-Site</c> (or, older ones, <c>Origin</c>),
/// and the server now listens. "Sign out other sessions" is the probe: a cookie-carrying
/// POST whose effect is easy to observe.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SameOriginRequestTests
{
    private readonly PostgresFixture _fixture;

    public SameOriginRequestTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(HttpClient Client, SyntheticLedger User)> TwoSessionsAsync(ApiFactory factory)
    {
        var user = await SyntheticLedger.CreateAsync(_fixture);
        await user.IssueSessionCookieAsync();                 // the "other" session
        var cookie = await user.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return (client, user);
    }

    private async Task<int> ActiveSessionsAsync(Guid userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.AuthSessions.CountAsync(s => s.UserId == userId && s.RevokedAt == null);
    }

    private static HttpRequestMessage SignOutOthers(string? fetchSite = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sessions/sign-out-others");
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Theory]
    [InlineData("same-site")]   // a sibling subdomain: the case SameSite=Strict lets through
    [InlineData("cross-site")]
    [InlineData("none")]
    public async Task A_cookie_carrying_post_from_elsewhere_is_refused_and_does_nothing(string fetchSite)
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, user) = await TwoSessionsAsync(factory);

        var response = await client.SendAsync(SignOutOthers(fetchSite));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, await ActiveSessionsAsync(user.UserId));
    }

    [Fact]
    public async Task A_same_origin_post_goes_through()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, user) = await TwoSessionsAsync(factory);

        var response = await client.SendAsync(SignOutOthers("same-origin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await ActiveSessionsAsync(user.UserId));
    }

    [Fact]
    public async Task Without_Sec_Fetch_Site_a_foreign_Origin_is_refused()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, user) = await TwoSessionsAsync(factory);

        var response = await client.SendAsync(SignOutOthers(origin: "https://other.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, await ActiveSessionsAsync(user.UserId));
    }

    [Fact]
    public async Task Without_Sec_Fetch_Site_our_own_Origin_goes_through()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, _) = await TwoSessionsAsync(factory);

        var response = await client.SendAsync(SignOutOthers(origin: "http://localhost"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_neither_header_is_not_from_a_browser_and_goes_through()
    {
        // A script or CLI holding a cookie: CSRF does not apply to it.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, _) = await TwoSessionsAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(SignOutOthers())).StatusCode);
    }

    [Fact]
    public async Task Reads_and_cookieless_requests_are_untouched()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var (client, _) = await TwoSessionsAsync(factory);

        // A GET is never refused for its origin.
        var read = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        read.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(read)).StatusCode);

        // No session cookie, nothing to forge: an anonymous POST reaches its endpoint.
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login/begin")
        {
            Content = JsonContent.Create(new { username = "nobody-" + Guid.NewGuid().ToString("N") }),
        };
        login.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await anonymous.SendAsync(login)).StatusCode);
    }
}
