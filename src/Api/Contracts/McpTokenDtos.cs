namespace Coffer.Api.Contracts;

/// <summary>
/// MCP access-token management shapes (ADR-0063). The plaintext token is only
/// ever in <see cref="IssuedMcpToken.Token"/>, returned once at creation; every
/// other shape carries metadata only.
/// </summary>
public sealed record McpTokenSummary(
    Guid Id,
    string Name,
    string Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt);

/// <summary>Request to mint a token. Name is the user's label for the client.
/// <paramref name="Writable"/> opts the token into the <c>coffer.write</c> scope
/// (ADR-0081 D1) — additive to read; default is read-only. A write-scoped token
/// still cannot mutate unless the deployment-wide MCP-writes kill-switch is on
/// (ADR-0081 D2), so this is an explicit two-key model.</summary>
public sealed record CreateMcpTokenRequest(string Name, bool Writable = false);

/// <summary>
/// Response to a mint: the row metadata plus the one-time plaintext
/// <see cref="Token"/>. The client must copy it now — it is never retrievable
/// again.
/// </summary>
public sealed record IssuedMcpToken(
    Guid Id,
    string Name,
    string Scopes,
    DateTime? ExpiresAt,
    string Token);
