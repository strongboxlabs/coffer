namespace Coffer.Api.Reminders;

/// <summary>
/// The bounds on one auto-post run.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than constants so a test can shrink them to a few days and a couple of
/// posts. Every one of these is a POLICY number with a consequence in money, and a test
/// that had to seed fifty occurrences to exercise the cap would not be written.
/// </para>
/// <para>
/// Every bound here is NON-DESTRUCTIVE. An occurrence a bound excludes stays exactly as
/// it was — un-acted, un-skipped, on the calendar, postable by hand. That is the whole
/// reason auto-post fires with <see cref="ReminderCatchUp.LeaveEarlierOpen"/>: a bound
/// that stopped the job posting AND let the cascade mark the slot skipped would be silent
/// data loss dressed up as a safety limit.
/// </para>
/// </remarks>
/// <param name="CatchUpDays">
/// How far back a run will post. The scheduler commits <c>next_run_at</c> BEFORE
/// dispatching (mig 194), so a crash, a SIGTERM or a cancelled tick loses that day's run
/// with no failure recorded — which means missed days are normal, not exceptional, and a
/// job that only ever posted "today" would drop them silently.
/// </param>
/// <param name="MaxPostsPerRun">
/// Ceiling on transactions written in one run. A month of downtime across several daily
/// series can make a three-figure backlog due at once, and posting all of it in one tick
/// is indistinguishable — to the person reading their register — from a runaway loop.
/// </param>
/// <param name="MaxAutoCommitDaysBefore">
/// Ceiling on a series' acdays (ADR-0097). The transaction is written early but DATED at
/// its due date, so a large value moves the balance well before the money leaves, and a
/// value beyond the recurrence interval would open an unbounded pipeline of future-dated
/// real transactions. Existing rows above it are CLAMPED, not refused.
/// </param>
public sealed record ReminderAutoPostLimits(
    int CatchUpDays = 45,
    int MaxPostsPerRun = 50,
    int MaxAutoCommitDaysBefore = 90)
{
    /// <summary>Production values.</summary>
    public static readonly ReminderAutoPostLimits Default = new();
}
