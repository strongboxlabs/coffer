namespace Coffer.Api.Db.Entities;

/// <summary>
/// One open lot as the pure FIFO walk derives it AS OF an instant, optionally
/// without one transaction (<c>holdings_fifo_open_lots</c>, migration 256).
/// </summary>
/// <remarks>
/// Not the <c>lots</c> table, which FIFO drains IN PLACE — that only ever knows
/// today, so a lot preview read from it shows "no open lots" for a years-old sale on
/// a since-closed account. This is the position the sale actually met. Keyed by the acquiring leg: a derived lot has no row id.
/// </remarks>
internal sealed class HoldingsFifoOpenLotRow
{
    public Guid LegId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
}
