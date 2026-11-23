using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The day a ledger's history begins: its earliest dated fact.
/// </summary>
/// <remarks>
/// <para>A valuation at an instant before this day is not a zero net worth, it is no
/// history at all, and a reader that shows a change from it reports the whole ledger as
/// growth. The facts are a transaction's date (live headers only: not hidden, not merged
/// away, not a reminder's template) and an account's opening date.</para>
/// <para>Never a magnitude test: a real net worth can be zero.</para>
/// </remarks>
public static class LedgerHistoryStart
{
    /// <summary>The earliest dated fact's day, or null when the ledger has none.</summary>
    public static async Task<DateOnly?> EarliestFactAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var posted = await FirstTransactionDayAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);

        var firstOpened = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType != "category" && a.OpenedOn != null)
            .MinAsync(a => a.OpenedOn, cancellationToken)
            .ConfigureAwait(false);

        return (posted, firstOpened) switch
        {
            ({ } a, { } b) => a < b ? a : b,
            ({ } a, null) => a,
            (null, var b) => b,
        };
    }

    /// <summary>
    /// The day of the ledger's first transaction of any kind, or null when it has none:
    /// a live header (not hidden, not merged away, not a reminder's template), dated by
    /// its UTC posting day, the day every report's daily figures are filed under.
    /// </summary>
    public static async Task<DateOnly?> FirstTransactionDayAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var firstPosted = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId
                        && !h.IsHidden
                        && h.IsMergedInto == null
                        && !h.IsRecurringTemplate)
            .MinAsync(h => (DateTime?)h.PostedAt, cancellationToken)
            .ConfigureAwait(false);

        return firstPosted is { } p ? DateOnly.FromDateTime(p) : null;
    }

    /// <summary>Whether a valuation at the end of <paramref name="day"/> precedes the
    /// ledger's history: no fact on or before that day.</summary>
    public static bool Precedes(DateOnly day, DateOnly? earliestFact) =>
        earliestFact is not { } first || day < first;
}
