using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The consistency heartbeat: a daily ping while healthy, which doubles as the all-clear.
/// </summary>
/// <remarks>
/// A monitor that publishes when drift appears and nothing when it clears leaves the
/// warning on a ledger's list looking current until it ages out of retention, long after
/// the drift is resolved. The heartbeat is the publication that supersedes it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyClearedTests : DatabaseTest
{
    public ConsistencyClearedTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

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
        await using var db = Db.NewDbContext();
        db.LedgerNotificationSubscribers.Add(new LedgerNotificationSubscriberRow
        {
            LedgerId = ledgerId,
            SubscriberKey = "healthchecks",
            DisplayName = "switch:" + monitor,
            MinSeverity = NotificationSeverity.Info,
            Monitors = monitor,
            ConfigCiphertext = Db.NewLedgerKeyService().SealWithMasterKey(
                System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(
                        new SubscriberConfig("https://hc.invalid/ping")))),
        });
        await db.SaveChangesAsync();
    }

    private ConsistencyMonitor MonitorDeliveringTo(INotificationSubscriber spy) => new(
        Db.NewServiceFactory(),
        new NotificationPublisher(
            Db.NewServiceFactory(),
            Db.NewLedgerKeyService(),
            new[] { spy },
            NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
        NullLogger<ConsistencyMonitor>.Instance, TimeProvider.System);

    private ConsistencyMonitor NewMonitor() => new(
        Db.NewServiceFactory(),
        new NotificationPublisher(
            Db.NewServiceFactory(),
            Db.NewLedgerKeyService(),
            Array.Empty<INotificationSubscriber>(),
            NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
        NullLogger<ConsistencyMonitor>.Instance, TimeProvider.System);

    private async Task SeedEventAsync(Guid ledgerId, string severity, string eventKey)
    {
        await using var db = Db.NewDbContext();
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
        await using var db = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);

        await NewMonitor().CheckAllAsync();

        Assert.Contains("consistency.ok", await ConsistencyKeysAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task The_healthy_ping_carries_the_monitor_and_a_success_signal()
    {
        // Without these a heartbeat target could never match it: Wants() routes on
        // monitor name plus signal, so an event with neither reaches no switch at all.
        var ledger = await SyntheticLedger.CreateAsync(Db);

        await NewMonitor().CheckAllAsync();

        await using var db = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedEventAsync(ledger.LedgerId, NotificationSeverity.Warning, "consistency.drift");

        var monitor = NewMonitor();
        await monitor.CheckAllAsync();
        await monitor.CheckAllAsync();
        await monitor.CheckAllAsync();

        var keys = await ConsistencyKeysAsync(ledger.LedgerId);
        Assert.Single(keys, k => k == "consistency.ok");
    }

    /// <summary>
    /// The drift summary says what each projection's rows are out of step with: the
    /// transactions, for a figure derived from them ("N of M disagree with the
    /// transactions"); the brokerage, for a brokerage's holdings, in the Maintenance
    /// panel's words ("N of M brokerages out of step"). The heartbeat is true of every
    /// projection.
    /// </summary>
    [Fact]
    public async Task The_drift_summary_says_what_each_projection_disagrees_with()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security M", ticker: "SECM");
        Guid brokerage;
        await using (var factory = new ApiFactory(Db).WithoutDevAuth())
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
            var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
                new CreateAccountRequest { Name = "Brokerage M", AccountType = "investment", CurrencyCode = "USD" });
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            brokerage = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            // A second brokerage, which stays in step: the count is of every brokerage
            // checked, and more than one reads "brokerages".
            var other = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
                new CreateAccountRequest { Name = "Brokerage N", AccountType = "investment", CurrencyCode = "USD" });
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            var bought = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage,
                    PostedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = 10m,
                    Price = 10m,
                    Amount = 100m,
                });
            Assert.Equal(HttpStatusCode.Created, bought.StatusCode);
        }

        // Premise: the ledger is healthy, and the heartbeat says so of every projection.
        await NewMonitor().CheckAllAsync();
        await using (var db = Db.NewDbContext())
        {
            var ok = await db.LedgerEvents.AsNoTracking()
                .SingleAsync(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.ok");
            Assert.Equal("Every projection agrees.", ok.Summary);
            var state = await db.LedgerConsistencyStates.SingleAsync(r => r.LedgerId == ledger.LedgerId);
            state.LastCheckedAt = null;
            state.Healthy = null;
            await db.SaveChangesAsync();
        }

        // The write an edit before migration 274 made: the brokerage's row alone. The
        // trade's dollar price then disagrees with the brokerage's new currency, and the
        // holdings account with the brokerage.
        await using (var seed = Db.NewDbContext())
        {
            await seed.Accounts.Where(a => a.Id == brokerage)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "EUR"));
        }

        await NewMonitor().CheckAllAsync();

        await using var read = Db.NewDbContext();
        var drift = await read.LedgerEvents.AsNoTracking()
            .SingleAsync(e => e.LedgerId == ledger.LedgerId && e.EventKey == "consistency.drift");
        Assert.Equal(
            "trade_prices: 1 of 1 disagree with the transactions. "
            + "holdings_currency: 1 of 2 brokerages out of step.",
            drift.Summary);
    }

    [Fact]
    public async Task The_all_clear_is_info_so_it_does_not_fill_the_problem_list()
    {
        // Recovery is good news. The row exists so the earlier warning can be MARKED
        // resolved, not so anyone is told twice — and the list is filtered to warnings
        // and above precisely so good news cannot crowd out bad.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedEventAsync(ledger.LedgerId, NotificationSeverity.Warning, "consistency.drift");

        await NewMonitor().CheckAllAsync();

        await using var db = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await BindSwitchAsync(ledger.LedgerId, NotificationMonitors.Snapshot);

        var spy = new SpySwitch();
        await MonitorDeliveringTo(spy).CheckAllAsync();

        Assert.Empty(spy.Delivered);
    }
}
