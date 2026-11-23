using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Quotes.MarketSeries;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// ADR-0114 D11's one rule, for every dated writer beyond the account and bank-transaction
/// ones <see cref="MarketSeriesScheduleAndSyncTests"/> covers: a write that moves the
/// ledger's earliest day, or the first invested day of a ledger with a benchmark, earlier
/// starts the backfill at once, and an ordinary write starts nothing. Both feeds are faked
/// at the HTTP level and the assertion is on what reached them, or on the run's row.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesStartTriggerTests : DatabaseTest
{
    public MarketSeriesStartTriggerTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private readonly MarketDataFake _fake = new(TimeProvider.System);

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    private static DateTime Noon(int daysAgo) => Today.AddDays(-daysAgo).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task EnableAsync(Guid userId, Guid ledgerId, params string[] providers)
    {
        await using var db = Db.NewDbContext();
        await new UserPreferencesRepository(db, TimeProvider.System).SetQuotesAsync(
            userId, ledgerId, new QuotesPrefs { EnabledProviders = providers });
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var cookie = await user.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task WhenIdleAsync(ApiFactory factory) =>
        factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();

    private async Task<IReadOnlyList<LedgerOperationRow>> BackfillRunsAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.ProviderKey == MarketSeriesBackfill.ProviderKey)
            .ToListAsync();
    }

    private IEnumerable<DateOnly> RatesRequestedFrom(string baseCurrency) =>
        _fake.RateRequests
            .Select(u => HttpUtility.ParseQueryString(u.Query))
            .Where(q => q["base"] == baseCurrency)
            .Select(q => DateOnly.ParseExact(q["from"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    private IEnumerable<DateOnly> GrowthRequestedFrom() =>
        _fake.YahooRequests
            .Select(u => HttpUtility.ParseQueryString(u.Query)["period1"])
            .Where(p => p is not null)
            .Select(p => DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(p!, CultureInfo.InvariantCulture)).UtcDateTime));

    /// <summary>A ledger in the reporting currency with the rates opt-in on, a spending
    /// category, and a EUR bank account opened 30 days ago through the API, whose creation
    /// backfilled EUR/USD from that day.</summary>
    private async Task<(SyntheticLedger Ledger, Guid Euro, Guid Category)> EuroLedgerAsync(
        ApiFactory factory, HttpClient client, SyntheticLedger ledger)
    {
        var category = await ledger.AddCategoryAsync("Spending");
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new { name = "Euro bank", accountType = "bank", currencyCode = "EUR", openedOn = Iso(Today.AddDays(-30)) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var euro = doc.RootElement.GetProperty("id").GetGuid();
        await WhenIdleAsync(factory);
        // Anchor: the EUR account's own creation backfilled from its opened date, once.
        Assert.Equal(Today.AddDays(-30 - MarketSeriesFetcher.PublishedDayReachBack), RatesRequestedFrom("EUR").Min());
        Assert.Single(await BackfillRunsAsync(ledger.LedgerId));
        return (ledger, euro, category.Id);
    }

    private async Task AssertRatesBackfilledFromAsync(ApiFactory factory, int daysAgo)
    {
        await WhenIdleAsync(factory);
        Assert.Equal(Today.AddDays(-daysAgo - MarketSeriesFetcher.PublishedDayReachBack), RatesRequestedFrom("EUR").Min());
    }

    /// <summary>A daily bank reminder on <paramref name="account"/> starting
    /// <paramref name="startDaysAgo"/> days ago; its id.</summary>
    private static async Task<Guid> DailyReminderAsync(
        HttpClient client, Guid ledgerId, Guid account, Guid category, int startDaysAgo, int? autoCommitDaysBefore = null)
    {
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=DAILY",
                StartDate = Today.AddDays(-startDaysAgo),
                AutoCommitDaysBefore = autoCommitDaysBefore,
                Payee = "Payee A",
                SourceAccountId = account,
                Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -10m }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private static async Task<Guid> FireBankAsync(
        HttpClient client, Guid ledgerId, Guid reminder, Guid account, Guid category, int daysAgo)
    {
        var fired = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/reminders/{reminder}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = Today.AddDays(-daysAgo),
                SourceAccountId = account,
                Payee = "Payee A",
                Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = -10m }],
            });
        Assert.Equal(HttpStatusCode.OK, fired.StatusCode);
        return (await fired.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;
    }

    // ---- reminders -----------------------------------------------------------------

    [Fact]
    public async Task A_reminder_fired_by_hand_before_the_earliest_day_backfills_the_pairs_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, euro, category) = await EuroLedgerAsync(factory, client, user);
        var reminder = await DailyReminderAsync(client, ledger.LedgerId, euro, category, 400);

        await FireBankAsync(client, ledger.LedgerId, reminder, euro, category, 400);

        await AssertRatesBackfilledFromAsync(factory, 400);
        var run = (await BackfillRunsAsync(ledger.LedgerId)).OrderBy(r => r.StartedAt).Last();
        Assert.Equal(("manual", (Guid?)ledger.UserId), (run.TriggeredVia, run.TriggeredByUserId));
    }

    [Fact]
    public async Task An_investment_reminder_fired_by_hand_before_the_earliest_day_backfills_the_pairs_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, _, _) = await EuroLedgerAsync(factory, client, user);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        var occurrence = Today.AddDays(-400);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=DAILY",
                StartDate = occurrence,
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security, Shares = 1m, Price = 10m,
                },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reminder = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var fired = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{reminder}/fire/investment",
            new FireInvestmentReminderRequest
            {
                OccurrenceDate = occurrence,
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security, Shares = 1m, Price = 10m,
                    PostedAt = Noon(400),
                },
            });
        Assert.Equal(HttpStatusCode.OK, fired.StatusCode);

        await AssertRatesBackfilledFromAsync(factory, 400);
    }

    [Fact]
    public async Task An_auto_posted_occurrence_before_the_earliest_day_backfills_as_the_schedules_user()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, euro, category) = await EuroLedgerAsync(factory, client, user);
        // Asked to be posted on its day, with nothing earlier in the series: the run whose
        // today is the series' first day posts exactly that occurrence.
        await DailyReminderAsync(client, ledger.LedgerId, euro, category, 400, autoCommitDaysBefore: 0);

        using (var scope = factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
                .Single(h => h.JobType == JobTypes.ReminderAutoPost);
            await using var db = Db.NewDbContext();
            var outcome = await handler.RunAsync(db, ledger.LedgerId, ledger.UserId,
                new JobRunClock(Noon(400), "UTC"), CancellationToken.None);
            Assert.Equal(JobRunResult.Ok, outcome.Result);
        }

        await AssertRatesBackfilledFromAsync(factory, 400);
        var run = (await BackfillRunsAsync(ledger.LedgerId)).OrderBy(r => r.StartedAt).Last();
        Assert.Equal(("scheduled", (Guid?)ledger.UserId), (run.TriggeredVia, run.TriggeredByUserId));
    }

    // ---- unhide --------------------------------------------------------------------

    /// <summary>A reminder occurrence posted 400 days ago and deleted, which hides it: a
    /// hidden row counts toward no start, so the earliest day is the EUR account's opened
    /// date again. Its header id, and how many backfill runs the ledger had then.</summary>
    private async Task<(Guid Header, int Runs)> HiddenOccurrenceAsync(
        ApiFactory factory, HttpClient client, SyntheticLedger ledger, Guid euro, Guid category)
    {
        var reminder = await DailyReminderAsync(client, ledger.LedgerId, euro, category, 400);
        var header = await FireBankAsync(client, ledger.LedgerId, reminder, euro, category, 400);
        var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{header}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("soft-hidden", (await deleted.Content.ReadFromJsonAsync<DeleteTransactionResponse>())!.Kind);
        await WhenIdleAsync(factory);
        await using var db = Db.NewDbContext();
        Assert.Equal(Today.AddDays(-30), await NeededFxPairs.LedgerEarliestDayAsync(db, ledger.LedgerId));
        return (header, (await BackfillRunsAsync(ledger.LedgerId)).Count);
    }

    [Fact]
    public async Task A_bulk_unhide_of_a_row_dated_before_the_earliest_day_starts_the_backfill()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, euro, category) = await EuroLedgerAsync(factory, client, user);
        var (header, runs) = await HiddenOccurrenceAsync(factory, client, ledger, euro, category);

        var unhidden = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/bulk-unhide",
            new BulkUnhideRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit", AccountId = euro, StatusFilter = "hidden", HeaderIds = [header],
                },
            });
        Assert.Equal(HttpStatusCode.OK, unhidden.StatusCode);
        Assert.Equal(1, (await unhidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unhidden").GetInt32());

        await WhenIdleAsync(factory);
        Assert.Equal(runs + 1, (await BackfillRunsAsync(ledger.LedgerId)).Count);
    }

    // ---- file import ---------------------------------------------------------------

    private const string CsvYaml = """
        version: 1
        delimiter: tab
        header_rows: 0
        date:
          column: 1
          format: MM/dd/yyyy
        payee:
          column: 3
        amount:
          shape: signed
          column: 2
        """;

    private static MultipartFormDataContent CsvUpload(Guid accountId, params int[] daysAgo)
    {
        var file = string.Concat(daysAgo.Select(d =>
            string.Join('\t',
                Today.AddDays(-d).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), "-10.00", $"Payee {d}") + "\n"));
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(body, "file", "statement.csv");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent("csv"), "providerAccountId");
        content.Add(new StringContent(CsvYaml), "mappingYaml");
        return content;
    }

    [Fact]
    public async Task A_file_import_with_a_row_before_the_earliest_day_backfills_the_pairs_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, euro, _) = await EuroLedgerAsync(factory, client, user);

        var imported = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            CsvUpload(euro, 20, 400));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        await AssertRatesBackfilledFromAsync(factory, 400);
        var run = (await BackfillRunsAsync(ledger.LedgerId)).OrderBy(r => r.StartedAt).Last();
        Assert.Equal(("manual", (Guid?)ledger.UserId), (run.TriggeredVia, run.TriggeredByUserId));
    }

    [Fact]
    public async Task A_file_import_inside_the_earliest_day_starts_nothing()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, euro, _) = await EuroLedgerAsync(factory, client, user);
        var requests = _fake.Requests.Count;

        var imported = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/csv/import",
            CsvUpload(euro, 20, 10));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        await WhenIdleAsync(factory);
        Assert.Single(await BackfillRunsAsync(ledger.LedgerId));
        Assert.Equal(requests, _fake.Requests.Count);
    }

    // ---- investment transactions ---------------------------------------------------

    private static async Task<Guid> BuyAsync(HttpClient client, Guid ledgerId, Guid brokerage, Guid security, int daysAgo)
    {
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage, Action = "buy", SecurityId = security,
                Shares = 1m, Price = 10m, PostedAt = Noon(daysAgo),
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    [Fact]
    public async Task A_trade_posted_before_the_earliest_day_backfills_the_pairs_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, _, _) = await EuroLedgerAsync(factory, client, user);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");

        await BuyAsync(client, ledger.LedgerId, brokerage.Id, security, 400);

        await AssertRatesBackfilledFromAsync(factory, 400);
    }

    [Fact]
    public async Task An_edit_of_a_trades_date_to_before_the_earliest_day_backfills_the_pairs_from_it()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, _, _) = await EuroLedgerAsync(factory, client, user);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        var header = await BuyAsync(client, ledger.LedgerId, brokerage.Id, security, 20);
        await WhenIdleAsync(factory);
        Assert.Single(await BackfillRunsAsync(ledger.LedgerId));

        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{header}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security,
                Shares = 1m, Price = 10m, PostedAt = Noon(400),
            });
        Assert.True(edit.IsSuccessStatusCode, $"The date edit answered {edit.StatusCode}.");

        await AssertRatesBackfilledFromAsync(factory, 400);
    }

    // ---- the benchmarks' start ------------------------------------------------------

    /// <summary>A ledger whose earliest day is a bank posting 400 days ago, a brokerage
    /// funded 30 days ago, and a benchmark whose add backfilled its growth from that
    /// funding. The bank and brokerage ids.</summary>
    private async Task<(SyntheticLedger Ledger, Guid Bank, Guid Brokerage, Guid Security)> BenchmarkedLedgerAsync(
        ApiFactory factory, HttpClient client, SyntheticLedger ledger)
    {
        var bank = await ledger.AddBankAccountAsync("Home bank");
        var category = await ledger.AddCategoryAsync("Spending");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, 10m, Noon(400));
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", openingBalance: 1000m, openedOn: Today.AddDays(-30));
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        await EnableAsync(ledger.UserId, ledger.LedgerId, YahooFinanceQuoteProvider.Key);
        var added = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/benchmarks", new { symbol = "SYNTHB" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await WhenIdleAsync(factory);
        // Anchor: the add backfilled the growth from the brokerage's funding, not the
        // ledger's earliest day.
        Assert.Equal(Today.AddDays(-30 - MarketSeriesFetcher.PublishedDayReachBack), GrowthRequestedFrom().Min());
        return (ledger, bank.Id, brokerage.Id, security);
    }

    private async Task AssertGrowthBackfilledFromAsync(ApiFactory factory, int daysAgo)
    {
        await WhenIdleAsync(factory);
        Assert.Equal(Today.AddDays(-daysAgo - MarketSeriesFetcher.PublishedDayReachBack), GrowthRequestedFrom().Min());
    }

    [Fact]
    public async Task A_trade_before_the_first_funding_backfills_the_benchmarks_from_its_day_though_the_earliest_day_holds()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, _, brokerage, security) = await BenchmarkedLedgerAsync(factory, client, user);

        await BuyAsync(client, ledger.LedgerId, brokerage, security, 200);

        await AssertGrowthBackfilledFromAsync(factory, 200);
        await using var db = Db.NewDbContext();
        Assert.Equal(Today.AddDays(-400), await NeededFxPairs.LedgerEarliestDayAsync(db, ledger.LedgerId));
    }

    [Fact]
    public async Task A_bulk_move_of_a_row_onto_a_brokerage_before_its_first_funding_backfills_the_benchmarks_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, bank, brokerage, _) = await BenchmarkedLedgerAsync(factory, client, user);
        var other = await ledger.AddCategoryAsync("Other spending");
        var (leg, _) = await ledger.AddTransactionPairAsync(bank, other.Id, 10m, Noon(200));
        var header = await ledger.ResolveHeaderIdAsync(leg);

        var moved = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account",
            new BulkMoveAccountRequest
            {
                Selection = new SelectionRequest { Kind = "explicit", AccountId = bank, HeaderIds = [header] },
                TargetAccountId = brokerage,
            });
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());

        await AssertGrowthBackfilledFromAsync(factory, 200);
    }

    [Fact]
    public async Task Funding_a_brokerage_opened_before_the_first_funding_backfills_the_benchmarks_from_its_opened_date()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, _, _, _) = await BenchmarkedLedgerAsync(factory, client, user);
        // Opened long before the first funding, with nothing in it, so it moves no start yet.
        var unfunded = await ledger.AddInvestmentAccountAsync("Brokerage B", openedOn: Today.AddDays(-200));

        var funded = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{unfunded.Id}",
            new { openingBalance = 500m });
        Assert.True(funded.IsSuccessStatusCode, await funded.Content.ReadAsStringAsync());

        await AssertGrowthBackfilledFromAsync(factory, 200);
    }

    [Fact]
    public async Task Reshaping_a_rows_postings_onto_a_brokerage_before_its_first_funding_backfills_the_benchmarks_from_its_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, bank, brokerage, _) = await BenchmarkedLedgerAsync(factory, client, user);
        var other = await ledger.AddCategoryAsync("Other spending");
        var (bankLeg, _) = await ledger.AddTransactionPairAsync(bank, other.Id, -10m, Noon(200));
        var header = await ledger.ResolveHeaderIdAsync(bankLeg);

        using var reshaped = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/transactions/{header}")
        {
            Content = JsonContent.Create(new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank,
                    Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = brokerage, Amount = -10m }],
                },
            }),
        });
        Assert.True(reshaped.IsSuccessStatusCode, await reshaped.Content.ReadAsStringAsync());

        await AssertGrowthBackfilledFromAsync(factory, 200);
    }

    /// <summary>
    /// A merge's survivor takes the folded row's date (ADR-0035). The folded row was live, so
    /// the ledger's earliest day holds, but when the survivor is the brokerage's first
    /// funding it moves the benchmarks' start to that date.
    /// </summary>
    [Fact]
    public async Task A_merge_that_dates_the_first_funding_earlier_backfills_the_benchmarks_from_its_new_day()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var bank = await ledger.AddBankAccountAsync("Home bank");
        var category = await ledger.AddCategoryAsync("Spending");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, 10m, Noon(400));
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        // The brokerage's first funding: a transfer entered by hand.
        var transfer = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = Noon(30),
            Payee = "Payee A",
            SourceAccountId = bank.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = brokerage.Id, Amount = -10m }],
        });
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        var funding = (await transfer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        await EnableAsync(ledger.UserId, ledger.LedgerId, YahooFinanceQuoteProvider.Key);
        var added = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/benchmarks", new { symbol = "SYNTHB" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await AssertGrowthBackfilledFromAsync(factory, 30);
        // The bank's own row for the same money, dated earlier, held for review.
        var imported = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/csv/import", CsvUpload(bank.Id, 36));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Guid feedRow;
        await using (var db = Db.NewDbContext())
            feedRow = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId && h.Id != funding && h.PostedAt >= Noon(37) && h.PostedAt <= Noon(35))
                .Select(h => h.Id)
                .SingleAsync();
        await WhenIdleAsync(factory);

        var merged = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{feedRow}/merge",
            new MergeTransactionRequest { FromHeaderId = funding });
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        await AssertGrowthBackfilledFromAsync(factory, 36);
        await using var read = Db.NewDbContext();
        Assert.Equal(Today.AddDays(-400), await NeededFxPairs.LedgerEarliestDayAsync(read, ledger.LedgerId));
    }

    /// <summary>
    /// A reminder's template is dated at the series' start, which is not a day any figure is
    /// on, so one dated before the first funding moves no benchmark start and starts nothing.
    /// </summary>
    [Fact]
    public async Task A_reminder_dated_before_the_first_funding_moves_no_benchmark_start()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var user = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, user);
        var (ledger, bank, brokerage, _) = await BenchmarkedLedgerAsync(factory, client, user);
        var requests = _fake.Requests.Count;

        await DailyReminderAsync(client, ledger.LedgerId, bank, brokerage, 200);

        await WhenIdleAsync(factory);
        await using var db = Db.NewDbContext();
        Assert.Equal(Today.AddDays(-30), await InvestmentReportingRepository.FirstInvestedDayAsync(db, ledger.LedgerId));
        Assert.Equal(requests, _fake.Requests.Count);
    }

    // ---- prices and positions: a pair added or reaching further back ----------------

    private IEnumerable<DateOnly> RatesRequestedFrom(string baseCurrency, string quote, int skip) =>
        _fake.RateRequests
            .Skip(skip)
            .Select(u => HttpUtility.ParseQueryString(u.Query))
            .Where(q => q["base"] == baseCurrency && q["quotes"]!.Split(',').Contains(quote))
            .Select(q => DateOnly.ParseExact(q["from"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    private async Task AssertPairBackfilledFromAsync(ApiFactory factory, string from, string to, int daysAgo, int skip)
    {
        await WhenIdleAsync(factory);
        Assert.Equal(Today.AddDays(-daysAgo - MarketSeriesFetcher.PublishedDayReachBack), RatesRequestedFrom(from, to, skip).Min());
    }

    private static async Task<Guid> AddPriceAsync(HttpClient client, Guid ledgerId, Guid security, int daysAgo, string currency)
    {
        var added = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/securities/{security}/prices",
            new CreateSecurityPriceRequest { Price = 12m, PriceDate = Today.AddDays(-daysAgo), CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priceId").GetGuid();
    }

    /// <summary>A ledger reported in USD with the rates opt-in on, and a USD brokerage
    /// holding a security bought 20 days ago, so its only price is the trade's, in USD. The
    /// brokerage and security ids.</summary>
    private async Task<(Guid Brokerage, Guid Security)> HeldSecurityAsync(
        ApiFactory factory, HttpClient client, SyntheticLedger ledger)
    {
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        await BuyAsync(client, ledger.LedgerId, brokerage.Id, security, 20);
        await WhenIdleAsync(factory);
        // Anchor: nothing needs a pound rate yet.
        Assert.Empty(RatesRequestedFrom("GBP", "USD", 0));
        return (brokerage.Id, security);
    }

    [Fact]
    public async Task A_price_in_a_currency_new_to_a_held_security_backfills_its_pair_from_the_price()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var (_, security) = await HeldSecurityAsync(factory, client, ledger);

        await AddPriceAsync(client, ledger.LedgerId, security, 100, "GBP");

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 100, 0);
    }

    [Fact]
    public async Task A_price_dated_before_the_first_in_its_currency_backfills_its_pair_from_it()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var (_, security) = await HeldSecurityAsync(factory, client, ledger);
        await AddPriceAsync(client, ledger.LedgerId, security, 50, "GBP");
        await WhenIdleAsync(factory);
        var seen = _fake.RateRequests.Count;

        await AddPriceAsync(client, ledger.LedgerId, security, 200, "GBP");

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 200, seen);
    }

    [Fact]
    public async Task A_price_edit_dated_before_the_first_in_its_currency_backfills_its_pair_from_it()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var (_, security) = await HeldSecurityAsync(factory, client, ledger);
        var price = await AddPriceAsync(client, ledger.LedgerId, security, 50, "GBP");
        await WhenIdleAsync(factory);
        var seen = _fake.RateRequests.Count;

        var edited = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/securities/{security}/prices/{price}",
            new PatchSecurityPriceRequest { PriceDate = Today.AddDays(-200) });
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 200, seen);
    }

    /// <summary>
    /// Two brokerages in two account currencies, so no account brings a new currency. A
    /// security priced in a third currency is held in one; a trade in the other puts it on
    /// an account in a currency its prices have not met, which adds a position pair from its
    /// first price, though neither the earliest day nor any benchmark start moves.
    /// </summary>
    [Fact]
    public async Task A_trade_that_puts_a_security_on_an_account_in_a_new_currency_backfills_its_position_pair()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        using var client = await ClientAsync(factory, ledger);
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        var euroBrokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var dollarBrokerage = await ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
        var security = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        await BuyAsync(client, ledger.LedgerId, euroBrokerage.Id, security, 30);
        await AddPriceAsync(client, ledger.LedgerId, security, 100, "GBP");
        await WhenIdleAsync(factory);
        var seen = _fake.RateRequests.Count;
        await using (var db = Db.NewDbContext())
            Assert.Equal(Today.AddDays(-30), await NeededFxPairs.LedgerEarliestDayAsync(db, ledger.LedgerId));

        await BuyAsync(client, ledger.LedgerId, dollarBrokerage.Id, security, 10);

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 100, seen);
        await using var read = Db.NewDbContext();
        Assert.Equal(Today.AddDays(-30), await NeededFxPairs.LedgerEarliestDayAsync(read, ledger.LedgerId));
    }

    // ---- MCP writes -----------------------------------------------------------------

    private static int _rpcId;

    private static async Task<JsonElement> ToolCallAsync(HttpClient client, string tool, object arguments)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _rpcId),
                method = "tools/call",
                @params = new { name = tool, arguments },
            }),
        });
        var body = await response.Content.ReadAsStringAsync();
        var json = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } data
            ? data["data:".Length..]
            : body;
        var result = JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), $"{tool} failed: {result}");
        return result;
    }

    /// <summary>A client holding a connected-apps bearer that may read and write for
    /// <paramref name="ledger"/>'s owner, with an initialized MCP session and writes on.</summary>
    private async Task<HttpClient> McpClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var (token, hash) = Coffer.Api.Auth.McpTokenService.Generate();
        await new McpTokensRepository(Db.NewServiceFactory(), TimeProvider.System)
            .IssueAsync(ledger.UserId, "market-series", hash, $"{Coffer.Api.Mcp.McpScopes.Read} {Coffer.Api.Mcp.McpScopes.Write}", expiresAt: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var init = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _rpcId),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "coffer-tests", version = "1.0" },
            },
        });
        init.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", init.Headers.GetValues("Mcp-Session-Id").Single());
        using var initialized = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        initialized.EnsureSuccessStatusCode();
        factory.Services.GetRequiredService<Coffer.Api.Mcp.McpRuntimeState>().WritesEnabled = true;
        return client;
    }

    [Fact]
    public async Task An_mcp_price_in_a_currency_new_to_a_held_security_backfills_its_pair_from_the_price()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var (_, security) = await HeldSecurityAsync(factory, client, ledger);
        using var mcp = await McpClientAsync(factory, ledger);

        await ToolCallAsync(mcp, "add_price", new
        {
            ledgerId = ledger.LedgerId, securityId = security, date = Iso(Today.AddDays(-100)), close = 12m, currency = "GBP",
        });

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 100, 0);
    }

    [Fact]
    public async Task An_mcp_price_edit_dated_before_the_first_in_its_currency_backfills_its_pair_from_it()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        var (_, security) = await HeldSecurityAsync(factory, client, ledger);
        var price = await AddPriceAsync(client, ledger.LedgerId, security, 50, "GBP");
        await WhenIdleAsync(factory);
        var seen = _fake.RateRequests.Count;
        using var mcp = await McpClientAsync(factory, ledger);

        await ToolCallAsync(mcp, "update_price", new
        {
            ledgerId = ledger.LedgerId, securityId = security, priceId = price, date = Iso(Today.AddDays(-200)),
        });

        await AssertPairBackfilledFromAsync(factory, "GBP", "USD", 200, seen);
    }

    /// <summary>
    /// The keeper takes the duplicate's positions and keeps its own prices, so its price
    /// currency meets the duplicate's account, which adds a position pair from the keeper's
    /// first price.
    /// </summary>
    [Fact]
    public async Task An_mcp_security_merge_that_adds_a_position_pair_backfills_it()
    {
        await using var factory = _fake.Install(new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth());
        var ledger = await SyntheticLedger.CreateAsync(Db);
        using var client = await ClientAsync(factory, ledger);
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        var dollarBrokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var euroBrokerage = await ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "EUR");
        var keeper = await ledger.AddSecurityAsync("Index Fund A", "SYNTHA");
        var duplicate = await ledger.AddSecurityAsync("Index Fund A (copy)");
        await BuyAsync(client, ledger.LedgerId, dollarBrokerage.Id, keeper, 60);
        await BuyAsync(client, ledger.LedgerId, euroBrokerage.Id, duplicate, 20);
        await WhenIdleAsync(factory);
        // Anchor: the keeper's dollar prices meet no euro account yet.
        Assert.Empty(RatesRequestedFrom("USD", "EUR", 0));
        using var mcp = await McpClientAsync(factory, ledger);

        await ToolCallAsync(mcp, "merge_securities", new { ledgerId = ledger.LedgerId, sourceId = duplicate, targetId = keeper });

        await AssertPairBackfilledFromAsync(factory, "USD", "EUR", 60, 0);
    }
}
