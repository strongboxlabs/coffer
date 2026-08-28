using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// A per-ledger scheduled run actually reaches that ledger's dead-man's switch.
/// </summary>
/// <remarks>
/// The mapping is unit-tested separately; this pins DELIVERY. A green suite over a
/// correct mapping that never gets published would be the worst of both worlds — a
/// monitor everyone believes is armed. So this runs the real SchedulerRunner against a
/// real database with a real publisher, and asserts what arrived at the subscriber.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerJobMonitorDeliveryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public LedgerJobMonitorDeliveryTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => NotificationTargetReset.ClearAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A heartbeat provider, so Wants() takes the monitor-matching branch.</summary>
    private sealed class SpyHeartbeat : INotificationSubscriber
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

    /// <summary>Reports whatever the test tells it to.</summary>
    private sealed class OutcomeHandler : IScheduledJobHandler
    {
        public OutcomeHandler(string jobType, JobRunOutcome outcome, string? throwMessage = null)
            => (JobType, _outcome, _throw) = (jobType, outcome, throwMessage);

        private readonly JobRunOutcome _outcome;
        private readonly string? _throw;
        public string JobType { get; }

        public Task<JobRunOutcome> RunAsync(
            AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken ct)
            => _throw is not null
                ? throw new InvalidOperationException(_throw)
                : Task.FromResult(_outcome);
    }

    private NotificationPublisher PublisherFor(params INotificationSubscriber[] subs) =>
        new(_fixture.NewServiceFactory(),
            _fixture.NewLedgerKeyService(),
            subs,
            NullLogger<NotificationPublisher>.Instance);

    private byte[] SealedConfig() =>
        _fixture.NewLedgerKeyService().SealWithMasterKey(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SubscriberConfig("https://hc.invalid/ping"))));

    private async Task AddLedgerSwitchAsync(Guid ledgerId, string monitor)
    {
        await using var db = _fixture.NewDbContext();
        db.LedgerNotificationSubscribers.Add(new LedgerNotificationSubscriberRow
        {
            LedgerId = ledgerId,
            SubscriberKey = "healthchecks",
            DisplayName = "switch:" + monitor,
            MinSeverity = NotificationSeverity.Info,
            Monitors = monitor,
            ConfigCiphertext = SealedConfig(),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDueJobAsync(SyntheticLedger ledger, string jobType)
    {
        await using var db = ledger.NewDbContext();
        db.ScheduledJobs.Add(new ScheduledJobRow
        {
            LedgerId = ledger.LedgerId,
            JobType = jobType,
            Enabled = true,
            HourLocal = 3,
            MinuteLocal = 0,
            ConfiguredByUserId = ledger.UserId,
            NextRunAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<NotificationEvent>> RunAsync(
        SyntheticLedger ledger, IScheduledJobHandler handler)
    {
        var spy = new SpyHeartbeat();
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [handler.JobType] = handler,
        };

        await using var db = _fixture.NewDbContext();
        await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default,
            PublisherFor(spy));
        return spy.Delivered;
    }

    [Fact]
    public async Task A_clean_run_pings_the_switch_watching_that_job()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(ledger.LedgerId, NotificationMonitors.QuoteRefresh);
        await SeedDueJobAsync(ledger, NotificationMonitors.QuoteRefresh);

        var delivered = await RunAsync(
            ledger, new OutcomeHandler(NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok()));

        var e = Assert.Single(delivered);
        Assert.Equal(MonitorSignal.Success, e.Signal);
        Assert.Equal(NotificationMonitors.QuoteRefresh, e.Monitor);
    }

    [Fact]
    public async Task A_degraded_run_reports_the_switch_DOWN()
    {
        // The end-to-end version of the trap: QuoteOrchestrator swallows provider
        // exceptions, so this run "succeeded" as far as the old code could tell. The
        // check must go red anyway.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(ledger.LedgerId, NotificationMonitors.QuoteRefresh);
        await SeedDueJobAsync(ledger, NotificationMonitors.QuoteRefresh);

        var delivered = await RunAsync(ledger, new OutcomeHandler(
            NotificationMonitors.QuoteRefresh,
            JobRunOutcome.Degraded("No quote provider succeeded")));

        var e = Assert.Single(delivered);
        Assert.Equal(MonitorSignal.Failure, e.Signal);
    }

    [Fact]
    public async Task A_thrown_run_reports_the_switch_DOWN()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(ledger.LedgerId, NotificationMonitors.Snapshot);
        await SeedDueJobAsync(ledger, NotificationMonitors.Snapshot);

        var delivered = await RunAsync(ledger, new OutcomeHandler(
            NotificationMonitors.Snapshot, JobRunOutcome.Ok(), throwMessage: "disk full"));

        var e = Assert.Single(delivered);
        Assert.Equal(MonitorSignal.Failure, e.Signal);

        // The check goes red, and the exception's words stay behind. This payload is
        // delivered to a caller-supplied URL, so it says what KIND of failure and where
        // the detail is, not what the exception said.
        Assert.DoesNotContain("disk full", e.Summary, StringComparison.Ordinal);
        Assert.Contains("server log", e.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_switch_watching_a_DIFFERENT_job_is_left_alone()
    {
        // One URL is one check. A snapshot switch must not be held green by the quote
        // refresh running — that is the false coverage the monitor binding exists to
        // remove, and it is the reason the signal names its job.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(ledger.LedgerId, NotificationMonitors.Snapshot);
        await SeedDueJobAsync(ledger, NotificationMonitors.QuoteRefresh);

        var delivered = await RunAsync(
            ledger, new OutcomeHandler(NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok()));

        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Another_ledgers_switch_never_hears_about_this_ledgers_job()
    {
        // The isolation that makes per-ledger monitors sound. Both ledgers watch the same
        // MONITOR NAME, so nothing but the target query's ledger filter separates them.
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var theirs = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(theirs.LedgerId, NotificationMonitors.QuoteRefresh);
        await SeedDueJobAsync(mine, NotificationMonitors.QuoteRefresh);

        var delivered = await RunAsync(
            mine, new OutcomeHandler(NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok()));

        Assert.Empty(delivered);
    }

    [Fact]
    public async Task A_publisher_that_throws_does_not_cost_the_job_its_bookkeeping()
    {
        // Announcing must never cost a job. The run advanced and was recorded before the
        // notification was attempted; a broken webhook must not undo that or abandon the
        // rest of the tick.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddLedgerSwitchAsync(ledger.LedgerId, NotificationMonitors.QuoteRefresh);
        await SeedDueJobAsync(ledger, NotificationMonitors.QuoteRefresh);

        var throwing = new ThrowingSubscriber();
        var handlers = new Dictionary<string, IScheduledJobHandler>
        {
            [NotificationMonitors.QuoteRefresh] =
                new OutcomeHandler(NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok()),
        };

        await using var db = _fixture.NewDbContext();
        var ran = await new SchedulerRunner().RunDueAsync(
            db, handlers, DateTime.UtcNow, NullLogger.Instance, default,
            PublisherFor(throwing));

        Assert.Equal(1, ran);

        await using var read = _fixture.NewDbContext();
        var job = await read.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledger.LedgerId);
        Assert.True(job.Enabled);
        Assert.True(job.NextRunAt > DateTime.UtcNow);
    }

    private sealed class ThrowingSubscriber : INotificationSubscriber
    {
        public string SubscriberKey => "healthchecks";
        public string DisplayName => "Healthchecks";
        public SubscriberCapability Capability => SubscriberCapability.Heartbeat;

        public Task DeliverAsync(
            NotificationEvent notification, SubscriberConfig config, CancellationToken ct)
            => throw new HttpRequestException("no such host");
    }
}
