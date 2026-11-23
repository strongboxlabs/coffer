using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.MarketSeries;

/// <summary>
/// The benchmarks and exchange-rates APIs (ADR-0114 D1, D2, D10), the reporting-currency
/// writes (ADR-0114 D7), and the immediate backfills they start (ADR-0114 D11), through the HTTP pipeline
/// with real cookie auth and both feeds faked at the HTTP level. A trigger is asserted by
/// what reached the fake, not by the code having run.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesEndpointsTests : DatabaseTest
{
    public MarketSeriesEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private readonly MarketDataFake _fake = new(TimeProvider.System);

    private ApiFactory Factory() => _fake.Install(new ApiFactory(Db).WithoutDevAuth());

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger user)
    {
        var cookie = await user.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task WhenBackfillsIdleAsync(ApiFactory factory) =>
        factory.Services.GetRequiredService<MarketSeriesBackfill>().WhenIdleAsync();

    private async Task EnableAsync(Guid userId, Guid ledgerId, params string[] providers)
    {
        await using var db = Db.NewDbContext();
        await new UserPreferencesRepository(db, TimeProvider.System).SetQuotesAsync(
            userId, ledgerId, new QuotesPrefs { EnabledProviders = providers });
    }

    /// <summary>A second user with <paramref name="role"/> on <paramref name="ledger"/>.</summary>
    private async Task<SyntheticLedger> MemberAsync(SyntheticLedger ledger, string role)
    {
        var other = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        db.UserLedgerGrants.Add(new UserLedgerGrantRow { UserId = other.UserId, LedgerId = ledger.LedgerId, Role = role });
        await db.SaveChangesAsync();
        return other;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static Task<HttpResponseMessage> AddBenchmark(HttpClient client, Guid ledgerId, string symbol) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/benchmarks", new { symbol });

    private async Task<IReadOnlyList<LedgerOperationRow>> BackfillRunsAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.ProviderKey == MarketSeriesBackfill.ProviderKey)
            .ToListAsync();
    }

    // ---- benchmarks ----------------------------------------------------------------

    [Fact]
    public async Task Adding_a_benchmark_without_the_users_Yahoo_opt_in_is_refused_and_sends_nothing()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var response = await AddBenchmark(client, alice.LedgerId, "VT");

        Assert.Equal(BusinessErrorCodes.BenchmarkYahooOff, await CodeOf(response));
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task Another_members_Yahoo_opt_in_does_not_count_for_the_one_adding()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await MemberAsync(alice, "editor");
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, bob);

        var response = await AddBenchmark(client, alice.LedgerId, "VT");

        Assert.Equal(BusinessErrorCodes.BenchmarkYahooOff, await CodeOf(response));
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task Adding_a_benchmark_stores_Yahoos_name_and_currency_and_backfills_its_growth()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        _fake.Currencies["VWRL.L"] = "GBP";
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var response = await AddBenchmark(client, alice.LedgerId, " vwrl.l ");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = (await response.Content.ReadFromJsonAsync<BenchmarkDto>())!;
        Assert.Equal(("VWRL.L", "Synthetic VWRL.L Fund", "GBP", 1), (added.Symbol, added.Name, added.CurrencyCode, added.Position));

        await WhenBackfillsIdleAsync(factory);
        // The check, then the backfill's growth request: delivered, not just started.
        Assert.Equal(2, _fake.YahooRequests.Count);
        Assert.Contains("period1=", _fake.YahooRequests[1].Query, StringComparison.Ordinal);
        var run = Assert.Single(await BackfillRunsAsync(alice.LedgerId));
        Assert.Equal(("quote", "manual", (Guid?)alice.UserId), (run.Family, run.TriggeredVia, run.TriggeredByUserId));
        var details = LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
        Assert.True(details.BenchmarkDaysInserted > 0);
        await using var db = Db.NewDbContext();
        Assert.Equal(details.BenchmarkDaysInserted,
            await db.BenchmarkGrowth.CountAsync(g => g.LedgerId == alice.LedgerId && g.Symbol == "VWRL.L"));
    }

    [Fact]
    public async Task An_index_is_refused_with_the_checks_reason()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        _fake.InstrumentTypes["^SP500TR"] = "INDEX";
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var response = await AddBenchmark(client, alice.LedgerId, "^SP500TR");

        Assert.Equal(YahooBenchmarkClient.IndexRefusedCode, await CodeOf(response));
        await using var db = Db.NewDbContext();
        Assert.False(await db.LedgerBenchmarks.AnyAsync(b => b.LedgerId == alice.LedgerId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("V T")]
    [InlineData("VT;DROP")]
    public async Task A_symbol_that_is_not_one_is_refused_before_any_request(string symbol)
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        Assert.Equal(BusinessErrorCodes.BenchmarkSymbolInvalid, await CodeOf(await AddBenchmark(client, alice.LedgerId, symbol)));
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task A_fourth_benchmark_and_a_duplicate_are_refused_before_Yahoo_is_asked()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        foreach (var s in new[] { "AAA", "BBB", "CCC" })
            Assert.Equal(HttpStatusCode.Created, (await AddBenchmark(client, alice.LedgerId, s)).StatusCode);
        await WhenBackfillsIdleAsync(factory);
        var asked = _fake.YahooRequests.Count;

        Assert.Equal(BusinessErrorCodes.BenchmarkAlreadyAdded, await CodeOf(await AddBenchmark(client, alice.LedgerId, "bbb")));
        Assert.Equal(BusinessErrorCodes.BenchmarkLimitReached, await CodeOf(await AddBenchmark(client, alice.LedgerId, "DDD")));
        Assert.Equal(asked, _fake.YahooRequests.Count);
    }

    [Fact]
    public async Task Removing_a_benchmark_closes_the_gap_and_keeps_its_stored_growth()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var ids = new List<Guid>();
        foreach (var s in new[] { "AAA", "BBB", "CCC" })
            ids.Add((await (await AddBenchmark(client, alice.LedgerId, s)).Content.ReadFromJsonAsync<BenchmarkDto>())!.Id);
        await WhenBackfillsIdleAsync(factory);
        await using (var db = Db.NewDbContext())
            Assert.True(await db.BenchmarkGrowth.AnyAsync(g => g.LedgerId == alice.LedgerId && g.Symbol == "AAA"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/ledgers/{alice.LedgerId}/benchmarks/{ids[0]}")).StatusCode);

        var list = (await client.GetFromJsonAsync<BenchmarkDto[]>($"/api/ledgers/{alice.LedgerId}/benchmarks"))!;
        Assert.Equal([("BBB", 1), ("CCC", 2)], list.Select(b => (b.Symbol, b.Position)));
        await using (var db = Db.NewDbContext())
            Assert.True(await db.BenchmarkGrowth.AnyAsync(g => g.LedgerId == alice.LedgerId && g.Symbol == "AAA"));
        Assert.Equal(BusinessErrorCodes.BenchmarkNotFound,
            await CodeOf(await client.DeleteAsync($"/api/ledgers/{alice.LedgerId}/benchmarks/{ids[0]}")));
    }

    [Fact]
    public async Task Adding_a_removed_benchmark_back_asks_Yahoo_for_no_range_it_already_asked()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var first = (await (await AddBenchmark(client, alice.LedgerId, "VT")).Content.ReadFromJsonAsync<BenchmarkDto>())!;
        await WhenBackfillsIdleAsync(factory);
        Assert.Single(_fake.YahooRequests, r => r.Query.Contains("period1=", StringComparison.Ordinal));
        await using (var db = Db.NewDbContext())
            Assert.True(await db.BenchmarkGrowth.AnyAsync(g => g.LedgerId == alice.LedgerId && g.Symbol == "VT"));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/ledgers/{alice.LedgerId}/benchmarks/{first.Id}")).StatusCode);
        await using (var db = Db.NewDbContext())
            Assert.True(await db.BenchmarkCoverage.AnyAsync(c => c.LedgerId == alice.LedgerId && c.Symbol == "VT"));
        Assert.Equal(HttpStatusCode.Created, (await AddBenchmark(client, alice.LedgerId, "VT")).StatusCode);
        await WhenBackfillsIdleAsync(factory);

        // The re-add's own check is the only new request: no growth range is asked again.
        Assert.Equal(3, _fake.YahooRequests.Count);
        Assert.Single(_fake.YahooRequests, r => r.Query.Contains("period1=", StringComparison.Ordinal));
        Assert.Equal(2, (await BackfillRunsAsync(alice.LedgerId)).Count);
    }

    [Fact]
    public async Task The_list_says_which_series_currencies_each_benchmark_has_no_rate_into()
    {
        // A USD ledger with a CAD brokerage: a series is drawn in USD or CAD.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await alice.AddInvestmentAccountAsync("Brokerage B", currencyCode: "CAD");
        var bob = await MemberAsync(alice, "editor");
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        _fake.Currencies["XCE"] = "CAD";
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        Assert.Equal(HttpStatusCode.Created, (await AddBenchmark(client, alice.LedgerId, "VT")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddBenchmark(client, alice.LedgerId, "XCE")).StatusCode);
        await WhenBackfillsIdleAsync(factory);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

        var before = (await client.GetFromJsonAsync<BenchmarkDto[]>($"/api/ledgers/{alice.LedgerId}/benchmarks"))!;
        Assert.Equal(
            [("VT", (string?)null, "CAD"), ("XCE", null, "USD")],
            before.Select(b => (b.Symbol, b.Availability!.UnavailableReason, string.Join(",", b.Availability.NoRatesInto))));

        // One CAD→USD rate is enough: the line is drawn, a point with no rate says so.
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "CAD", "USD", Iso(today.AddDays(-3)), 0.72m)).StatusCode);
        var after = (await client.GetFromJsonAsync<BenchmarkDto[]>($"/api/ledgers/{alice.LedgerId}/benchmarks"))!;
        Assert.Empty(after.Single(b => b.Symbol == "XCE").Availability!.NoRatesInto);

        // With its history stored a benchmark is drawable whoever reads: Bob has Yahoo off.
        using var bobClient = await ClientAsync(factory, bob);
        var bobs = (await bobClient.GetFromJsonAsync<BenchmarkDto[]>($"/api/ledgers/{alice.LedgerId}/benchmarks"))!;
        Assert.All(bobs, b => Assert.Null(b.Availability!.UnavailableReason));
    }

    [Theory]
    [InlineData(true, MarketSeriesRepository.BenchmarkNoHistory)]
    [InlineData(false, MarketSeriesRepository.BenchmarkYahooOff)]
    public async Task A_benchmark_with_no_history_yet_says_why_for_the_reader(bool yahooOn, string reason)
    {
        // The row as the add writes it, before any growth is fetched: the add's own
        // repository write, without the check and backfill the endpoint wraps it in.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        var series = new MarketSeriesRepository(db, TimeProvider.System);
        var (result, _) = await series.AddBenchmarkAsync(alice.LedgerId, "VT", "Synthetic VT Fund", "USD");
        Assert.Equal(MarketSeriesRepository.AddBenchmarkResult.Ok, result);

        var listed = Assert.Single(await series.ListBenchmarksAsync(alice.LedgerId, yahooOn));

        Assert.Equal(reason, listed.Availability!.UnavailableReason);
        // A USD benchmark on a USD ledger with no brokerage needs no rate.
        Assert.Empty(listed.Availability.NoRatesInto);
    }

    [Fact]
    public async Task A_viewer_reads_the_lists_and_cannot_change_them()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var vera = await MemberAsync(alice, "viewer");
        await EnableAsync(vera.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, vera);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/ledgers/{alice.LedgerId}/benchmarks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/ledgers/{alice.LedgerId}/fx-rates")).StatusCode);
        Assert.Equal("ledger-not-writable", await CodeOf(await AddBenchmark(client, alice.LedgerId, "VT")));
        Assert.Equal("ledger-not-writable", await CodeOf(await client.PostAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/fx-rates",
            new { fromCurrency = "EUR", toCurrency = "USD", date = "2026-05-20", rate = 1.1m })));
        Assert.Empty(_fake.Requests);
    }

    // ---- hand-entered rates and the rates view -------------------------------------

    private async Task<SyntheticLedger> EuroLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");
        return ledger;
    }

    private static Task<HttpResponseMessage> AddRate(HttpClient client, Guid ledgerId, string from, string to, string date, decimal rate) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/fx-rates", new { fromCurrency = from, toCurrency = to, date, rate });

    [Fact]
    public async Task A_rate_is_typed_once_per_day_for_a_needed_pair_and_names_who_typed_it()
    {
        var alice = await EuroLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var created = await AddRate(client, alice.LedgerId, "eur", "usd", "2026-05-20", 1.0812345678901234m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rate = (await created.Content.ReadFromJsonAsync<FxRateDto>())!;
        Assert.Equal((1.081234567890m, "manual", (Guid?)alice.UserId), (rate.Rate, rate.Source, rate.EnteredByUserId));

        Assert.Equal(BusinessErrorCodes.FxRateDayTaken, await CodeOf(await AddRate(client, alice.LedgerId, "EUR", "USD", "2026-05-20", 2m)));
        Assert.Equal(BusinessErrorCodes.FxPairNotNeeded, await CodeOf(await AddRate(client, alice.LedgerId, "USD", "EUR", "2026-05-20", 1m)));
        Assert.Equal(BusinessErrorCodes.FxRateInvalid, await CodeOf(await AddRate(client, alice.LedgerId, "EUR", "USD", "2026-05-21", 0m)));
        Assert.Equal(BusinessErrorCodes.FxRateDateInvalid, await CodeOf(await AddRate(client, alice.LedgerId, "EUR", "USD", "20-05-2026", 1m)));
        Assert.Equal(BusinessErrorCodes.FxRateCurrencyInvalid, await CodeOf(await AddRate(client, alice.LedgerId, "EUR", "EUR", "2026-05-21", 1m)));
        Assert.Empty(_fake.Requests);

        var page = (await client.GetFromJsonAsync<FxRatePageDto>($"/api/ledgers/{alice.LedgerId}/fx-rates/EUR/USD"))!;
        Assert.Equal(new DateOnly(2026, 5, 20), Assert.Single(page.Rates).Date);
        Assert.Null(page.NextBefore);
    }

    [Fact]
    public async Task A_rate_for_a_day_after_the_servers_UTC_today_is_refused_and_today_is_accepted()
    {
        var alice = await EuroLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

        Assert.Equal(BusinessErrorCodes.FxRateDateInFuture,
            await CodeOf(await AddRate(client, alice.LedgerId, "EUR", "USD", Iso(today.AddDays(1)), 1.1m)));
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "USD", Iso(today), 1.1m)).StatusCode);

        await using var db = Db.NewDbContext();
        Assert.Equal(today, await db.FxRates.Where(r => r.LedgerId == alice.LedgerId).MaxAsync(r => r.RateDate));
    }

    [Fact]
    public async Task The_rates_view_lists_the_needed_pairs_and_takes_the_later_of_fetched_and_typed_coverage()
    {
        var alice = await EuroLedgerAsync();
        await alice.AddBankAccountAsync("Pound bank", currencyCode: "GBP");
        await EnableAsync(alice.UserId, alice.LedgerId, FrankfurterRatesClient.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var refresh = await client.PostAsync($"/api/ledgers/{alice.LedgerId}/quotes/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        // A typed rate never lands after today, so it cannot outrun the fetch here; the
        // GBP pair's typed day sits before its coverage and leaves it as fetched.
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "GBP", "USD", Iso(today.AddDays(-400)), 1.3m)).StatusCode);

        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{alice.LedgerId}/fx-rates"))!;

        Assert.Equal("USD", view.ReportingCurrency);
        Assert.Equal(["EUR/USD", "GBP/USD"], view.Pairs.Select(p => $"{p.FromCurrency}/{p.ToCurrency}"));
        Assert.All(view.Pairs, p => Assert.Equal(["net-worth"], p.NeededFor));
        Assert.All(view.Pairs, p => Assert.Equal(today, p.FetchedThroughDate));
        // No history: the first fetch asked from a week back, for the latest rate.
        Assert.All(view.Pairs, p => Assert.Equal(today.AddDays(-MarketSeriesFetcher.PublishedDayReachBack), p.FetchedFromDate));
        Assert.All(view.Pairs, p => Assert.Equal(today, p.CoveredThroughDate));
        Assert.Equal(today.AddDays(-400), view.Pairs[1].LatestManualDate);
        Assert.Null(view.Pairs[0].LatestManualDate);
    }

    [Fact]
    public async Task Coverage_runs_to_a_typed_day_when_nothing_was_fetched()
    {
        var alice = await EuroLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "USD", "2026-05-20", 1.1m)).StatusCode);

        var pair = Assert.Single((await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{alice.LedgerId}/fx-rates"))!.Pairs);

        Assert.Null(pair.FetchedThroughDate);
        Assert.Equal(new DateOnly(2026, 5, 20), pair.CoveredThroughDate);
        Assert.Equal(1, pair.RateCount);
    }

    [Fact]
    public async Task The_rates_view_names_what_needs_each_pair_and_the_days_it_has_no_rate_for()
    {
        // USD ledger: a CAD brokerage holding a fund priced in EUR, a GBP account, and a CAD
        // benchmark. Synthetic names throughout.
        var alice = await SyntheticLedger.CreateAsync(Db);
        var savings = await alice.AddBankAccountAsync("Savings", currencyCode: "GBP");
        var brokerage = await alice.AddInvestmentAccountAsync("Brokerage B", currencyCode: "CAD");
        var fund = await alice.AddSecurityAsync("Euro Equity Fund", ticker: "EUEQ");
        await alice.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, fund, 10m, 1000m);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        await alice.AddSecurityPriceAsync(fund, 100m, today.AddDays(-20).ToDateTime(TimeOnly.MinValue), currencyCode: "EUR");
        await EnableAsync(alice.UserId, alice.LedgerId, YahooFinanceQuoteProvider.Key);
        _fake.Currencies["VXC"] = "CAD";
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var benchmark = (await (await AddBenchmark(client, alice.LedgerId, "VXC")).Content.ReadFromJsonAsync<BenchmarkDto>())!;
        await WhenBackfillsIdleAsync(factory);
        // EUR→CAD typed ten days back only; CAD→USD typed five days back and yesterday.
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "CAD", Iso(today.AddDays(-10)), 1.46m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "CAD", "USD", Iso(today.AddDays(-5)), 0.72m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "CAD", "USD", Iso(today.AddDays(-1)), 0.73m)).StatusCode);

        var view = (await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{alice.LedgerId}/fx-rates"))!;
        var byPair = view.Pairs.ToDictionary(p => $"{p.FromCurrency}/{p.ToCurrency}");

        Assert.Equal(["CAD/USD", "EUR/CAD", "GBP/USD"], byPair.Keys);
        var cad = byPair["CAD/USD"];
        Assert.Equal(
            [("net-worth", brokerage.Id, "Brokerage B", (string?)null, (Guid?)null), ("benchmark", benchmark.Id, benchmark.Name, "VXC", null)],
            cad.NeededBy.Select(n => (n.NeededFor, n.Id, n.Name, n.Symbol, n.AccountId)));
        Assert.Equal(today.AddDays(-5), cad.FirstRateDate);
        Assert.Empty(cad.MissingDays);

        var eur = byPair["EUR/CAD"];
        var held = Assert.Single(eur.NeededBy);
        Assert.Equal(("position", fund, "Euro Equity Fund", "EUEQ", (Guid?)brokerage.Id, "Brokerage B"),
            (held.NeededFor, held.Id, held.Name, held.Symbol, held.AccountId, held.AccountName));
        Assert.Equal(today.AddDays(-10), eur.FirstRateDate);
        // Before the first rate, and after the typed coverage through today.
        Assert.Equal(
            [(today.AddDays(-20), today.AddDays(-11)), (today.AddDays(-9), today)],
            eur.MissingDays.Select(m => (m.FromDate, m.ThroughDate)));

        var gbp = byPair["GBP/USD"];
        Assert.Equal([("net-worth", savings.Id, "Savings")], gbp.NeededBy.Select(n => (n.NeededFor, n.Id, n.Name)));
        Assert.Null(gbp.FirstRateDate);
        // No history: only the latest rate is needed, and it is missing.
        Assert.Equal([(today, today)], gbp.MissingDays.Select(m => (m.FromDate, m.ThroughDate)));
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ---- the derived rate between two account currencies ----------------------------

    /// <summary>The past day the derived-rate cases ask for: before the server's UTC today,
    /// so each leg reads its own rate on or before it.</summary>
    private static DateOnly AskedDay => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime).AddDays(-10);

    /// <summary>A USD ledger with a EUR and a GBP bank account, so both legs into USD are
    /// needed and can be typed.</summary>
    private async Task<SyntheticLedger> TwoCurrencyLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Euro bank", currencyCode: "EUR");
        await ledger.AddBankAccountAsync("Pound bank", currencyCode: "GBP");
        return ledger;
    }

    /// <summary>A rate typed after <see cref="AskedDay"/> so the pair's coverage reaches past
    /// it; its value is never the one read.</summary>
    private static Task<HttpResponseMessage> CoverPast(HttpClient client, Guid ledgerId, string from) =>
        AddRate(client, ledgerId, from, "USD", Iso(AskedDay.AddDays(2)), 7m);

    private static string DerivedPath(Guid ledgerId, string from, string to, string date, string? amount = null) =>
        $"/api/ledgers/{ledgerId}/fx-rates/{from}/{to}/derived?date={date}" + (amount is null ? "" : $"&amount={amount}");

    [Fact]
    public async Task The_derived_rate_crosses_both_stored_legs_and_is_good_for_the_older_legs_day()
    {
        var alice = await TwoCurrencyLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        // The EUR leg is stored five days before the day asked for, the GBP leg two days.
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "USD", Iso(AskedDay.AddDays(-5)), 1.1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "GBP", "USD", Iso(AskedDay.AddDays(-2)), 1.25m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CoverPast(client, alice.LedgerId, "EUR")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CoverPast(client, alice.LedgerId, "GBP")).StatusCode);
        await using (var count = Db.NewDbContext())
            Assert.Equal(4, await count.FxRates.CountAsync(r => r.LedgerId == alice.LedgerId));

        var derived = (await client.GetFromJsonAsync<DerivedRateDto>(
            DerivedPath(alice.LedgerId, "eur", "gbp", Iso(AskedDay), "-10.01")))!;

        Assert.Equal(("EUR", "GBP", AskedDay, "USD"), (derived.FromCurrency, derived.ToCurrency, derived.Date, derived.ReportingCurrency));
        Assert.Equal(1.1m / 1.25m, derived.Rate);
        Assert.Equal(AskedDay.AddDays(-5), derived.RateDate);
        Assert.Equal(
            [new DerivedRateLegDto("EUR", "USD", AskedDay.AddDays(-5), 1.1m), new DerivedRateLegDto("GBP", "USD", AskedDay.AddDays(-2), 1.25m)],
            derived.Legs);
        // A negative two-place amount, converted at full precision and rounded once at the
        // destination's two places, half away from zero, keeping its sign.
        Assert.Equal(-8.81m, derived.Converted);
        Assert.Empty(derived.RateUnavailable);

        // No amount: the rate alone. And nothing was written.
        var bare = (await client.GetFromJsonAsync<DerivedRateDto>(DerivedPath(alice.LedgerId, "EUR", "GBP", Iso(AskedDay))))!;
        Assert.Equal((1.1m / 1.25m, (decimal?)null), (bare.Rate, bare.Converted));
        await using var read = Db.NewDbContext();
        Assert.Equal(4, await read.FxRates.CountAsync(r => r.LedgerId == alice.LedgerId));
    }

    [Fact]
    public async Task A_missing_leg_is_named_in_the_direction_it_is_stored_and_leaves_no_rate()
    {
        var alice = await TwoCurrencyLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "USD", Iso(AskedDay.AddDays(-5)), 1.1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CoverPast(client, alice.LedgerId, "EUR")).StatusCode);

        var derived = (await client.GetFromJsonAsync<DerivedRateDto>(
            DerivedPath(alice.LedgerId, "EUR", "GBP", Iso(AskedDay), "10")))!;

        Assert.Null(derived.Rate);
        Assert.Null(derived.RateDate);
        Assert.Null(derived.Converted);
        Assert.Empty(derived.Legs);
        var missing = Assert.Single(derived.RateUnavailable);
        Assert.Equal(("GBP", "USD", AskedDay), (missing.FromCurrency, missing.ToCurrency, missing.Date));
    }

    [Fact]
    public async Task A_day_before_the_ledgers_earliest_day_has_no_rate()
    {
        // The EUR account opens on the day its first rate is stored; the day before it has
        // none, while the opening day itself reads that rate.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);
        var opened = AskedDay.AddDays(-30);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ledgers/{alice.LedgerId}/accounts",
            new { name = "Euro bank", accountType = "bank", currencyCode = "EUR", openedOn = Iso(opened) })).StatusCode);
        await WhenBackfillsIdleAsync(factory);
        Assert.Equal(opened, Assert.Single((await client.GetFromJsonAsync<FxRatesViewDto>($"/api/ledgers/{alice.LedgerId}/fx-rates"))!.Pairs).NeededFrom);
        Assert.Equal(HttpStatusCode.Created, (await AddRate(client, alice.LedgerId, "EUR", "USD", Iso(opened), 1.1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CoverPast(client, alice.LedgerId, "EUR")).StatusCode);

        var before = (await client.GetFromJsonAsync<DerivedRateDto>(
            DerivedPath(alice.LedgerId, "EUR", "USD", Iso(opened.AddDays(-1)), "10")))!;
        var onTheDay = (await client.GetFromJsonAsync<DerivedRateDto>(
            DerivedPath(alice.LedgerId, "EUR", "USD", Iso(opened), "10")))!;

        Assert.Null(before.Rate);
        Assert.Null(before.RateDate);
        Assert.Null(before.Converted);
        var missing = Assert.Single(before.RateUnavailable);
        Assert.Equal(("EUR", "USD", opened.AddDays(-1)), (missing.FromCurrency, missing.ToCurrency, missing.Date));
        Assert.Equal((1.1m, (DateOnly?)opened, (decimal?)11m), (onTheDay.Rate, onTheDay.RateDate, onTheDay.Converted));
    }

    [Fact]
    public async Task A_viewer_reads_the_derived_rate_and_a_ledger_the_caller_cannot_see_is_refused()
    {
        var alice = await TwoCurrencyLedgerAsync();
        var vera = await MemberAsync(alice, "viewer");
        var mallory = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var viewer = await ClientAsync(factory, vera);
        using var stranger = await ClientAsync(factory, mallory);
        var path = DerivedPath(alice.LedgerId, "EUR", "GBP", Iso(AskedDay));

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(path)).StatusCode);
        Assert.Equal("ledger-not-visible", await CodeOf(await stranger.GetAsync(path)));
    }

    [Fact]
    public async Task A_derived_rate_needs_two_currencies_and_a_readable_day()
    {
        var alice = await TwoCurrencyLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        Assert.Equal(BusinessErrorCodes.FxRateCurrencyInvalid,
            await CodeOf(await client.GetAsync(DerivedPath(alice.LedgerId, "EUR", "eur", Iso(AskedDay)))));
        Assert.Equal(BusinessErrorCodes.FxRateCurrencyInvalid,
            await CodeOf(await client.GetAsync(DerivedPath(alice.LedgerId, "EURO", "GBP", Iso(AskedDay)))));
        Assert.Equal(BusinessErrorCodes.FxRateDateInvalid,
            await CodeOf(await client.GetAsync(DerivedPath(alice.LedgerId, "EUR", "GBP", "20-05-2026"))));
        Assert.Equal(BusinessErrorCodes.FxRateDateInvalid,
            await CodeOf(await client.GetAsync($"/api/ledgers/{alice.LedgerId}/fx-rates/EUR/GBP/derived")));
    }

    // ---- the manual Refresh --------------------------------------------------------

    [Fact]
    public async Task The_manual_refresh_fetches_rates_only_under_the_requesting_users_Frankfurter_opt_in()
    {
        var alice = await EuroLedgerAsync();
        var bob = await MemberAsync(alice, "editor");
        await EnableAsync(alice.UserId, alice.LedgerId, FrankfurterRatesClient.Key);
        await using var factory = Factory();

        using (var bobClient = await ClientAsync(factory, bob))
        {
            var outcome = await (await bobClient.PostAsync($"/api/ledgers/{alice.LedgerId}/quotes/refresh", null))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, outcome.GetProperty("ratesInserted").GetInt32());
            Assert.Empty(_fake.Requests);
        }

        using var aliceClient = await ClientAsync(factory, alice);
        var mine = await (await aliceClient.PostAsync($"/api/ledgers/{alice.LedgerId}/quotes/refresh", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(mine.GetProperty("ratesInserted").GetInt32() > 0);
        Assert.Single(_fake.RateRequests);
        Assert.Empty(_fake.YahooRequests);
    }

    // ---- triggers ------------------------------------------------------------------

    [Fact]
    public async Task Turning_Frankfurter_on_backfills_at_once_and_turning_it_on_again_does_not()
    {
        var alice = await EuroLedgerAsync();
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var put = await client.PutAsJsonAsync($"/api/ledgers/{alice.LedgerId}/preferences/quotes",
            new { enabledProviders = new[] { FrankfurterRatesClient.Key } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await WhenBackfillsIdleAsync(factory);

        Assert.Single(_fake.RateRequests);
        var run = Assert.Single(await BackfillRunsAsync(alice.LedgerId));
        Assert.True(LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson).RatesInserted > 0);

        await client.PutAsJsonAsync($"/api/ledgers/{alice.LedgerId}/preferences/quotes",
            new { enabledProviders = new[] { FrankfurterRatesClient.Key, YahooFinanceQuoteProvider.Key } });
        await WhenBackfillsIdleAsync(factory);
        Assert.Single(_fake.RateRequests);
    }

    [Fact]
    public async Task A_viewer_turning_Frankfurter_on_starts_no_backfill()
    {
        var alice = await EuroLedgerAsync();
        var vera = await MemberAsync(alice, "viewer");
        await using var factory = Factory();
        using var client = await ClientAsync(factory, vera);

        var put = await client.PutAsJsonAsync($"/api/ledgers/{alice.LedgerId}/preferences/quotes",
            new { enabledProviders = new[] { FrankfurterRatesClient.Key } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await WhenBackfillsIdleAsync(factory);

        Assert.Empty(_fake.Requests);
        Assert.Empty(await BackfillRunsAsync(alice.LedgerId));
    }

    [Fact]
    public async Task Creating_an_account_in_a_new_currency_backfills_and_one_in_a_known_currency_does_not()
    {
        var alice = await EuroLedgerAsync();
        await EnableAsync(alice.UserId, alice.LedgerId, FrankfurterRatesClient.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ledgers/{alice.LedgerId}/accounts",
            new { name = "Another euro bank", accountType = "bank", currencyCode = "EUR" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ledgers/{alice.LedgerId}/accounts",
            new { name = "Dollar bank", accountType = "bank", currencyCode = "USD" })).StatusCode);
        await WhenBackfillsIdleAsync(factory);
        Assert.Empty(_fake.Requests);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/ledgers/{alice.LedgerId}/accounts",
            new { name = "Franc bank", accountType = "bank", currencyCode = "chf" })).StatusCode);
        await WhenBackfillsIdleAsync(factory);

        // The backfill fetches whatever the ledger's rates lack, the new pair among them.
        Assert.Contains(_fake.RateRequests, r => r.Query.Contains("base=CHF", StringComparison.Ordinal));
        Assert.Single(await BackfillRunsAsync(alice.LedgerId));
    }

    [Fact]
    public async Task Editing_an_account_into_a_new_currency_backfills()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bank = await alice.AddBankAccountAsync("Bank");
        await EnableAsync(alice.UserId, alice.LedgerId, FrankfurterRatesClient.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var patch = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{alice.LedgerId}/accounts/{bank.Id}")
        {
            Content = JsonContent.Create(new { currencyCode = "JPY" }),
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        await WhenBackfillsIdleAsync(factory);

        Assert.Contains("base=JPY", Assert.Single(_fake.RateRequests).Query, StringComparison.Ordinal);
    }

    // ---- the reporting currency ----------------------------------------------------

    private static HttpRequestMessage PatchLedger(Guid ledgerId, object body) =>
        new(HttpMethod.Patch, $"/api/ledgers/{ledgerId}") { Content = JsonContent.Create(body) };

    [Fact]
    public async Task A_ledger_is_created_in_the_currency_named_and_USD_otherwise()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        var euro = (await (await client.PostAsJsonAsync("/api/ledgers", new { name = "Euro books", currencyCode = "eur", seedDefaultCategories = false }))
            .Content.ReadFromJsonAsync<LedgerSummary>())!;
        var plain = (await (await client.PostAsJsonAsync("/api/ledgers", new { name = "Plain books", seedDefaultCategories = false }))
            .Content.ReadFromJsonAsync<LedgerSummary>())!;

        Assert.Equal(("EUR", "USD"), (euro.CurrencyCode, plain.CurrencyCode));
        var listed = (await client.GetFromJsonAsync<LedgerSummary[]>("/api/ledgers"))!;
        Assert.Equal("EUR", listed.Single(l => l.Id == euro.Id).CurrencyCode);
        Assert.Equal(BusinessErrorCodes.LedgerCurrencyInvalid,
            await CodeOf(await client.PostAsJsonAsync("/api/ledgers", new { name = "Bad", currencyCode = "EURO" })));
    }

    [Fact]
    public async Task Only_an_owner_changes_the_reporting_currency()
    {
        var alice = await EuroLedgerAsync();
        var bob = await MemberAsync(alice, "editor");
        await using var factory = Factory();
        using var bobClient = await ClientAsync(factory, bob);

        Assert.Equal("ledger-not-owner", await CodeOf(await bobClient.SendAsync(PatchLedger(alice.LedgerId, new { currencyCode = "EUR" }))));

        await using var db = Db.NewDbContext();
        Assert.Equal("USD", await db.Ledgers.Where(l => l.Id == alice.LedgerId).Select(l => l.CurrencyCode).SingleAsync());
    }

    [Fact]
    public async Task An_owners_currency_change_is_stored_and_backfills_the_new_pairs()
    {
        var alice = await EuroLedgerAsync();
        await EnableAsync(alice.UserId, alice.LedgerId, FrankfurterRatesClient.Key);
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        Assert.Equal(BusinessErrorCodes.LedgerCurrencyInvalid,
            await CodeOf(await client.SendAsync(PatchLedger(alice.LedgerId, new { currencyCode = "X" }))));
        var resp = await client.SendAsync(PatchLedger(alice.LedgerId, new { currencyCode = "gbp" }));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        await WhenBackfillsIdleAsync(factory);

        await using (var db = Db.NewDbContext())
            Assert.Equal("GBP", await db.Ledgers.Where(l => l.Id == alice.LedgerId).Select(l => l.CurrencyCode).SingleAsync());
        var request = Assert.Single(_fake.RateRequests);
        Assert.Contains("base=EUR", request.Query, StringComparison.Ordinal);
        Assert.Contains("quotes=GBP", request.Query, StringComparison.Ordinal);

        // The same code again changes nothing and fetches nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PatchLedger(alice.LedgerId, new { currencyCode = "GBP" }))).StatusCode);
        await WhenBackfillsIdleAsync(factory);
        Assert.Single(_fake.RateRequests);
    }

    [Fact]
    public async Task A_rename_alone_leaves_the_currency_and_an_empty_patch_is_refused()
    {
        var alice = await SyntheticLedger.CreateAsync(Db, currencyCode: "CAD");
        await using var factory = Factory();
        using var client = await ClientAsync(factory, alice);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(PatchLedger(alice.LedgerId, new { name = "Renamed" }))).StatusCode);
        Assert.Equal(BusinessErrorCodes.LedgerNameRequired, await CodeOf(await client.SendAsync(PatchLedger(alice.LedgerId, new { }))));

        var row = (await client.GetFromJsonAsync<LedgerSummary[]>("/api/ledgers"))!.Single(l => l.Id == alice.LedgerId);
        Assert.Equal(("Renamed", "CAD"), (row.Name, row.CurrencyCode));
    }
}

