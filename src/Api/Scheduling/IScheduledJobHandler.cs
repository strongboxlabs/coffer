using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>Known <c>scheduled_jobs.job_type</c> values (DB CHECK mirrors this).</summary>
public static class JobTypes
{
    public const string QuoteRefresh = "quote-refresh";
    public const string Snapshot = "snapshot";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { QuoteRefresh, Snapshot };
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
    Task RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken cancellationToken);
}
