using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Notifications;

/// <summary>
/// Checks every ledger's derived projections and announces disagreement.
/// </summary>
/// <remarks>
/// The point of migration 206 was making projections checkable without rewriting
/// them; the point of this is that somebody finds out. A scrub left three accounts'
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
    /// <summary>
    /// How long a drift report stays quiet after being raised, per ledger.
    /// </summary>
    /// <remarks>
    /// Drift is a standing condition: still drifted on the next tick, and every tick
    /// after, until somebody repairs it. Announcing it each time would mute the
    /// channel within a day, which is the cry-wolf failure ADR-0096 D4 warns about
    /// reaching us through repetition rather than severity.
    /// </remarks>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(24);

    private readonly ServiceDbContextFactory _dbFactory;
    private readonly NotificationPublisher _publisher;
    private readonly ILogger<ConsistencyMonitor> _logger;

    public ConsistencyMonitor(
        ServiceDbContextFactory dbFactory,
        NotificationPublisher publisher,
        ILogger<ConsistencyMonitor> logger)
    {
        _dbFactory = dbFactory;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Check every ledger, announcing any that disagrees.</summary>
    /// <returns>How many ledgers were found to have drift.</returns>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        List<Guid> ledgerIds;
        await using (var db = _dbFactory.Create())
        {
            ledgerIds = await db.Ledgers.AsNoTracking()
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
    /// THROTTLED TO A DAY, which is what makes this affordable. The monitor runs on every
    /// scheduler tick — every fifteen minutes — so an unthrottled ping would write ~96
    /// rows per ledger per day into a table nothing prunes below a year. Daily costs one
    /// row per ledger, the same as every scheduled job's success, and buys nothing less:
    /// healthchecks grace periods are measured in hours, so fifteen-minute granularity
    /// would be precision no alert threshold can use.
    /// </para>
    /// <para>
    /// It replaced a transition-only version that fired just once, on drift -> healthy.
    /// That was enough to resolve a warning and useless as a heartbeat: a check pinged
    /// only when something changes is indistinguishable from a check whose app has died.
    /// </para>
    /// </remarks>
    private async Task AnnounceHealthyAsync(Guid ledgerId, CancellationToken cancellationToken)
    {
        await _publisher.PublishLedgerThrottledAsync(ledgerId, new NotificationEvent(
            Severity: NotificationSeverity.Info,
            Topic: NotificationTopics.Consistency,
            EventKey: "consistency.ok",
            Summary: "Projections agree with the transactions.",
            Detail: null,
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.Success),
            RepeatAfter, cancellationToken).ConfigureAwait(false);
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
        // Both collaborators take only an AppDbContext, so constructing them over the
        // service context is enough and makes the role explicit at the call site.
        await using var db = _dbFactory.Create();
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db), new HoldingsRecomputeService(db));

        var report = await consistency.CheckAsync(ledgerId, cancellationToken)
                                      .ConfigureAwait(false);
        if (report.Healthy)
        {
            await AnnounceHealthyAsync(ledgerId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var unhealthy = report.Projections.Where(p => !p.Healthy).ToList();
        var detail = unhealthy.ToDictionary(
            p => p.Projection,
            p => p.MismatchedCount.ToString());

        // Warning, not critical. Drift is real and wants fixing, but it is slow
        // damage with a repair button — unlike a missing backup, which is data that
        // does not come back. Reserving critical for the irreversible keeps the word
        // meaning something.
        return await _publisher.PublishLedgerThrottledAsync(ledgerId, new NotificationEvent(
            Severity: NotificationSeverity.Warning,
            Topic: NotificationTopics.Consistency,
            EventKey: "consistency.drift",
            Summary: string.Join(", ", unhealthy.Select(
                         p => $"{p.Projection}: {p.MismatchedCount} of {p.Checked}"))
                     + " disagree with the transactions.",
            Detail: detail,
            // Without these a switch bound to consistency would be pinged healthy every
            // day and NEVER told about drift — the check would stay green through exactly
            // the condition it was bound to catch.
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.Failure),
            RepeatAfter, cancellationToken).ConfigureAwait(false);
    }
}
