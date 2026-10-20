using System.Globalization;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Notifications;

/// <summary>
/// Checks every ledger's derived projections and announces disagreement.
/// </summary>
/// <remarks>
/// The point of migration 206 was making projections checkable without rewriting
/// them; the point of this is that somebody finds out. A scrub left several accounts'
/// balances wrong for months, and it surfaced only because a person happened to run
/// a maintenance action by hand — the check existing was never the missing piece.
/// <para>
/// Ledger scope, not deployment scope (ADR-0096 D1). Drift belongs to a ledger: it
/// dies with it, and its audience is whoever holds it. That is also why this
/// publishes through <see cref="NotificationPublisher.PublishLedgerAsync"/>, which
/// honours the ledger's inherit-or-own choice, rather than writing to
/// <c>system_events</c> — doing that would have broken the scope split for
/// convenience.
/// </para>
/// </remarks>
public sealed class ConsistencyMonitor
{
    /// <summary>How often a ledger is actually re-checked.</summary>
    /// <remarks>
    /// The monitor is invoked on every scheduler tick (15 minutes) because it must
    /// not be a <c>global_scheduled_jobs</c> row — nobody should be able to switch
    /// off the thing whose purpose is noticing that the configurable jobs stopped.
    /// But being INVOKED often is not a reason to WORK often: the full check walks
    /// every balance row and every position, a six-figure row count on a real
    /// ledger, and it ran 96 times a day to announce at most once (FU-045).
    /// <para>Twice a day is the agreed cadence. The gate is
    /// <c>ledger_consistency_state.last_checked_at</c> rather than an in-memory
    /// timer, so it survives a restart — a process recycled every few minutes would
    /// otherwise check on every boot and be back where it started.</para>
    /// <para>This is the ONLY rate control. Announcements used to be throttled to
    /// 24h per ledger while the check behind them ran 96 times a day; that throttle
    /// existed solely because of the cadence, and suppressing the publish starved
    /// the dead-man's-switch of its liveness signal at the same time as it spared
    /// the human. Fixing the cadence removes the need for both, and one control is
    /// easier to reason about than two that interact.</para>
    /// </remarks>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    /// <summary>The name oversold positions are recorded and announced under (FU-097).</summary>
    public const string OversoldFinding = "oversold";


    private readonly ServiceDbContextFactory _dbFactory;
    private readonly NotificationPublisher _publisher;
    private readonly ILogger<ConsistencyMonitor> _logger;
    private readonly TimeProvider _clock;

