namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_market_value_as_of_set</c> (migration 200): the same
/// shape as <see cref="HoldingsMarketValueAsOfRow"/> plus the instant each row
/// belongs to, so many instants come back from one call.
/// <para>
/// The batched form exists because a time-weighted return values the portfolio
/// once per external-flow instant, and the single-instant function replays every
/// position's whole history for each one — a per-instant cost that, multiplied over
/// a multi-year whole-portfolio call, made the call too slow to serve. That cost was the sole reason a boundary cap existed.
/// </para>
/// </summary>
internal sealed class HoldingsMarketValueAsOfSetRow
{
    public DateTime AsOf { get; init; }
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal MarketValue { get; init; }
    /// <summary><c>feed</c> (a close), <c>trade</c> (the security's last trade in the
    /// ledger, <c>fn_last_trade_price</c>) or <c>none</c>: neither, and since migration
    /// 259 <see cref="MarketValue"/> is then the position's cost basis, not 0.</summary>
    public string PricedFrom { get; init; } = string.Empty;
    /// <summary>The currency of the price <see cref="MarketValue"/> is at (migration 281):
    /// the close's, else the trade's; null for a position carried at cost basis, which is
    /// in its account's currency.</summary>
    public string? PriceCurrency { get; init; }
}
