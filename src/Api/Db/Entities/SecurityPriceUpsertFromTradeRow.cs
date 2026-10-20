namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>security_price_upsert_from_trade</c> function
/// (migration 177, ADR-0084). The function rank-gated-upserts a <c>trade</c>
/// source price for a (security, UTC-day) and returns the input security_id so
/// EF has a typed projection. Callers discard the value; the side effect on
/// <c>security_prices</c> is what matters.
/// </summary>
/// <remarks>
/// Bound via <c>HasDbFunction</c> in <see cref="AppDbContext"/> so
/// <see cref="Repositories.TradePriceRecomputeService"/> invokes the upsert via
/// LINQ. Parallels <see cref="RecomputeHoldingsForAccountSecurityRow"/> (mig
/// 104) — a function-call anchor invoked post-save from the
/// <c>TradePriceFromLegInterceptor</c>, not a trigger (ADR-0032).
/// </remarks>
internal sealed class SecurityPriceUpsertFromTradeRow
{
    public Guid SecurityId { get; init; }
}