/// <summary>The business codes these tests read, by their wire spelling.</summary>
internal static class BusinessErrorCodes
{
    public const string BenchmarkYahooOff = Coffer.Api.Errors.BusinessError.Codes.BenchmarkYahooOff;
    public const string BenchmarkSymbolInvalid = Coffer.Api.Errors.BusinessError.Codes.BenchmarkSymbolInvalid;
    public const string BenchmarkAlreadyAdded = Coffer.Api.Errors.BusinessError.Codes.BenchmarkAlreadyAdded;
    public const string BenchmarkLimitReached = Coffer.Api.Errors.BusinessError.Codes.BenchmarkLimitReached;
    public const string BenchmarkNotFound = Coffer.Api.Errors.BusinessError.Codes.BenchmarkNotFound;
    public const string FxRateDayTaken = Coffer.Api.Errors.BusinessError.Codes.FxRateDayTaken;
    public const string FxPairNotNeeded = Coffer.Api.Errors.BusinessError.Codes.FxPairNotNeeded;
    public const string FxRateInvalid = Coffer.Api.Errors.BusinessError.Codes.FxRateInvalid;
    public const string FxRateDateInvalid = Coffer.Api.Errors.BusinessError.Codes.FxRateDateInvalid;
    public const string FxRateDateInFuture = Coffer.Api.Errors.BusinessError.Codes.FxRateDateInFuture;
    public const string FxRateCurrencyInvalid = Coffer.Api.Errors.BusinessError.Codes.FxRateCurrencyInvalid;
    public const string LedgerCurrencyInvalid = Coffer.Api.Errors.BusinessError.Codes.LedgerCurrencyInvalid;
    public const string LedgerNameRequired = Coffer.Api.Errors.BusinessError.Codes.LedgerNameRequired;
}
