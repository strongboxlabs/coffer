using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenIddict.Abstractions;

using Coffer.Api.Tests.Integration.Infra;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// OAuth authorization-code flow (ADR-0063 §D2, slice 3). The authorization
/// endpoint reuses the WebAuthn cookie session: an anonymous request for a
/// validly-registered client is bounced to the SPA login with a returnUrl back
/// to the authorize request. (The authenticated consent/issue path needs a real
/// WebAuthn cookie + the consent page; it's exercised end-to-end in the tunnel
/// validation.)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OAuthFlowTests
{
    private readonly PostgresFixture _fixture;

    public OAuthFlowTests(PostgresFixture fixture) => _fixture = fixture;

    private const string RedirectUri = "https://example.com/callback";
    // A syntactically valid S256 PKCE challenge (43-char base64url).
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static async Task SeedClientAsync(WebApplicationFactory<Program> factory, string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is not null)
            return;
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            RedirectUris = { new Uri(RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "coffer.read",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        });
    }

    [Fact]
    public async Task Anonymous_authorize_for_a_valid_client_redirects_to_login()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        await SeedClientAsync(factory, "test-client-login");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var url = "/oauth/authorize?response_type=code&client_id=test-client-login"
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=coffer.read"
            + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256"
            + "&state=abc123";

        var response = await client.GetAsync(url);

        // OpenIddict validated the request (client + PKCE + scope), then our
        // passthrough handler found no session and bounced to the SPA login.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/login", location);
        Assert.Contains("returnUrl", location);
        Assert.Contains("oauth", location); // the encoded return target is the authorize request
    }

    [Fact]
    public async Task Authorize_accepts_the_rfc8707_resource_parameter()
    {
        // MCP clients send a `resource` parameter (the MCP server URI). OpenIddict
        // rejects unknown resources (ID2190 invalid_target); we strip it, so the
        // request proceeds to login rather than erroring.
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        await SeedClientAsync(factory, "test-client-resource");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var url = "/oauth/authorize?response_type=code&client_id=test-client-resource"
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=coffer.read"
            + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256"
            + "&resource=" + Uri.EscapeDataString("https://example.com/mcp");

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Authorize_for_an_unknown_client_is_rejected()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        var url = "/oauth/authorize?response_type=code&client_id=does-not-exist"
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&scope=coffer.read"
            + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256";

        var response = await client.GetAsync(url);

        // OpenIddict rejects before our handler — never a redirect to login.
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Dcr_registration_is_rate_limited_per_ip()
    {
        // ADR-0081 D4 — limit 2/min: registrations 1+2 are created, the 3rd is
        // rejected by the per-IP limiter (429) before it reaches the handler. The
        // client cap is lifted so the count ceiling can't shadow the 429 in the
        // shared test DB.
        await using var factory = new ApiFactory(_fixture)
            .WithMcpEnabled()
            .WithConfig("Api:Mcp:DcrRateLimitPerMinute", "2")
            .WithConfig("Api:Mcp:MaxDynamicClients", "1000");
        using var client = factory.CreateClient();

        var body = new
        {
            client_name = "Rate Limit Client",
            redirect_uris = new[] { "https://example.com/cb" },
            token_endpoint_auth_method = "none",
        };

        var r1 = await client.PostAsJsonAsync("/oauth/register", body);
        var r2 = await client.PostAsJsonAsync("/oauth/register", body);
        var r3 = await client.PostAsJsonAsync("/oauth/register", body);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task Dynamic_client_registration_creates_a_usable_client()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        // Register (RFC 7591), anonymous, public client (auth method "none").
        var registration = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "DCR Test Client",
            redirect_uris = new[] { "https://example.com/cb" },
            token_endpoint_auth_method = "none",
        });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var body = await registration.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = body.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrEmpty(clientId));
        // Public client → no secret returned.
        Assert.False(body.TryGetProperty("client_secret", out _));

        // The freshly-registered client is immediately usable at /authorize.
        var url = "/oauth/authorize?response_type=code&client_id=" + clientId
            + "&redirect_uri=" + Uri.EscapeDataString("https://example.com/cb")
            + "&scope=coffer.read"
            + "&code_challenge=" + CodeChallenge + "&code_challenge_method=S256";
        var authorize = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.StartsWith("/login", authorize.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Authorization_code_flow_issues_a_refresh_token_that_can_be_exchanged()
    {
        // The full authenticated flow (cookie session via /dev-login) → consent →
        // code → token, then refresh. Proves the ADR-0063 §D2 refresh path: a
        // refresh token IS issued (offline_access granted server-side) and renews
        // the access token silently, so the 1h access-token expiry no longer forces
        // a full interactive re-auth. The client here has the SAME shape as a
        // DCR'd / production client — coffer.read scope + refresh-token grant, NO
        // explicit offline_access scope permission — so this also proves
        // offline_access is permitted via the refresh-token grant alone.
        // RequireHttps=false so the session cookie (Secure by default) rides on the
        // plain-HTTP test server — otherwise /oauth/authorize never sees the session.
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled()
            .WithConfig("Api:Cookie:RequireHttps", "false");
        await SeedClientAsync(factory, "test-client-refresh");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            // HandleCookies defaults true: the /dev-login session cookie rides on
            // the subsequent /oauth/authorize, satisfying its cookie-scheme check.
        });

        // Authenticate (Development-only escape hatch → a real cookie session).
        var login = await client.PostAsync("/api/auth/dev-login", content: null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Consent + issue: POST with decision=allow issues directly (skips the SPA
        // consent page). RFC 7636 example verifier ↔ the CodeChallenge constant.
        const string codeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var authorize = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = "test-client-refresh",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "coffer.read",
                ["code_challenge"] = CodeChallenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "xyz",
                ["decision"] = "allow",
            }));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = authorize.Headers.Location!.Query.TrimStart('?').Split('&')
            .Select(kv => kv.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0] == "code")
            .Select(kv => Uri.UnescapeDataString(kv[1]))
            .FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(code));

        // Exchange the code: the response MUST carry a refresh_token (the fix).
        var token = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = "test-client-refresh",
                ["code_verifier"] = codeVerifier,
            }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var tokenBody = await token.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokenBody.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(refreshToken));
        Assert.Contains("offline_access", tokenBody.GetProperty("scope").GetString());

        // Exchange the refresh token → a fresh access token (silent renewal).
        var refreshed = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken!,
                ["client_id"] = "test-client-refresh",
            }));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedBody = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(refreshedBody.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Dynamic_client_registration_rejects_an_insecure_redirect_uri()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var registration = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Bad Client",
            redirect_uris = new[] { "http://evil.example.com/cb" }, // non-loopback http
            token_endpoint_auth_method = "none",
        });

        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);
    }
}
