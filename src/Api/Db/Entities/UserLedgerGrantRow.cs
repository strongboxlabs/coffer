namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>user_ledger_grants</c>. Composite
/// primary key (user_id, ledger_id); see migration 014 for the
/// ≥1-owner constraint trigger that operates on this table.
/// </summary>
public sealed class UserLedgerGrantRow
{
    public Guid UserId { get; init; }
    public Guid LedgerId { get; init; }
    public string Role { get; init; } = string.Empty;
    public DateTime GrantedAt { get; init; }
}
