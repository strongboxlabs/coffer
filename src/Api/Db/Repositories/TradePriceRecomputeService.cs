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
    /// Re-derive and write the <c>trade</c>-source price for every distinct
    /// (ledger, security, day) touched. Empty input is a no-op.
    /// </summary>
    /// <remarks>
    /// Callers name the DAY, never a price. Migration 234's
    /// <c>fn_trade_price_reseed</c> decides what that day's price is, by the one
    /// rule the consistency check also reads.
    /// <para>
    /// This used to take the price of whatever leg the save touched and write it
    /// straight through. That meant correcting one leg seeded its price even when
    /// a later leg on the same day was the one the rule names — so the writer and
    /// the checker ran different rules, and the check reported the difference as
    /// drift on a ledger that was fine. Passing keys instead of prices makes that
    /// class of disagreement unrepresentable.
    /// </para>
    /// </remarks>
    public async Task ReseedAsync(
        IEnumerable<(Guid LedgerId, Guid SecurityId, DateOnly Day)> days,
        CancellationToken cancellationToken = default)
    {
        // One call per distinct key; a multi-leg event on one (security, day)
        // collapses to a single re-derivation.
        foreach (var (ledgerId, securityId, day) in days.Distinct())
        {
            _ = await _db.TradePriceReseed(ledgerId, securityId, day)
                .Select(r => r.Price)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
