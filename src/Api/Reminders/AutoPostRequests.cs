using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Reminders;

/// <summary>
/// The reminders of a ledger that ask to be auto-posted: the one definition of "this ledger
/// needs its <c>reminder-auto-post</c> job", read by the Moneydance import that turns the
/// job on and by the consistency check that finds a ledger without it.
/// </summary>
/// <remarks>
/// <para>
/// A reminder asks when its <c>AutoCommitDaysBefore</c> is set, and nothing else is asked
/// of it: an inactive reminder, one past its end date and a legacy row with no template
/// ask too. That is the rule a reminder save applies (<c>RemindersEndpoints</c> turns the
/// job on whenever a saved reminder carries the setting, whatever its state), and it is
/// the right one for a ledger-wide switch: reactivating a reminder turns no job on, so a
/// ledger whose only auto-posting reminder is inactive must already have its job for that
/// reminder to post once it is active again. Inactive is the only way a reminder is put
/// away; there is no deleted state to leave out, and a reminder's row goes only with its
/// ledger.
/// </para>
/// <para>
/// Which occurrences the job then posts is its own narrower question
/// (<c>RemindersRepository.AutoPostFactsAsync</c>: active, materialized series only).
/// </para>
/// </remarks>
internal static class AutoPostRequests
{
    /// <summary>The ledger's reminders that ask to be auto-posted, untracked.</summary>
    internal static IQueryable<RecurringTransactionRow> In(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.AutoCommitDaysBefore != null);
    }
}
