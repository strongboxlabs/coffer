namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>security_price_upsert_from_trade</c> function
/// (migrations 177, 274, ADR-0084). The function rank-gated-upserts a <c>trade</c>
/// source price for a (security, UTC-day), in the currency given, and returns the
/// input security_id so EF has a typed projection. Callers discard the value; the
/// side effect on <c>security_prices</c> is what matters.
/// </summary>
/// <remarks>
/// Bound via <c>HasDbFunction</c> in <see cref="AppDbContext"/>. Every writer but the
/// trade-price repair reaches the function in SQL, through <c>fn_trade_price_reseed</c>
/// (<see cref="AppDbContext.TradePriceReseed"/>), which
/// <see cref="Repositories.TradePriceRecomputeService"/> calls for the
/// <c>TradePriceFromLegInterceptor</c>, the bulk delete and restore,
/// <c>merge_securities</c> and a brokerage's currency edit. Through this binding only the
/// trade-price repair calls it, with the price and the brokerage's currency the check
/// derived. A function call, not a trigger (ADR-0032).
/// </remarks>
internal sealed class SecurityPriceUpsertFromTradeRow
{
    public Guid SecurityId { get; init; }
}
