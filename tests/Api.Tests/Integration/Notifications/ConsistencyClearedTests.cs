using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The consistency heartbeat: a daily ping while healthy, which doubles as the all-clear.
/// </summary>
/// <remarks>
/// ConsistencyMonitor published when drift appeared and published nothing when it went,
/// so a warning stayed on a ledger's list looking current until it aged out of retention.
/// That is not hypothetical: the first real row the in-app list ever displayed was a
/// six-day-old drift warning that had already been resolved.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyClearedTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public ConsistencyClearedTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => NotificationTargetReset.ClearAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A heartbeat provider, so Wants() takes the monitor-matching branch.</summary>
    private sealed class SpySwitch : INotificationSubscriber
    {
        public string SubscriberKey => "healthchecks";
        public string DisplayName => "Healthchecks";
        public SubscriberCapability Capability => SubscriberCapability.Heartbeat;
        public List<NotificationEvent> Delivered { get; } = [];

        public Task DeliverAsync(
            NotificationEvent notification, SubscriberConfig config, CancellationToken ct)
        {
            Delivered.Add(notification);
            return Task.CompletedTask;
        }
    }

    private async Task BindSwitchAsync(Guid ledgerId, string monitor)
    {
        await using var db = _fixture.NewDbContext();
        db.LedgerNotificationSubscribers.Add(new LedgerNotificationSubscriberRow
        {
            LedgerId = ledgerId,
            SubscriberKey = "healthchecks",
            DisplayName = "switch:" + monitor,
            MinSeverity = NotificationSeverity.Info,
            Monitors = monitor,
            ConfigCiphertext = _fixture.NewLedgerKeyService().SealWithMasterKey(
                System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(
                        new SubscriberConfig("https://hc.invalid/ping")))),
        });
        await db.SaveChangesAsync();
    }

    private ConsistencyMonitor MonitorDeliveringTo(INotificationSubscriber spy) => new(
        _fixture.NewServiceFactory(),
        new NotificationPublisher(
            _fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            new[] { spy },
            NullLogger<NotificationPublisher>.Instance),
        NullLogger<ConsistencyMonitor>.Instance);

    private ConsistencyMonitor NewMonitor() => new(
        _fixture.NewServiceFactory(),
        new NotificationPublisher(
            _fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            Array.Empty<INotificationSubscriber>(),
            NullLogger<NotificationPublisher>.Instance),
        NullLogger<ConsistencyMonitor>.Instance);

    private async Task SeedEventAsync(Guid ledgerId, string severity, string eventKey)
    {
        await using var db = _fixture.NewDbContext();
        db.LedgerEvents.Add(new LedgerEventRow
        {
            LedgerId = ledgerId,
            Severity = severity,
            Topic = NotificationTopics.Consistency,
            EventKey = eventKey,
            Summary = "seeded",
            DetailJson = "{}",
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> ConsistencyKeysAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && e.Topic == NotificationTopics.Consistency)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.EventKey)
            .ToListAsync();
    }

    [Fact]
    public async Task A_healthy_ledger_that_was_drifting_announces_the_all_clear()
    {
        // A synthetic ledger has no positions, so the check finds it healthy — which is
        // the state under test. The seeded warning is what makes this a TRANSITION.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedEventAsync(ledger.LedgerId, NotificationSeverity.Warning, "consistency.drift");

        await NewMonitor().CheckAllAsync();

        Assert.Contains("consistency.ok", await ConsistencyKeysAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task A_healthy_ledger_pings_even_with_no_drift_to_clear()
    {
        // Replaces A_healthy_ledger_that_was_already_fine_says_nothing, which pinned the
        // transition-only version. Saying nothing while healthy is fine for resolving a
        // warning and useless as a heartbeat: a check pinged only when something CHANGES
        // is indistinguishable from a check whose app has died. A switch bound to
        // consistency needs the ping on a predictable cadence or healthchecks marks it
        // down from inactivity while everything is fine.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await NewMonitor().CheckAllAsync();

        Assert.Contains("consistency.ok", await ConsistencyKeysAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task The_healthy_ping_carries_the_monitor_and_a_success_signal()
    {
        // Without these a heartbeat target could never match it: Wants() routes on
        // monitor name plus signal, so an event with neither reaches no switch at all.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await NewMonitor().CheckAllAsync();

        await using var db = _fixture.NewDbContext();
        var row = await db.LedgerEvents.AsNoTracking()
            .SingleAsync(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.ok");
        Assert.Equal(NotificationSeverity.Info, row.Severity);
    }

    [Fact]
    public async Task The_ping_is_throttled_to_one_a_day_not_one_a_tick()
    {
        // What makes a daily heartbeat affordable. The monitor runs on every scheduler
        // tick — every fifteen minutes — so an unthrottled ping would write ~96 rows per
        // ledger per day into a table nothing prunes below a year, and would bury the
        // warnings the in-app list exists to show.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedEventAsync(ledger.LedgerId, NotificationSeverity.Warning, "consistency.drift");

        var monitor = NewMonitor();
        await monitor.CheckAllAsync();
        await monitor.CheckAllAsync();
        await monitor.CheckAllAsync();

        var keys = await ConsistencyKeysAsync(ledger.LedgerId);
        Assert.Single(keys, k => k == "consistency.ok");
    }

    [Fact]
    public async Task The_all_clear_is_info_so_it_does_not_fill_the_problem_list()
    {
        // Recovery is good news. The row exists so the earlier warning can be MARKED
        // resolved, not so anyone is told twice — and the list is filtered to warnings
        // and above precisely so good news cannot crowd out bad.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedEventAsync(ledger.LedgerId, NotificationSeverity.Warning, "consistency.drift");

        await NewMonitor().CheckAllAsync();

        await using var db = _fixture.NewDbContext();
        var severity = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.ok")
            .Select(e => e.Severity)
            .SingleAsync();
        Assert.Equal(NotificationSeverity.Info, severity);
    }

    [Fact]
    public async Task A_switch_bound_to_consistency_is_pinged_while_healthy()
    {
        // The point of the whole change, proven end to end rather than inferred from the
        // stored row: a switch can only go green if the event actually REACHES it, and
        // Wants() routes heartbeat targets on monitor name plus signal.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await BindSwitchAsync(ledger.LedgerId, NotificationMonitors.Consistency);

        var spy = new SpySwitch();
        await MonitorDeliveringTo(spy).CheckAllAsync();

        var e = Assert.Single(spy.Delivered);
        Assert.Equal(MonitorSignal.Success, e.Signal);
        Assert.Equal(NotificationMonitors.Consistency, e.Monitor);
    }

    [Fact]
    public async Task A_switch_watching_a_DIFFERENT_job_is_not_pinged_by_consistency()
    {
        // One URL is one check. A snapshot switch held green by the consistency check
        // would be false coverage of exactly the kind the monitor binding exists to
        // remove.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await BindSwitchAsync(ledger.LedgerId, NotificationMonitors.Snapshot);

        var spy = new SpySwitch();
        await MonitorDeliveringTo(spy).CheckAllAsync();

        Assert.Empty(spy.Delivered);
    }
}
