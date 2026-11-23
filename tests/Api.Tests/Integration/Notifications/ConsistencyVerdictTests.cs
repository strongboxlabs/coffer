using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// A check a person runs, and a repair, record their verdict where the Overview reads it:
/// the stored state (its "checked" time) and the ledger's consistency notice (its Problems
/// widget, and the navigation's count).
/// </summary>
/// <remarks>
/// <para>The state a test reaches through the API is two projections out of step at once:
/// a brokerage whose currency changed past its edit, which leaves its trade's price in the
/// old currency (trade_prices) and its holdings account in the old currency
/// (holdings_currency). An install upgraded from an earlier version can hold exactly that,
/// and nothing in the API can make it now, so it is written past the API, as an earlier
/// version's edit wrote it.</para>
/// <para>Every <see cref="ApiFactory"/> runs one monitor pass at start, at a moment nothing
/// controls; each test waits for that pass's announcement before changing anything, so
/// the pass cannot see, or announce over, what the test does.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyVerdictTests : DatabaseTest
{
    public ConsistencyVerdictTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static HttpClient AuthedClient(ApiFactory factory, string cookie)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>A session cookie for <paramref name="userId"/>, a member added to the ledger.</summary>
    private async Task<string> SessionCookieForAsync(Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using var db = Db.NewDbContext();
        db.AuthSessions.Add(new Coffer.Api.Db.Entities.SessionRow
        {
            Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        return plaintext;
    }

    /// <summary>
    /// A brokerage and one purchase through the API, the monitor's start pass settled on
    /// them (healthy), then the brokerage's currency changed past its edit. Returns the
    /// brokerage.
    /// </summary>
    private async Task<Guid> TwoProjectionsOutOfStepAsync(HttpClient client, SyntheticLedger ledger)
    {
        var securityId = await ledger.AddSecurityAsync(name: "Security V", ticker: "SECV");
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Brokerage V", AccountType = "investment", CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var brokerage = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
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

        await WaitForStartPassAsync(ledger.LedgerId);

        await using (var seed = Db.NewDbContext())
        {
            await seed.Accounts.Where(a => a.Id == brokerage)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "EUR"));
        }
        return brokerage;
    }

    /// <summary>Block until the start pass has announced its verdict for this ledger.</summary>
    private async Task WaitForStartPassAsync(Guid ledgerId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using (var db = Db.NewDbContext())
            {
                if (await db.LedgerEvents.AsNoTracking()
                        .AnyAsync(e => e.LedgerId == ledgerId && e.EventKey == ConsistencyVerdicts.OkKey))
                    return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The monitor's start pass never announced a verdict for this ledger.");
    }

    private static async Task<LedgerConsistencyStateDto> StateAsync(HttpClient client, Guid ledgerId) =>
        (await client.GetFromJsonAsync<LedgerConsistencyStateDto>(
            $"/api/ledgers/{ledgerId}/balances/consistency/state"))!;

    private static async Task<List<OutstandingProblemDto>> OutstandingAsync(HttpClient client, Guid ledgerId) =>
        (await client.GetFromJsonAsync<List<OutstandingProblemDto>>(
            $"/api/ledgers/{ledgerId}/notifications/outstanding"))!;

    private static async Task<LedgerAttentionDto?> AttentionAsync(HttpClient client, Guid ledgerId) =>
        (await client.GetFromJsonAsync<List<LedgerAttentionDto>>("/api/ledgers/attention"))!
            .SingleOrDefault(a => a.LedgerId == ledgerId);

    private static async Task<HttpResponseMessage> CheckAsync(HttpClient client, Guid ledgerId) =>
        await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", content: null);

    private static async Task<ProjectionRepairResult> RepairAsync(HttpClient client, Guid ledgerId, string projection)
    {
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/balances/consistency/{projection}/repair", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
    }

    private async Task<List<(string Key, string Severity, string Summary, Dictionary<string, string> Detail)>>
        ConsistencyEventsAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var rows = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId && e.Topic == NotificationTopics.Consistency)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.EventKey, e.Severity, e.Summary, e.DetailJson })
            .ToListAsync();
        return rows.Select(r => (r.EventKey, r.Severity, r.Summary,
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.DetailJson)!))
            .ToList();
    }

    /// <summary>
    /// A person's check that finds what the monitor did not reaches the Overview: the stored
    /// verdict says unhealthy as of that check, the Problems list and the navigation count
    /// carry the drift, and pressing Check again records the time and sends nothing new.
    /// </summary>
    [Fact]
    public async Task A_persons_check_records_its_verdict_where_the_Overview_reads_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await TwoProjectionsOutOfStepAsync(client, ledger);

        // Premise: the Overview says what the start pass found, before anything changed.
        var before = await StateAsync(client, ledger.LedgerId);
        Assert.True(before.Healthy);
        Assert.Empty(await OutstandingAsync(client, ledger.LedgerId));

        var resp = await CheckAsync(client, ledger.LedgerId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False((await resp.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!.Healthy);

        var state = await StateAsync(client, ledger.LedgerId);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyProjections.HoldingsCurrency, ConsistencyProjections.TradePrices],
            state.FailingProjections);
        Assert.True(state.LastCheckedAt > before.LastCheckedAt,
            $"the check ran at {state.LastCheckedAt:O}, the start pass at {before.LastCheckedAt:O}");

        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal(("consistency.", ConsistencyVerdicts.DriftKey, NotificationSeverity.Warning),
            (problem.SubjectKey, problem.EventKey, problem.Severity));
        Assert.Equal(
            "trade_prices: 1 of 1 disagree with the transactions. "
            + "holdings_currency: 1 of 1 brokerage out of step.",
            problem.Summary);
        Assert.Equal(1, (await AttentionAsync(client, ledger.LedgerId))?.Count);

        // Pressed again with nothing changed: the time moves, no notice is sent.
        var announced = (await ConsistencyEventsAsync(ledger.LedgerId)).Count;
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        Assert.Equal(announced, (await ConsistencyEventsAsync(ledger.LedgerId)).Count);
        var again = await StateAsync(client, ledger.LedgerId);
        Assert.True(again.LastCheckedAt > state.LastCheckedAt);
        Assert.False(again.Healthy);
    }

    /// <summary>
    /// Anyone who reads the ledger may run the check, a viewer included: the check is a POST,
    /// which the ledger gate takes as a write, and only the endpoint's read-access override
    /// lets a viewer through. A viewer's check records its verdict and announces a changed
    /// one as an owner's does, through the service context, which a viewer's role cannot
    /// write through.
    /// </summary>
    [Fact]
    public async Task A_viewers_check_records_its_verdict_and_announces_a_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewerId = await ledger.AddMemberAsync("viewer");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var owner = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        using var viewer = AuthedClient(factory, await SessionCookieForAsync(viewerId));
        await TwoProjectionsOutOfStepAsync(owner, ledger);
        var before = await StateAsync(viewer, ledger.LedgerId);
        Assert.True(before.Healthy);
        Assert.Empty(await OutstandingAsync(viewer, ledger.LedgerId));

        var resp = await CheckAsync(viewer, ledger.LedgerId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False((await resp.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!.Healthy);
        var state = await StateAsync(viewer, ledger.LedgerId);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyProjections.HoldingsCurrency, ConsistencyProjections.TradePrices],
            state.FailingProjections);
        Assert.True(state.LastCheckedAt > before.LastCheckedAt,
            $"the check ran at {state.LastCheckedAt:O}, the start pass at {before.LastCheckedAt:O}");
        var problem = Assert.Single(await OutstandingAsync(viewer, ledger.LedgerId));
        Assert.Equal((ConsistencyVerdicts.DriftKey, NotificationSeverity.Warning),
            (problem.EventKey, problem.Severity));
    }

    /// <summary>
    /// The stored verdict alone does not decide whether a person's check announces: an
    /// earlier version's repair published an info event that cleared the ledger's problem
    /// and left the stored verdict unhealthy, so a check that finds that same verdict must
    /// still announce it, or the Overview goes on saying there is no problem.
    /// </summary>
    [Fact]
    public async Task A_persons_check_announces_a_verdict_the_ledgers_notice_contradicts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await TwoProjectionsOutOfStepAsync(client, ledger);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);

        // What an earlier version's repair wrote: an info event, with the verdict untouched.
        await using (var db = Db.NewDbContext())
        {
            db.LedgerEvents.Add(new Coffer.Api.Db.Entities.LedgerEventRow
            {
                LedgerId = ledger.LedgerId,
                Severity = NotificationSeverity.Info,
                Topic = NotificationTopics.Consistency,
                EventKey = "consistency.repaired",
                Summary = "Repaired posting_counts: nothing needed changing.",
                DetailJson = "{}",
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await OutstandingAsync(client, ledger.LedgerId));
        Assert.False((await StateAsync(client, ledger.LedgerId)).Healthy);

        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);

        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal(ConsistencyVerdicts.DriftKey, problem.EventKey);
    }

    /// <summary>
    /// Repairing one of two projections out of step leaves the ledger's problem outstanding
    /// and says what remains; repairing the other clears it. Each repair answers what it
    /// rewrote and the verdict after it, records that verdict, and its event says both.
    /// </summary>
    [Fact]
    public async Task A_repair_clears_the_problem_only_when_nothing_else_disagrees()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await TwoProjectionsOutOfStepAsync(client, ledger);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        Assert.Single(await OutstandingAsync(client, ledger.LedgerId));

        // The first repair: what it rewrote, and that another projection still disagrees.
        var prices = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.TradePrices);
        Assert.Equal((false, 1), (prices.Repaired.Healthy, prices.Repaired.MismatchedCount));
        Assert.True(prices.ProjectionAfter().Healthy);
        Assert.NotNull(prices.After);
        Assert.False(prices.After.Healthy);
        Assert.Equal(ConsistencyProjections.HoldingsCurrency,
            Assert.Single(prices.After.Projections, p => !p.Healthy).Projection);

        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning),
            (problem.EventKey, problem.Severity));
        Assert.Equal(1, (await AttentionAsync(client, ledger.LedgerId))?.Count);
        var state = await StateAsync(client, ledger.LedgerId);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyProjections.HoldingsCurrency], state.FailingProjections);
        Assert.NotNull(state.LastRepairedAt);

        var first = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal(ConsistencyVerdicts.RepairedKey, first.Key);
        Assert.Equal(
            "Repaired trade_prices: rewrote the 1 that disagreed with the transactions. "
            + "Still to fix: holdings_currency: 1 of 1 brokerage out of step.",
            first.Summary);
        Assert.Equal(new Dictionary<string, string>
        {
            ["projection"] = ConsistencyProjections.TradePrices,
            ["rewritten"] = "1",
            ["remaining"] = "0",
            ["healthy"] = "false",
            ["still_to_fix"] = "holdings_currency: 1",
        }, first.Detail);

        // The second repair: nothing disagrees any more, so the problem clears.
        var currency = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.HoldingsCurrency);
        Assert.Equal((false, 1), (currency.Repaired.Healthy, currency.Repaired.MismatchedCount));
        Assert.NotNull(currency.After);
        Assert.True(currency.After.Healthy);

        Assert.Empty(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Null(await AttentionAsync(client, ledger.LedgerId));
        state = await StateAsync(client, ledger.LedgerId);
        Assert.True(state.Healthy);
        Assert.Empty(state.FailingProjections);

        var second = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Info), (second.Key, second.Severity));
        Assert.Equal(
            "Repaired holdings_currency: recorded the holdings of the 1 brokerage out of step in its own currency. "
            + "Every projection agrees.",
            second.Summary);
        Assert.Equal(new Dictionary<string, string>
        {
            ["projection"] = ConsistencyProjections.HoldingsCurrency,
            ["rewritten"] = "1",
            ["remaining"] = "0",
            ["healthy"] = "true",
        }, second.Detail);

        // The check the panel runs after a repair finds what the repair recorded, so it
        // sends nothing.
        var announced = (await ConsistencyEventsAsync(ledger.LedgerId)).Count;
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        Assert.Equal(announced, (await ConsistencyEventsAsync(ledger.LedgerId)).Count);
    }

    /// <summary>
    /// The holdings-currency summaries count brokerages in the Maintenance panel's words:
    /// "brokerages", and "their own currency", for more than one.
    /// </summary>
    [Fact]
    public async Task The_holdings_currency_summaries_count_brokerages()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        var brokerages = new List<Guid>();
        foreach (var name in new[] { "Brokerage W", "Brokerage X" })
        {
            var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
                new CreateAccountRequest { Name = name, AccountType = "investment", CurrencyCode = "USD" });
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            brokerages.Add((await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }
        await WaitForStartPassAsync(ledger.LedgerId);

        // Both brokerages' currencies changed past the edit, as an earlier version's edit
        // left them: each one's holdings are still in the old currency.
        await using (var seed = Db.NewDbContext())
        {
            await seed.Accounts.Where(a => brokerages.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "EUR"));
        }

        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal("holdings_currency: 2 of 2 brokerages out of step.", problem.Summary);

        var repaired = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.HoldingsCurrency);
        Assert.Equal(2, repaired.Repaired.MismatchedCount);
        Assert.True(repaired.After?.Healthy);
        Assert.Equal(
            "Repaired holdings_currency: recorded the holdings of the 2 brokerages out of step in their own currency. "
            + "Every projection agrees.",
            (await ConsistencyEventsAsync(ledger.LedgerId)).Last().Summary);
    }

    /// <summary>
    /// The Category amounts finding (ADR-0114 D13) is a problem like any repairable class:
    /// its summary counts category lines in the Maintenance panel's words, "N of M category
    /// lines out of step", and its repair, with nothing else out of step, clears it.
    /// </summary>
    [Fact]
    public async Task The_category_amounts_summary_counts_category_lines_and_its_repair_clears_the_problem()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await WaitForStartPassAsync(ledger.LedgerId);

        // One category line kept at its own figure beside an account in another currency, as
        // the released import left one, its balances recomputed as the seed helpers do; one
        // mirrored.
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        var postedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var (_, categoryLeg) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, postedAt);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -3m, postedAt);
        await using (var seed = Db.NewDbContext())
        {
            await using var write = await seed.BeginLedgerWriteAsync(ledger.LedgerId);
            (await seed.TxnLegs.SingleAsync(l => l.Id == categoryLeg)).Amount = 11m;
            await seed.SaveChangesAsync();
            await write.CommitAsync();
        }
        await ledger.RecomputeBalancesAsync([category.Id]);

        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal((ConsistencyVerdicts.DriftKey, "category_amounts: 1 of 2 category lines out of step."),
            (problem.EventKey, problem.Summary));
        Assert.Equal([ConsistencyProjections.CategoryAmounts], (await StateAsync(client, ledger.LedgerId)).FailingProjections);

        var repaired = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.CategoryAmounts);
        Assert.Equal(1, repaired.Repaired.MismatchedCount);
        Assert.True(repaired.After?.Healthy);
        Assert.Empty(await OutstandingAsync(client, ledger.LedgerId));
        var last = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Info), (last.Key, last.Severity));
        // What this repair does, in its own words (ADR-0114 D13), not the generic rewrite.
        Assert.Equal(
            "Repaired category_amounts: set the 1 category line out of step to the paying account's amount, "
            + "negated. Every projection agrees.",
            last.Summary);
    }

    /// <summary>
    /// The Category amounts repair's summary counts "category lines" for more than one.
    /// </summary>
    [Fact]
    public async Task The_category_amounts_repair_summary_counts_category_lines()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await WaitForStartPassAsync(ledger.LedgerId);

        // Two category lines kept at their own figures, as the released import left them.
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        var postedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var (_, first) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, postedAt);
        var (_, second) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -3m, postedAt);
        await using (var seed = Db.NewDbContext())
        {
            await using var write = await seed.BeginLedgerWriteAsync(ledger.LedgerId);
            (await seed.TxnLegs.SingleAsync(l => l.Id == first)).Amount = 11m;
            (await seed.TxnLegs.SingleAsync(l => l.Id == second)).Amount = 4m;
            await seed.SaveChangesAsync();
            await write.CommitAsync();
        }
        await ledger.RecomputeBalancesAsync([category.Id]);

        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        var repaired = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.CategoryAmounts);
        Assert.Equal(2, repaired.Repaired.MismatchedCount);
        Assert.True(repaired.After?.Healthy);
        Assert.Equal(
            "Repaired category_amounts: set the 2 category lines out of step to the paying account's amount, "
            + "negated. Every projection agrees.",
            (await ConsistencyEventsAsync(ledger.LedgerId)).Last().Summary);
    }

    /// <summary>
    /// A person's check that found nothing new records the time and sends nothing, so the
    /// monitor's cadence counts announcements as well as checks: the ledger whose last
    /// announcement is older than the cadence gets its pass, however recent its check.
    /// </summary>
    [Fact]
    public async Task A_persons_check_does_not_hold_back_the_monitors_announcement()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var publisher = new NotificationPublisher(
            Db.NewServiceFactory(), Db.NewLedgerKeyService(), Array.Empty<INotificationSubscriber>(),
            NullLogger<NotificationPublisher>.Instance, clock);
        var monitor = new ConsistencyMonitor(
            Db.NewServiceFactory(), publisher, NullLogger<ConsistencyMonitor>.Instance, clock);

        await monitor.CheckAllAsync();
        Assert.Single(await ConsistencyEventsAsync(ledger.LedgerId), e => e.Key == ConsistencyVerdicts.OkKey);

        // Past the cadence, a person checks: the same verdict, so nothing is announced.
        clock.Advance(ConsistencyMonitor.CheckEvery + TimeSpan.FromHours(1));
        LedgerConsistencyReport report;
        var checkedAt = clock.GetUtcNow().UtcDateTime;
        await using (var db = Db.NewServiceDbContext())
        {
            report = await new LedgerConsistencyRepository(
                    db, new RegisterRepository(db, clock), new HoldingsRecomputeService(db), clock)
                .CheckAsync(ledger.LedgerId);
        }
        Assert.False(await new ConsistencyVerdicts(Db.NewServiceFactory(), publisher, clock)
            .RecordCheckAsync(ledger.LedgerId, report, ConsistencyCheckOrigin.Person, checkedAt));
        Assert.Single(await ConsistencyEventsAsync(ledger.LedgerId));

        // The monitor's pass still runs, and pings.
        await monitor.CheckAllAsync();
        Assert.Equal(2, (await ConsistencyEventsAsync(ledger.LedgerId)).Count(e => e.Key == ConsistencyVerdicts.OkKey));
    }

    /// <summary>
    /// A repair whose check fails after the rewrite has committed is recorded all the same:
    /// the rewrite stands, so <c>last_repaired_at</c> and a <c>consistency.repaired</c> event
    /// are written, the verdict taken before the rewrite is cleared ("not checked yet"), the
    /// event is a warning that says no check followed, and the answer says what was rewritten
    /// rather than that the repair failed.
    /// </summary>
    [Fact]
    public async Task A_repair_whose_check_fails_is_still_recorded()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var afterCommit = new AfterRepairCommit();
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<Coffer.Api.Db.AppDbContext>(o => o.AddInterceptors(afterCommit)));
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await TwoProjectionsOutOfStepAsync(client, ledger);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);

        afterCommit.Arm(_ => throw new InvalidOperationException("The check after the repair failed."));
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.HoldingsCurrency}/repair",
            content: null);

        Assert.True(afterCommit.Fired, "the check after the repair never ran a command");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var repair = (await resp.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.Equal((ConsistencyProjections.HoldingsCurrency, false, 1),
            (repair.Projection, repair.Repaired.Healthy, repair.Repaired.MismatchedCount));
        Assert.Null(repair.After);

        var state = await StateAsync(client, ledger.LedgerId);
        Assert.NotNull(state.LastRepairedAt);
        Assert.Null(state.Healthy);
        Assert.Null(state.LastCheckedAt);
        Assert.Empty(state.FailingProjections);

        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning), (recorded.Key, recorded.Severity));
        Assert.Equal(
            "Repaired holdings_currency: recorded the holdings of the 1 brokerage out of step in its own currency. "
            + "The check after it did not finish, so what still disagrees is not known.",
            recorded.Summary);
        Assert.Equal(new Dictionary<string, string>
        {
            ["projection"] = ConsistencyProjections.HoldingsCurrency,
            ["rewritten"] = "1",
            ["checked"] = "false",
        }, recorded.Detail);
        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal(ConsistencyVerdicts.RepairedKey, problem.EventKey);

        // The rewrite stands: the next check finds only the projection it did not touch.
        afterCommit.Disarm();
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        Assert.Equal([ConsistencyProjections.TradePrices], (await StateAsync(client, ledger.LedgerId)).FailingProjections);
    }

    /// <summary>
    /// A client that hangs up once a repair's rewrite has committed (a closed tab, a proxy's
    /// timeout) stops neither the check after it nor the record: both run to the end, so the
    /// stored verdict, <c>last_repaired_at</c> and the event are there although nobody read
    /// the answer.
    /// </summary>
    [Fact]
    public async Task A_repair_is_recorded_when_its_client_hangs_up_after_the_rewrite()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var afterCommit = new AfterRepairCommit();
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<Coffer.Api.Db.AppDbContext>(o => o.AddInterceptors(afterCommit)));
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await TwoProjectionsOutOfStepAsync(client, ledger);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        var accessor = factory.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

        // The request is aborted as the check after the rewrite sends its first command.
        afterCommit.Arm(_ => accessor.HttpContext!.Abort());
        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.HoldingsCurrency}/repair",
            content: null));
        Assert.True(afterCommit.Fired, "the check after the repair never ran a command");

        LedgerConsistencyStateDto? state = null;
        for (var attempt = 0; attempt < 300 && state?.LastRepairedAt is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(100);
            state = await StateAsync(client, ledger.LedgerId);
        }
        Assert.NotNull(state!.LastRepairedAt);
        Assert.False(state.Healthy);
        Assert.Equal(state.LastRepairedAt, state.LastCheckedAt);
        Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);

        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning), (recorded.Key, recorded.Severity));
        Assert.Equal(
            "Repaired holdings_currency: recorded the holdings of the 1 brokerage out of step in its own currency. "
            + "Still to fix: trade_prices: 1 of 1 disagree with the transactions.",
            recorded.Summary);
    }

    /// <summary>
    /// A client that hangs up as a repair's rewrite sends its first command stops neither the
    /// rewrite nor its record. Several rewrites commit in pieces, so one stopped partway would
    /// leave part of it committed and nothing saying so: the rewrite runs to the end, and the
    /// stored verdict, <c>last_repaired_at</c> and the event are there although nobody read
    /// the answer.
    /// </summary>
    [Fact]
    public async Task A_repair_runs_to_the_end_and_is_recorded_when_its_client_hangs_up_during_the_rewrite()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var rewriteStart = new AtRewriteStart();
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<Coffer.Api.Db.AppDbContext>(o => o.AddInterceptors(rewriteStart)));
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        var brokerage = await TwoProjectionsOutOfStepAsync(client, ledger);
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        var accessor = factory.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

        // The request is aborted as the rewrite sends its first command.
        rewriteStart.Arm(_ => accessor.HttpContext!.Abort());
        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.HoldingsCurrency}/repair",
            content: null));
        Assert.True(rewriteStart.Fired, "the rewrite never ran a command");

        LedgerConsistencyStateDto? state = null;
        for (var attempt = 0; attempt < 300 && state?.LastRepairedAt is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(100);
            state = await StateAsync(client, ledger.LedgerId);
        }
        Assert.NotNull(state!.LastRepairedAt);
        Assert.False(state.Healthy);
        Assert.Equal(state.LastRepairedAt, state.LastCheckedAt);
        Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);

        // The rewrite ran to the end: the holdings account is in its brokerage's currency.
        await using (var db = Db.NewDbContext())
        {
            var holdings = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == brokerage).Select(a => a.HoldingsAccountId).SingleAsync();
            Assert.Equal("EUR", await db.Accounts.AsNoTracking()
                .Where(a => a.Id == holdings).Select(a => a.CurrencyCode).SingleAsync());
        }

        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning), (recorded.Key, recorded.Severity));
        Assert.Equal(
            "Repaired holdings_currency: recorded the holdings of the 1 brokerage out of step in its own currency. "
            + "Still to fix: trade_prices: 1 of 1 disagree with the transactions.",
            recorded.Summary);
    }

    /// <summary>
    /// A repair whose rewrite throws partway, for a reason other than cancellation, answers
    /// the error, and what it committed stands: the verdict taken before it no longer
    /// describes the data, so it is cleared ("not checked yet", due on the monitor's next
    /// tick), <c>last_repaired_at</c> keeps the last repair that completed, and nothing is
    /// announced, so the ledger's problem stays outstanding. The clearing is as of when the
    /// rewrite stopped: a check that began while the rewrite ran, and records once the repair
    /// has answered, is not written, since what it read may predate a piece the rewrite
    /// committed.
    /// </summary>
    [Fact]
    public async Task A_repair_whose_rewrite_fails_partway_answers_the_error_and_clears_the_verdict_as_of_when_it_stopped()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var secondPiece = new AtNthCommand("recompute_posting_counts_for_header", 2);
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<Coffer.Api.Db.AppDbContext>(o => o.AddInterceptors(secondPiece)));
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        var clock = factory.Services.GetRequiredService<TimeProvider>();

        // Two purchases, each its own header, so the posting-counts rewrite commits in two
        // pieces, one header at a time.
        var securityId = await ledger.AddSecurityAsync(name: "Security V", ticker: "SECV");
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Brokerage V", AccountType = "investment", CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var brokerage = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var day in new[] { 1, 2 })
        {
            var bought = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage,
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = 10m,
                    Price = 10m,
                    Amount = 100m,
                });
            Assert.Equal(HttpStatusCode.Created, bought.StatusCode);
        }
        await WaitForStartPassAsync(ledger.LedgerId);

        // A repair that completed, so last_repaired_at holds a time to keep.
        await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.PostingCounts);
        var repairedBefore = (await StateAsync(client, ledger.LedgerId)).LastRepairedAt;
        Assert.NotNull(repairedBefore);

        // Every header's posting count overstated past the writers, as a writer that skipped
        // the recompute leaves it, and a person's check records the drift.
        int headers;
        await using (var seed = Db.NewDbContext())
        {
            headers = await seed.TxnHeaders.CountAsync(h => h.LedgerId == ledger.LedgerId);
            await seed.TxnLegs.Where(l => l.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeaderTotalPostings, l => l.HeaderTotalPostings + 1));
        }
        Assert.True(headers >= 2, $"the rewrite must commit in more than one piece; the ledger has {headers} header(s)");
        Assert.Equal(HttpStatusCode.OK, (await CheckAsync(client, ledger.LedgerId)).StatusCode);
        Assert.Equal([ConsistencyProjections.PostingCounts], (await StateAsync(client, ledger.LedgerId)).FailingProjections);
        var announced = await ConsistencyEventsAsync(ledger.LedgerId);
        Assert.Equal(ConsistencyVerdicts.DriftKey, announced.Last().Key);

        // The rewrite's second piece fails, with the first committed. A person's check
        // begins while the rewrite runs, a moment after its first piece committed, and the
        // rewrite stops a moment after that.
        DateTime? checkBegan = null;
        secondPiece.Arm(_ =>
        {
            checkBegan = AMomentLater(clock);
            AMomentLater(clock);
            throw new Npgsql.PostgresException(
                "The rewrite's second piece failed.", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.InternalError);
        });
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.PostingCounts}/repair",
            content: null);

        Assert.True(secondPiece.Fired, "the rewrite never reached its second piece");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        // That check finishes after the repair has answered, and records with the time it
        // began, as the check endpoint records. It began before the rewrite stopped, so the
        // clearing stands and its verdict is neither written nor announced.
        Assert.NotNull(checkBegan);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<ConsistencyVerdicts>().RecordCheckAsync(
                ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Person, checkBegan.Value));
        }

        var state = await StateAsync(client, ledger.LedgerId);
        Assert.Null(state.Healthy);
        Assert.Null(state.LastCheckedAt);
        Assert.Empty(state.FailingProjections);
        Assert.Equal(repairedBefore, state.LastRepairedAt);

        // Nothing announced: no event says the ledger is healthy, and the drift stays
        // outstanding.
        Assert.Equal(announced.Select(e => (e.Key, e.Severity, e.Summary)),
            (await ConsistencyEventsAsync(ledger.LedgerId)).Select(e => (e.Key, e.Severity, e.Summary)));
        var problem = Assert.Single(await OutstandingAsync(client, ledger.LedgerId));
        Assert.Equal((ConsistencyVerdicts.DriftKey, NotificationSeverity.Warning), (problem.EventKey, problem.Severity));

        // What the first piece committed stands: the next check finds one header fewer.
        var checkedAgain = await CheckAsync(client, ledger.LedgerId);
        Assert.Equal(HttpStatusCode.OK, checkedAgain.StatusCode);
        var report = (await checkedAgain.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!;
        Assert.Equal(headers - 1,
            Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.PostingCounts).MismatchedCount);
    }

    /// <summary>Every projection healthy, as a check of a ledger in step reports it.</summary>
    private static LedgerConsistencyReport HealthyReport() =>
        new(true,
            ConsistencyProjections.All.Select(p => new ProjectionConsistency(p, true, 1, 0, [])).ToList(),
            [], []);

    /// <summary>The trade prices out of step, the rest healthy.</summary>
    private static LedgerConsistencyReport TradePricesDrifted() =>
        new(false,
            ConsistencyProjections.All
                .Select(p => p == ConsistencyProjections.TradePrices
                    ? new ProjectionConsistency(p, false, 1, 1, [])
                    : new ProjectionConsistency(p, true, 1, 0, []))
                .ToList(),
            [], []);

    private NotificationPublisher Publisher(TimeProvider clock, ServiceDbContextFactory? factory = null) =>
        new(factory ?? Db.NewServiceFactory(), Db.NewLedgerKeyService(), Array.Empty<INotificationSubscriber>(),
            NullLogger<NotificationPublisher>.Instance, clock);

    /// <summary>
    /// A monitor pass that began before a repair committed, and records after the repair
    /// did, neither overwrites the repair's verdict nor announces the drift the repair
    /// fixed: its verdict is older than the one stored.
    /// </summary>
    [Fact]
    public async Task A_monitor_pass_that_began_before_a_repair_does_not_overwrite_or_announce_over_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);

        // The pass begins, and finds the trade prices out of step.
        var passBegan = clock.GetUtcNow().UtcDateTime;
        var found = TradePricesDrifted();
        // While it walks the rest, a repair rewrites them, checks and records.
        clock.Advance(TimeSpan.FromMinutes(1));
        var repairedAt = clock.GetUtcNow().UtcDateTime;
        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.TradePrices,
                found.Projections.Single(p => p.Projection == ConsistencyProjections.TradePrices),
                HealthyReport()),
            repairedAt);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await verdicts.RecordCheckAsync(
            ledger.LedgerId, found, ConsistencyCheckOrigin.Monitor, passBegan));

        await using var db = Db.NewServiceDbContext();
        var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
        Assert.True(state.Healthy);
        Assert.Empty(state.FailingProjections);
        Assert.Equal(state.LastRepairedAt, state.LastCheckedAt);
        var events = await ConsistencyEventsAsync(ledger.LedgerId);
        Assert.Equal([(ConsistencyVerdicts.RepairedKey, NotificationSeverity.Info)],
            events.Select(e => (e.Key, e.Severity)).ToList());
    }

    /// <summary>Whether the ledger's consistency subject is outstanding, by the Overview's own read.</summary>
    private async Task<bool> ConsistencyOutstandingAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return (await OutstandingSubjects.ListAsync(db, [ledgerId], CancellationToken.None))
            .Any(s => s.Subject == EventSubjects.Of(ConsistencyVerdicts.DriftKey));
    }

    /// <summary>A test clock at whole microseconds, as a <c>timestamptz</c> holds a time, so a
    /// time read back compares equal to the one written.</summary>
    private static TestClock WholeMicrosecondClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new TestClock(now.AddTicks(-(now.Ticks % 10)));
    }

    /// <summary>
    /// The clock's time once it has moved on more than a millisecond: far more than a
    /// <c>timestamptz</c>'s microsecond, so a time read before this call and one read after
    /// it stay in that order once stored.
    /// </summary>
    private static DateTime AMomentLater(TimeProvider clock)
    {
        var from = clock.GetUtcNow();
        DateTimeOffset now;
        while ((now = clock.GetUtcNow()) - from <= TimeSpan.FromMilliseconds(1)) Thread.Sleep(1);
        return now.UtcDateTime;
    }

    /// <summary>What a posting-counts repair rewrote: one header.</summary>
    private static ProjectionConsistency PostingCountsRewritten() =>
        new(ConsistencyProjections.PostingCounts, false, 1, 1, []);

    /// <summary>
    /// A repair whose check after it finished after a later check had recorded follows the
    /// verdict that stands, the later check's, and not its own: its event leaves the
    /// ledger's consistency subject outstanding exactly when the stored verdict is
    /// unhealthy. Every consistency event shares that one subject, so an event read off the
    /// repair's own check would clear it on a ledger stored as unhealthy, or reopen it on one
    /// stored as healthy.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repairs_event_follows_the_verdict_a_later_check_recorded(bool laterHealthy)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);

        // The monitor's verdict before the repair is the other one, so the later check
        // announces what it finds.
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            laterHealthy ? TradePricesDrifted() : HealthyReport(), ConsistencyCheckOrigin.Monitor,
            clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        // The repair's rewrite commits, and the check after it begins.
        var repairedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        // A person's check begins after it, and records first.
        var laterBegan = clock.GetUtcNow().UtcDateTime;
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            laterHealthy ? HealthyReport() : TradePricesDrifted(), ConsistencyCheckOrigin.Person, laterBegan));
        Assert.Equal(!laterHealthy, await ConsistencyOutstandingAsync(ledger.LedgerId));
        clock.Advance(TimeSpan.FromMinutes(1));

        // The repair's check finishes with the other verdict, and records.
        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(),
                laterHealthy ? TradePricesDrifted() : HealthyReport()),
            repairedAt);

        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Equal(laterHealthy, state.Healthy);
            Assert.Equal(laterBegan, state.LastCheckedAt);
            Assert.Equal(repairedAt, state.LastRepairedAt);
        }
        Assert.Equal(!laterHealthy, await ConsistencyOutstandingAsync(ledger.LedgerId));

        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        const string rewrote = "Repaired posting_counts: rewrote the 1 that disagreed with the transactions.";
        if (laterHealthy)
        {
            Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Info), (recorded.Key, recorded.Severity));
            Assert.Equal(rewrote + " A check that began after it found every projection agrees.", recorded.Summary);
            Assert.Equal(new Dictionary<string, string>
            {
                ["projection"] = ConsistencyProjections.PostingCounts,
                ["rewritten"] = "1",
                ["superseded"] = "true",
                ["healthy"] = "true",
            }, recorded.Detail);
        }
        else
        {
            Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning), (recorded.Key, recorded.Severity));
            Assert.Equal(rewrote + " A check that began after it found what still disagrees: trade_prices.",
                recorded.Summary);
            Assert.Equal(new Dictionary<string, string>
            {
                ["projection"] = ConsistencyProjections.PostingCounts,
                ["rewritten"] = "1",
                ["superseded"] = "true",
                ["healthy"] = "false",
                ["still_to_fix"] = ConsistencyProjections.TradePrices,
            }, recorded.Detail);
        }
    }

    /// <summary>
    /// A repair whose check after it finished after a restore had cleared the verdict leaves
    /// the verdict cleared, and its event does not clear the ledger's consistency subject on
    /// a ledger whose verdict is not known: it is a warning saying so.
    /// </summary>
    [Fact]
    public async Task A_repairs_event_does_not_clear_the_problem_when_the_verdict_was_cleared_since()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);

        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        Assert.True(await ConsistencyOutstandingAsync(ledger.LedgerId));
        clock.Advance(TimeSpan.FromMinutes(1));
        var repairedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        // A restore commits while the repair's check runs, and clears the verdict as the
        // restore endpoint does.
        var restoredAt = clock.GetUtcNow().UtcDateTime;
        await using (var db = Db.NewServiceDbContext())
        {
            await db.LedgerConsistencyStates
                .Where(r => r.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.LastCheckedAt, (DateTime?)null)
                    .SetProperty(r => r.Healthy, (bool?)null)
                    .SetProperty(r => r.FailingProjections, Array.Empty<string>())
                    .SetProperty(r => r.UpdatedAt, restoredAt));
        }
        clock.Advance(TimeSpan.FromMinutes(1));

        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), HealthyReport()),
            repairedAt);

        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Null(state.Healthy);
            Assert.Null(state.LastCheckedAt);
            Assert.Equal(repairedAt, state.LastRepairedAt);
        }
        Assert.True(await ConsistencyOutstandingAsync(ledger.LedgerId));
        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Warning), (recorded.Key, recorded.Severity));
        Assert.Equal(
            "Repaired posting_counts: rewrote the 1 that disagreed with the transactions. "
            + "The verdict has been cleared since, so what still disagrees is not known.",
            recorded.Summary);
        Assert.Equal(new Dictionary<string, string>
        {
            ["projection"] = ConsistencyProjections.PostingCounts,
            ["rewritten"] = "1",
            ["superseded"] = "true",
        }, recorded.Detail);
    }

    /// <summary>
    /// A repair whose check failed clears the verdict as of its rewrite, not as of when it
    /// recorded: a later repair, whose rewrite committed after the first one's and whose
    /// check finished after the first one recorded, writes its own verdict, and its event
    /// says what its check found, not that the verdict was cleared since.
    /// </summary>
    [Fact]
    public async Task A_repair_that_began_after_another_repairs_rewrite_writes_over_the_verdict_that_one_cleared()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        // The first repair's rewrite commits, and its check begins.
        var firstAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        // The second repair's rewrite commits, and its check begins.
        var secondAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        // The first one's check fails, and it records: the verdict is cleared.
        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), null),
            firstAt);
        clock.Advance(TimeSpan.FromMinutes(1));

        // The second one's check finds every projection agrees, and records.
        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), HealthyReport()),
            secondAt);

        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.True(state.Healthy);
            Assert.Equal(secondAt, state.LastCheckedAt);
            Assert.Equal(secondAt, state.LastRepairedAt);
        }
        Assert.False(await ConsistencyOutstandingAsync(ledger.LedgerId));
        var recorded = (await ConsistencyEventsAsync(ledger.LedgerId)).Last();
        Assert.Equal((ConsistencyVerdicts.RepairedKey, NotificationSeverity.Info), (recorded.Key, recorded.Severity));
        Assert.Equal(
            "Repaired posting_counts: rewrote the 1 that disagreed with the transactions. Every projection agrees.",
            recorded.Summary);
        Assert.Equal(new Dictionary<string, string>
        {
            ["projection"] = ConsistencyProjections.PostingCounts,
            ["rewritten"] = "1",
            ["remaining"] = "0",
            ["healthy"] = "true",
        }, recorded.Detail);
    }

    /// <summary>
    /// A repair whose rewrite failed partway clears the verdict as of when the rewrite
    /// stopped: a check that began before then, and records after the clearing, is not
    /// written, and one that began after it is. Neither <c>last_repaired_at</c> nor an event
    /// is written by the clearing.
    /// </summary>
    [Fact]
    public async Task A_failed_rewrite_clears_the_verdict_as_of_when_it_stopped()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        var announced = (await ConsistencyEventsAsync(ledger.LedgerId)).Count;
        clock.Advance(TimeSpan.FromMinutes(1));
        // A person's check begins while the rewrite runs.
        var earlierBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        // The rewrite throws partway.
        var stoppedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        // A monitor pass begins after it.
        var laterBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        await verdicts.RecordFailedRewriteAsync(ledger.LedgerId, stoppedAt);

        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Null(state.Healthy);
            Assert.Null(state.LastCheckedAt);
            Assert.Empty(state.FailingProjections);
            Assert.Null(state.LastRepairedAt);
            Assert.Equal(stoppedAt, state.UpdatedAt);
        }
        Assert.Equal(announced, (await ConsistencyEventsAsync(ledger.LedgerId)).Count);
        Assert.True(await ConsistencyOutstandingAsync(ledger.LedgerId));

        // The check that began before the rewrite stopped may have read a piece of the data
        // from before it, so it is not written; the pass that began after it is.
        Assert.False(await verdicts.RecordCheckAsync(
            ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Person, earlierBegan));
        await using (var db = Db.NewServiceDbContext())
        {
            Assert.Null((await db.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId)).Healthy);
        }
        Assert.True(await verdicts.RecordCheckAsync(
            ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Monitor, laterBegan));
        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.True(state.Healthy);
            Assert.Equal(laterBegan, state.LastCheckedAt);
        }
    }

    /// <summary>
    /// A repair whose rewrite failed partway does not clear a verdict that a check which
    /// began after the rewrite stopped has already recorded: that check saw what the rewrite
    /// committed.
    /// </summary>
    [Fact]
    public async Task A_failed_rewrite_does_not_clear_a_verdict_a_later_check_recorded()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        var stoppedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        var laterBegan = clock.GetUtcNow().UtcDateTime;
        Assert.True(await verdicts.RecordCheckAsync(
            ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Person, laterBegan));
        clock.Advance(TimeSpan.FromMinutes(1));

        await verdicts.RecordFailedRewriteAsync(ledger.LedgerId, stoppedAt);

        await using var db = Db.NewServiceDbContext();
        var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
        Assert.True(state.Healthy);
        Assert.Equal(laterBegan, state.LastCheckedAt);
        Assert.False(await ConsistencyOutstandingAsync(ledger.LedgerId));
    }

    /// <summary>
    /// A repair older than a restore records after it without moving the time the cleared
    /// verdict is compared with: a check that began after the restore, and records after
    /// that repair, writes its verdict.
    /// </summary>
    [Fact]
    public async Task A_superseded_repair_does_not_hold_back_a_check_that_began_after_the_restore()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            HealthyReport(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        var repairedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        // A restore commits while the repair's check runs, and clears the verdict as the
        // restore endpoint does.
        var restoredAt = clock.GetUtcNow().UtcDateTime;
        await using (var db = Db.NewServiceDbContext())
        {
            await db.LedgerConsistencyStates
                .Where(r => r.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.LastCheckedAt, (DateTime?)null)
                    .SetProperty(r => r.Healthy, (bool?)null)
                    .SetProperty(r => r.FailingProjections, Array.Empty<string>())
                    .SetProperty(r => r.UpdatedAt, restoredAt));
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        // A monitor pass begins after the restore.
        var passBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        // The repair's check finishes, and it records, superseded by the restore.
        await verdicts.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), HealthyReport()),
            repairedAt);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await verdicts.RecordCheckAsync(
            ledger.LedgerId, TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, passBegan));

        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.False(state.Healthy);
            Assert.Equal(passBegan, state.LastCheckedAt);
            Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);
            Assert.Equal(repairedAt, state.LastRepairedAt);
        }
        Assert.True(await ConsistencyOutstandingAsync(ledger.LedgerId));
        Assert.Equal(ConsistencyVerdicts.DriftKey, (await ConsistencyEventsAsync(ledger.LedgerId)).Last().Key);
    }

    /// <summary>
    /// A repair's event commits with the row it follows, so no writer lands between the
    /// two: a monitor pass that began after the rewrite, and records as the repair records
    /// its event, is ordered wholly before the repair or wholly after it. Either way the
    /// ledger's consistency subject is outstanding exactly when the stored verdict is
    /// unhealthy, and the repair never clears it on a ledger stored as unhealthy. So too
    /// for a repair a later one superseded, whose save changes no field of the row.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_writer_lands_between_a_repairs_verdict_and_its_event(bool superseded)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = WholeMicrosecondClock();
        var verdicts = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);
        Assert.True(await verdicts.RecordCheckAsync(ledger.LedgerId,
            TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(1));
        var repairedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));
        var lastRepairedAt = repairedAt;
        if (superseded)
        {
            // A later repair records first, every projection agreeing: this repair's save
            // then writes neither the verdict nor a later last_repaired_at.
            lastRepairedAt = clock.GetUtcNow().UtcDateTime;
            await verdicts.RecordRepairAsync(ledger.LedgerId,
                new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), HealthyReport()),
                lastRepairedAt);
            Assert.False(await ConsistencyOutstandingAsync(ledger.LedgerId));
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        // A monitor pass begins after the rewrites committed, and finds the trade prices
        // out of step again.
        var passBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromMinutes(1));

        // As the repair sends its event, the pass records. It cannot save while the repair
        // holds the row, so the repair goes on once the pass has finished or has had the
        // time it would take.
        Task<bool>? pass = null;
        var sendingEvent = new BeforeCommand(
            c => c.CommandText.Contains("INSERT INTO ledger_events", StringComparison.Ordinal),
            async () =>
            {
                pass = verdicts.RecordCheckAsync(
                    ledger.LedgerId, TradePricesDrifted(), ConsistencyCheckOrigin.Monitor, passBegan);
                await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(5)));
            });
        var repairing = Db.NewServiceFactoryWith(sendingEvent);
        var repairer = new ConsistencyVerdicts(repairing, Publisher(clock, repairing), clock);

        await repairer.RecordRepairAsync(ledger.LedgerId,
            new ProjectionRepairResult(ConsistencyProjections.PostingCounts, PostingCountsRewritten(), HealthyReport()),
            repairedAt);

        Assert.NotNull(pass);
        Assert.True(await pass.WaitAsync(TimeSpan.FromSeconds(30)));
        await using (var db = Db.NewServiceDbContext())
        {
            var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.False(state.Healthy);
            Assert.Equal(passBegan, state.LastCheckedAt);
            Assert.Equal(lastRepairedAt, state.LastRepairedAt);
        }
        Assert.True(await ConsistencyOutstandingAsync(ledger.LedgerId));
    }

    /// <summary>
    /// Two checks that record at once on a ledger with no stored verdict yet do not fail:
    /// the one that finds the row created by the other reads it and decides again, and the
    /// verdict whose check began later is the one stored.
    /// </summary>
    [Fact]
    public async Task Two_first_checks_at_once_both_record_and_the_later_verdict_stays()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var earlierBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(1));
        var laterBegan = clock.GetUtcNow().UtcDateTime;
        var earlier = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);

        // The later check has read that there is no row and is creating it, when the
        // earlier one creates it first.
        Task<bool>? first = null;
        var creating = new BeforeCommand(
            c => c.CommandText.Contains("INSERT INTO ledger_consistency_state", StringComparison.Ordinal),
            async () =>
            {
                first = earlier.RecordCheckAsync(ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Person, earlierBegan);
                await first.WaitAsync(TimeSpan.FromSeconds(30));
            });
        var later = new ConsistencyVerdicts(Db.NewServiceFactoryWith(creating), Publisher(clock), clock);

        Assert.True(await later.RecordCheckAsync(
            ledger.LedgerId, TradePricesDrifted(), ConsistencyCheckOrigin.Person, laterBegan));

        Assert.NotNull(first);
        Assert.True(await first);
        await using var db = Db.NewServiceDbContext();
        var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyProjections.TradePrices], state.FailingProjections);
        Assert.Equal([ConsistencyVerdicts.OkKey, ConsistencyVerdicts.DriftKey],
            (await ConsistencyEventsAsync(ledger.LedgerId)).Select(e => e.Key).ToList());
    }

    /// <summary>
    /// Two people's checks that find the same new verdict at once announce it once. The one
    /// that saves second finds the row saved since it read it, reads it again, and finds its
    /// verdict already stored, with the other's announcement, which commits with it; it
    /// announces nothing.
    /// </summary>
    [Fact]
    public async Task Two_peoples_checks_at_once_announce_a_new_verdict_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var monitorBegan = clock.GetUtcNow().UtcDateTime;
        Assert.True(await new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock)
            .RecordCheckAsync(ledger.LedgerId, HealthyReport(), ConsistencyCheckOrigin.Monitor, monitorBegan));
        clock.Advance(TimeSpan.FromMinutes(1));
        var firstBegan = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(1));
        var secondBegan = clock.GetUtcNow().UtcDateTime;
        var other = new ConsistencyVerdicts(Db.NewServiceFactory(), Publisher(clock), clock);

        // This check has read the healthy row and is saving its drift when the other records first.
        Task<bool>? otherRecord = null;
        var saving = new BeforeCommand(
            c => c.CommandText.Contains("UPDATE ledger_consistency_state", StringComparison.Ordinal),
            async () =>
            {
                otherRecord = other.RecordCheckAsync(
                    ledger.LedgerId, TradePricesDrifted(), ConsistencyCheckOrigin.Person, firstBegan);
                await otherRecord.WaitAsync(TimeSpan.FromSeconds(30));
            });
        var self = new ConsistencyVerdicts(Db.NewServiceFactoryWith(saving), Publisher(clock), clock);

        var announced = await self.RecordCheckAsync(
            ledger.LedgerId, TradePricesDrifted(), ConsistencyCheckOrigin.Person, secondBegan);

        Assert.False(announced);
        Assert.NotNull(otherRecord);
        Assert.True(await otherRecord.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal([ConsistencyVerdicts.OkKey, ConsistencyVerdicts.DriftKey],
            (await ConsistencyEventsAsync(ledger.LedgerId)).Select(e => e.Key).ToList());
        await using var db = Db.NewServiceDbContext();
        var state = await db.LedgerConsistencyStates.AsNoTracking().SingleAsync(r => r.LedgerId == ledger.LedgerId);
        Assert.False(state.Healthy);
    }

    /// <summary>
    /// Runs <c>before</c> once, ahead of the first command that matches, and then lets that
    /// command run: how a test makes another writer land between a read and its write.
    /// </summary>
    private sealed class BeforeCommand(Func<System.Data.Common.DbCommand, bool> matches, Func<Task> before)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        private async Task RunAsync(System.Data.Common.DbCommand command)
        {
            if (matches(command) && Interlocked.Exchange(ref _fired, 1) == 0) await before();
        }
    }

    /// <summary>
    /// Acts once, on the first command any request context sends after a transaction it
    /// committed, once armed: in a repair request, the first command of the check that follows
    /// the rewrite's commit.
    /// </summary>
    private sealed class AfterRepairCommit : Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor,
        Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor
    {
        private Action<System.Data.Common.DbCommand>? _act;
        private volatile bool _committed;

        public bool Fired { get; private set; }

        public void Arm(Action<System.Data.Common.DbCommand> act)
        {
            _committed = false;
            Fired = false;
            _act = act;
        }

        public void Disarm() => _act = null;

        public Task TransactionCommittedAsync(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_act is not null) _committed = true;
            return Task.CompletedTask;
        }

        public void TransactionCommitted(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData)
        {
            if (_act is not null) _committed = true;
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        private void Act(System.Data.Common.DbCommand command)
        {
            var act = _act;
            if (act is null || !_committed || Fired) return;
            Fired = true;
            _act = null;
            act(command);
        }
    }

    /// <summary>
    /// Acts once, once armed, on the first command a request context sends after reading the
    /// visible ledgers that is not that read: in a repair request, after the ledger gate's
    /// read and the endpoint's own, the rewrite's first command.
    /// </summary>
    private sealed class AtRewriteStart : Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor
    {
        private Action<System.Data.Common.DbCommand>? _act;
        private volatile bool _ledgerRead;

        public bool Fired { get; private set; }

        public void Arm(Action<System.Data.Common.DbCommand> act)
        {
            _ledgerRead = false;
            Fired = false;
            _act = act;
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        private void Act(System.Data.Common.DbCommand command)
        {
            var act = _act;
            if (act is null || Fired) return;
            if (command.CommandText.Contains("user_visible_ledgers", StringComparison.Ordinal))
            {
                _ledgerRead = true;
                return;
            }
            if (!_ledgerRead) return;
            Fired = true;
            _act = null;
            act(command);
        }
    }

    /// <summary>
    /// Acts once, once armed, on the <paramref name="n"/>th command whose text contains
    /// <paramref name="marker"/>: in a rewrite that commits in pieces, one piece after the
    /// pieces before it have committed.
    /// </summary>
    private sealed class AtNthCommand(string marker, int n) : Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor
    {
        private Action<System.Data.Common.DbCommand>? _act;
        private int _seen;

        public bool Fired { get; private set; }

        public void Arm(Action<System.Data.Common.DbCommand> act)
        {
            _seen = 0;
            Fired = false;
            _act = act;
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        public ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Act(command);
            return ValueTask.FromResult(result);
        }

        private void Act(System.Data.Common.DbCommand command)
        {
            var act = _act;
            if (act is null || Fired || !command.CommandText.Contains(marker, StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref _seen) < n) return;
            Fired = true;
            _act = null;
            act(command);
        }
    }
}
