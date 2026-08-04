namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>auth_sessions</c>. The cookie value
/// itself never lives in the DB — only its SHA-256, so a DB read can't
/// forge sessions (per ADR-0013).
/// </summary>
public sealed class SessionRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public byte[] SessionHash { get; init; } = Array.Empty<byte>();
    public string? UserAgent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}
