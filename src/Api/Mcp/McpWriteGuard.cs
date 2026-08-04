using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using OpenIddict.Abstractions;

namespace Coffer.Api.Mcp;

/// <summary>
/// The single authorization + kill-switch choke point for MCP write tools
/// (ADR-0081 D1/D2). Every write tool calls <see cref="EnsureWritable"/> before it
/// touches data: it throws unless BOTH
/// <list type="number">
///   <item>writes are enabled right now (<see cref="McpRuntimeState.WritesEnabled"/>
///   — the hot flag; an admin toggle takes effect immediately, no restart); AND</item>
///   <item>the caller's token carries the <see cref="McpScopes.Write"/> scope.</item>
/// </list>
/// A <c>coffer.read</c> token can never mutate — enforcement is a real check, not the
/// absence of the tool (ADR-0081 D1). Scoped so it reads the current request's
/// principal.
/// </summary>
public sealed class McpWriteGuard
{
    private readonly McpRuntimeState _runtime;
    private readonly IHttpContextAccessor _http;

    public McpWriteGuard(McpRuntimeState runtime, IHttpContextAccessor http)
    {
        _runtime = runtime;
        _http = http;
    }

    /// <summary>Throws if writes are globally disabled or the caller lacks the write
    /// scope. Call at the very top of every write tool.</summary>
    public void EnsureWritable()
    {
        if (!_runtime.WritesEnabled)
            throw new InvalidOperationException(
                "MCP writes are disabled for this deployment. An administrator must enable them in Settings.");

        var user = _http.HttpContext?.User;
        if (user is null || !HasWriteScope(user))
            throw new InvalidOperationException(
                "This MCP token is read-only. Writing requires a token granted the 'coffer.write' scope.");
    }

    // Covers BOTH auth paths (the load-bearing correctness point, ADR-0081 D1):
    //   * OAuth (OpenIddict) surfaces granted scopes via HasScope (the oi_scp claims);
    //   * the manual bearer scheme stamps a space-delimited "scope" claim
    //     (McpTokenAuthHandler).
    private static bool HasWriteScope(ClaimsPrincipal user) =>
        user.HasScope(McpScopes.Write)
        || user.FindAll("scope").Any(c =>
            c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(McpScopes.Write));
}
