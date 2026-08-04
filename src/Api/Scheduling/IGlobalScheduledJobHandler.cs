using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>
/// A handler for one <c>global_scheduled_jobs.job_type</c> (mig 139) — a
/// deployment-wide job with no owning ledger (e.g. the whole-DB backup). The
/// single <see cref="SchedulerService"/> resolves these alongside the
/// per-ledger <see cref="IScheduledJobHandler"/> and dispatches each due global
/// row to the matching one. Distinct interface (not <see cref="IScheduledJobHandler"/>)
/// precisely because there is no ledger / configuring-user context to pass.
/// </summary>
public interface IGlobalScheduledJobHandler
{
    /// <summary>The job_type this handler runs (see <see cref="GlobalJobTypes"/>).</summary>
    string JobType { get; }

    /// <summary>
    /// Run the global job. <paramref name="db"/> is the service-role (BYPASSRLS)
    /// context the worker owns; a handler that does its own work (e.g. shelling
    /// out to pg_dump) may ignore it.
    /// </summary>
    Task RunAsync(AppDbContext db, CancellationToken cancellationToken);
}
