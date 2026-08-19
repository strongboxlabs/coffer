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
/// <remarks>
/// <para>
/// <b>The advance is committed BEFORE the handler runs</b> (mig 194). It used to
/// be applied in memory after the handler returned and persisted by a single
/// <c>SaveChangesAsync</c> after the whole loop, over the same context the
/// handler had just used. That made the bookkeeping a hostage of the work: on
/// 2026-08-13 the daily snapshot OOM-killed its Postgres backend, the postmaster
/// entered crash recovery, the save failed against a recovering database, and the
/// job stayed due — so a daily job re-ran every 15 minutes for two days and took
/// the nightly whole-DB backup down with it. The failure modes that most deserve
/// not to be retried are exactly the ones that stop you recording "I ran".
/// </para>
/// <para>
/// There is deliberately no backoff. These are daily schedules, so the correct
/// response to a failure is the next daily slot; a shorter retry interval would
/// make a broken job run more often than a healthy one. What repeated failure
/// buys instead is <see cref="DisableAfterConsecutiveFailures"/> — after that
/// many consecutive failures the row is disabled and needs an operator, rather
/// than failing silently forever.
/// </para>
/// </remarks>
public sealed class SchedulerRunner
{
    /// <summary>Consecutive failures after which a job disables itself. These are
    /// daily jobs, so this is roughly "failing for five days running".</summary>
    public const int DisableAfterConsecutiveFailures = 5;

    /// <summary>Cap on the stored <c>last_error</c>. A message, never a stack
    /// trace — the column is surfaced in the SPA.</summary>
    private const int MaxErrorLength = 500;

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

        var ran = 0;
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Claim the slot first: advance and COMMIT before doing any work, so
            // a handler that kills the connection cannot cost us the advance.
            job.LastRunAt = nowUtc;
            job.NextRunAt = DailyScheduleTiming.NextRunUtc(
                job.HourLocal, job.MinuteLocal, job.Timezone, nowUtc);
            job.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // Counts due rows processed, not handler invocations — an unhandled
            // job_type still advanced, and callers have always counted it.
            ran++;

            if (!handlers.TryGetValue(job.JobType, out var handler))
            {
                logger.LogWarning(
                    "No handler registered for scheduled job_type '{JobType}'; skipping.", job.JobType);
                continue;
            }

            Exception? failure = null;
            try
            {
                await handler.RunAsync(db, job.LedgerId, job.ConfiguredByUserId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogError(ex,
                    "Scheduled job {JobType} failed for ledger {LedgerId}; continuing.",
                    job.JobType, job.LedgerId);
            }

            ApplyOutcome(job, failure, nowUtc, logger, job.JobType, job.LedgerId.ToString());

            // Bookkeeping only — the advance is already durable. If this save
            // fails the connection is probably gone, so abandon the tick and let
            // the next one start with a fresh context rather than watching every
            // remaining job fail against a dead connection.
            if (!await TrySaveAsync(db, logger, cancellationToken).ConfigureAwait(false))
                break;
        }

        return ran;
    }

    /// <summary>
    /// The global (non-ledger) counterpart: find every due
    /// <c>global_scheduled_jobs</c> row (mig 139), dispatch it to its
    /// <see cref="IGlobalScheduledJobHandler"/>, and advance <c>next_run_at</c>.
    /// Same claim-before-work ordering and failure handling as the per-ledger
    /// path — this loop had the identical bug, and the backup job was the
    /// collateral casualty of it.
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

        var ran = 0;
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            job.LastRunAt = nowUtc;
            job.NextRunAt = DailyScheduleTiming.NextRunUtc(
                job.HourLocal, job.MinuteLocal, job.Timezone, nowUtc);
            job.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ran++;

            if (!handlers.TryGetValue(job.JobType, out var handler))
            {
                logger.LogWarning(
                    "No handler registered for global job_type '{JobType}'; skipping.", job.JobType);
                continue;
            }

            Exception? failure = null;
            try
            {
                await handler.RunAsync(db, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogError(ex, "Global scheduled job {JobType} failed; continuing.", job.JobType);
            }

            ApplyOutcome(job, failure, nowUtc, logger, job.JobType, ledgerId: null);

            if (!await TrySaveAsync(db, logger, cancellationToken).ConfigureAwait(false))
                break;
        }

        return ran;
    }

    /// <summary>
    /// Records success (reset the counter) or failure (increment, capture the
    /// message, disable at the threshold) on either job shape.
    /// </summary>
    private static void ApplyOutcome(
        object row, Exception? failure, DateTime nowUtc, ILogger logger,
        string jobType, string? ledgerId)
    {
        int failures;
        if (failure is null)
        {
            SetFailureState(row, 0, lastError: null, lastFailureAt: null, enabled: null);
            return;
        }

        failures = GetConsecutiveFailures(row) + 1;
        var message = Truncate(failure.Message, MaxErrorLength);
        var disable = failures >= DisableAfterConsecutiveFailures;

        SetFailureState(row, failures, message, nowUtc, disable ? false : null);

        if (disable)
        {
            logger.LogError(
                "Scheduled job {JobType}{LedgerSuffix} disabled after {Failures} consecutive "
                + "failures; re-enable it once the cause is fixed. Last error: {LastError}",
                jobType,
                ledgerId is null ? string.Empty : $" for ledger {ledgerId}",
                failures,
                message);
        }
    }

    private static int GetConsecutiveFailures(object row) => row switch
    {
        Db.Entities.ScheduledJobRow j => j.ConsecutiveFailures,
        Db.Entities.GlobalScheduledJobRow g => g.ConsecutiveFailures,
        _ => 0,
    };

    private static void SetFailureState(
        object row, int failures, string? lastError, DateTime? lastFailureAt, bool? enabled)
    {
        switch (row)
        {
            case Db.Entities.ScheduledJobRow j:
                j.ConsecutiveFailures = failures;
                j.LastError = lastError;
                j.LastFailureAt = lastFailureAt;
                if (enabled is not null) j.Enabled = enabled.Value;
                break;
            case Db.Entities.GlobalScheduledJobRow g:
                g.ConsecutiveFailures = failures;
                g.LastError = lastError;
                g.LastFailureAt = lastFailureAt;
                if (enabled is not null) g.Enabled = enabled.Value;
                break;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Saves post-run bookkeeping, returning false when the context can no longer
    /// be written (the handler took the connection with it). Never throws — the
    /// durable advance already happened, so giving up on the tick is safe.
    /// </summary>
    private static async Task<bool> TrySaveAsync(
        AppDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Scheduler could not persist job outcome; abandoning this tick. The next "
                + "run time was already committed, so jobs will not re-fire early.");
            return false;
        }
    }
}
