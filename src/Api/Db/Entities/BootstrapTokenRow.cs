namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>bootstrap_tokens</c>. The plaintext
/// token never lives in the DB; <see cref="TokenHash"/> is its SHA-256.
/// </summary>
public sealed class BootstrapTokenRow
{
    public byte[] TokenHash { get; init; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? ConsumedAt { get; init; }
}
