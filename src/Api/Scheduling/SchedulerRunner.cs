using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>
/// The testable core of the generic scheduler: find every due
/// <c>scheduled_jobs</c> row, dispatch it to the handler for its
/// <c>job_type</c>, and advance <c>next_run_at</c>. A per-job failure logs and
/// continues; an unknown job_type is logged and skipped (still advanced).
/// </summary>
public sealed class SchedulerRunner
{
    public async Task<int> RunDueAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, IScheduledJobHandler> handlers,
        DateTime nowUtc,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var due = await db.ScheduledJobs
            .Where(j => j.Enabled && j.NextRunAt != null && j.NextRunAt <= nowUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handlers.TryGetValue(job.JobType, out var handler))
            {
                try
                {
                    await handler.RunAsync(db, job.LedgerId, job.ConfiguredByUserId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Scheduled job {JobType} failed for ledger {LedgerId}; continuing.",
                        job.JobType, job.LedgerId);
                }
            }
            else
            {
                logger.LogWarning(
                    "No handler registered for scheduled job_type '{JobType}'; skipping.", job.JobType);
            }

            job.LastRunAt = nowUtc;
            job.NextRunAt = DailyScheduleTiming.NextRunUtc(
                job.HourLocal, job.MinuteLocal, job.Timezone, nowUtc);
            job.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due.Count;
    }

    /// <summary>
    /// The global (non-ledger) counterpart: find every due
    /// <c>global_scheduled_jobs</c> row (mig 139), dispatch it to its
    /// <see cref="IGlobalScheduledJobHandler"/>, and advance <c>next_run_at</c>.
    /// Same failure handling as the per-ledger path — a per-job failure logs
    /// and continues; an unknown job_type is skipped but still advanced.
    /// </summary>
    public async Task<int> RunDueGlobalAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, IGlobalScheduledJobHandler> handlers,
        DateTime nowUtc,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var due = await db.GlobalScheduledJobs
            .Where(j => j.Enabled && j.NextRunAt != null && j.NextRunAt <= nowUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handlers.TryGetValue(job.JobType, out var handler))
            {
                try
                {
                    await handler.RunAsync(db, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Global scheduled job {JobType} failed; continuing.", job.JobType);
                }
            }
            else
            {
                logger.LogWarning(
                    "No handler registered for global job_type '{JobType}'; skipping.", job.JobType);
            }

            job.LastRunAt = nowUtc;
            job.NextRunAt = DailyScheduleTiming.NextRunUtc(
                job.HourLocal, job.MinuteLocal, job.Timezone, nowUtc);
            job.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due.Count;
    }
}
