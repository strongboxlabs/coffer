namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_leg_overrides</c> (ADR-0022 Rule 5). User
/// edits to per-leg fields (amount, leg memo) land here.
/// </summary>
internal sealed class TxnLegOverrideRow
{
    public Guid LegId { get; init; }
    /// <summary>
    /// Denormalized from <c>txn_legs.ledger_id</c> (migration 072).
    /// Composite FK <c>(leg_id, ledger_id) → txn_legs(id, ledger_id)</c>
    /// enforces coherence; RLS gates on this column directly.
    /// </summary>
    public Guid LedgerId { get; init; }
    public string? LegMemo { get; init; }
    public decimal? Amount { get; init; }
    public DateTime UpdatedAt { get; init; }
}
