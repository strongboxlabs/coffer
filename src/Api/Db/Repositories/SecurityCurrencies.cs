using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The currencies a security's current figures are in, for every reader that prints
/// one: the securities catalog, the security's detail and the unbacked-price advisory.
/// Nothing converts, so each figure is labelled in its own currency, and one derivation
/// keeps two screens from labelling the same security differently.
/// </summary>
/// <remarks>
/// <para>A current price is <c>security_prices_as_of_now</c>'s (migration 259): the latest
/// close dated on or before the caller's today, else the last trade's price. The market
/// value it makes (price times quantity) is in the price's currency. A position with no
/// price carries at its cost basis, which is in the holding accounts' currency.</para>
/// <para>Both as-of-now functions are costly on a long history (<c>holdings_as_of_now</c>
/// walks FIFO for every account with a leg at or after the boundary), so neither is
/// asked again here: the caller reads each once and derives its figures and their
/// currencies from the same rows.</para>
/// </remarks>
internal static class SecurityCurrencies
{
    /// <summary>
    /// What is held on the caller's today (<c>holdings_as_of_now</c>), one read for the
    /// figures and the currencies alike: per security and holdings account, with the
    /// account's currency, the quantity and cost basis.
    /// </summary>
    /// <param name="securityIds">The securities to answer for; null for every security
    /// in the ledger.</param>
    public static async Task<List<HeldNow>> HeldNowAsync(
        AppDbContext db,
        Guid ledgerId,
        DateTime scheduledFrom,
        Guid[]? securityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var held = from h in db.HoldingsAsOfNow(ledgerId, scheduledFrom, null).AsNoTracking()
                   join a in db.Accounts.AsNoTracking() on h.AccountId equals a.Id
                   select new { h.SecurityId, h.AccountId, a.CurrencyCode, h.Quantity, h.CostBasis };
        if (securityIds is not null)
            held = held.Where(x => securityIds.Contains(x.SecurityId));
        return await held
            .GroupBy(x => new { x.SecurityId, x.AccountId, x.CurrencyCode })
            .Select(g => new HeldNow(
                g.Key.SecurityId, g.Key.AccountId, g.Key.CurrencyCode, g.Sum(x => x.Quantity), g.Sum(x => x.CostBasis)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Per security in <paramref name="held"/>, the display currency of the accounts that
    /// hold it (<see cref="DisplayCurrency.Of"/>), and whether they span more than one. A
    /// security nobody holds that day has no entry.
    /// </summary>
    public static Dictionary<Guid, (string Code, bool Mixed)> Holders(IEnumerable<HeldNow> held) =>
        held.GroupBy(h => h.SecurityId)
            .ToDictionary(g => g.Key, g => DisplayCurrency.Of(g.Select(h => h.CurrencyCode)));

    /// <summary>
    /// Per security with a price row, the currency its current price is in: the close
    /// the price was read from (a close owns its day, ADR-0070 D1, and only a close has a
    /// row on the day the price is dated, since a last trade's price is used only when no
    /// close is dated on or before the day); for a last trade's price, or no current
    /// price at all, the security's newest price row's. A security with no price row has
    /// no entry, and its reader falls back to <see cref="Holders"/>.
    /// </summary>
    /// <param name="current">The current prices the caller read from
    /// <c>security_prices_as_of_now</c> for the same securities; their dates name the
    /// close each was read from.</param>
    /// <param name="securityIds">The securities to answer for; null for every security
    /// in the ledger.</param>
    public static async Task<Dictionary<Guid, string>> PriceRowAsync(
        AppDbContext db,
        Guid ledgerId,
        IReadOnlyCollection<SecurityPriceAsOfNowRow> current,
        Guid[]? securityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(current);

        // The rows on the days the current prices are dated: one row per (security, day)
        // (UNIQUE), so a row whose day is its own security's price date is that close.
        // Asked by the two key lists, which can also return a row of one security on
        // another's day; those are dropped here.
        var dated = current.ToDictionary(c => c.SecurityId, c => c.PriceDate);
        var bySecurity = new Dictionary<Guid, string>();
        if (dated.Count > 0)
        {
            var ids = dated.Keys.ToArray();
            var days = dated.Values.Distinct().ToArray();
            var onTheirDays = await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == ledgerId && ids.Contains(p.SecurityId) && days.Contains(p.PriceDate))
                .Select(p => new { p.SecurityId, p.PriceDate, p.CurrencyCode })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in onTheirDays)
            {
                if (dated[row.SecurityId] == row.PriceDate)
                    bySecurity[row.SecurityId] = row.CurrencyCode;
            }
        }

        // Every other security's newest row. Asked only of the securities no close named,
        // so the per-row newest-day test runs over their rows alone.
        var named = bySecurity.Keys.ToArray();
        var newestRows = db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && !named.Contains(p.SecurityId));
        if (securityIds is not null)
            newestRows = newestRows.Where(p => securityIds.Contains(p.SecurityId));
        var newest = await newestRows
            .Where(p => p.PriceDate == db.SecurityPrices
                .Where(q => q.LedgerId == ledgerId && q.SecurityId == p.SecurityId)
                .Max(q => q.PriceDate))
            .Select(p => new { p.SecurityId, p.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in newest)
            bySecurity[row.SecurityId] = row.CurrencyCode;
        return bySecurity;
    }
}

/// <summary>What one holdings account holds of a security on the caller's today, in the
/// account's currency: the quantity and cost basis of its position.</summary>
internal readonly record struct HeldNow(
    Guid SecurityId, Guid AccountId, string CurrencyCode, decimal Quantity, decimal CostBasis);
