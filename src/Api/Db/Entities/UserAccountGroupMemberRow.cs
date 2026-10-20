namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of one row in <c>user_account_group_members</c>
/// (migration 033) — the N:M membership join between a user-curated
/// account group and an account. Composite primary key
/// (group_id, account_id); both sides cascade on delete.
/// </summary>
public sealed class UserAccountGroupMemberRow
{
    public Guid GroupId { get; init; }
    public Guid AccountId { get; init; }
    /// <summary>
    /// Denormalized from <c>user_account_groups.ledger_id</c> +
    /// <c>accounts.ledger_id</c> (migration 072). Composite FKs lock
    /// both parents to the same ledger; RLS gates on this column.
    /// </summary>
    public Guid LedgerId { get; init; }
    public DateTime AddedAt { get; init; }
}
