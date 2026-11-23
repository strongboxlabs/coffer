using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Quotes.MarketSeries;
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
/// <para>
/// <b>A fire refused because the ledger was being written is deferred, not failed.</b>
/// The fire takes the ledger's write lock first (migration 275), and another write held
/// past the wait refuses it with nothing written: the occurrence is still due, the next
/// run posts it, and the backlog announcement counts it meanwhile. The run reports it as
/// Degraded (DidNothing when nothing posted), never a throw, so a busy ledger never walks
/// the job toward the auto-disable.
/// </para>
/// </remarks>
public sealed class ReminderAutoPostJobHandler : IScheduledJobHandler
{
    private readonly ServiceDbContextFactory _factory;
    private readonly RecurrenceExpander _expander;
    private readonly NotificationPublisher _publisher;
    private readonly ReminderAutoPostLimits _limits;
    private readonly ILogger<ReminderAutoPostJobHandler> _logger;
    private readonly TimeProvider _clock;
    private readonly MarketSeriesBackfill? _backfill;

    /// <param name="backfill">Started when the run's posts move a needed series' start
    /// earlier (ADR-0114 D11). Null only in tests that construct the handler by hand and
    /// assert nothing of the market series.</param>
    public ReminderAutoPostJobHandler(
        ServiceDbContextFactory factory,
        RecurrenceExpander expander,
        NotificationPublisher publisher,
        ILogger<ReminderAutoPostJobHandler> logger,
        TimeProvider clock,
        ReminderAutoPostLimits? limits = null,
        MarketSeriesBackfill? backfill = null)
    {
        _factory = factory;
        _expander = expander;
        _publisher = publisher;
        _logger = logger;
        _clock = clock;
        _limits = limits ?? ReminderAutoPostLimits.Default;
        _backfill = backfill;
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
        NeededStarts? startsBefore = null;
        await using (var scanDb = _factory.Create())
        {
            scan = await NewRepository(scanDb)
                .ScanForAutoPostAsync(
                    ledgerId, today, _limits.CatchUpDays, _limits.MaxAutoCommitDaysBefore,
                    cancellationToken)
                .ConfigureAwait(false);
            // ADR-0114 D11: an occurrence posted before the ledger's earliest day moves the
            // starts earlier, as a fire by hand does; read before the posts that can move them.
            if (_backfill is not null && scan.Due.Count > 0)
                startsBefore = await NeededFxPairs.StartsAsync(scanDb, ledgerId, cancellationToken)
                    .ConfigureAwait(false);
        }

        // Investment-shape series are excluded from auto-post in this slice. Firing one
        // means holdings and lots, and the verbatim-clone path is not the route for that
        // (FireInvestmentAsync exists for the edited case). Excluded LOUDLY via the
        // backlog event rather than silently ignored — a user who set acdays on an
        // investment reminder is owed the same honesty the UI failed to give them.
        //
        // A series whose template has no lines is excluded the same way. Firing it is
        // refused, so trying would fail the run every time, and it is announced by name
        // because only the user can fix it: give it its lines, or delete it.
        //
        // So is a bank series whose template has a line on an inactive account: a new
        // transaction never lands on a retired account, so firing it is refused too, and
        // only the user can resolve it, by reactivating the account or editing the
        // reminder. An investment series is already excluded as investment, whatever its
        // accounts, so it is not also named here.
        //
        // So is a series whose fire would write a figure finer than its currency, a template
        // line's own or a loan's stored escrow or fixed payment (ADR-0114 D15): the fire
        // refuses it, so it can never post until the reminder or the loan terms are edited,
        // and it is named because only the user can edit it to fit.
        //
        // So is a managed loan series whose payment would cross currencies (ADR-0114 D13,
        // ADR-0097): the fire refuses it, and it is named, as one that uses an inactive
        // account is, because only the user can resolve it.
        var eligible = new List<AutoPostCandidate>();
        var excludedInvestment = 0;
        var excludedNoLines = 0;
        var noLinesSeries = new Dictionary<Guid, string?>();
        var excludedInactive = 0;
        var inactiveSeries = new Dictionary<Guid, string?>();
        var excludedUnfit = 0;
        var unfitSeries = new Dictionary<Guid, string?>();
        var excludedLoanAcross = 0;
        var loanAcrossSeries = new Dictionary<Guid, string?>();
        foreach (var c in scan.Due)
        {
            switch (c.ExcludedReason)
            {
                case AutoPostExclusion.NoLines:
                    excludedNoLines++;
                    noLinesSeries[c.ReminderId] = c.Payee;
                    break;
                case AutoPostExclusion.Investment:
                    excludedInvestment++;
                    break;
                case AutoPostExclusion.InactiveAccount:
                    excludedInactive++;
                    inactiveSeries[c.ReminderId] = c.Payee;
                    break;
                case AutoPostExclusion.LoanAcrossCurrencies:
                    excludedLoanAcross++;
                    loanAcrossSeries[c.ReminderId] = c.Payee;
                    break;
                case AutoPostExclusion.TemplateAmountDoesNotFit:
                    excludedUnfit++;
                    unfitSeries[c.ReminderId] = c.Payee;
                    break;
                default:
                    eligible.Add(c);
                    break;
            }
        }

        // The cap is applied AFTER the exclusions, so permanently-ineligible candidates
        // cannot consume it and starve the postable ones run after run.
        var deferredByCap = Math.Max(0, eligible.Count - _limits.MaxPostsPerRun);
        var toPost = eligible.Take(_limits.MaxPostsPerRun).ToList();

        var posted = 0;
        var resolvedByHand = 0;
        var refusedUnfit = 0;
        var refusedLoanAcross = 0;
        var estimateUnavailable = 0;
        var refusedBusy = 0;
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
                        ledgerId, candidate.ReminderId, candidate.OccurrenceDate, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Outcome == RemindersRepository.FireOutcome.Ok)
                {
                    posted++;
                }
                else if (result.Outcome == RemindersRepository.FireOutcome.TemplateAmountDoesNotFit)
                {
                    // A figure stored before each currency kept its own places, and finer than
                    // its currency allows (ADR-0114 D15). The fire is a new write, so it is
                    // refused: nothing posts a figure that does not fit. The scan sets such a
                    // series aside by the fire's own predicate, so this is the backstop for a
                    // series that changed between the scan and the fire. Not the schedule
                    // failing, so not counted toward the auto-disable, and not a person
                    // acting on the slot either: it is counted with the excluded ones, stays
                    // due until it is edited, and the consistency check lists it. It is
                    // named with the excluded ones too.
                    refusedUnfit++;
                    unfitSeries[candidate.ReminderId] = candidate.Payee;
                    _logger.LogWarning(
                        "Auto-post refused series {Series} occurrence {Date}: a template amount has more "
                        + "decimal places than {Currency} allows.",
                        candidate.ReminderId, candidate.OccurrenceDate, result.UnfitCurrencyCode);
                }
                else if (result.Outcome == RemindersRepository.FireOutcome.LoanAcrossCurrencies)
                {
                    // The backstop for a managed loan series whose account changed currency
                    // between the scan and the fire (ADR-0114 D13): refused, nothing written.
                    // Like the places backstop above, not the schedule failing and not a
                    // person acting on the slot: counted and named with the excluded ones,
                    // and it stays due.
                    refusedLoanAcross++;
                    loanAcrossSeries[candidate.ReminderId] = candidate.Payee;
                    _logger.LogWarning(
                        "Auto-post refused series {Series} occurrence {Date}: the loan is in {Loan} and the "
                        + "payment would use an account in {Other}.",
                        candidate.ReminderId, candidate.OccurrenceDate,
                        result.LoanCurrencies?.LoanCurrency, result.LoanCurrencies?.OtherCurrency);
                }
                else if (result.Outcome == RemindersRepository.FireOutcome.EstimateUnavailableForFire)
                {
                    // The series estimates its amount and has no estimate for this
                    // occurrence: no history yet, or, on a transfer between currencies, no
                    // derived rate for its due date (ADR-0114 D13, ADR-0097). Nothing is
                    // written and the occurrence stays due for the next run. Not a person
                    // acting on the slot, and not the schedule failing either, so never a
                    // step toward the auto-disable: it is counted under its own key.
                    estimateUnavailable++;
                    _logger.LogInformation(
                        "Auto-post left series {Series} occurrence {Date} due: its estimate is unavailable.",
                        candidate.ReminderId, candidate.OccurrenceDate);
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
            catch (Exception ex) when (LedgerWriteLock.IsBusy(ex))
            {
                // Another write to the ledger held its write lock past the wait (migration
                // 275). The fire's transaction rolled back, so the occurrence is neither
                // posted nor marked: it is still due, and the next run posts it. Ordinary
                // activity on the ledger, not the schedule failing, so it is not counted
                // toward the auto-disable either.
                refusedBusy++;
                _logger.LogInformation(
                    ex, "Auto-post deferred series {Series} occurrence {Date}: the ledger was being written.",
                    candidate.ReminderId, candidate.OccurrenceDate);
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

        // As the schedule's user, under that user's consent, as the scheduled quote refresh
        // fetches. A read that fails is logged and starts nothing: it never costs the run.
        if (posted > 0 && _backfill is not null && startsBefore is { } before)
        {
            await using var startsDb = _factory.Create();
            await _backfill.StartIfEarlierAsync(
                    startsDb, ledgerId, configuredByUserId, before, cancellationToken, "scheduled")
                .ConfigureAwait(false);
        }

        await AnnounceBacklogAsync(
                ledgerId, scan.TooOldToPost, deferredByCap, refusedBusy, excludedInvestment,
                excludedNoLines, noLinesSeries.Values.ToList(),
                excludedInactive, inactiveSeries.Values.ToList(),
                excludedUnfit + refusedUnfit, unfitSeries.Values.ToList(),
                excludedLoanAcross + refusedLoanAcross, loanAcrossSeries.Values.ToList(),
                estimateUnavailable,
                cancellationToken)
            .ConfigureAwait(false);

        if (failed == 0)
        {
            if (posted > 0 || resolvedByHand > 0 || refusedUnfit > 0 || refusedLoanAcross > 0)
            {
                _logger.LogInformation(
                    "Auto-post posted {Posted} occurrence(s) for ledger {Ledger}; {Unfit} refused for a template "
                    + "amount that does not fit its currency; {LoanAcross} refused for a loan payment across "
                    + "currencies.", posted, ledgerId, refusedUnfit, refusedLoanAcross);
            }
            return SettledOutcome(posted, refusedBusy, refusedUnfit, refusedLoanAcross, toPost.Count);
        }

        // Nothing landed and something broke: THROW. This is the only path to the
        // five-strike auto-disable, because ApplyOutcome zeroes the failure counter on a
        // Degraded outcome. A job that can never be switched off by its own failures is
        // the silent-writer problem ADR-0097 exists to avoid.
        if (posted == 0)
        {
            throw new InvalidOperationException(
                $"Auto-post posted nothing: {NotPosted(failed, refusedBusy, toPost.Count)} "
                + "See the server log for per-occurrence detail.",
                firstFailure);
        }

        return JobRunOutcome.Degraded(
            $"{NotPosted(failed, refusedBusy, toPost.Count)} See the server log for per-occurrence detail.");
    }

    /// <summary>
    /// The outcome of a run in which no fire failed: Ok when every fire it tried landed,
    /// else Degraded with counts, and DidNothing when nothing posted at all, which leaves
    /// <c>last_success_at</c> where it was.
    /// </summary>
    /// <remarks>
    /// Three refusals leave part of the work undone without a failure, so none is a throw
    /// toward the auto-disable: a fire deferred because the ledger was being written (the
    /// next run posts it), the backstop refusal of a figure that does not fit its currency
    /// (ADR-0114 D15), which stays due until it is edited, and the backstop refusal of a
    /// managed loan payment across currencies (ADR-0114 D13), which stays due until its
    /// accounts are in the loan's currency. A series the scan set aside is not counted
    /// here: like every exclusion it is announced in the backlog event and is not the
    /// run's health.
    /// </remarks>
    internal static JobRunOutcome SettledOutcome(
        int posted, int refusedBusy, int refusedUnfit, int refusedLoanAcross, int due)
    {
        if (refusedBusy == 0 && refusedUnfit == 0 && refusedLoanAcross == 0) return JobRunOutcome.Ok();

        var message = string.Join(" ", new[]
        {
            refusedBusy == 0 ? null
                : $"{refusedBusy} of {due} due occurrence(s) were not posted because another change "
                  + "to this ledger was still being saved. The next run posts them.",
            refusedUnfit == 0 ? null
                : $"{refusedUnfit} of {due} due occurrence(s) were not posted because a figure has more "
                  + "decimal places than its currency uses. They stay due until the figure is edited to fit.",
            refusedLoanAcross == 0 ? null
                : $"{refusedLoanAcross} of {due} due occurrence(s) were not posted because a loan payment "
                  + "would use an account in another currency than the loan's. They stay due until it does not.",
        }.Where(s => s is not null));
        return posted > 0 ? JobRunOutcome.Degraded(message) : JobRunOutcome.DidNothing(message);
    }

    /// <summary>
    /// How many of the run's <paramref name="due"/> occurrences failed, and how many were
    /// deferred because the ledger was being written, for a run in which at least one
    /// failed.
    /// </summary>
    /// <remarks>Both counts, because they ask different things of the reader: a failure is
    /// in the server log, and a deferred occurrence is still due and the next run posts it.
    /// An occurrence a person acted on between the scan and the fire is neither.</remarks>
    private static string NotPosted(int failed, int deferredBusy, int due) =>
        $"{failed} of {due} due occurrence(s) failed"
        + (deferredBusy == 0
            ? "."
            : $", and {deferredBusy} {(deferredBusy == 1 ? "was" : "were")} deferred because another change "
              + $"to this ledger was still being saved (the next run posts {(deferredBusy == 1 ? "it" : "them")}).");

    /// <summary>
    /// A reminders repository over one worker-owned context.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than resolved from DI, for the reason
    /// <c>FeedSyncJobHandler</c> documents at length: DI's <c>AppDbContext</c> is the APP
    /// role with the request-user interceptor attached, and on a background tick there is
    /// no <c>app.user_id</c>, so RLS hides every row and the job would report a clean run
    /// over an empty ledger forever. Both collaborators take only a context (and the clock), so
    /// constructing them here is enough and makes the role explicit at the call site.
    /// </remarks>
    private RemindersRepository NewRepository(AppDbContext db) =>
        new(db, _expander, new InvestmentTransactionsRepository(db, _clock), new TransactionsRepository(db, _clock), _clock);

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
        Guid ledgerId, int tooOld, int deferredByCap, int deferredBusy, int excludedInvestment,
        int excludedNoLines, IReadOnlyList<string?> noLinesNames,
        int excludedInactive, IReadOnlyList<string?> inactiveNames,
        int notFitting, IReadOnlyList<string?> notFittingNames,
        int loanAcross, IReadOnlyList<string?> loanAcrossNames,
        int estimateUnavailable,
        CancellationToken cancellationToken)
    {
        var total = tooOld + deferredByCap + deferredBusy + excludedInvestment + excludedNoLines + excludedInactive
                    + notFitting + loanAcross + estimateUnavailable;

        var (severity, key, summary) = total == 0
            ? (NotificationSeverity.Info, "reminder-backlog.clear",
               "Every reminder due for automatic posting was posted.")
            : (NotificationSeverity.Warning, "reminder-backlog.deferred",
               $"{total} reminder occurrence(s) were not posted automatically and are "
               + "waiting for you on the calendar."
               + NamedSentence(noLinesNames, "they have no lines",
                   "Edit each to give it its lines, or delete it.")
               + NamedSentence(inactiveNames, "they use an inactive account",
                   "Reactivate the account, or edit each to use an active one.")
               // ADR-0097's 2026-11-14 amendment: named as those that use an inactive account are.
               + NamedSentence(loanAcrossNames, "they use an account in another currency than their loan's",
                   "Use accounts in the loan's currency, or enter each payment as a transfer with both amounts.")
               // ADR-0114 D15 (decided by the owner, 2026-11-18): named as the others are.
               + NamedSentence(notFittingNames, "a figure has more decimal places than its currency uses",
                   "Edit each reminder, or its loan's terms, so every figure fits its currency."));

        // Counts, never dates. This is delivered to whatever URL a ledger holder
        // configured and is readable by every grant holder; a payment schedule is not
        // something to put on the wire to answer "how many".
        var detail = total == 0
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["older_than_catch_up_window"] = tooOld.ToString(),
                ["over_per_run_cap"] = deferredByCap.ToString(),
                // Refused by the ledger write lock's busy refusal this run; still due, and
                // posted by the next run.
                ["ledger_busy"] = deferredBusy.ToString(),
                ["investment_series_not_supported"] = excludedInvestment.ToString(),
                ["template_has_no_lines"] = excludedNoLines.ToString(),
                ["template_uses_inactive_account"] = excludedInactive.ToString(),
                // Set aside by the scan, or refused by the fire's backstop, because a figure
                // the fire would write has more places than its currency (ADR-0114 D15).
                ["template_amount_does_not_fit"] = notFitting.ToString(),
                // Set aside by the scan, or refused by the fire's backstop, because a managed
                // loan payment would use an account in another currency than the loan's
                // (ADR-0114 D13, ADR-0097).
                ["managed_loan_across_currencies"] = loanAcross.ToString(),
                // Refused by the fire because the series' estimate is unavailable for the
                // occurrence: no history yet, or no derived rate for its due date between
                // currencies (ADR-0114 D13, ADR-0097). Still due, tried by the next run.
                ["estimate_unavailable"] = estimateUnavailable.ToString(),
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

    /// <summary>
    /// The sentence naming the reminders that cannot be posted for one reason (their
    /// template has no lines, uses an inactive account, is a loan payment across
    /// currencies, or has a figure finer than its currency) and what the user can do, or
    /// nothing when there are none.
    /// </summary>
    /// <remarks>
    /// Named, unlike the other reasons, because the fix is by hand and per reminder: the
    /// calendar shows the occurrence but not why it never posts. It is the name the
    /// reminders list shows, with no date or amount, so the payment schedule the counts
    /// above keep off the wire stays off it; the consistency monitor names positions on the
    /// same channel for the same reason. At most three are listed per reason, so a ledger
    /// with many does not turn the summary into a list.
    /// </remarks>
    private static string NamedSentence(IReadOnlyList<string?> names, string because, string fix)
    {
        if (names.Count == 0) return string.Empty;
        var shown = names
            .Select(n => string.IsNullOrWhiteSpace(n) ? "Untitled reminder" : n)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(3)
            .Select(n => $"\"{n}\"");
        return $" {names.Count} reminder(s) cannot be posted because {because} ("
               + string.Join("; ", shown) + (names.Count > 3 ? "; ..." : "")
               + $"). {fix}";
    }
}
