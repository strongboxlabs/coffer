namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>holdings</c>. One row per (account, security) where
/// <c>account_id</c> is the Holdings sibling account of a brokerage (per
/// ADR-0019). <see cref="Quantity"/> and <see cref="CostBasis"/> are the
/// authoritative current-position values — the importer's holdings writer
/// keeps them in sync; <c>lots</c> rows are the audit trail of opens
/// (ADR-0018 Rule 4 defers lot-closing).
/// </summary>
internal sealed class HoldingRow
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    /// <summary>Denormalized from the account's (or security's)
    /// ledger (migration 049). DB composite FKs require both
    /// references to share this value.</summary>
    public Guid LedgerId { get; init; }
    public decimal Quantity { get; init; }
    public decimal CostBasis { get; init; }
    public DateTime AsOf { get; init; }
}
