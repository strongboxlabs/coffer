using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Scheduling;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Reminders;

/// <summary>
/// Scheduled-job handler for <c>reminder-auto-post</c> (mig 219, ADR-0097): posts the
/// occurrences whose series asked to be posted automatically.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only scheduled job that writes financial transactions.</b> Everything unusual
/// about this class follows from that.
/// </para>
/// <para>
/// <b>It never writes on the <paramref name="db"/> the runner hands it.</b> It creates
/// its own short-lived service context for the scan, and ONE PER OCCURRENCE for the
/// fires. Three reasons, all of them concrete:
/// </para>
/// <list type="number">
///   <item><description>
///   <c>FireAsync</c>'s duplicate-key path calls <c>ChangeTracker.Clear()</c>. On the
///   runner's context that would detach the <c>scheduled_jobs</c> row the runner is
///   tracking, so the advance and the failure bookkeeping it writes after the handler
///   returns would silently not persist.
///   </description></item>
///   <item><description>
///   <c>FireAsync</c> opens its own transaction. Posting several occurrences on one
///   context would make them one transaction in practice, so occurrence five failing
///   would roll back one through four — turning a partial success into a total loss.
///   </description></item>
///   <item><description>
///   A failed fire can leave its context's change tracker holding entities from the
///   attempt. Reusing it for the next occurrence would carry them into that write.
///   </description></item>
/// </list>
/// <para>
/// <b>Failure reporting is four-armed, and the shape is forced by
/// <c>SchedulerRunner.ApplyOutcome</c>.</b> A <c>Degraded</c> outcome sets
/// <c>consecutive_failures</c> to ZERO and never disables the job, while still publishing
/// a failure signal to the monitor. So Degraded is the right answer for "some work
/// landed", and THROWING is the only way this job type can ever reach the five-strike
/// auto-disable — which is the entire reason ADR-0097 made auto-post a job type instead
/// of a tick-riding monitor.
/// </para>
/// </remarks>
public sealed class ReminderAutoPostJobHandler : IScheduledJobHandler
{
    private readonly ServiceDbContextFactory _factory;
    private readonly RecurrenceExpander _expander;
    private readonly NotificationPublisher _publisher;
    private readonly ReminderAutoPostLimits _limits;
    private readonly ILogger<ReminderAutoPostJobHandler> _logger;

    public ReminderAutoPostJobHandler(
        ServiceDbContextFactory factory,
        RecurrenceExpander expander,
        NotificationPublisher publisher,
        ILogger<ReminderAutoPostJobHandler> logger,
        ReminderAutoPostLimits? limits = null)
    {
        _factory = factory;
        _expander = expander;
        _publisher = publisher;
        _logger = logger;
        _limits = limits ?? ReminderAutoPostLimits.Default;
    }

    public string JobType => JobTypes.ReminderAutoPost;

