namespace Coffer.Api.Db.Entities;

/// <summary>
/// One open lot as the pure FIFO walk derives it AS OF an instant, optionally
/// without one transaction (<c>holdings_fifo_open_lots</c>, migration 256).
/// </summary>
/// <remarks>
/// The editor's lot preview used to read the <c>lots</c> table, which FIFO
/// drains IN PLACE — so it only ever knew today, and editing a years-old sale on a
/// since-closed account showed "no open lots". This is the position the sale
/// actually met. Keyed by the acquiring leg: a derived lot has no row id.
/// </remarks>
internal sealed class HoldingsFifoOpenLotRow
{
    public Guid LegId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
}
