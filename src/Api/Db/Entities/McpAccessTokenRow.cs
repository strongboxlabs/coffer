namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>mcp_access_tokens</c> (ADR-0063). The token
/// plaintext never lives in the DB — only its SHA-256 (<see cref="TokenHash"/>) —
/// so a DB read can't forge a working bearer token.
/// </summary>
public sealed class McpAccessTokenRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte[] TokenHash { get; init; } = Array.Empty<byte>();
    public string Scopes { get; init; } = "coffer.read";
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}
