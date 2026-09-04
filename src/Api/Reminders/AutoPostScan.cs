namespace Coffer.Api.Reminders;

/// <summary>
/// What firing an occurrence does to the EARLIER un-acted occurrences of the same series.
/// </summary>
/// <remarks>
/// <para>
/// Required at every call site rather than defaulted, because the two callers want
/// opposite things and the wrong default is silent. <c>CascadeSkipEarlierAsync</c> writes
/// a skip exception for every earlier un-acted slot, which is right for a person: the SPA
/// shows them <c>SkippedEarlierCount</c> before they confirm, and clearing an overdue
/// backlog off the calendar is what they asked for.
/// </para>
/// <para>
/// A timer has no dialog. Cascading from a scheduled run would mark occurrences skipped
/// that nobody decided to skip — turning a bounded catch-up window into silent data loss,
/// where the bound stops the job posting them and the cascade stops anyone else from ever
/// seeing them.
/// </para>
/// </remarks>
public enum ReminderCatchUp
{
    /// <summary>Mark earlier un-acted occurrences skipped. The manual path.</summary>
    SkipEarlierUnacted,

    /// <summary>
    /// Leave earlier un-acted occurrences exactly as they are — un-acted, un-skipped,
    /// still on the calendar. The scheduled path.
    /// </summary>
    LeaveEarlierOpen,
}

/// <summary>
/// One occurrence the auto-post job may post, with the facts the policy needs.
/// </summary>
/// <param name="ReminderId">The series.</param>
/// <param name="OccurrenceDate">The slot. Also half of the idempotency key (mig 218).</param>
/// <param name="AutoCommitDaysBefore">
/// The series' acdays, already clamped to the configured ceiling.
/// </param>
/// <param name="IsInvestmentShape">
/// The series' template header carries an <c>action</c>, so firing it means holdings and
/// lots rather than a bank pair.
/// </param>
/// <param name="IsLoanReminder">
/// A managed loan series: the amount is computed from the loan terms and the CURRENT
/// balance at fire time, not read off the template.
/// </param>
public sealed record AutoPostCandidate(
    Guid ReminderId,
    DateOnly OccurrenceDate,
    int AutoCommitDaysBefore,
    bool IsInvestmentShape,
    bool IsLoanReminder);

/// <summary>
/// What one scan for auto-postable occurrences found.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TooOldToPost"/> is separate from <see cref="Due"/> and is the reason the
/// scan looks further back than it is willing to post. A catch-up window has to be
/// bounded — a ledger down for a month must not wake up and post thirty occurrences —
/// but a bound whose effect is invisible reads to the operator as "nothing was due". So
/// the scan counts one window beyond what it will act on, and the count is published.
/// </para>
/// <para>
/// It is a COUNT, not a list. The occurrences are still open, still on the calendar, and
/// still actionable by hand; enumerating dates into a notification that may be delivered
/// to a webhook would put a ledger's payment schedule somewhere the ledger cannot reach.
/// </para>
/// </remarks>
public sealed record AutoPostScanResult(
    IReadOnlyList<AutoPostCandidate> Due,
    int TooOldToPost);