    public ConsistencyMonitor(
        ServiceDbContextFactory dbFactory,
        NotificationPublisher publisher,
        ILogger<ConsistencyMonitor> logger,
        TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _publisher = publisher;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Check every ledger, announcing any that disagrees.</summary>
    /// <returns>How many ledgers were found to have drift.</returns>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> ledgerIds;
        await using (var db = _dbFactory.Create())
        {
            // Only ledgers actually DUE a check. Invoked every 15 minutes, this
            // used to re-verify every ledger in full each time — every balance
            // row and position, a six-figure row count on a real one, 96 times a
            // day, to announce at most once (FU-045).
            //
            // The cutoff is compared against the RECORDED last check rather than
            // an in-memory timer, so a restart does not reset it: a process
            // recycled every few minutes would otherwise check everything on
            // every boot and be exactly where it started. A ledger with no row
            // has never been checked and is always due.
            //
            // So is one whose LastCheckedAt is NULL — no current verdict, because
            // a restore replaced the data underneath it or a repair ran before any
            // check had. `NULL > cutoff` is NULL, not true, so the comparison
            // alone gets that right and anything wanting to force a re-check only
            // has to null the column. That is deliberate: an invalidation should
            // not have to know what the cadence is.
            var due = _clock.GetUtcNow().UtcDateTime - CheckEvery;
            ledgerIds = await db.Ledgers.AsNoTracking()
                .Where(l => !db.LedgerConsistencyStates
                               .Any(s => s.LedgerId == l.Id && s.LastCheckedAt > due))
                .Select(l => l.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var drifted = 0;
        foreach (var ledgerId in ledgerIds)
        {
            // Per-ledger scope, and per-ledger failure isolation: one broken ledger
            // must not stop the others being checked.
            try
            {
                if (await CheckOneAsync(ledgerId, cancellationToken).ConfigureAwait(false))
                    drifted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Consistency check failed for ledger {LedgerId}; continuing.", ledgerId);
            }
        }

        return drifted;
    }

    /// <summary>
    /// Ping once a day while healthy — the liveness signal a dead-man's switch needs,
    /// and the all-clear that marks an earlier drift warning resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs, one event, because they want the same row. A switch bound to
    /// <c>consistency</c> needs a ping on a predictable cadence or healthchecks marks the
    /// check down from inactivity while everything is fine — a switch that cries wolf
    /// permanently. And an earlier <c>consistency.drift</c> warning needs a later info
    /// event on the same subject before the in-app list will show it as resolved.
    /// </para>
    /// <para>
    /// NOT throttled any more, and the drift path is not either — the two are
    /// symmetric on purpose. The 24h throttle here existed because the monitor
    /// re-checked on every scheduler tick, so an unthrottled ping would have
    /// written ~96 rows per ledger per day into a table nothing prunes below a
    /// year. <see cref="CheckEvery"/> now rate-limits the CHECK itself, so this
    /// writes twice a day; a second throttle on top would be a rule doing nothing
    /// but obscuring which one governs.
    /// </para>
    /// <para>
    /// Suppressing a publish is never free, which is the deeper reason to have one
    /// control rather than two. A Heartbeat subscriber takes the Signal off this
    /// event; everyone else is filtered by topic and severity. Skipping the publish
    /// starves the switch at the same moment it spares the human, and those two
    /// want opposite things — the human wants silence when nothing changed, the
    /// switch reads silence as the checker being dead.
    /// </para>
    /// <para>
    /// It replaced a transition-only version that fired just once, on drift -> healthy.
    /// That was enough to resolve a warning and useless as a heartbeat: a check pinged
    /// only when something changes is indistinguishable from a check whose app has died.
    /// </para>
    /// </remarks>
    private async Task AnnounceHealthyAsync(Guid ledgerId, CancellationToken cancellationToken)
    {
        await _publisher.PublishLedgerAsync(ledgerId, new NotificationEvent(
            Severity: NotificationSeverity.Info,
            Topic: NotificationTopics.Consistency,
            EventKey: "consistency.ok",
            Summary: "Projections agree with the transactions.",
            Detail: null,
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.Success),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CheckOneAsync(Guid ledgerId, CancellationToken cancellationToken)
    {
        // Built by hand over the SERVICE-role context, not resolved from DI.
        //
        // Resolving it from a scope hands back the repository wired to the request
        // AppDbContext, which is the RLS-bound coffer_app connection. There is no user
        // behind a scheduler tick, so AppUserDbConnectionInterceptor leaves
        // app.user_id unset, current_app_user_id() is null, and every ledger-scoped
        // read returns ZERO ROWS. Stored and walked would then both be empty, compare
        // equal, and this monitor would report every ledger healthy while seeing
        // nothing — a drift detector that cannot detect drift, failing in exactly the
        // silent way it exists to prevent. The previous comment here claimed it read
        // through the service role; it did not.
        //
        // Both collaborators take only an AppDbContext (and the clock), so constructing
        // them over the service context is enough and makes the role explicit at the
        // call site.
        await using var db = _dbFactory.Create();
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db, _clock), new HoldingsRecomputeService(db));

        var report = await consistency.CheckAsync(ledgerId, cancellationToken)
                                      .ConfigureAwait(false);

        var unhealthy = report.Projections.Where(p => !p.Healthy).ToList();
        // Oversold positions are findings without a projection (FU-097); recorded under
        // their own name so the state row says what is wrong.
        var failing = unhealthy.Select(p => p.Projection)
                               .Concat(report.Oversold.Count > 0 ? [OversoldFinding] : [])
                               .OrderBy(n => n, StringComparer.Ordinal)
                               .ToArray();

        await RecordAsync(db, ledgerId, report.Healthy, failing, cancellationToken)
            .ConfigureAwait(false);

        // EVERY pass announces, in both states, with no throttle and no
        // change-detection.
        //
        // One publish serves two audiences with different needs, and the
        // publisher already separates them: a Heartbeat subscriber takes the
        // Signal, everyone else is filtered by topic and severity floor. So
        // suppressing a publish starves the dead-man's-switch at the same time as
        // it spares the human — and a drifted ledger going quiet would make the
        // switch report the MONITOR as down, losing the distinction between "your
        // ledger is corrupt" and "the checker stopped running", which is the one
        // thing the switch exists to tell apart.
        //
        // Announcing only on change had exactly that bug, asymmetrically: health
        // kept a heartbeat and drift did not. The cadence is what made repetition
        // a problem — 96 identical announcements a day — and at twice a day
        // "still drifted" twice is true and wanted.
        if (report.Healthy)
        {
            await AnnounceHealthyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var detail = unhealthy.ToDictionary(
            p => p.Projection,
            p => p.MismatchedCount.ToString(CultureInfo.InvariantCulture));
        var parts = unhealthy.Select(p => $"{p.Projection}: {p.MismatchedCount} of {p.Checked}"
                                          + " disagree with the transactions").ToList();
        if (report.Oversold.Count > 0)
        {
            detail[OversoldFinding] = report.Oversold.Count.ToString(CultureInfo.InvariantCulture);
            // Named, because the fix is by hand and per position: there is no repair.
            parts.Add($"{report.Oversold.Count} position(s) sold more shares than they held, "
                      + "so their gain is overstated ("
                      + string.Join("; ", report.Oversold.Take(3).Select(o => $"{o.Account} / {o.Security}"))
                      + (report.Oversold.Count > 3 ? "; ..." : "")
                      + "). Correct the sale or enter the missing purchase");
        }

        // Warning, not critical. Drift is real and wants fixing, but it is slow
        // damage with a repair button — unlike a missing backup, which is data that
        // does not come back. Reserving critical for the irreversible keeps the word
        // meaning something.
        await _publisher.PublishLedgerAsync(ledgerId, new NotificationEvent(
            Severity: NotificationSeverity.Warning,
            Topic: NotificationTopics.Consistency,
            EventKey: "consistency.drift",
            Summary: string.Join(". ", parts) + ".",
            Detail: detail,
            // Without these a switch bound to consistency would be pinged healthy every
            // day and NEVER told about drift — the check would stay green through exactly
            // the condition it was bound to catch.
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.Failure),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Write what this pass found. Every pass, healthy or not (FU-012): a verdict
    /// without a date cannot distinguish "fine an hour ago" from "fine last
    /// August", and that distinction is the point of recording it at all.
    /// </summary>
    /// <remarks>
    /// <paramref name="db"/> is the SERVICE context. The table has a read policy
    /// for the app role and no write policy, so this path is the only one that
    /// can write it — an app-role write is refused by RLS default-deny rather
    /// than by nobody happening to try.
    /// <para><c>LastRepairedAt</c> is deliberately untouched here: that a ledger
    /// was once repaired is history, and a green check does not retract it.</para>
    /// </remarks>
    private async Task RecordAsync(
        AppDbContext db,
        Guid ledgerId,
        bool healthy,
        string[] failing,
        CancellationToken cancellationToken)
    {
        var row = await db.LedgerConsistencyStates
            .FirstOrDefaultAsync(r => r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            db.LedgerConsistencyStates.Add(new LedgerConsistencyStateRow
            {
                LedgerId = ledgerId,
                LastCheckedAt = now,
                Healthy = healthy,
                FailingProjections = failing,
            });
        }
        else
        {
            row.LastCheckedAt = now;
            row.Healthy = healthy;
            row.FailingProjections = failing;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
