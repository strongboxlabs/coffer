using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Notifications;

/// <summary>
/// The bus: records a deployment-scope event, then routes it to every subscriber
/// that asked for it.
/// </summary>
/// <remarks>
/// Publication is one path for all subscribers (ADR-0096 D2). Storage and
/// authorization are per-scope, but routing is not — otherwise every delivery target
/// re-derives "is this system-wide?" and the scope split leaks into the providers.
/// <para>
/// Persist first, deliver second, and record the delivery outcome: the event row is
/// the source of truth, and a provider that has quietly stopped accepting pings must
/// be visible in <c>notification_subscribers</c> rather than only in a log nobody
/// reads. Reproducing silent failure inside the thing built to end silent failure
/// would be a special kind of defeat.
/// </para>
/// <para>
/// Runs over the SERVICE role: a system event has no user behind it, and under the
/// RLS app role a background publish would see nothing and write nothing.
/// </para>
/// </remarks>
public sealed class NotificationPublisher
{
    /// <summary>Truncation ceiling for a recorded provider error.</summary>
    private const int MaxErrorLength = 500;

    private readonly ServiceDbContextFactory _dbFactory;
    private readonly LedgerKeyService _keys;
    private readonly IEnumerable<INotificationSubscriber> _subscribers;
    private readonly ILogger<NotificationPublisher> _logger;

    public NotificationPublisher(
        ServiceDbContextFactory dbFactory,
        LedgerKeyService keys,
        IEnumerable<INotificationSubscriber> subscribers,
        ILogger<NotificationPublisher> logger)
    {
        _dbFactory = dbFactory;
        _keys = keys;
        _subscribers = subscribers;
        _logger = logger;
    }

    /// <summary>
    /// Record <paramref name="notification"/> and deliver it to matching subscribers.
    /// </summary>
    /// <remarks>
    /// Never throws for a delivery failure. A backup job must not fail because a
    /// chat webhook is down — the notification is a side channel, and letting it
    /// break the work it reports on would be worse than the silence it replaces.
    /// </remarks>
    public async Task PublishAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using var db = _dbFactory.Create();

