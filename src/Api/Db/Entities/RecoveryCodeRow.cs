namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>recovery_codes</c>. The plaintext
/// codes are shown to the user once at registration; only the Argon2id
/// PHC string lives here.
/// </summary>
public sealed class RecoveryCodeRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string CodeHash { get; init; } = string.Empty;
    public DateTime? UsedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
