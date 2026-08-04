namespace Coffer.Api.Mcp;

/// <summary>
/// MCP OAuth scopes (ADR-0081 D1). <see cref="Read"/> is the default granted to
/// every token; <see cref="Write"/> is opt-in and required by every write tool
/// (enforced in <see cref="McpWriteGuard"/>). A read-only token can never mutate.
/// </summary>
public static class McpScopes
{
    public const string Read = "coffer.read";
    public const string Write = "coffer.write";
}
