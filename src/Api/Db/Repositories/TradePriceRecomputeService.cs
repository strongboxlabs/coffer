using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Single entry point for seeding <c>trade</c>-source rows into
/// <c>security_prices</c> from investment trade legs (ADR-0084). Invoked at the
/// terminal commit boundary by <see cref="TradePriceFromLegInterceptor"/>; the
/// rank-gated conflict logic lives in the Postgres function
/// <c>security_price_upsert_from_trade</c> (migration 177) so a truer
/// <c>fetch</c>/<c>manual</c> close is never clobbered by a trade.
/// </summary>
/// <remarks>
/// Parallels <see cref="HoldingsRecomputeService"/> (mig 104). Same rationale:
/// an explicit upsert at the writer is visible, debuggable, and testable in
/// isolation; a function call (not a trigger, ADR-0032) can't re-fire the
/// interceptors. The service dedupes per (ledger, security, day) so a
/// multi-leg event on the same (security, day) collapses to one call.
/// </remarks>
public sealed class TradePriceRecomputeService
{
    private readonly AppDbContext _db;

    public TradePriceRecomputeService(AppDbContext db) => _db = db;

    /// <summary>
    /// Upsert a <c>trade</c>-source price for every distinct (ledger, security,
    /// day) in <paramref name="trades"/>. Dedupes to one call per key, keeping
    /// the LAST price in enumeration order for a repeated key. Empty input is a
    /// no-op.
    /// </summary>
    public async Task UpsertAsync(
        IEnumerable<(Guid LedgerId, Guid SecurityId, DateOnly Day, decimal Price)> trades,
        CancellationToken cancellationToken = default)
    {
        // Dedupe per (ledger, security, day); last write wins. A multi-leg event
        // on one (security, day) — or two trades of the same security the same
        // day in one SaveChanges — collapses to a single upsert.
        var deduped = new Dictionary<(Guid, Guid, DateOnly), decimal>();
        foreach (var (ledgerId, securityId, day, price) in trades)
            deduped[(ledgerId, securityId, day)] = price;

        foreach (var ((ledgerId, securityId, day), price) in deduped)
        {
            // EF's HasDbFunction binding requires us to materialise the result;
            // the row is discarded — the side effect on security_prices is the
            // point.
            _ = await _db.SecurityPriceUpsertFromTrade(ledgerId, securityId, day, price)
                .Select(r => r.SecurityId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
