namespace Coffer.Api.Auth;

/// <summary>
/// Authorization policy names and the custom claims they read, referenced
/// by both the registration (in Program.cs) and the endpoints they gate.
/// Centralised so a policy name typo can't silently leave an endpoint
/// ungated (a misspelled <c>RequireAuthorization("...")</c> throws at
/// startup only when the name is unknown — using the constant removes
/// the chance of drift).
/// </summary>
public static class AuthPolicies
{
    /// <summary>
    /// Admin-only policy (ADR-0060): authenticated AND carrying the
    /// <see cref="IsAdminClaim"/> with value <c>"true"</c>. Gates the
    /// deployment-wide backup surface. UI gating is never the boundary —
    /// every admin endpoint asserts this server-side.
    /// </summary>
    public const string RequireAdmin = "RequireAdmin";

    /// <summary>
    /// MCP-only policy (ADR-0063): authenticated via the
    /// <see cref="AuthSchemes.Mcp"/> bearer scheme and only that scheme. Gates
    /// the <c>/mcp</c> endpoint. Keeping the bearer scheme out of the default
    /// policy and naming it explicitly here is the least-privilege boundary —
    /// a read-only MCP token authenticates report tools, nothing else.
    /// </summary>
    public const string RequireMcp = "RequireMcp";

    /// <summary>
    /// Claim stamped by the auth handlers from <c>users.is_admin</c>. Only
    /// present (value <c>"true"</c>) for admins; absent otherwise, so a
    /// <c>RequireClaim</c> match is a clean allow/deny.
    /// </summary>
    public const string IsAdminClaim = "is_admin";

    /// <summary>Canonical value of <see cref="IsAdminClaim"/> for an admin.</summary>
    public const string IsAdminTrue = "true";
}
