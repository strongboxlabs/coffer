namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>realized_gains</c> (migration 148, ADR-0064).
/// One row per sell leg, owned by <c>recompute_holdings_cost_basis</c> (FIFO).
/// Read-only from the API: the recompute function writes it. <see cref="AccountId"/>
/// is the holdings-sibling account (resolve to the brokerage via
/// <c>accounts.holdings_account_id</c>).
/// </summary>
public sealed class RealizedGainRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public Guid SellLegId { get; init; }
    public DateTime SoldAt { get; init; }
    public decimal Quantity { get; init; }
    public decimal Proceeds { get; init; }
    public decimal CostBasisSold { get; init; }
    public decimal RealizedGain { get; init; }

    // Mig 169 (ADR-0064 D2): the LONG-TERM portion of the sale (lots held > 1 year
    // at disposal). Short-term = the corresponding total minus this. The FIFO
    // recompute buckets each consumed lot by holding period and writes these.
    public decimal ProceedsLt { get; init; }
    public decimal CostBasisSoldLt { get; init; }
    public decimal RealizedGainLt { get; init; }

    public DateTime CreatedAt { get; init; }
}
