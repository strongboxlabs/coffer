using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The locks every path that writes a trade price into a committed ledger, or a currency
/// the trade-price rule reads, takes before it reads (migration 274; the order is stated
/// on <see cref="AppDbContext.LockTradePriceAccounts"/>): the accounts whose currency the
/// rule reads for the days the path derives, in id order, in one statement, before any
/// holdings account or price row.
/// </summary>
internal static class TradePriceLocks
{
    /// <summary>
    /// Lock the accounts whose currency the trade-price rule can read for the days
    /// <paramref name="read"/>'s result names, with the accounts <paramref name="accounts"/>
    /// names, then read again, until a read names no account not yet locked; returns that
    /// read.
    /// </summary>
    /// <remarks>
    /// The first read only says what to lock. Every later read begins after its locks were
    /// granted, so under READ COMMITTED it sees what they waited for: a currency edit, a
    /// trade-price repair or a trade's re-derivation in flight. An account that joins the
    /// set between two reads (an execution another request commits on another brokerage)
    /// is locked and the read repeated. Runs in the caller's READ COMMITTED transaction.
    /// </remarks>
    /// <param name="forWrite"><c>FOR NO KEY UPDATE</c> for a currency edit and the
    /// trade-price repair; <c>FOR SHARE</c> for the holdings-currency repair.</param>
    public static async Task<T> LockThenReadAsync<T>(
        AppDbContext db,
        Guid ledgerId,
        Func<CancellationToken, Task<T>> read,
        Func<T, IEnumerable<(Guid SecurityId, DateOnly Day)>> days,
        Func<T, IEnumerable<Guid>> accounts,
        bool forWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(accounts);

        var locked = new HashSet<Guid>();
        while (true)
        {
            var result = await read(cancellationToken).ConfigureAwait(false);
            var needed = await AccountsAsync(db, ledgerId, days(result), cancellationToken)
                .ConfigureAwait(false);
            needed.UnionWith(accounts(result));
            needed.ExceptWith(locked);
            if (needed.Count == 0) return result;
            await LockAsync(db, needed, forWrite, cancellationToken).ConfigureAwait(false);
            locked.UnionWith(needed);
        }
    }

    /// <summary>
    /// The accounts whose currency the trade-price rule can read for the given
    /// (security, UTC day) pairs, whichever execution of each day it picks
    /// (<see cref="AppDbContext.TradePriceAccounts"/>).
    /// </summary>
    public static async Task<HashSet<Guid>> AccountsAsync(
        AppDbContext db, Guid ledgerId, IEnumerable<(Guid SecurityId, DateOnly Day)> days,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keys = days.Distinct().ToList();
        if (keys.Count == 0) return [];
        var securityIds = keys.Select(k => k.SecurityId).ToArray();
        var dates = keys.Select(k => k.Day).ToArray();
        var ids = await db.TradePriceAccounts(ledgerId, securityIds, dates)
            .Select(r => r.AccountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. ids];
    }

    /// <summary>
    /// Lock the accounts in id order, in one statement, until the caller's transaction
    /// ends (<see cref="AppDbContext.LockTradePriceAccounts"/>). Nothing for none.
    /// </summary>
    public static async Task LockAsync(
        AppDbContext db, IEnumerable<Guid> accountIds, bool forWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = accountIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        _ = await db.LockTradePriceAccounts(ids, forWrite)
            .Select(r => r.Locked)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
