namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_as_of_now</c> (migration 259): a position held on the
/// caller's today (everything the register does not call Scheduled), its quantity and
/// FIFO cost basis, and the price a current value is computed at
/// (<see cref="SecurityPriceAsOfNowRow"/>: the latest close on or before that day, else
/// the security's last trade price; <see cref="Price"/> and <see cref="PriceDate"/>
/// both null when it has neither).
/// <para>
/// The current-value readers read this rather than the <c>holdings</c> projection,
/// which counts every trade and split entered, including ones the register calls
/// Scheduled.
/// </para>
/// </summary>
internal sealed class HoldingsAsOfNowRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal CostBasis { get; init; }
    public decimal? Price { get; init; }
    public DateOnly? PriceDate { get; init; }
    /// <summary>The currency of <see cref="Price"/> (migration 281); null with it.</summary>
    public string? PriceCurrency { get; init; }
}
