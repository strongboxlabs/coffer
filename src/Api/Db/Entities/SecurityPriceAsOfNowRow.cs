namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>security_prices_as_of_now</c> (migration 259): the price a current
/// value is computed at. That is the latest <c>security_prices</c> row dated on or
/// before the caller's today, else the unit price of the security's last trade in the
/// ledger (<c>fn_last_trade_price</c>, the one no-price rule, ADR-0084), back-adjusted
/// onto the day's split basis for any split after it (the rule the net-worth history's
/// as-of feeder applies). <see cref="PriceDate"/> is the date the price was observed,
/// a close's or a trade's. No row for a security with neither.
/// </summary>
internal sealed class SecurityPriceAsOfNowRow
{
    public Guid SecurityId { get; init; }
    public decimal Price { get; init; }
    public DateOnly PriceDate { get; init; }
    /// <summary>The currency of <see cref="Price"/> (migration 281): the close's, else the
    /// trade's.</summary>
    public string PriceCurrency { get; init; } = string.Empty;
}
