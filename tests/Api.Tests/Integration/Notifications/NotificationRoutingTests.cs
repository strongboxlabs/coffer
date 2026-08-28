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
/// This file exists because the delivery path had NO tests, and three separate
/// blockers rode out on that. Each test below pins one of them, and each one passed
/// its own negative control — the assertion fails against the code as it shipped.
/// </para>
/// <para>
/// Fakes rather than HTTP on purpose: what needs pinning is which events reach a
/// target and what gets recorded when one misbehaves, not whether HttpClient works.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NotificationRoutingTests
{
    private readonly PostgresFixture _fixture;

    public NotificationRoutingTests(PostgresFixture fixture) => _fixture = fixture;

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
        new(_fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            providers,
            NullLogger<NotificationPublisher>.Instance);

    /// <summary>
    /// Deployment-scope targets are not isolated by a per-test ledger, so this clears
    /// them first (the engineering-standards §5.2 exception for global tables).
    /// </summary>
    private async Task<Guid> SeedTargetAsync(
        string subscriberKey, string minSeverity, bool clearFirst = true,
        string? monitors = null)
    {
        await using var db = _fixture.NewServiceDbContext();
        if (clearFirst)
            await db.NotificationSubscribers.ExecuteDeleteAsync();

        var row = new NotificationSubscriberRow
        {
            SubscriberKey = subscriberKey,
            DisplayName = subscriberKey,
            MinSeverity = minSeverity,
            Monitors = monitors,
            ConfigCiphertext = _fixture.NewLedgerKeyService().SealWithMasterKey(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new SubscriberConfig("https://fake.invalid/ping", null)))),
        };
        db.NotificationSubscribers.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static NotificationEvent Event(
        string severity, string key,
        string? monitor = null, MonitorSignal signal = MonitorSignal.None) =>
        new(Severity: severity,
            Topic: NotificationTopics.Backup,
            EventKey: key,
            Summary: key,
            Detail: null,
            Monitor: monitor,
            Signal: signal);

    [Fact]
    public async Task An_info_heartbeat_reaches_a_heartbeat_target_whose_floor_is_warning()
    {
        // THE D5 BUG. The only heartbeat in the system (backup.succeeded) is published
        // at info, and min_severity defaults to warning in all four places it is set.
        // Applying the floor to a heartbeat therefore meant a healthchecks target added
        // with the defaults never received a single ping — so its check never entered
        // an up state and never alerted on absence, while the panel reported absence
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
        // info event has no representation. It used to be dropped INSIDE the provider,
        // after which the publisher recorded a SUCCESS for a delivery that never
        // happened — advancing last_success_at and showing a healthy target on the
        // strength of pings it never sent.
        var id = await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Info, "quotes.refreshed"));

        Assert.Empty(hc.Delivered);

        await using var db = _fixture.NewServiceDbContext();
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

    [Fact]
    public async Task A_provider_timeout_records_a_failure_and_the_next_target_still_gets_it()
    {
        // THE CATCH-FILTER BUG. An HttpClient timeout throws TaskCanceledException,
        // which derives from OperationCanceledException — the one type the old filter
        // `when (ex is not OperationCanceledException)` let escape. So the 10s timeout
        // deliberately configured for these providers skipped RecordFailure (a dead
        // target kept looking healthy) AND abandoned the loop, starving every
        // remaining target behind the first hung one.
        var deadId = await SeedTargetAsync("dead", NotificationSeverity.Info);
        var liveId = await SeedTargetAsync("live", NotificationSeverity.Info, clearFirst: false);

        var dead = new FakeSubscriber("dead", SubscriberCapability.Message,
            _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var live = new FakeSubscriber("live", SubscriberCapability.Message);

        // An uncancelled token: this is a timeout, not a shutdown.
        await Publisher(dead, live).PublishAsync(
            Event(NotificationSeverity.Warning, "consistency.drift"), CancellationToken.None);

        Assert.Single(live.Delivered);

        await using var db = _fixture.NewServiceDbContext();
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

        await using var db = _fixture.NewServiceDbContext();
        var row = await db.NotificationSubscribers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Null(row.LastFailureAt);
    }

    [Fact]
    public async Task A_critical_event_that_is_no_ones_signal_never_reaches_a_heartbeat_target()
    {
        // THE BUG THIS DESIGN REMOVES. Routing used to deliver any Critical event to a
        // heartbeat target and the provider turned it into /fail. One healthchecks URL
        // is one check, so a critical CONSISTENCY event would have marked the BACKUP
        // check down — the check's identity and the event's subject were unrelated, and
        // an operator would be paged about a backup that was fine.
        var id = await SeedTargetAsync("hc", NotificationSeverity.Info,
            monitors: NotificationMonitors.Backup);
        var hc = new FakeSubscriber("hc", SubscriberCapability.Heartbeat);

        await Publisher(hc).PublishAsync(
            Event(NotificationSeverity.Critical, "consistency.drift"));

        Assert.Empty(hc.Delivered);

        // And nothing recorded either way: a delivery that never happened must not
        // advance last_success_at, and a routing decision is not a failure.
        await using var db = _fixture.NewServiceDbContext();
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

        await using var db = _fixture.NewServiceDbContext();
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
