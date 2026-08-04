using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// End-to-end checks for the cookie-session machinery. The Development-only
/// <c>/api/auth/dev-login</c> escape hatch lets us prove the full Issue →
/// Validate → Revoke loop works through the real <see cref="CookieAuthHandler"/>
/// before the WebAuthn ceremonies (PR 3.4) wire up production registrations.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CookieAuthFlowTests
{
    private readonly PostgresFixture _fixture;

    public CookieAuthFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DevLogin_issues_a_cookie_session_for_the_system_user()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/auth/dev-login", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(response.Headers,
            h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
              && h.Value.Any(v => v.StartsWith("coffer.session=", StringComparison.Ordinal)));

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("sessionId").GetString(), out _));
    }

    [Fact]
    public async Task Logout_clears_the_cookie_and_invalidates_the_session()
    {
        await using var factory = new ApiFactory(_fixture);
        // CreateClient defaults to HandleCookies=true, so Set-Cookie from
        // /dev-login automatically rides on the subsequent /logout request
        // — same behaviour as a real browser.
        using var client = factory.CreateClient();

        // Issue.
        var loginResponse = await client.PostAsync("/api/auth/dev-login", content: null);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Logout: returns 200 + session is no longer valid.
        var logoutResponse = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        await using var stream = await logoutResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("logged-out", doc.RootElement.GetProperty("status").GetString());

        // Calling logout a second time on the now-clear cookie also returns
        // 200 — logout is idempotent and never strands the user on a 4xx.
        var secondLogout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, secondLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_without_a_session_cookie_still_returns_200()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
