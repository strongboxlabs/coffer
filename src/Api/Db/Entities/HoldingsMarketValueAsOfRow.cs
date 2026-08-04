namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>holdings_market_value_as_of</c> TVF
/// (migration 172). One row per (holdings-sibling account, security) that held
/// a non-zero split-adjusted quantity at the as-of instant, valued at the price
/// as of that instant. <see cref="PricedFrom"/> records which tier priced it:
/// <c>feed</c> (a <c>security_prices</c> close ≤ the instant), <c>trade</c>
/// (the latest execution price ≤ the instant), or <c>none</c> (no priced
/// observation — valued at 0). Bound via <c>HasDbFunction</c> in
/// <see cref="AppDbContext"/>; the as-of valuation feeder for net-worth-over-
/// time and true time-weighted return (ADR-0063 v2 / Track-2 historical
/// valuations).
/// </summary>
internal sealed class HoldingsMarketValueAsOfRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal MarketValue { get; init; }
    public string PricedFrom { get; init; } = string.Empty;
}
