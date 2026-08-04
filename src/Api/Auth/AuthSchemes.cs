namespace Coffer.Api.Auth;

/// <summary>
/// Authentication scheme names referenced by both the registration
/// (in Program.cs) and the policy attached to authenticated endpoints.
/// Centralised so future schemes are added in one place.
/// </summary>
public static class AuthSchemes
{
    /// <summary>
    /// Cookie-session scheme that PR 3.3 introduces. Reserved here so the
    /// authorisation policy in PR 3.1 can already reference it; the actual
    /// handler is registered later.
    /// </summary>
    public const string Cookie = "Cookie";

    /// <summary>
    /// Development-only short-circuit. Registered iff
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> AND <c>Api:DevAuth=true</c>.
    /// Treats every request as the bootstrap system user; never registered
    /// in non-Development environments per ADR-0013.
    /// </summary>
    public const string DevAuth = "DevAuth";

    /// <summary>
    /// MCP bearer-token scheme (ADR-0063). Reads <c>Authorization: Bearer</c>
    /// and validates against <c>mcp_access_tokens</c>. Listed ONLY in the
    /// <see cref="AuthPolicies.RequireMcp"/> policy that gates <c>/mcp</c> —
    /// deliberately absent from the default policy so a read-only MCP token
    /// can't authenticate a REST mutation endpoint.
    /// </summary>
    public const string Mcp = "Mcp";
}