        db.SystemEvents.Add(new SystemEventRow
        {
            Severity = notification.Severity,
            Topic = notification.Topic,
            EventKey = notification.EventKey,
            Summary = notification.Summary,
            DetailJson = notification.Detail is null or { Count: 0 }
                ? "{}"
                : JsonSerializer.Serialize(notification.Detail),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var targets = await db.NotificationSubscribers
            .Where(t => t.IsEnabled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await DeliverToAllAsync(db, targets.Select(Target.From), notification, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Record a LEDGER-scope event and deliver it wherever that ledger says.
    /// </summary>
    /// <remarks>
    /// A ledger either inherits the deployment's targets or names its own
    /// (<c>ledgers.notification_mode</c>). Inheriting is the default because on a
    /// single-operator install the deployment's target is almost always the right
    /// answer, and making every new ledger silent until someone configured it would
    /// recreate the problem this subsystem exists to solve.
    /// <para>
    /// Written to <c>ledger_events</c>, never <c>system_events</c>: a drift notice
    /// dies with its ledger and is gated by grant, which is the whole basis of
    /// ADR-0096 D1's scope split.
    /// </para>
    /// </remarks>
    public async Task PublishLedgerAsync(
        Guid ledgerId,
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using var db = _dbFactory.Create();

        db.LedgerEvents.Add(new LedgerEventRow
        {
            LedgerId = ledgerId,
            Severity = notification.Severity,
            Topic = notification.Topic,
            EventKey = notification.EventKey,
            Summary = notification.Summary,
            DetailJson = notification.Detail is null or { Count: 0 }
                ? "{}"
                : JsonSerializer.Serialize(notification.Detail),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // One small read for the name the delivered payload needs.
        var ledger = await db.Ledgers.AsNoTracking()
            .Where(l => l.Id == ledgerId)
            .Select(l => new { l.Name })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // WHICH ledger this is about, in the delivered payload. Without it an operator
        // pointing several ledgers at one webhook receives a set of identical messages
        // and cannot act on any of them — and pointing several ledgers at one destination
        // is exactly the intended way to get a single channel now that each ledger keeps
        // its own targets.
        //
        // Carried in Detail rather than as a new field on NotificationEvent: Detail is
        // where every provider already renders arbitrary key/values, so no
        // INotificationSubscriber implementation changes, and the record stays shared
        // with deployment scope where a ledger field would always be null.
        //
        // Rendered as a name, not a UUID: a raw id in a Discord message is unusable. It
        // is snapshotted at publish time and will NOT follow a later rename, which is
        // correct for an event history and wrong to assume otherwise.
        //
        // "(unknown ledger)" rather than omitting the key when the row is missing — the
        // one case where identity matters most must not be the case that silently drops
        // it.
        var detail = new Dictionary<string, string>(
            notification.Detail ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["ledger"] = ledger?.Name ?? "(unknown ledger)",
        };
        var delivered = notification with { Detail = detail };

        // A ledger's own targets, always. There used to be an 'inherit' mode that read
        // the DEPLOYMENT table here instead, with no ledger filter — so on a shared
        // install one ledger's activity went to whoever watched the deployment channel,
        // and it was the default. Migration 214 moved the inherited targets into each
        // ledger and dropped the mode; two scopes that overlap by default are not two
        // scopes.
        var targets = (await db.LedgerNotificationSubscribers
                .Where(t => t.LedgerId == ledgerId && t.IsEnabled)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(Target.From);

        // `delivered`, not `notification`: the ledger_events row above deliberately keeps
        // the ORIGINAL detail — the ledger id is already a column there, so repeating the
        // name inside the JSON would duplicate it into every historical row and freeze a
        // stale copy of a renameable thing in the store as well as the payload.
        await DeliverToAllAsync(db, targets, delivered, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One delivery loop for both scopes.
    /// </summary>
    /// <remarks>
    /// The routing rules, the provider lookup and the failure recording are identical
    /// for a system target and a ledger target; only where the list came from
    /// differs. Writing the loop twice is how the two would quietly drift apart, and
    /// a divergence here means one scope silently stops delivering.
    /// </remarks>
    private async Task DeliverToAllAsync(
        AppDbContext db,
        IEnumerable<Target> targets,
        NotificationEvent notification,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            // Provider first, then routing: whether a heartbeat may bypass the
            // severity floor depends on the provider's CAPABILITY, which the stored
            // row does not carry. An unroutable target is still recorded below even
            // if it would not have wanted this event — a key that resolves to nothing
            // is a configuration fault worth surfacing on its own.
            var provider = _subscribers
                .FirstOrDefault(p => p.SubscriberKey == target.SubscriberKey);
            if (provider is null)
            {
                // A configured target whose provider is not registered — a renamed
                // key, or a provider removed from the build. Recorded rather than
                // ignored, because an unroutable target looks exactly like a working
                // one from the settings page.
                target.RecordFailure(
                    "No provider registered for '" + target.SubscriberKey + "'.");
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!Wants(target, provider.Capability, notification)) continue;

            try
            {
                var config = OpenConfig(target.ConfigCiphertext, target.SubscriberKey);
                await provider.DeliverAsync(notification, config, cancellationToken)
                              .ConfigureAwait(false);
                target.RecordSuccess();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            // `is not OperationCanceledException` alone is a trap here: an HttpClient
            // timeout throws TaskCanceledException, which DERIVES from
            // OperationCanceledException — so the 10s timeout deliberately configured
            // for these providers ("a heartbeat ping that hangs must not hold up the
            // job reporting it") was the one exception the filter let escape. It
            // skipped RecordFailure, so a dead target still looked healthy on the
            // settings page, and it abandoned the loop, so every remaining target was
            // starved by the first hung one.
            //
            // The token is what distinguishes the two: real cancellation means OUR
            // token was signalled. Anything else wearing that exception type is a
            // provider that timed out, which is a delivery failure like any other.
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Notification delivery failed: subscriber={SubscriberKey} event={EventKey}",
                    target.SubscriberKey, notification.EventKey);
                target.RecordFailure(ex.Message);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A delivery target, whichever table it came from.
    /// </summary>
    /// <remarks>
    /// The two rows live in separate tables on purpose (ADR-0096 D1) but are
    /// identical to deliver to, so this adapts both rather than the loop existing
    /// twice. The mutations write through to the tracked entity, so the caller's
    /// SaveChanges persists the delivery health.
    /// </remarks>
    private sealed class Target
    {
        private readonly Action<string> _fail;
        private readonly Action _succeed;

        private Target(
            string subscriberKey, string minSeverity, string[]? topics, string? monitors,
            byte[] configCiphertext, Action<string> fail, Action succeed)
        {
            SubscriberKey = subscriberKey;
            MinSeverity = minSeverity;
            Topics = topics;
            Monitors = monitors;
            ConfigCiphertext = configCiphertext;
            _fail = fail;
            _succeed = succeed;
        }

        public string SubscriberKey { get; }
        public string MinSeverity { get; }
        public string[]? Topics { get; }

        /// <summary>
        /// The one monitor a heartbeat target is bound to; null for a message target,
        /// which is not bound to anything.
        /// </summary>
        public string? Monitors { get; }

        public byte[] ConfigCiphertext { get; }

        public void RecordFailure(string error) => _fail(error);
        public void RecordSuccess() => _succeed();

        public static Target From(NotificationSubscriberRow r) => new(
            r.SubscriberKey, r.MinSeverity, r.Topics, r.Monitors, r.ConfigCiphertext,
            error =>
            {
                r.LastFailureAt = DateTime.UtcNow;
                r.ConsecutiveFailures += 1;
                r.LastError = Truncate(error);
                r.UpdatedAt = DateTime.UtcNow;
            },
            () =>
            {
                r.LastSuccessAt = DateTime.UtcNow;
                r.ConsecutiveFailures = 0;
                r.LastError = null;
                r.UpdatedAt = DateTime.UtcNow;
            });

        public static Target From(LedgerNotificationSubscriberRow r) => new(
            r.SubscriberKey, r.MinSeverity, r.Topics, r.Monitors, r.ConfigCiphertext,
            error =>
            {
                r.LastFailureAt = DateTime.UtcNow;
                r.ConsecutiveFailures += 1;
                r.LastError = Truncate(error);
                r.UpdatedAt = DateTime.UtcNow;
            },
            () =>
            {
                r.LastSuccessAt = DateTime.UtcNow;
                r.ConsecutiveFailures = 0;
                r.LastError = null;
                r.UpdatedAt = DateTime.UtcNow;
            });

        private static string Truncate(string error) =>
            error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

    /// <summary>
    /// Publish unless the same <c>EventKey</c> was already published inside
    /// <paramref name="window"/>.
    /// </summary>
    /// <returns>True when published; false when suppressed as a repeat.</returns>
    /// <remarks>
    /// For monitors that RE-CHECK a standing condition rather than reporting a
    /// one-off. A stale backup is still stale fifteen minutes later, and a check on
    /// the scheduler tick would otherwise announce it ~96 times a day — which mutes
    /// the channel, and a muted channel protects nothing. That is the same
    /// cry-wolf failure ADR-0096 D4 is about, arriving through repetition instead of
    /// through severity.
    /// <para>
    /// The suppression window is deliberately checked against
    /// <c>system_events</c> rather than kept in memory: a process restart must not
    /// re-announce everything, and the history is already the source of truth.
    /// </para>
    /// </remarks>
    public async Task<bool> PublishThrottledAsync(
        NotificationEvent notification,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using (var db = _dbFactory.Create())
        {
            var since = DateTime.UtcNow - window;
            var alreadySaid = await db.SystemEvents
                .AsNoTracking()
                .AnyAsync(e => e.EventKey == notification.EventKey && e.OccurredAt >= since,
                          cancellationToken)
                .ConfigureAwait(false);
            if (alreadySaid) return false;
        }

        await PublishAsync(notification, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ledger-scope publish, unless the same <c>EventKey</c> was already published
    /// for that ledger inside <paramref name="window"/>.
    /// </summary>
    /// <returns>True when published; false when suppressed as a repeat.</returns>
    /// <remarks>
    /// The per-ledger counterpart of <see cref="PublishThrottledAsync"/>, and scoped
    /// per ledger deliberately: one ledger's standing drift must not silence a
    /// different ledger's first report of the same kind.
    /// </remarks>
    public async Task<bool> PublishLedgerThrottledAsync(
        Guid ledgerId,
        NotificationEvent notification,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using (var db = _dbFactory.Create())
        {
            var since = DateTime.UtcNow - window;
            var alreadySaid = await db.LedgerEvents
                .AsNoTracking()
                .AnyAsync(e => e.LedgerId == ledgerId
                               && e.EventKey == notification.EventKey
                               && e.OccurredAt >= since,
                          cancellationToken)
                .ConfigureAwait(false);
            if (alreadySaid) return false;
        }

        await PublishLedgerAsync(ledgerId, notification, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Which of THIS ledger's scheduled jobs have somebody watching for their absence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the jobs the ledger actually has ENABLED, not on the build's monitor
    /// list. Copying the deployment shape verbatim would warn a ledger with snapshots
    /// switched off that nothing is watching its snapshots — a warning about a job that
    /// is not supposed to run. A monitor that cries wolf gets its channel muted, which
    /// costs more than the warning was ever worth.
    /// </para>
    /// <para>
    /// A job the ledger has enabled but no longer has a monitor for cannot happen while
    /// MonitorScopeTests holds, but the intersection is taken anyway rather than assumed:
    /// a job_type row can outlive the build that knew about it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, bool>> LedgerMonitorCoverageAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        var heartbeatKeys = _subscribers
            .Where(p => p.Capability == SubscriberCapability.Heartbeat)
            .Select(p => p.SubscriberKey)
            .ToList();

        await using var db = _dbFactory.Create();

        var enabledJobs = await db.ScheduledJobs
            .Where(j => j.LedgerId == ledgerId && j.Enabled)
            .Select(j => j.JobType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var bound = await db.LedgerNotificationSubscribers
            .Where(t => t.LedgerId == ledgerId
                        && t.IsEnabled
                        && t.Monitors != null
                        && heartbeatKeys.Contains(t.SubscriberKey))
            .Select(t => t.Monitors!)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return NotificationMonitors.Ledger
            .Where(m => enabledJobs.Contains(m, StringComparer.Ordinal))
            .ToDictionary(
                m => m,
                m => bound.Contains(m, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Which monitors have somebody watching for their absence, and which do not.
    /// </summary>
    /// <remarks>
    /// This replaced a boolean — "does ANY heartbeat subscriber exist?" — which the
    /// settings page then rendered as "absence detection is covered". That claim is
    /// false the moment more than one thing is worth monitoring: one healthchecks URL
    /// is one check, so a target bound to backups says nothing whatever about
    /// snapshots, while the UI implied everything was watched.
    /// <para>
    /// Returns every known monitor, covered or not, because the uncovered ones are the
    /// answer people need. A map with absences in it beats a true/false that cannot
    /// express them.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, bool>> MonitorCoverageAsync(
        CancellationToken cancellationToken = default)
    {
        var heartbeatKeys = _subscribers
            .Where(p => p.Capability == SubscriberCapability.Heartbeat)
            .Select(p => p.SubscriberKey)
            .ToList();

        await using var db = _dbFactory.Create();
        var bound = await db.NotificationSubscribers
            .Where(t => t.IsEnabled
                        && t.Monitors != null
                        && heartbeatKeys.Contains(t.SubscriberKey))
            .Select(t => t.Monitors!)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return NotificationMonitors.Deployment.ToDictionary(
            m => m,
            m => bound.Contains(m, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>Does this target want this event?</summary>
    /// <remarks>
    /// Capability decides the rule, and the two rules are not variations of each other.
    /// <para>
    /// A HEARTBEAT target is bound to ONE monitor and hears only about that one. Its
    /// provider has no other shape available: a healthchecks.io check is a single URL,
    /// its request body is stored for diagnostics but does not affect alerting, and it
    /// has no notion of severity — the only things it can say are "alive" and "down",
    /// about that one check. So severity is not consulted here at all, and an event
    /// belonging to a different monitor (or to none) is not this target's business.
    /// </para>
    /// <para>
    /// This replaced a rule that delivered ANY Critical event to a heartbeat target and
    /// turned it into /fail. With one URL per check that is actively wrong: a critical
    /// CONSISTENCY event would have marked the BACKUP check down, because the check's
    /// identity and the event's subject were unrelated. Requiring the monitor to match
    /// makes that unrepresentable rather than merely unlikely.
    /// </para>
    /// <para>
    /// A MESSAGE target is the opposite: it can render text and any severity, so it
    /// keeps the floor (how much the operator wants to hear about) and the topic filter.
    /// It is also the only class that can say anything about an event which is nobody's
    /// liveness signal — which is most of them.
    /// </para>
    /// </remarks>
    private static bool Wants(
        Target target, SubscriberCapability capability, NotificationEvent n)
    {
        if (capability == SubscriberCapability.Heartbeat)
        {
            return n.Signal != MonitorSignal.None
                && n.Monitor is not null
                && string.Equals(n.Monitor, target.Monitors, StringComparison.Ordinal);
        }

        if (target.Topics is { Length: > 0 } topics && !topics.Contains(n.Topic))
            return false;

        return NotificationSeverity.MeetsFloor(n.Severity, target.MinSeverity);
    }

    private SubscriberConfig OpenConfig(byte[] ciphertext, string subscriberKey)
    {
        var json = Encoding.UTF8.GetString(_keys.OpenWithMasterKey(ciphertext));
        return JsonSerializer.Deserialize<SubscriberConfig>(json)
               ?? throw new InvalidOperationException(
                   "Subscriber config for '" + subscriberKey + "' is empty.");
    }
}
