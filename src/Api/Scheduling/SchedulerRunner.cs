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

    /// <param name="publisher">
    /// Emits each finished run as its monitor's signal. Optional so the runner stays
    /// constructible in tests that are not about notifications; when it is null the jobs
    /// run exactly as before and nothing is announced.
    /// </param>
    public async Task<int> RunDueAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, IScheduledJobHandler> handlers,
        DateTime nowUtc,
        ILogger logger,
        CancellationToken cancellationToken,
        Notifications.NotificationPublisher? publisher = null)
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
            var outcome = JobRunOutcome.Ok();
            try
            {
                // The SAME instant that decided this row was due, and computed its next
                // slot, also decides the job's "today". Reading a clock inside a handler
                // would let those disagree across a midnight boundary.
                outcome = await handler
                    .RunAsync(
                        db, job.LedgerId, job.ConfiguredByUserId,
                        new JobRunClock(nowUtc, job.Timezone), cancellationToken)
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

            var justDisabled = ApplyOutcome(
                job, failure, outcome, nowUtc, logger, job.JobType, job.LedgerId.ToString());

            // One signal per run, emitted HERE rather than in the handlers: a handler
            // that forgot to emit would produce precisely the silence its monitor exists
            // to detect, and this point is reached by the run that threw as well as the
            // one that returned.
            //
            // Announcing must never cost a job. A publisher that throws — an unreachable
            // webhook, a DNS failure — has already been recorded against the subscriber
            // row, and letting it escape here would abandon the rest of the tick over a
            // notification.
            if (publisher is not null)
            {
                var signal = JobMonitorSignal.For(job.JobType, outcome, failure);
                if (signal is not null)
                {
                    try
                    {
                        await publisher
                            .PublishLedgerAsync(job.LedgerId, signal, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Could not announce {JobType} for ledger {LedgerId}; the job "
                            + "itself is unaffected.", job.JobType, job.LedgerId);
                    }
                }

                // Second event, and only on the tick that gives up. disabled_reason
                // answers "why is this off now" and is erased the moment someone
                // re-enables; this is what survives, in ledger_events.
                if (justDisabled && failure is not null)
                {
                    try
                    {
                        await publisher
                            .PublishLedgerAsync(
                                job.LedgerId,
                                DisabledEvent(
                                    job.JobType, GetConsecutiveFailures(job), failure),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Could not announce that {JobType} was disabled for ledger "
                            + "{LedgerId}; the job is still off.", job.JobType, job.LedgerId);
                    }
                }
            }

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
        CancellationToken cancellationToken,
        // Optional and last, mirroring the per-ledger overload: every existing caller
        // and test keeps compiling, and a caller that does not supply one simply gets
        // no announcement rather than a null-reference at 3am.
        Notifications.NotificationPublisher? publisher = null)
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

            // Global handlers have no outcome channel: the backup job already publishes
            // its own Success/Failure signal directly (DailyBackupJobHandler), so there
            // is nothing here for a third state to describe.
            var justDisabled = ApplyOutcome(
                job, failure, JobRunOutcome.Ok(), nowUtc, logger, job.JobType, ledgerId: null);

            // Deployment scope, so this one goes to system_events rather than a
            // ledger's log. The backup job publishes its own per-run signal; nothing
            // announced the moment the scheduler stopped running it at all, which is
            // the outage that matters most here — a deployment whose backups have
            // silently ended.
            if (publisher is not null && justDisabled && failure is not null)
            {
                try
                {
                    await publisher
                        .PublishAsync(
                            DisabledEvent(job.JobType, GetConsecutiveFailures(job), failure),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Could not announce that global job {JobType} was disabled; it is "
                        + "still off.", job.JobType);
                }
            }

            if (!await TrySaveAsync(db, logger, cancellationToken).ConfigureAwait(false))
                break;
        }

        return ran;
    }

    /// <summary>
    /// Records success (reset the counter), a degraded run (keep the counter, keep the
    /// reason), or failure (increment, capture the message, disable at the threshold).
    /// </summary>
    /// <remarks>
    /// Degraded sits between the two on purpose. It resets the consecutive-failure count
    /// — the job DID run, and auto-disabling a daily refresh because one provider was
    /// flaky for five days loses more than it protects — but it keeps last_error
    /// populated so the reason is visible rather than logged, and it must never be
    /// reported upward as a success. See <see cref="JobRunResult"/> for why a two-state
    /// model was not enough.
    /// </remarks>
    /// <returns>
    /// True when THIS run is the one that switched the job off, so the caller can
    /// announce the disable. The disable is a different event from the run's own
    /// failure signal: the run failing is routine until it is not, and the moment the
    /// scheduler gives up is the one a person needs to see. It is also the only durable
    /// record — disabled_reason is current state and clears on re-enable.
    /// </returns>
    private static bool ApplyOutcome(
        object row, Exception? failure, JobRunOutcome outcome, DateTime nowUtc, ILogger logger,
        string jobType, string? ledgerId)
    {
        int failures;
        if (failure is null && outcome.Result == JobRunResult.Degraded)
        {
            SetFailureState(
                row, 0, Truncate(outcome.Message ?? "Ran with degraded results.", MaxErrorLength),
                nowUtc, enabled: null, disabledReason: null);
            logger.LogWarning(
                "Scheduled job {JobType}{LedgerSuffix} completed with degraded results: {Reason}",
                jobType,
                ledgerId is null ? string.Empty : $" for ledger {ledgerId}",
                outcome.Message);
            return false;
        }

        if (failure is null)
        {
            // disabledReason cleared as well: a run that succeeded is not a job anyone
            // is still holding off. It should already be null (a disabled job does not
            // run), so this is belt and braces against a row that was re-enabled by a
            // path that forgot to clear it.
            SetFailureState(
                row, 0, lastError: null, lastFailureAt: null, enabled: null,
                disabledReason: null);
            return false;
        }

        failures = GetConsecutiveFailures(row) + 1;
        var message = Truncate(failure.Message, MaxErrorLength);

        // REMINDER AUTO-POST HAS NO DISABLED STATE.
        //
        // Auto-disable protects a job whose repeated failure is itself harmful — a
        // backup hammering a full disk, a sync retrying a dead endpoint. Posting a
        // reminder is not that: a failed fire writes nothing and costs nothing, and the
        // occurrence stays on the calendar for a person to act on.
        //
        // Switching it off would create the failure this whole area was fixed for twice
        // over: reminders quietly stop posting, and the user finds out from a missing
        // transaction. Worse, the job is switched ON by ticking Auto-post on a reminder
        // and by nothing else — there is no switch to find — so a disabled row would be
        // a state with no way out.
        //
        // Failures are still counted, still stamped in last_error, and still published
        // to the monitor. They just never stop it trying.
        var disable = failures >= DisableAfterConsecutiveFailures
                      && jobType != JobTypes.ReminderAutoPost;

        SetFailureState(
            row, failures, message, nowUtc,
            disable ? false : null,
            // Stamped only when this run is the one that switches the job off. mig 216:
            // `enabled = FALSE` has three authors and used to record none of them, so a
            // job the scheduler gave up on was indistinguishable from one an operator
            // meant to leave off.
            disable ? ScheduleDisableReasons.ConsecutiveFailures : null);

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

        return disable;
    }

    /// <summary>
    /// The announcement that a job has been switched off, as distinct from the run that
    /// failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MonitorSignal.None</c>, deliberately. The run's own signal was already
    /// published a moment earlier and a dead-man's switch counts signals; emitting a
    /// second Failure for the same tick would report two outages where there was one.
    /// This event exists to be READ — in the bell, in the event log, by a subscriber
    /// filtering on the scheduler topic — not to drive a check.
    /// </para>
    /// <para>
    /// The exception is described, never quoted, for the same reason every other
    /// published summary is: this text reaches whatever URL an operator configured.
    /// </para>
    /// </remarks>
    private static Notifications.NotificationEvent DisabledEvent(
        string jobType, int failures, Exception failure) =>
        new(
            Severity: Notifications.NotificationSeverity.Critical,
            Topic: Notifications.NotificationTopics.Scheduler,
            EventKey: "scheduler.job-disabled",
            Summary: $"Scheduled {jobType} has been switched OFF after {failures} "
                + $"consecutive failures. It will not run again until it is re-enabled. "
                + Notifications.PublishedFailure.Describe(failure),
            Detail: Notifications.PublishedFailure.DetailFor(failure),
            Monitor: null,
            Signal: Notifications.MonitorSignal.None);

    private static int GetConsecutiveFailures(object row) => row switch
    {
        Db.Entities.ScheduledJobRow j => j.ConsecutiveFailures,
        Db.Entities.GlobalScheduledJobRow g => g.ConsecutiveFailures,
        _ => 0,
    };

    private static void SetFailureState(
        object row, int failures, string? lastError, DateTime? lastFailureAt, bool? enabled,
        string? disabledReason)
    {
        switch (row)
        {
            case Db.Entities.ScheduledJobRow j:
                j.ConsecutiveFailures = failures;
                j.LastError = lastError;
                j.LastFailureAt = lastFailureAt;
                j.DisabledReason = disabledReason;
                if (enabled is not null) j.Enabled = enabled.Value;
                break;
            case Db.Entities.GlobalScheduledJobRow g:
                g.ConsecutiveFailures = failures;
                g.LastError = lastError;
                g.LastFailureAt = lastFailureAt;
                g.DisabledReason = disabledReason;
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
