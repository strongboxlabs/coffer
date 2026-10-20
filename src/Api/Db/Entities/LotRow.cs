namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>lots</c>. One row per share-acquiring event
/// (Buy / BuyXfr / DividendReinvest); the audit trail behind a
/// <see cref="HoldingRow"/>. FIFO consumption on Sell / SellX is
/// run by <c>fn_recompute_holdings_cost_basis</c> (migration 056),
/// not by application code — this entity is the read shape for
/// the editor's FIFO preview popover (per ADR-0029).
/// </summary>
/// <remarks>
/// <see cref="LegId"/> is the holdings-side <c>txn_legs.id</c> the
/// lot was created from. <see cref="UnitCost"/> is a placeholder
/// the recompute function may override based on the brokerage's
/// <c>is_trade_commission</c> flag (migration 056).
/// <see cref="IsClosed"/> flips to TRUE when FIFO consumption
/// exhausts the lot's quantity; closed lots stay in the table for
/// audit (split-aware basis lookups join through them).
/// </remarks>
internal sealed class LotRow
{
    public Guid Id { get; init; }
    public Guid HoldingId { get; init; }
    public Guid LegId { get; init; }
    /// <summary>Denormalized ledger id (migration 049) for the
    /// composite-FK ledger-isolation pattern.</summary>
    public Guid LedgerId { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
    public DateTime AcquiredAt { get; init; }
    public bool IsClosed { get; init; }
}
