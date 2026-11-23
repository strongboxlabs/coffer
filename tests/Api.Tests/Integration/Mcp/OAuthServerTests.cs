using System.Net;
using System.Net.Http.Json;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// OpenIddict AS foundation (ADR-0063 §D2, slice 1): with MCP enabled the
/// authorization server is live and publishes its RFC 8414 discovery document
/// advertising the authorize/token endpoints. This is the document Claude reads
/// to drive the connector OAuth flow.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OAuthServerTests : DatabaseTest
{
    public OAuthServerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Discovery_document_advertises_endpoints_when_mcp_enabled()
    {
        await using var factory = new ApiFactory(Db).WithMcpEnabled();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("authorization_endpoint", json);
        Assert.Contains("oauth/authorize", json);
        Assert.Contains("oauth/token", json);
        // PKCE is required (ADR-0063): the server must advertise S256.
        Assert.Contains("S256", json);
        // DCR endpoint advertised (we add it; OpenIddict 7.5 has no built-in DCR).
        Assert.Contains("registration_endpoint", json);
        Assert.Contains("oauth/register", json);
    }

    [Fact]
    public async Task Discovery_urls_honor_reverse_proxy_forwarded_headers()
    {
        // Behind Traefik the app speaks HTTP on an internal host; the generated
        // OAuth URLs must be the external https://<domain> the client used.
        await using var factory = new ApiFactory(Db).WithMcpEnabled();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "coffer.example.com");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("https://coffer.example.com/oauth/authorize", json);
        Assert.Contains("https://coffer.example.com/oauth/token", json);
        // The DCR endpoint we inject must also reflect the external origin.
        Assert.Contains("https://coffer.example.com/oauth/register", json);
    }

    [Fact]
    public async Task Issuer_has_no_trailing_slash_and_matches_the_protected_resource_metadata()
    {
        // RFC 8414 §3.3: the issuer in the document must equal, exactly, the issuer the
        // client built the metadata URL from — the authorization_servers entry. The MCP
        // SDK enforces this; a trailing "/" made mcp-remote abort during auth.
        await using var factory = new ApiFactory(Db).WithMcpEnabled();
        using var client = factory.CreateClient();

        var discovery = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/.well-known/oauth-authorization-server");
        var resource = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/.well-known/oauth-protected-resource");

        var issuer = discovery.GetProperty("issuer").GetString();
        var named = resource.GetProperty("authorization_servers")[0].GetString();
        Assert.False(string.IsNullOrEmpty(issuer));
        Assert.False(issuer!.EndsWith('/'));
        Assert.Equal(named, issuer);
    }

    [Fact]
    public async Task Protected_resource_metadata_points_at_the_authorization_server()
    {
        await using var factory = new ApiFactory(Db).WithMcpEnabled();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("authorization_servers", json);
        Assert.Contains("/mcp", json);
        Assert.Contains("coffer.read", json);
    }

    [Fact]
    public async Task Unauthenticated_mcp_401_advertises_the_resource_metadata()
    {
        // No dev-auth: the only way to authenticate /mcp is a token, so an
        // anonymous call 401s and must carry the discovery pointer.
        await using var factory = new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/mcp", new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var values));
        Assert.Contains(values!, v => v.Contains("oauth-protected-resource"));
    }
}
