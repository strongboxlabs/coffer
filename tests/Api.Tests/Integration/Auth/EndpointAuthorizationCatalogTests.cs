using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Every endpoint says who may call it (FU-095). An endpoint that says nothing falls to
/// the fallback policy, a signed-in user, and that is the safe side; but a route nobody
/// decided about is a route nobody reviewed, so each one is held to declaring either
/// <c>RequireAuthorization</c> or <c>AllowAnonymous</c>, and the anonymous ones are named
/// here with the reason.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointAuthorizationCatalogTests
{
    private readonly PostgresFixture _fixture;

    public EndpointAuthorizationCatalogTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Routes anyone may call, by pattern prefix, and why.</summary>
    private static readonly Dictionary<string, string> Anonymous = new()
    {
        ["/healthz"] = "liveness; an orchestrator has no session",
        ["/readyz"] = "readiness; likewise",
        ["/api/auth/login"] = "signing in is how a session begins",
        ["/api/auth/recovery"] = "signing in with a recovery code",
        ["/api/auth/setup/"] = "the first-admin ceremony; the setup token is the credential",
        ["/api/auth/invite/{token}"] = "redeeming an invite; the token is the credential",
        ["/api/auth/me"] = "answers 'no session' to the SPA rather than refusing it",
        ["/api/auth/logout"] = "ending a session that may already be gone",
        ["/api/auth/dev-login"] = "dev auth only, compiled out of Release (FU-089)",
        ["/api/restore-outcome/"] = "the setup page reads a restore's result before anyone can sign in; the attempt id is the credential",
        ["/oauth/authorize"] = "OAuth: redirects to the login page itself, which a 401 would not",
        ["/oauth/token"] = "OAuth: the client authenticates with its code or refresh token",
        ["/oauth/register"] = "OAuth dynamic client registration (RFC 7591): a client registers before it holds anything",
        ["/.well-known/oauth-protected-resource"] = "OAuth discovery (RFC 9728)",
        ["/api/admin/drive-sync/oauth/callback"] = "Google redirects the browser here; the single-use state minted by the admin-only connect/start is the guard",
        ["/openapi/"] = "Development only; not mapped in production (FU-095)",
        ["{*path:nonfile}"] = "the SPA shell, which every page loads before it knows who is signed in",
    };

    [Fact]
    public async Task Every_endpoint_declares_who_may_call_it()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled();
        using var _ = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        string Describe(RouteEndpoint e) =>
            $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}";

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null
                     && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(Describe).Distinct().Order().ToList();
        Assert.True(undeclared.Count == 0,
            "These endpoints declare neither RequireAuthorization() nor AllowAnonymous():\n  "
            + string.Join("\n  ", undeclared));

        // Anonymous must be on purpose: each anonymous route is one named above.
        var anonymous = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !Anonymous.Keys.Any(k => (e.RoutePattern.RawText ?? "").StartsWith(k, StringComparison.Ordinal)))
            .Select(Describe).Distinct().Order().ToList();
        Assert.True(anonymous.Count == 0,
            "These endpoints allow anonymous callers, and nobody named why:\n  " + string.Join("\n  ", anonymous));
    }

    [Fact]
    public async Task A_route_that_declares_nothing_requires_a_signed_in_user()
    {
        // The fallback policy is what an undeclared endpoint gets.
        await using var factory = new ApiFactory(_fixture);
        using var _ = factory.CreateClient();
        var fallback = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task A_signed_out_browser_still_reaches_the_spa_fallback()
    {
        // The login page is a client route: the fallback serves it to someone with no
        // session. Tests carry no built shell, so the fallback's answer is its 404; a 401
        // would mean the fallback policy caught it.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/no-such-route")).StatusCode);
    }

    [Fact]
    public async Task The_api_description_is_not_served_outside_development()
    {
        await using var factory = new ApiFactory(_fixture)
            .WithEnvironment("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
