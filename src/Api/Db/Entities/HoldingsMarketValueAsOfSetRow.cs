namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_market_value_as_of_set</c> (migration 200): the same
/// shape as <see cref="HoldingsMarketValueAsOfRow"/> plus the instant each row
/// belongs to, so many instants come back from one call.
/// <para>
/// The batched form exists because a time-weighted return values the portfolio
/// once per external-flow instant, and the single-instant function replays every
/// position's whole history for each one — ~139 ms per instant on a real ledger,
/// ~60 s for a five-year whole-portfolio call. That cost was the sole reason a
/// boundary cap existed.
/// </para>
/// </summary>
internal sealed class HoldingsMarketValueAsOfSetRow
{
    public DateTime AsOf { get; init; }
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal MarketValue { get; init; }
    public string PricedFrom { get; init; } = string.Empty;
}
