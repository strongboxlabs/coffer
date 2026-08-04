namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_leg_recon</c> (ADR-0082, migration 171): the per-leg
/// reconciliation overlay. One row per reconciled real-account leg; an absent
/// row resolves to <c>'uncleared'</c>. Reconciliation is a per-account activity
/// (a transfer can be cleared in one account and uncleared in the other), so
/// status lives here — per leg — rather than on the header. ADR-0003: the raw
/// feed (<c>txn_legs</c>) stays immutable; the user's clearing action is overlay
/// state, mirroring <see cref="TxnLegOverrideRow"/>.
/// </summary>
internal sealed class TxnLegReconRow
{
    public Guid LegId { get; init; }
    /// <summary>
    /// Denormalized from <c>txn_legs.ledger_id</c>; composite FK
    /// <c>(leg_id, ledger_id) → txn_legs(id, ledger_id)</c> enforces coherence
    /// and RLS gates on this column directly (same shape as the overrides
    /// overlay).
    /// </summary>
    public Guid LedgerId { get; init; }
    // Mutable: the recon upsert flips status + the cleared audit pair in
    // lockstep to satisfy the DB CHECK (status='cleared' ⇔ cleared_at not null).
    public string Status { get; set; } = "uncleared";
    public DateTime? ClearedAt { get; set; }
    public Guid? ClearedByUserId { get; set; }
}
