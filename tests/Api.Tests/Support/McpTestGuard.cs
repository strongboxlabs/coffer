using System.Collections.Immutable;
using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using OpenIddict.Abstractions;

using Coffer.Api.Mcp;

namespace Coffer.Api.Tests.Support;

/// <summary>
/// Builds <see cref="McpWriteGuard"/> instances and the two token-shaped principals
/// the guard must accept (ADR-0081 D1). The guard reads write authority off the
/// caller's token via TWO paths, and both are exercised here:
/// <list type="bullet">
///   <item><b>OAuth</b> — scopes set with OpenIddict's own <c>SetScopes</c>, exactly
///   as <c>OAuthEndpoints.AuthorizeAsync</c> does, so <c>HasScope</c> reads them
///   back by construction (no assumption about the internal claim shape).</item>
///   <item><b>manual bearer</b> — a single space-delimited <c>scope</c> claim, as
///   <c>McpTokenAuthHandler</c> stamps.</item>
/// </list>
/// Test-only.
/// </summary>
public static class McpTestGuard
{
    /// <summary>A guard that permits writes: kill-switch on + a write-scoped (OAuth) principal.</summary>
    public static McpWriteGuard Writable() =>
        Build(writesEnabled: true, OAuthPrincipal(McpScopes.Read, McpScopes.Write));

    /// <summary>A guard wired to a given kill-switch state and caller principal
    /// (<paramref name="user"/> null models an unauthenticated request).</summary>
    public static McpWriteGuard Build(bool writesEnabled, ClaimsPrincipal? user)
    {
        var runtime = new McpRuntimeState(active: true, writesEnabled: writesEnabled);
        // A plain-field accessor, NOT the framework HttpContextAccessor: the latter
        // stores the context in a static AsyncLocal that only flows within the async
        // context that set it, so a guard held in a static test field would read the
        // context back as null when a test method (a different async flow) calls it.
        // This stub holds the context unconditionally, flow-independent.
        var accessor = new FixedHttpContextAccessor
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user },
        };
        return new McpWriteGuard(runtime, accessor);
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>OAuth-shaped principal (OpenIddict scope claims).</summary>
    public static ClaimsPrincipal OAuthPrincipal(params string[] scopes)
    {
        var identity = new ClaimsIdentity("test-oauth");
        identity.SetScopes(scopes.ToImmutableArray());
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Manual-bearer-shaped principal (one space-delimited "scope" claim).</summary>
    public static ClaimsPrincipal BearerPrincipal(params string[] scopes)
    {
        var identity = new ClaimsIdentity("test-bearer");
        identity.AddClaim(new Claim("scope", string.Join(' ', scopes)));
        return new ClaimsPrincipal(identity);
    }
}
