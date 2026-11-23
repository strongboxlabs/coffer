using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// Routing and failure-recording for the notification bus, with fake providers.
/// </summary>
/// <remarks>
/// <para>
/// Each test below pins one failure mode of the delivery path: a heartbeat filtered
/// out by a severity floor, a timeout that escapes failure recording and starves the
/// remaining targets, and a heartbeat target paged by an event that is not its signal.
/// </para>
/// <para>
/// Fakes rather than HTTP on purpose: what needs pinning is which events reach a
/// target and what gets recorded when one misbehaves, not whether HttpClient works.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NotificationRoutingTests : DatabaseTest
{
    public NotificationRoutingTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>A provider that records what it was handed, and can misbehave.</summary>
    private sealed class FakeSubscriber : INotificationSubscriber
    {
        private readonly Func<NotificationEvent, Task>? _onDeliver;

        public FakeSubscriber(
            string key, SubscriberCapability capability, Func<NotificationEvent, Task>? onDeliver = null)
        {
            SubscriberKey = key;
            Capability = capability;
            _onDeliver = onDeliver;
        }

        public string SubscriberKey { get; }
        public string DisplayName => SubscriberKey;
        public SubscriberCapability Capability { get; }
        public List<NotificationEvent> Delivered { get; } = [];

        public async Task DeliverAsync(
            NotificationEvent notification, SubscriberConfig config, CancellationToken cancellationToken)
        {
            if (_onDeliver is not null) await _onDeliver(notification).ConfigureAwait(false);
            Delivered.Add(notification);
        }
    }

    private NotificationPublisher Publisher(params INotificationSubscriber[] providers) =>
        new(Db.NewServiceFactory(),
            Db.NewLedgerKeyService(),
            providers,
            NullLogger<NotificationPublisher>.Instance, TimeProvider.System);

    /// <summary>Seed one deployment-scope delivery target.</summary>
    private async Task<Guid> SeedTargetAsync(
        string subscriberKey, string minSeverity, string? monitors = null, string[]? topics = null)
    {
        await using var db = Db.NewServiceDbContext();
        var row = new NotificationSubscriberRow
        {
            SubscriberKey = subscriberKey,
            DisplayName = subscriberKey,
            MinSeverity = minSeverity,
            Monitors = monitors,
            Topics = topics,
            ConfigCiphertext = Db.NewLedgerKeyService().SealWithMasterKey(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new SubscriberConfig("https://fake.invalid/ping", null)))),
        };
        db.NotificationSubscribers.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static NotificationEvent Event(
        string severity, string key,
        string? monitor = null, MonitorSignal signal = MonitorSignal.None,
        string topic = NotificationTopics.Backup) =>
        new(Severity: severity,
            Topic: topic,
            EventKey: key,
            Summary: key,
            Detail: null,
            Monitor: monitor,
            Signal: signal);

    [Fact]
    public async Task An_info_heartbeat_reaches_a_heartbeat_target_whose_floor_is_warning()
    {
        // ADR-0096 D5. The only heartbeat in the system (backup.succeeded) is published
        // at info, and min_severity defaults to warning in all four places it is set.
        // Applying the floor to a heartbeat would mean a healthchecks target added
        // with the defaults never receives a single ping — so its check never enters
        // an up state and never alerts on absence, while the panel reports absence
        // detection as covered.
        await SeedTargetAsync("hc", NotificationSeverity.Warning,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Info, "backup.succeeded",
                  NotificationMonitors.Backup, MonitorSignal.Success));

        Assert.Single(hc.Delivered);
    }

    [Fact]
    public async Task A_plain_info_event_is_not_delivered_to_a_heartbeat_target_and_records_nothing()
    {
        // A dead-man's switch can only say "it happened" or "it failed", so an ordinary
        // info event has no representation. Dropping it INSIDE the provider would let
        // the publisher record a SUCCESS for a delivery that never happened —
        // advancing last_success_at and showing a healthy target on the strength of
        // pings it never sent. So routing skips it and nothing is recorded.
        var id = await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Info, "quotes.refreshed"));

        Assert.Empty(hc.Delivered);

        await using var db = Db.NewServiceDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Null(row.LastSuccessAt);
        Assert.Null(row.LastFailureAt);
    }

    [Fact]
    public async Task A_message_target_still_honours_its_severity_floor()
    {
        // The floor is not gone — for a target that CAN express any severity it is the
        // operator saying how much they want to hear about.
        await SeedTargetAsync("hook", NotificationSeverity.Warning);
        var hook = new FakeSubscriber("hook", SubscriberCapability.Message);
        var publisher = Publisher(hook);

        await publisher.PublishAsync(Event(NotificationSeverity.Info, "below.floor"));
        Assert.Empty(hook.Delivered);

        await publisher.PublishAsync(Event(NotificationSeverity.Warning, "at.floor"));
        Assert.Single(hook.Delivered);
    }

    // ---- opt-in topics (ADR-0110) ------------------------------------------------------------

    [Fact]
    public async Task A_target_with_no_topic_filter_does_not_receive_an_opt_in_topic()
    {
        // "No filter" means every topic everywhere else. If it also meant upgrade notices, a new
        // warning-level topic would reach every existing webhook and monitor the day it shipped,
        // unasked, which is the thing opt-in exists to prevent.
        await SeedTargetAsync("everything", NotificationSeverity.Info);
        var everything = new FakeSubscriber("everything", SubscriberCapability.Message);

        await Publisher(everything).PublishAsync(Event(
            NotificationSeverity.Warning, "upgrade.available", topic: NotificationTopics.Upgrade));

        Assert.Empty(everything.Delivered);
    }

    [Fact]
    public async Task A_target_that_names_the_opt_in_topic_receives_it()
    {
        await SeedTargetAsync("opted-in", NotificationSeverity.Warning, topics: [NotificationTopics.Upgrade]);
        var optedIn = new FakeSubscriber("opted-in", SubscriberCapability.Message);

        await Publisher(optedIn).PublishAsync(Event(
            NotificationSeverity.Warning, "upgrade.available", topic: NotificationTopics.Upgrade));

        Assert.Equal("upgrade.available", Assert.Single(optedIn.Delivered).EventKey);
    }

    [Fact]
    public async Task Opting_in_is_per_topic_and_the_others_still_follow_the_filter()
    {
        // Naming only upgrade must not turn on every other topic, and an unfiltered target must
        // keep hearing every ordinary one: opt-in changes one topic and nothing else.
        await SeedTargetAsync("upgrade-only", NotificationSeverity.Info, topics: [NotificationTopics.Upgrade]);
        await SeedTargetAsync("everything", NotificationSeverity.Info);
        var upgradeOnly = new FakeSubscriber("upgrade-only", SubscriberCapability.Message);
        var everything = new FakeSubscriber("everything", SubscriberCapability.Message);

        await Publisher(upgradeOnly, everything).PublishAsync(Event(
            NotificationSeverity.Warning, "backup.failed", topic: NotificationTopics.Backup));

        Assert.Empty(upgradeOnly.Delivered);
        Assert.Single(everything.Delivered);
    }

    [Fact]
    public async Task A_target_listing_every_topic_receives_the_opt_in_one_too()
    {
        // What the System form sends when "Also send upgrade notices" is ticked.
        await SeedTargetAsync("all-named", NotificationSeverity.Warning, topics: [.. NotificationTopics.All]);
        var allNamed = new FakeSubscriber("all-named", SubscriberCapability.Message);
        var publisher = Publisher(allNamed);

        await publisher.PublishAsync(Event(NotificationSeverity.Warning, "backup.failed"));
        await publisher.PublishAsync(Event(
            NotificationSeverity.Warning, "upgrade.available", topic: NotificationTopics.Upgrade));

        Assert.Equal(2, allNamed.Delivered.Count);
    }

    [Fact]
    public async Task A_heartbeat_target_never_receives_an_upgrade_notice()
    {
        // A heartbeat is bound to one job's liveness signal; an upgrade notice is nobody's signal.
        await SeedTargetAsync("hc", NotificationSeverity.Info, monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(Event(
            NotificationSeverity.Warning, "upgrade.available", topic: NotificationTopics.Upgrade));

        Assert.Empty(hc.Delivered);
    }

    [Fact]
    public async Task A_provider_timeout_records_a_failure_and_the_next_target_still_gets_it()
    {
        // THE CATCH FILTER. An HttpClient timeout throws TaskCanceledException,
        // which derives from OperationCanceledException — the one type a filter of
        // `when (ex is not OperationCanceledException)` lets escape. Under that filter
        // the 10s timeout configured for these providers would skip RecordFailure (a
        // dead target keeps looking healthy) AND abandon the loop, starving every
        // remaining target behind the first hung one.
        var deadId = await SeedTargetAsync("dead", NotificationSeverity.Info);
        var liveId = await SeedTargetAsync("live", NotificationSeverity.Info);

        var dead = new FakeSubscriber("dead", SubscriberCapability.Message,
            _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var live = new FakeSubscriber("live", SubscriberCapability.Message);

        // An uncancelled token: this is a timeout, not a shutdown.
        await Publisher(dead, live).PublishAsync(
            Event(NotificationSeverity.Warning, "consistency.drift"), CancellationToken.None);

        Assert.Single(live.Delivered);

        await using var db = Db.NewServiceDbContext();
        var deadRow = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == deadId);
        Assert.Equal(1, deadRow.ConsecutiveFailures);
        Assert.NotNull(deadRow.LastFailureAt);
        Assert.NotNull(deadRow.LastError);

        var liveRow = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == liveId);
        Assert.NotNull(liveRow.LastSuccessAt);
        Assert.Equal(0, liveRow.ConsecutiveFailures);
    }

    [Fact]
    public async Task Real_cancellation_is_not_recorded_as_a_delivery_failure()
    {
        // The other side of that filter: when OUR token is signalled the process is
        // shutting down, and stamping every target as failed on the way out would be
        // noise an operator has to un-learn.
        var id = await SeedTargetAsync("hook", NotificationSeverity.Info);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var hook = new FakeSubscriber("hook", SubscriberCapability.Message,
            _ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Publisher(hook).PublishAsync(
                Event(NotificationSeverity.Warning, "shutting.down"), cts.Token));

        await using var db = Db.NewServiceDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Null(row.LastFailureAt);
    }

    [Fact]
    public async Task A_critical_event_that_is_no_ones_signal_never_reaches_a_heartbeat_target()
    {
        // MONITOR BINDING. If routing delivered any Critical event to a heartbeat
        // target, the provider would turn it into /fail. One healthchecks URL is one
        // check, so a critical CONSISTENCY event would mark the BACKUP check down —
        // the check's identity and the event's subject are unrelated, and an operator
        // is paged about a backup that is fine.
        var id = await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Critical, "consistency.drift"));

        Assert.Empty(hc.Delivered);

        // And nothing recorded either way: a delivery that never happened must not
        // advance last_success_at, and a routing decision is not a failure.
        await using var db = Db.NewServiceDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Null(row.LastSuccessAt);
        Assert.Null(row.LastFailureAt);
    }

    [Fact]
    public async Task A_signal_for_a_different_monitor_is_not_this_targets_business()
    {
        // The binding is what makes one URL mean one check. A target watching backups
        // must not hear a snapshot job's ping, or its check would stay green on the
        // strength of a signal from something else entirely — the exact failure a
        // dead-man's switch exists to prevent, inverted.
        var id = await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Info, "snapshot.succeeded",
                  "snapshot", MonitorSignal.Success));

        Assert.Empty(hc.Delivered);

        await using var db = Db.NewServiceDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Null(row.LastSuccessAt);
    }

    [Fact]
    public async Task A_failure_signal_for_the_bound_monitor_is_delivered()
    {
        // The other half of the binding: a heartbeat target is not absence-only. Its
        // provider can report its own check down, and reporting it promptly is the
        // point — the docs call actively signalling a failure the way to minimise the
        // delay before an operator hears about it.
        await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Critical, "backup.failed",
                  NotificationMonitors.Backup, MonitorSignal.Failure));

        var delivered = Assert.Single(hc.Delivered);
        Assert.Equal(MonitorSignal.Failure, delivered.Signal);
    }
}
