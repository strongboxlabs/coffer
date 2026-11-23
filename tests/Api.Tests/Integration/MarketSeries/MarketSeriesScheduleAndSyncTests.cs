using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// The two other ways a run reaches the market series (ADR-0114 D8, D11): the scheduled
/// quote refresh fetches under the schedule's user's consent, and a feed sync never
/// fetches at all (ADR-0054 D1), with both feeds faked at the HTTP level and the
/// assertion made on what reached them. Also how far back a refresh fetches a net-worth
/// pair: from the ledger's earliest day (ADR-0114 D11, ADR-0114 D13).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesScheduleAndSyncTests : DatabaseTest
{
    public MarketSeriesScheduleAndSyncTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private readonly MarketDataFake _fake = new(TimeProvider.System);

    private async Task EnableAsync(Guid userId, Guid ledgerId, params string[] providers)
    {
        await using var db = Db.NewDbContext();
        await new UserPreferencesRepository(db, TimeProvider.System).SetQuotesAsync(
            userId, ledgerId, new QuotesPrefs { EnabledProviders = providers });
    }

    private async Task AddBenchmarkAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        var (result, _) = await new MarketSeriesRepository(db, TimeProvider.System)
            .AddBenchmarkAsync(ledger.LedgerId, "SYNTH", "Synthetic fund", "USD");
        Assert.Equal(MarketSeriesRepository.AddBenchmarkResult.Ok, result);
    }

    private static async Task<JobRunOutcome> RunScheduledRefreshAsync(ApiFactory factory, TestDatabase database, SyntheticLedger ledger, Guid scheduleUser)
    {
        using var scope = factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
            .Single(h => h.JobType == JobTypes.QuoteRefresh);
        await using var db = database.NewDbContext();
        return await handler.RunAsync(db, ledger.LedgerId, scheduleUser,
            new JobRunClock(TimeProvider.System.GetUtcNow().UtcDateTime, null), CancellationToken.None);
    }

    [Fact]
    public async Task The_scheduled_refresh_fetches_under_the_schedules_user_and_records_it_on_its_own_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");
        await AddBenchmarkAsync(ledger);
        await EnableAsync(ledger.UserId, ledger.LedgerId, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        await using var factory = _fake.Install(new ApiFactory(Db));

        await RunScheduledRefreshAsync(factory, Db, ledger, ledger.UserId);

        Assert.Single(_fake.RateRequests);
        Assert.Single(_fake.YahooRequests);
        await using var read = Db.NewDbContext();
        var run = await read.LedgerOperations.AsNoTracking()
            .SingleAsync(r => r.LedgerId == ledger.LedgerId && r.Family == "quote");
        Assert.Equal((QuoteOrchestrator.QuoteRefreshProviderKey, "scheduled", (Guid?)ledger.UserId),
            (run.ProviderKey, run.TriggeredVia, run.TriggeredByUserId));
        var details = LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
        Assert.Equal(await read.FxRates.CountAsync(r => r.LedgerId == ledger.LedgerId), details.RatesInserted);
        Assert.Equal(await read.BenchmarkGrowth.CountAsync(g => g.LedgerId == ledger.LedgerId), details.BenchmarkDaysInserted);
        Assert.True(details.RatesInserted > 0 && details.BenchmarkDaysInserted > 0);
    }

    [Fact]
    public async Task A_schedule_whose_user_has_not_opted_in_fetches_nothing_whoever_else_has()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");
        await AddBenchmarkAsync(ledger);
        var editor = await ledger.AddMemberAsync("editor");
        await EnableAsync(editor, ledger.LedgerId, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        await using var factory = _fake.Install(new ApiFactory(Db));

        await RunScheduledRefreshAsync(factory, Db, ledger, ledger.UserId);

        Assert.Empty(_fake.Requests);
    }

    // ---- how far back a net-worth pair is needed -----------------------------------

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var cookie = await user.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Creates a bank account through the API and returns its id.</summary>
    private static async Task<Guid> CreateBankAsync(
        HttpClient client, Guid ledgerId, string name, string currencyCode, DateOnly? openedOn)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/accounts",
            new { name, accountType = "bank", currencyCode, openedOn = openedOn is { } d ? Iso(d) : null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static IEnumerable<DateOnly> RequestedFrom(MarketDataFake fake, string baseCurrency) =>
        fake.RateRequests
            .Select(u => HttpUtility.ParseQueryString(u.Query))
            .Where(q => q["base"] == baseCurrency)
            .Select(q => DateOnly.ParseExact(q["from"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    [Fact]
    public async Task A_net_worth_pair_is_needed_from_the_ledgers_earliest_posting_whatever_its_currency_and_the_refresh_fetches_from_there()
    {
        // USD ledger. The earliest posting is on a USD account later made inactive; the EUR
        // account opens long after it, and a CAD brokerage's holdings sibling counts as the
        // brokerage.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", openedOn: Today.AddDays(-60), currencyCode: "CAD");
        var category = await ledger.AddCategoryAsync("Spending");
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        var closed = await CreateBankAsync(client, ledger.LedgerId, "Closed bank", "USD", Today.AddDays(-200));
        await CreateBankAsync(client, ledger.LedgerId, "Euro bank", "EUR", Today.AddDays(-30));
        await ledger.AddTransactionPairAsync(closed, category.Id, 10m, Today.AddDays(-400).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        var deactivate = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{closed}/active")
        {
            Content = JsonContent.Create(new { active = false }),
        });
        Assert.True(deactivate.IsSuccessStatusCode, $"Deactivating answered {deactivate.StatusCode}.");
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();
        Assert.Empty(_fake.Requests);

        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{ledger.LedgerId}/fx-rates"))!;
        var byPair = view.Pairs.ToDictionary(p => $"{p.FromCurrency}/{p.ToCurrency}");

        Assert.Equal(["CAD/USD", "EUR/USD"], byPair.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Today.AddDays(-400), byPair["EUR/USD"].NeededFrom);
        Assert.Equal(Today.AddDays(-400), byPair["CAD/USD"].NeededFrom);
        Assert.Equal([brokerage.Id], byPair["CAD/USD"].NeededBy.Select(n => n.Id));

        // The fetch reaches back from that day to the last published one before it.
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        await RunScheduledRefreshAsync(factory, Db, ledger, ledger.UserId);
        Assert.Equal(Today.AddDays(-400 - MarketSeriesFetcher.PublishedDayReachBack), RequestedFrom(_fake, "EUR").Min());
    }

    [Fact]
    public async Task A_net_worth_pair_is_needed_from_an_earlier_opened_date_in_another_currency()
    {
        // No postings at all: the GBP account's start date is the ledger's earliest day.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        await CreateBankAsync(client, ledger.LedgerId, "Pound bank", "GBP", Today.AddDays(-90));
        await CreateBankAsync(client, ledger.LedgerId, "Euro bank", "EUR", Today.AddDays(-10));
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();

        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{ledger.LedgerId}/fx-rates"))!;

        Assert.Equal(
            [("EUR/USD", (DateOnly?)Today.AddDays(-90)), ("GBP/USD", Today.AddDays(-90))],
            view.Pairs.Select(p => ($"{p.FromCurrency}/{p.ToCurrency}", p.NeededFrom)));
    }

    [Fact]
    public async Task A_reminder_starting_before_the_first_posting_leaves_a_net_worth_pair_needed_from_the_first_posting()
    {
        // Both accounts open on the day of the first posting. A reminder on the
        // reporting-currency account starts long before it: its template is no posting, so
        // its start date is no day a figure is on (ADR-0114 D11).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var category = await ledger.AddCategoryAsync("Spending");
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        var home = await CreateBankAsync(client, ledger.LedgerId, "Home bank", "USD", Today.AddDays(-30));
        await CreateBankAsync(client, ledger.LedgerId, "Euro bank", "EUR", Today.AddDays(-30));
        await ledger.AddTransactionPairAsync(home, category.Id, 10m, Today.AddDays(-30).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        var reminder = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = Today.AddDays(-1000),
                Payee = "Payee A",
                SourceAccountId = home,
                Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -40m }],
            });
        Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();

        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{ledger.LedgerId}/fx-rates"))!;

        var pair = Assert.Single(view.Pairs);
        Assert.Equal(("EUR/USD", (DateOnly?)Today.AddDays(-30)), ($"{pair.FromCurrency}/{pair.ToCurrency}", pair.NeededFrom));
    }

    // ---- a write that moves the earliest day earlier starts the backfill -----------

    /// <summary>A reporting-currency ledger with the rates opt-in on and a EUR bank account
    /// opened <paramref name="euroOpenedDaysAgo"/> days ago, whose creation backfilled
    /// EUR/USD from that day. The EUR account's id.</summary>
    private async Task<Guid> EuroPairBackfilledAsync(
        ApiFactory factory, HttpClient client, SyntheticLedger ledger, int euroOpenedDaysAgo)
    {
        await EnableAsync(ledger.UserId, ledger.LedgerId, FrankfurterRatesClient.Key);
        var euro = await CreateBankAsync(client, ledger.LedgerId, "Euro bank", "EUR", Today.AddDays(-euroOpenedDaysAgo));
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();
        // Anchor: the EUR account's own creation backfilled from its opened date.
        Assert.Equal(Today.AddDays(-euroOpenedDaysAgo - MarketSeriesFetcher.PublishedDayReachBack),
            RequestedFrom(_fake, "EUR").Min());
        return euro;
    }

    private async Task AssertBackfilledFromAsync(ApiFactory factory, int daysAgo)
    {
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();
        Assert.Equal(Today.AddDays(-daysAgo - MarketSeriesFetcher.PublishedDayReachBack),
            RequestedFrom(_fake, "EUR").Min());
    }

    [Fact]
    public async Task An_account_in_the_reporting_currency_created_before_the_earliest_day_backfills_the_pairs_from_its_opened_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        await EuroPairBackfilledAsync(factory, client, ledger, 30);

        await CreateBankAsync(client, ledger.LedgerId, "Home bank", "USD", Today.AddDays(-400));

        await AssertBackfilledFromAsync(factory, 400);
    }

    [Fact]
    public async Task An_edit_of_an_accounts_opened_date_to_before_the_earliest_day_backfills_the_pairs_from_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        var euro = await EuroPairBackfilledAsync(factory, client, ledger, 30);

        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{euro}",
            new { openedOn = Iso(Today.AddDays(-400)) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        await AssertBackfilledFromAsync(factory, 400);
    }

    [Fact]
    public async Task A_transaction_posted_before_the_earliest_day_backfills_the_pairs_from_its_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var category = await ledger.AddCategoryAsync("Spending");
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        var euro = await EuroPairBackfilledAsync(factory, client, ledger, 30);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Today.AddDays(-400).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
                Payee = "Payee A",
                SourceAccountId = euro,
                Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -10m }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await AssertBackfilledFromAsync(factory, 400);
    }

    [Fact]
    public async Task An_edit_of_a_transactions_date_to_before_the_earliest_day_backfills_the_pairs_from_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var category = await ledger.AddCategoryAsync("Spending");
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        using var client = await ClientAsync(factory, ledger);
        var euro = await EuroPairBackfilledAsync(factory, client, ledger, 30);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Today.AddDays(-20).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
                Payee = "Payee A",
                SourceAccountId = euro,
                Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -10m }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new { postedAt = Today.AddDays(-400).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc) });
        Assert.True(edit.IsSuccessStatusCode, $"The date edit answered {edit.StatusCode}.");

        await AssertBackfilledFromAsync(factory, 400);
    }

    // ---- no egress from a feed sync ------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(impl(request));
    }

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public async Task A_feed_sync_that_brings_a_currency_the_ledger_has_no_rates_for_sends_nothing_to_either_feed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
            await seed.SaveChangesAsync();
        }
        await AddBenchmarkAsync(ledger);
        await EnableAsync(ledger.UserId, ledger.LedgerId, YahooFinanceQuoteProvider.Key, FrankfurterRatesClient.Key);
        var bank = await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");

        const string accessUrl = "https://u:p@bridge.simplefin.org/access/x";
        var simplefin = new StubHandler(req => req.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"connections":[{"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}],
                     "errlist":[],"accounts":[
                      {"id":"sf-A","conn_id":"c-A","name":"Konto","currency":"EUR","balance":"0.00","transactions":[
                        {"id":"fitid-A","posted":1715000000,"amount":"-10.00","description":"A1","pending":false}]}]}
                    """),
            });
        await using var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => new SimpleFinClient(new HttpClient(simplefin), TimeProvider.System)));
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        var connection = (await (await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://x/c") }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-A",
            }),
        })).StatusCode);

        var sync = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        await factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();

        // The sync landed a EUR transaction on a USD ledger, so EUR→USD is needed; it waits
        // for the next refresh.
        await using var read = Db.NewDbContext();
        Assert.Contains(await NeededFxPairs.ForLedgerAsync(read, ledger.LedgerId),
            p => p is { FromCurrency: "EUR", ToCurrency: "USD" });
        Assert.Empty(_fake.Requests);
        Assert.False(await read.LedgerOperations.AnyAsync(r =>
            r.LedgerId == ledger.LedgerId && r.ProviderKey == MarketSeriesBackfill.ProviderKey));
    }
}
