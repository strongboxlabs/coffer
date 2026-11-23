using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Notifications;

/// <summary>
/// Checks every ledger's derived projections and announces disagreement.
/// </summary>
/// <remarks>
/// The point of migration 206 was making projections checkable without rewriting
/// them; the point of this is that somebody finds out. A check nobody runs lets drift —
/// a balance left wrong by a bad scrub, say — sit unnoticed until a person happens to
/// run a maintenance action by hand; the check existing was never the missing piece.
/// <para>
/// Ledger scope, not deployment scope (ADR-0096 D1). Drift belongs to a ledger: it
/// dies with it, and its audience is whoever holds it. That is also why this
/// publishes through <see cref="NotificationPublisher.PublishLedgerAsync"/>, which
/// honours the ledger's inherit-or-own choice, rather than writing to
/// <c>system_events</c>, which would break the scope split.
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
    /// every balance row and every position, a count that grows with the
    /// ledger's whole history, and running it on every tick would do that walk 96
    /// times a day to announce at most once (FU-045).
    /// <para>So a ledger is re-checked at most every 12 hours: drift surfaces
    /// within about half a day, and the walk runs on two ticks a day rather than all of
    /// them. The gate is
    /// <c>ledger_consistency_state.last_checked_at</c> rather than an in-memory
    /// timer, so it survives a restart — a process recycled every few minutes would
    /// otherwise check on every boot and be back where it started.</para>
    /// <para>This is the ONLY rate control. A separate announcement throttle would
    /// exist only to compensate for a too-frequent check, and suppressing the publish
    /// starves the dead-man's-switch of its liveness signal at the same time as it
    /// spares the human. The cadence makes a throttle unnecessary, and one control is
    /// easier to reason about than two that interact.</para>
    /// <para>A ledger is due when its last check OR its last announcement is older than
    /// this. A check a person runs records its verdict like a pass but announces only a
    /// change (<see cref="ConsistencyVerdicts.RecordCheckAsync"/>), so a recent check alone
    /// does not mean a recent ping: gated on the check alone, a person checking every few
    /// hours would keep the monitor from ever announcing, and a dead-man's switch bound to
    /// consistency would report the checker down.</para>
    /// </remarks>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    /// <summary>The name oversold positions are recorded and announced under (FU-097).</summary>
    public const string OversoldFinding = ConsistencyVerdicts.OversoldFinding;

    private readonly ServiceDbContextFactory _dbFactory;
    private readonly ConsistencyVerdicts _verdicts;
    private readonly ILogger<ConsistencyMonitor> _logger;
    private readonly TimeProvider _clock;

    public ConsistencyMonitor(
        ServiceDbContextFactory dbFactory,
        NotificationPublisher publisher,
        ILogger<ConsistencyMonitor> logger,
        TimeProvider clock)
    {
        _dbFactory = dbFactory;
        // The one recording path, built over the same collaborators: what a pass writes
        // and announces is what a person's check and a repair write and announce.
        _verdicts = new ConsistencyVerdicts(dbFactory, publisher, clock);
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
            // would otherwise re-verify every ledger in full each time — every
            // balance row and position of its whole history, 96 times a day
            // (FU-045).
            //
            // The cutoff is compared against the RECORDED last check rather than
            // an in-memory timer, so a restart does not reset it: a process
            // recycled every few minutes would otherwise check everything on
            // every boot and be exactly where it started. A ledger with no row
            // has never been checked and is always due.
            //
            // So is one whose LastCheckedAt is NULL — no current verdict, because
            // a restore replaced the data underneath it. `NULL > cutoff` is NULL,
            // not true, so the comparison alone gets that right and anything
            // wanting to force a re-check only has to null the column. That is
            // deliberate: an invalidation should not have to know what the cadence
            // is.
            //
            // And so is one whose verdict was last ANNOUNCED before the cutoff,
            // however recent its check (see CheckEvery): a person's check that found
            // nothing new records the time and sends nothing, and the switch still
            // needs its ping.
            var due = _clock.GetUtcNow().UtcDateTime - CheckEvery;
            ledgerIds = await db.Ledgers.AsNoTracking()
                .Where(l => !(db.LedgerConsistencyStates
                                  .Any(s => s.LedgerId == l.Id && s.LastCheckedAt > due)
                              && db.LedgerEvents
                                  .Any(e => e.LedgerId == l.Id
                                            && ConsistencyVerdicts.VerdictKeys.Contains(e.EventKey)
                                            && e.OccurredAt > due)))
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
    /// Check one ledger and record what the check found, announcing it on every pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EVERY pass announces, in both states, with no throttle and no change-detection
    /// (ConsistencyVerdicts.RecordCheckAsync). One publish serves two audiences with
    /// different needs, and the publisher already separates them: a Heartbeat subscriber
    /// takes the Signal, everyone else is filtered by topic and severity floor. So
    /// suppressing a publish starves the dead-man's-switch at the same time as it spares
    /// the human, and a drifted ledger going quiet would make the switch report the
    /// MONITOR as down, losing the distinction between "your ledger is corrupt" and "the
    /// checker stopped running", which is the one thing the switch exists to tell apart.
    /// </para>
    /// <para>
    /// The healthy ping is also the all-clear: an earlier <c>consistency.drift</c> warning
    /// needs a later info event on the same subject before the in-app list shows it
    /// resolved. Repetition is a problem only at a high cadence (every 15 minutes is 96
    /// identical announcements a day); at twice a day "still drifted" twice is true and
    /// wanted, and <see cref="CheckEvery"/> is what keeps it at twice a day.
    /// </para>
    /// </remarks>
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
        // silent way it exists to prevent.
        //
        // Both collaborators take only an AppDbContext (and the clock), so constructing
        // them over the service context is enough and makes the role explicit at the
        // call site.
        LedgerConsistencyReport report;
        // When the check began to read the ledger: the verdict's time, which a record
        // that lands after a newer one's does not overwrite.
        var checkedAt = _clock.GetUtcNow().UtcDateTime;
        await using (var db = _dbFactory.Create())
        {
            var consistency = new LedgerConsistencyRepository(
                db, new RegisterRepository(db, _clock), new HoldingsRecomputeService(db), _clock);

            // No caller's date: a background run takes the register's fallback.
            report = await consistency.CheckAsync(ledgerId, today: null, cancellationToken)
                                      .ConfigureAwait(false);
        }

        await _verdicts.RecordCheckAsync(ledgerId, report, ConsistencyCheckOrigin.Monitor, checkedAt, cancellationToken)
            .ConfigureAwait(false);
        return !report.Healthy;
    }
}
