using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The currency a brokerage's holdings account is kept in: its brokerage's (ADR-0019
/// Rule 4, 2026-11-04 amendment). One statement of the rule for its writer, the account
/// edit, and for the consistency check and its repair.
/// </summary>
/// <remarks>
/// <para>The holdings account is a system account the person never edits; its security
/// lines are the brokerage's positions, whose cost basis is named in the holding account's
/// currency. So a holdings account in another currency than its brokerage names the
/// brokerage's shares in a currency the person has replaced.</para>
/// <para>No trade price reads it: a trade price is in the currency of the brokerage that
/// made the trade (migration 274's <c>fn_trade_currency</c>), so aligning the holdings
/// account writes no price, and a holdings account that disagrees leaves every trade
/// price in its brokerage's currency.</para>
/// <para>Nothing converts (FU-106): aligning relabels. When more than one brokerage names
/// one holdings account and they disagree, the expected code is the first in ordinal
/// order, as <see cref="DisplayCurrency.Of"/> names a mixed set and as
/// <c>fn_trade_currency</c> picks the trade price's, so the check, its repair and the
/// trade prices agree.</para>
/// </remarks>
internal static class HoldingsAccountCurrency
{
    /// <summary>
    /// Per holdings account of the ledger that a brokerage names: the currency it is kept
    /// in, the one its brokerages imply, and the brokerages, for display.
    /// </summary>
    /// <param name="holdingsAccountId">One holdings account to answer for; null for every
    /// one in the ledger.</param>
    public static async Task<List<Row>> ReadAsync(
        AppDbContext db, Guid ledgerId, Guid? holdingsAccountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var pairs = await (
                from b in db.Accounts.AsNoTracking()
                join s in db.Accounts.AsNoTracking() on b.HoldingsAccountId equals (Guid?)s.Id
                where b.LedgerId == ledgerId && s.LedgerId == ledgerId
                      && (holdingsAccountId == null || s.Id == holdingsAccountId)
                select new
                {
                    HoldingsAccountId = s.Id,
                    Stored = s.CurrencyCode,
                    BrokerageId = b.Id,
                    BrokerageName = b.Name,
                    Brokerage = b.CurrencyCode,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return pairs
            .GroupBy(p => p.HoldingsAccountId)
            .Select(g =>
            {
                var brokerages = g.OrderBy(p => p.BrokerageName, StringComparer.Ordinal)
                    .ThenBy(p => p.BrokerageId)
                    .ToList();
                return new Row(
                    g.Key,
                    g.First().Stored,
                    DisplayCurrency.Of(g.Select(p => p.Brokerage)).Code,
                    brokerages[0].BrokerageId,
                    string.Join(", ", brokerages.Select(p => p.BrokerageName)),
                    [.. brokerages.Select(p => p.BrokerageId)]);
            })
            .OrderBy(r => r.BrokerageNames, StringComparer.Ordinal)
            .ThenBy(r => r.HoldingsAccountId)
            .ToList();
    }

    /// <summary>
    /// Set the holdings account's currency to the one its brokerages imply. Nothing when it
    /// already agrees. No price is written: a trade price is in its brokerage's currency,
    /// never the holdings account's (migration 274). Runs in the caller's transaction,
    /// which holds the brokerages' rows locked, the trade-price lock order's first step
    /// (<see cref="AppDbContext.LockTradePriceAccounts"/>): the account edit by its update
    /// of its brokerage's row (and, when it changes the currency, by the lock before it),
    /// the holdings-currency repair <c>FOR SHARE</c>, so a currency edit in flight is
    /// waited for and the code written is the one it commits.
    /// </summary>
    /// <returns>Whether the currency changed.</returns>
    public static async Task<bool> AlignAsync(
        AppDbContext db, Guid ledgerId, Guid holdingsAccountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = (await ReadAsync(db, ledgerId, holdingsAccountId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();
        if (row is null || row.Stored == row.Expected) return false;

        await db.Accounts
            .Where(a => a.Id == holdingsAccountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, row.Expected), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>One holdings account's currency against its brokerages'.</summary>
    /// <param name="BrokerageId">The brokerage that names the holdings account, carried as
    /// the mismatch's AccountId, as any per-account finding carries its account. The first
    /// by name when more than one names it.</param>
    /// <param name="BrokerageNames">The brokerages naming it, by name.</param>
    /// <param name="BrokerageIds">Every brokerage naming it, whose currencies
    /// <paramref name="Expected"/> is read from: what the repair locks.</param>
    internal sealed record Row(
        Guid HoldingsAccountId,
        string Stored,
        string Expected,
        Guid BrokerageId,
        string BrokerageNames,
        IReadOnlyList<Guid> BrokerageIds);
}
