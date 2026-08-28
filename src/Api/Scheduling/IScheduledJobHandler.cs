using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>Known <c>scheduled_jobs.job_type</c> values (DB CHECK mirrors this).</summary>
public static class JobTypes
{
    public const string QuoteRefresh = "quote-refresh";
    public const string Snapshot = "snapshot";

    /// <summary>Daily pull of every bank feed connection on the ledger (mig 215).</summary>
    public const string FeedSync = "feed-sync";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { QuoteRefresh, Snapshot, FeedSync };
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
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken cancellationToken);
}