    /// <summary>
    /// Post every due occurrence for this ledger, oldest first, within the run's bounds.
    /// </summary>
    public async Task<JobRunOutcome> RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock,
        CancellationToken cancellationToken)
    {
        // The ledger's local date, not UTC's. An evening slot west of UTC fires on the
        // NEXT UTC date, so DateOnly.FromDateTime(DateTime.UtcNow) would ask about
        // tomorrow.
        var today = clock.LocalToday();

        AutoPostScanResult scan;
        await using (var scanDb = _factory.Create())
        {
            scan = await NewRepository(scanDb)
                .ScanForAutoPostAsync(
                    ledgerId, today, _limits.CatchUpDays, _limits.MaxAutoCommitDaysBefore,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Investment-shape series are excluded from auto-post in this slice. Firing one
        // means holdings and lots, and the verbatim-clone path is not the route for that
        // (FireInvestmentAsync exists for the edited case). Excluded LOUDLY via the
        // backlog event rather than silently ignored — a user who set acdays on an
        // investment reminder is owed the same honesty the UI failed to give them.
        var eligible = new List<AutoPostCandidate>();
        var excludedInvestment = 0;
        foreach (var c in scan.Due)
        {
            if (c.IsInvestmentShape) { excludedInvestment++; continue; }
            eligible.Add(c);
        }

        // The cap is applied AFTER the exclusions, so permanently-ineligible candidates
        // cannot consume it and starve the postable ones run after run.
        var deferredByCap = Math.Max(0, eligible.Count - _limits.MaxPostsPerRun);
        var toPost = eligible.Take(_limits.MaxPostsPerRun).ToList();

        var posted = 0;
        var resolvedByHand = 0;
        var failed = 0;
        Exception? firstFailure = null;

        foreach (var candidate in toPost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var fireDb = _factory.Create();
                var result = await NewRepository(fireDb)
                    .FireAsync(
                        ledgerId, candidate.ReminderId, candidate.OccurrenceDate,
                        configuredByUserId, ReminderCatchUp.LeaveEarlierOpen, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Outcome == RemindersRepository.FireOutcome.Ok)
                {
                    posted++;
                }
                else
                {
                    // A person acted on the slot between the scan and the fire — deleted
                    // the series, un-materialized it, or skipped this occurrence. None of
                    // those is the schedule failing, and counting them as failures would
                    // let ordinary human activity walk a job toward auto-disable.
                    resolvedByHand++;
                    _logger.LogInformation(
                        "Auto-post skipped series {Series} occurrence {Date}: {Outcome}.",
                        candidate.ReminderId, candidate.OccurrenceDate, result.Outcome);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                firstFailure ??= ex;
                // Full detail here and nowhere else. The outcome message reaches
                // scheduled_jobs.last_error and a configured webhook, so it carries
                // counts only — an exception message from this layer routinely names a
                // host, a database or a constraint.
                _logger.LogError(
                    ex, "Auto-post failed for series {Series} occurrence {Date}.",
                    candidate.ReminderId, candidate.OccurrenceDate);
            }
        }

        await AnnounceBacklogAsync(
                ledgerId, scan.TooOldToPost, deferredByCap, excludedInvestment, cancellationToken)
            .ConfigureAwait(false);

        if (failed == 0)
        {
            if (posted > 0 || resolvedByHand > 0)
            {
                _logger.LogInformation(
                    "Auto-post posted {Posted} occurrence(s) for ledger {Ledger}.", posted, ledgerId);
            }
            return JobRunOutcome.Ok();
        }

        // Nothing landed and something broke: THROW. This is the only path to the
        // five-strike auto-disable, because ApplyOutcome zeroes the failure counter on a
        // Degraded outcome. A job that can never be switched off by its own failures is
        // the silent-writer problem ADR-0097 exists to avoid.
        if (posted == 0)
        {
            throw new InvalidOperationException(
                $"Auto-post posted nothing: all {failed} due occurrence(s) failed. "
                + "See the server log for per-occurrence detail.",
                firstFailure);
        }

        return JobRunOutcome.Degraded(
            $"{failed} of {toPost.Count} due occurrence(s) did not post. "
            + "See the server log for per-occurrence detail.");
    }

    /// <summary>
    /// A reminders repository over one worker-owned context.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than resolved from DI, for the reason
    /// <c>FeedSyncJobHandler</c> documents at length: DI's <c>AppDbContext</c> is the APP
    /// role with the request-user interceptor attached, and on a background tick there is
    /// no <c>app.user_id</c>, so RLS hides every row and the job would report a clean run
    /// over an empty ledger forever. Both collaborators take only a context, so
    /// constructing them here is enough and makes the role explicit at the call site.
    /// </remarks>
    private RemindersRepository NewRepository(AppDbContext db) =>
        new(db, _expander, new InvestmentTransactionsRepository(db), new TransactionsRepository(db));

    /// <summary>
    /// Publish, or clear, the standing "this run left occurrences behind" warning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own subject, and that is the point.</b>
    /// <c>LedgerNotificationsEndpoints</c> derives resolution from the event key's
    /// SUBJECT — the part before the dot — so any later Info event sharing it marks the
    /// warning resolved. Published as <c>reminder-auto-post.deferred</c> this warning
    /// would be cleared by the job's own next successful run, which is exactly when the
    /// backlog is still there. Under <c>reminder-backlog</c> nothing clears it but this
    /// method, on a run that defers nothing.
    /// </para>
    /// <para>
    /// <see cref="MonitorSignal.None"/> deliberately: this is not the job's health. The
    /// run may have been a complete success and still have left a backlog outside its
    /// window, and binding it to the job's monitor would flap a dead-man's switch that is
    /// supposed to mean "the scheduler is alive".
    /// </para>
    /// </remarks>
    private async Task AnnounceBacklogAsync(
        Guid ledgerId, int tooOld, int deferredByCap, int excludedInvestment,
        CancellationToken cancellationToken)
    {
        var total = tooOld + deferredByCap + excludedInvestment;

        var (severity, key, summary) = total == 0
            ? (NotificationSeverity.Info, "reminder-backlog.clear",
               "Every reminder due for automatic posting was posted.")
            : (NotificationSeverity.Warning, "reminder-backlog.deferred",
               $"{total} reminder occurrence(s) were not posted automatically and are "
               + "waiting for you on the calendar.");

        // Counts, never dates. This is delivered to whatever URL a ledger holder
        // configured and is readable by every grant holder; a payment schedule is not
        // something to put on the wire to answer "how many".
        var detail = total == 0
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["older_than_catch_up_window"] = tooOld.ToString(),
                ["over_per_run_cap"] = deferredByCap.ToString(),
                ["investment_series_not_supported"] = excludedInvestment.ToString(),
            };

        try
        {
            await _publisher.PublishLedgerAsync(
                    ledgerId,
                    new NotificationEvent(
                        Severity: severity,
                        Topic: NotificationTopics.Scheduler,
                        EventKey: key,
                        Summary: summary,
                        Detail: detail,
                        Monitor: null,
                        Signal: MonitorSignal.None),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Announcing must never cost the job. The runner takes the same position for
            // its own per-run signal, and for the same reason: an unreachable webhook is
            // not a reason to report that transactions failed to post when they posted.
            _logger.LogError(ex, "Auto-post could not publish its backlog event.");
        }
    }
}
