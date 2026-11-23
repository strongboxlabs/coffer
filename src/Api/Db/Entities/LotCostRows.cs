namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_open_lots_as_of</c> (migration 282): a FIFO lot open at an
/// instant, with its acquisition instant and cost, so a cost basis can convert lot by lot
/// at each acquisition date (ADR-0114 D7).
/// </summary>
internal sealed class OpenLotAsOfRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>The lot's shares times its unit cost, in its account's currency.</summary>
    public decimal Cost { get; init; }
}

/// <summary>
/// One row of <c>realized_gain_lots</c> (migration 282): one lot a sale consumed, with the
/// lot's acquisition instant and the cost taken from it. A sale's rows sum to its cost basis
/// sold.
/// </summary>
internal sealed class RealizedGainLotRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public Guid SellLegId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>The cost taken from the lot, in its account's currency.</summary>
    public decimal Cost { get; init; }
}
