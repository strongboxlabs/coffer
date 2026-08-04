namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_operation_promotions</c> (ADR-0055; formerly
/// <c>sync_run_promotions</c>, migration 038). Ingest-specific: one row per
/// slice-2c promote-on-clear event — the bank-side amount delta between a
/// pending hold and the cleared transaction (restaurant tip, exchange-rate
/// shift, etc.). Quote runs don't write these. Cascades on header delete.
/// </summary>
internal sealed class LedgerOperationPromotionRow
{
    public Guid Id { get; init; }
    public Guid LedgerOperationId { get; init; }
    /// <summary>
    /// Denormalized from <c>ledger_operations.ledger_id</c> (migration 072).
    /// Composite FK locks the parent's ledger; RLS gates on this column.
    /// </summary>
    public Guid LedgerId { get; init; }
    public Guid HeaderId { get; init; }
    public decimal WasAmount { get; init; }
    public decimal BecameAmount { get; init; }
    public DateTime PromotedAt { get; init; }
}
