using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Every response carries the browser security headers (FU-091): the app sent none, and
/// whether an install had them depended on a proxy configuration nobody was told to write.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityHeadersTests
{
    private readonly PostgresFixture _fixture;

    public SecurityHeadersTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/api/meta/version")]   // an API answer
    [InlineData("/api/no-such-route")]  // a 404, which carries them too
    public async Task A_response_carries_the_security_headers(string path)
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("fonts.googleapis.com", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        // Plain HTTP: HSTS would be meaningless, and wrong for a localhost install.
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Over_https_it_also_asks_for_https_from_now_on()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync("/api/meta/version");

        var hsts = Assert.Single(response.Headers.GetValues("Strict-Transport-Security"));
        Assert.Equal("max-age=31536000", hsts);   // no includeSubDomains: not this app's to decide
    }

    [Fact]
    public async Task No_cross_origin_opener_policy_that_would_cut_a_connectors_popup_sign_in()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/meta/version");

        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
    }
}
