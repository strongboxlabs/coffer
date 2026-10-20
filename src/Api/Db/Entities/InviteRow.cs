namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>invites</c> (ADR-0083 slice B). Mirrors
/// <see cref="BootstrapTokenRow"/> — the plaintext token never lives in the DB;
/// <see cref="TokenHash"/> is its SHA-256 — plus the invite's scope: who issued it,
/// the ledger + role it grants (both null = an instance-only invite), and an
/// optional instance-admin grant. Single-use via <see cref="ConsumedAt"/>, expiring
/// via <see cref="ExpiresAt"/>.
/// </summary>
public sealed class InviteRow
{
    public byte[] TokenHash { get; init; } = Array.Empty<byte>();
    /// <summary>Public, non-secret handle for list/revoke (the token itself is never persisted).</summary>
    public Guid Id { get; init; }
    public Guid IssuedByUserId { get; init; }
    public Guid? LedgerId { get; init; }
    public string? Role { get; init; }
    public bool GrantsAdmin { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? ConsumedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
