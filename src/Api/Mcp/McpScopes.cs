using System.Security.Claims;

using OpenIddict.Abstractions;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP OAuth scopes (ADR-0081 D1). <see cref="Read"/> is granted to every token and
/// required by <c>/mcp</c> itself; <see cref="Write"/> is opt-in and required by every
/// write tool (enforced in <see cref="McpWriteGuard"/>). A read-only token can never mutate.
/// </summary>
public static class McpScopes
{
    public const string Read = "coffer.read";
    public const string Write = "coffer.write";

    /// <summary>
    /// Whether <paramref name="user"/> was granted <paramref name="scope"/>, by either auth
    /// path: OpenIddict surfaces granted scopes through <c>HasScope</c> (its oi_scp
    /// claims), and the manual bearer scheme stamps one space-delimited <c>scope</c> claim
    /// (McpTokenAuthHandler).
    /// </summary>
    public static bool Has(ClaimsPrincipal user, string scope) =>
        user.HasScope(scope)
        || user.FindAll("scope").Any(c =>
            c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope));
}
