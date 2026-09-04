using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>Known <c>scheduled_jobs.job_type</c> values (DB CHECK mirrors this).</summary>
public static class JobTypes
{
    public const string QuoteRefresh = "quote-refresh";
    public const string Snapshot = "snapshot";

    /// <summary>Daily pull of every bank feed connection on the ledger (mig 215).</summary>
    public const string FeedSync = "feed-sync";

    /// <summary>
    /// Post reminder occurrences that are within their series' acdays window (mig 219,
    /// ADR-0097). The only job type here that WRITES FINANCIAL TRANSACTIONS.
    /// </summary>
    public const string ReminderAutoPost = "reminder-auto-post";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { QuoteRefresh, Snapshot, FeedSync, ReminderAutoPost };
}

/// <summary>
/// The instant a scheduled run is happening at, and the timezone its ledger reckons days
/// in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a handler cannot just read the clock.</b> Reminders are pure
/// <see cref="DateOnly"/> — an occurrence is due on a DATE — so a handler has to answer
/// "what is today?". <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> is the wrong answer
/// for any ledger whose local date differs from UTC's at the moment the job fires, which
/// for an evening slot in the Americas is every single run.
/// </para>
/// <para>
/// It also makes the handler untestable in the one way that matters. A test cannot place
/// "today" anywhere, so a due-ness assertion either drifts with the wall clock or is
/// written against whatever today happens to be — and this repo has already shipped one
/// vacuous test of exactly that shape (see <c>SchedulesRepositoryDstTests</c>, whose
/// remarks record the endpoint version passing with the fix deleted).
/// </para>
/// <para>
/// The runner already holds both values — the <c>nowUtc</c> that decided due-ness and the
/// row's <c>Timezone</c> — so passing them down costs nothing and guarantees the instant a
/// job fired at and the date it reasons about cannot disagree.
/// </para>
/// </remarks>
public readonly record struct JobRunClock(DateTime NowUtc, string? TimezoneId)
{
    /// <summary>
    /// The local calendar date this run is happening on, in the schedule's timezone.
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="DailyScheduleTiming"/> so a blank or unknown id falls
    /// back exactly the way <c>NextRunUtc</c> falls back. One definition of the ledger's
    /// "today", next to the one definition of its next run.
    /// </remarks>
    public DateOnly LocalToday()
    {
        var tz = DailyScheduleTiming.Resolve(TimezoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(NowUtc, DateTimeKind.Utc), tz);
        return DateOnly.FromDateTime(local);
    }
}

/// <summary>What a scheduled run actually achieved.</summary>
/// <remarks>
/// <para>
/// Three states, not two, because "the handler returned without throwing" is not the
/// same as "the job did its work" for any of the per-ledger job types.
/// <c>QuoteOrchestrator</c> catches every provider exception into an error list and
/// returns normally, so a total provider outage completes cleanly.
/// <c>LedgerSnapshotsRepository</c> returns <c>SkippedDueToFullPool</c> rather than
/// throwing when auto-snap cannot fit. Both look identical to a try/catch.
/// </para>
/// <para>
/// That distinction is the whole reason this type exists: a dead-man's switch wired to
/// "did not throw" would report a green check over a job that quietly did nothing, which
/// is worse than having no switch at all — it manufactures the confidence the switch was
/// supposed to earn.
/// </para>
/// <para>
/// <c>Degraded</c> deliberately does NOT count toward the consecutive-failure
/// auto-disable. A ledger holding one delisted ticker would otherwise disable its own
/// quote refresh after five days, and losing the whole job is a worse outcome than the
/// partial result that provoked it. It also must not be reported as success — it is the
/// state where a human should look, and neither extreme says that.
/// </para>
/// </remarks>
public enum JobRunResult
{
    /// <summary>The job did what it exists to do.</summary>
    Ok,

    /// <summary>It ran, and part of the work did not land. Not a failure; not a success.</summary>
    Degraded,

    /// <summary>It threw. The runner records this — handlers do not return it.</summary>
    Failed,
}

/// <summary>The outcome of one scheduled run, with a reason when it is not <c>Ok</c>.</summary>
/// <param name="Message">Short, human-facing, and safe to store: it lands in
/// <c>scheduled_jobs.last_error</c> and is surfaced rather than logged.</param>
public readonly record struct JobRunOutcome(JobRunResult Result, string? Message = null)
{
    /// <summary>The job did its work.</summary>
    public static JobRunOutcome Ok() => new(JobRunResult.Ok);

    /// <summary>It ran but part of the work did not land, for the stated reason.</summary>
    public static JobRunOutcome Degraded(string message) => new(JobRunResult.Degraded, message);
}

/// <summary>
/// A handler for one <c>scheduled_jobs.job_type</c>. The generic
/// <c>SchedulerService</c> resolves all registered handlers and dispatches each
/// due row to the matching one. Implementations build whatever they need over
/// the supplied (service-role) context and run the work for one ledger.
/// </summary>
public interface IScheduledJobHandler
{
    /// <summary>The job_type this handler runs (see <see cref="JobTypes"/>).</summary>
    string JobType { get; }

    /// <summary>
    /// Run the job for one ledger. <paramref name="db"/> is the service-role
    /// (BYPASSRLS) context the worker owns; <paramref name="configuredByUserId"/>
    /// is the schedule's owner (used for attribution / pref resolution).
    /// </summary>
    /// <returns>
    /// What the run achieved. Throwing is still the right way to report a failure the
    /// handler cannot describe — the runner catches it. Returning
    /// <see cref="JobRunResult.Degraded"/> is for the work the handler CAN see went
    /// partly undone, which is precisely what its own error handling would otherwise
    /// hide from the runner.
    /// </returns>
    Task<JobRunOutcome> RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock,
        CancellationToken cancellationToken);
}
