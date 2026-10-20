namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>fn_trade_price_reseed(ledger, security, day)</c>
/// (migration 234) — the price the rule chose and wrote, or NULL when no
/// eligible leg prices that day.
/// </summary>
/// <remarks>
/// A table-returning function rather than a scalar so EF can materialise it:
/// <c>src/Api</c> is LINQ-only, so a side effect has to be reachable through a
/// projection. The same shape as <see cref="SecurityPriceUpsertFromTradeRow"/>,
/// which exists for the same reason.
/// </remarks>
internal sealed class TradePriceReseedRow
{
    public decimal? Price { get; init; }
}
