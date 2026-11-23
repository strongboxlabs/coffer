using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Single entry point for seeding <c>trade</c>-source rows into
/// <c>security_prices</c> from investment trade legs (ADR-0084). Its callers are the
/// writers of a trade's day: <see cref="TradePriceFromLegInterceptor"/>, at the terminal
/// commit boundary of every EF write, and the writers that bypass the change tracker (the
/// bulk delete and restore, <c>merge_securities</c>, and a brokerage's currency edit). The
/// rank-gated conflict logic lives in the Postgres function
/// <c>security_price_upsert_from_trade</c> (migrations 177, 274), which
/// <c>fn_trade_price_reseed</c> calls, so a truer <c>fetch</c>/<c>manual</c> close is
/// never clobbered by a trade.
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
    /// Callers name the DAY, never a price. <c>fn_trade_price_reseed</c> (migrations 234,
    /// 274) decides what that day's price and currency are, by the one rule the
    /// consistency check also reads.
    /// <para>
    /// Taking the price of whatever leg the save touched and writing it straight
    /// through would let correcting one leg seed its price even when a later leg on
    /// the same day is the one the rule names — so the writer and the checker would
    /// run different rules, and the check would report the difference as drift on
    /// a ledger that is fine. Passing keys instead of prices makes that
    /// class of disagreement unrepresentable.
    /// </para>
    /// <para>
    /// A day the rule no longer derives a price for keeps its stored row
    /// (ADR-0084 D4), so the same call serves the day a trade arrives on and the day
    /// it leaves: the latter is re-derived from the executions that remain, or left
    /// as it is when none does (ADR-0084 D4, 2026-11-03 amendment).
    /// </para>
    /// <para>
    /// Before the first day is re-derived, the accounts whose currency the rule can read
    /// for every day of the call are locked <c>FOR SHARE</c>, in id order, in one
    /// statement (the lock order: <see cref="AppDbContext.LockTradePriceAccounts"/>), and
    /// the call runs in the caller's transaction or, when there is none, in one of its own,
    /// so the locks are held until every day is written. Locked day by day instead, two
    /// days whose accounts sort the other way round would take their locks out of order,
    /// and a currency edit or a trade-price repair taking the same accounts in id order
    /// could wait on this call while it waited on them.
    /// </para>
    /// <para>
    /// A transaction of its own is opened by the ledger write lock
    /// (<see cref="LedgerWriteLock"/>, migration 275), its first statement, ahead of those
    /// row locks: one transaction per ledger, since a transaction holds one ledger's write
    /// lock at most. In the caller's transaction the lock is the caller's to take: a writer
    /// that can recompute balances or holdings took it first (a save whose interceptors
    /// recompute, the bulk writers, <c>merge_securities</c>), and one that re-derives
    /// trade prices alone need not.
    /// </para>
    /// <para>
    /// The days are then written in one order, <see cref="InWriteOrder"/>'s, whatever order
    /// the caller named them in: the third step of the lock order. Two re-derivations that
    /// hold their ledger's write lock never run at once, so between them the order decides
    /// nothing. It serves a re-derivation in a caller's transaction that took no ledger
    /// write lock: re-derivations lock their accounts <c>FOR SHARE</c> and so do not wait for
    /// each other there, and each upsert holds its price row until the transaction ends, so
    /// two that wrote the same two days in opposite orders (a date edit moving one trade
    /// from the first day to the second, and another moving a trade of the same security
    /// back) would each hold a row the other waits for.
    /// </para>
    /// <para>
    /// No deadlock forms between two writers of one ledger that take its write lock, and the
    /// timing windows the row-lock order alone leaves (a security merge racing a re-dated
    /// trade, two saves re-deriving the same day) do not open between them. A transaction
    /// that row-locks the same accounts without the ledger write lock can still deadlock
    /// with one of them; Postgres then cancels one request, which writes nothing, and a
    /// retry succeeds. That is an accepted limit.
    /// </para>
    /// </remarks>
    public async Task ReseedAsync(
        IEnumerable<(Guid LedgerId, Guid SecurityId, DateOnly Day)> days,
        CancellationToken cancellationToken = default)
    {
        // One call per distinct key; a multi-leg event on one (security, day)
        // collapses to a single re-derivation.
        var keys = InWriteOrder(days.Distinct(), k => k).ToList();
        if (keys.Count == 0) return;

        if (_db.Database.CurrentTransaction is not null)
        {
            await ReseedInTransactionAsync(keys, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No transaction open: one per ledger, each opened by that ledger's write lock.
        // The keys are in write order, ledger first, so each ledger's days stay in it.
        foreach (var ledger in keys.GroupBy(k => k.LedgerId))
        {
            await using var write = await _db.BeginLedgerWriteAsync(ledger.Key, cancellationToken)
                .ConfigureAwait(false);
            await ReseedInTransactionAsync([.. ledger], cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lock the accounts whose currency <paramref name="keys"/> read, then re-derive each
    /// day, in the transaction already open.
    /// </summary>
    private async Task ReseedInTransactionAsync(
        IReadOnlyList<(Guid LedgerId, Guid SecurityId, DateOnly Day)> keys,
        CancellationToken cancellationToken)
    {
        var accounts = new HashSet<Guid>();
        foreach (var ledger in keys.GroupBy(k => k.LedgerId))
        {
            accounts.UnionWith(await TradePriceLocks.AccountsAsync(
                    _db, ledger.Key, ledger.Select(k => (k.SecurityId, k.Day)), cancellationToken)
                .ConfigureAwait(false));
        }
        await TradePriceLocks.LockAsync(_db, accounts, forWrite: false, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (ledgerId, securityId, day) in keys)
        {
            _ = await _db.TradePriceReseed(ledgerId, securityId, day)
                .Select(r => r.Price)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The order every path writes trade-source <c>security_prices</c> rows in: by ledger,
    /// then security, then day, by one comparer. The re-derivation above and the
    /// trade-price repair both write through it, so no two writers hold two price rows in
    /// opposite orders.
    /// </summary>
    internal static IEnumerable<T> InWriteOrder<T>(
        IEnumerable<T> rows, Func<T, (Guid LedgerId, Guid SecurityId, DateOnly Day)> key) =>
        rows.OrderBy(r => key(r).LedgerId)
            .ThenBy(r => key(r).SecurityId)
            .ThenBy(r => key(r).Day);
}
