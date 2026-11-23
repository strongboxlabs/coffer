using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// The repair of fetched prices stamped USD before the currency fix (ADR-0114 D5,
/// amendment), through the real Yahoo provider over a fake HTTP handler: a read that names
/// a security's currency confirms it and re-stamps its fetch rows, and nothing else; the
/// refresh asks once for every unconfirmed security with fetched prices, held or not.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteCurrencyRestampTests : DatabaseTest
{
    public QuoteCurrencyRestampTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers each chart request with the currency its symbol maps to (a missing
    /// symbol answers 404), and records every symbol asked for.</summary>
    private sealed class FakeYahoo : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _currencyBySymbol;
        public FakeYahoo(Dictionary<string, string> currencyBySymbol) { _currencyBySymbol = currencyBySymbol; }
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var symbol = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/')[^1]);
            Asked.Add(symbol);
            if (!_currencyBySymbol.TryGetValue(symbol, out var currency))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bar = new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var json = $$"""
                {
                  "chart": {
                    "result": [
                      {
                        "meta": { "currency": "{{currency}}", "symbol": "{{symbol}}" },
                        "timestamp": [{{bar}}],
                        "indicators": { "quote": [ { "close": [50.5] } ] }
                      }
                    ],
                    "error": null
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static readonly DateOnly OldDay1 = new(2026, 3, 2);
    private static readonly DateOnly OldDay2 = new(2026, 3, 3);
    private static readonly DateOnly ManualDay = new(2026, 3, 4);
    private static readonly DateOnly ImportDay = new(2026, 3, 5);

    [Fact]
    public async Task A_fetch_that_names_a_currency_confirms_it_and_restamps_only_older_fetch_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var held = await HeldSecurityAsync(ledger, "HELDF");
        await SeedRowsAsync(ledger, held);
        await EnableYahooAsync(ledger);
        var yahoo = new FakeYahoo(new() { ["HELDF"] = "EUR" });

        var outcome = await RunAsync(ledger, yahoo);

        var rows = await RowsAsync(held);
        // The two pre-fix fetch rows now name EUR, at exactly the figures they had.
        AssertRow(rows[OldDay1], "EUR", 41.25m, 42m, 40m, 1000);
        AssertRow(rows[OldDay2], "EUR", 41.5m, 42.5m, 40.5m, 2000);
        // Another source's row is not Yahoo's to name: untouched.
        AssertRow(rows[ManualDay], "USD", 39m, null, null, null);
        AssertRow(rows[ImportDay], "USD", 38m, null, null, null);
        Assert.Equal("EUR", rows[new DateOnly(2026, 6, 1)].CurrencyCode);
        Assert.Equal(Now.UtcDateTime, await ConfirmedAtAsync(held));
        Assert.Equal(1, outcome.CurrenciesConfirmed);
        Assert.Equal(2, outcome.RowsRestamped);
        Assert.Empty(outcome.Errors);

        var details = await RunDetailsAsync(ledger);
        Assert.Equal(1, details.CurrenciesConfirmed);
        Assert.Equal(2, details.RowsRestamped);
    }

    [Fact]
    public async Task The_one_time_pass_asks_once_for_an_unheld_security_with_fetched_prices_and_stores_no_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var unheld = await ledger.AddSecurityAsync("Sold Fund", ticker: "GONEF");
        await SeedRowsAsync(ledger, unheld);
        await EnableYahooAsync(ledger);
        var yahoo = new FakeYahoo(new() { ["GONEF"] = "CAD" });

        var first = await RunAsync(ledger, yahoo);

        // Delivered: the unheld security's symbol reached Yahoo, though nothing holds it.
        Assert.Equal(["GONEF"], yahoo.Asked);
        var rows = await RowsAsync(unheld);
        Assert.Equal(4, rows.Count);   // no price stored by the lookup
        AssertRow(rows[OldDay1], "CAD", 41.25m, 42m, 40m, 1000);
        AssertRow(rows[OldDay2], "CAD", 41.5m, 42.5m, 40.5m, 2000);
        AssertRow(rows[ManualDay], "USD", 39m, null, null, null);
        Assert.Equal(1, first.CurrenciesConfirmed);
        Assert.Equal(2, first.RowsRestamped);
        Assert.Equal(0, first.PricesInserted);

        // Once: confirmed, so the next refresh does not ask again.
        var second = await RunAsync(ledger, yahoo);
        Assert.Equal(["GONEF"], yahoo.Asked);
        Assert.Equal(0, second.CurrenciesConfirmed);
    }

    [Fact]
    public async Task Without_the_runs_users_yahoo_opt_in_nothing_is_asked_or_restamped()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var unheld = await ledger.AddSecurityAsync("Sold Fund", ticker: "GONEF");
        await SeedRowsAsync(ledger, unheld);
        var yahoo = new FakeYahoo(new() { ["GONEF"] = "CAD" });

        var outcome = await RunAsync(ledger, yahoo);

        Assert.Empty(yahoo.Asked);
        Assert.Null(await ConfirmedAtAsync(unheld));
        Assert.Equal("USD", (await RowsAsync(unheld))[OldDay1].CurrencyCode);
        Assert.Equal(0, outcome.CurrenciesConfirmed);
    }

    [Fact]
    public async Task A_failed_lookup_confirms_nothing_and_is_asked_again_next_time()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var unheld = await ledger.AddSecurityAsync("Sold Fund", ticker: "GONEF");
        await SeedRowsAsync(ledger, unheld);
        await EnableYahooAsync(ledger);
        var yahoo = new FakeYahoo([]);   // 404 for every symbol

        var outcome = await RunAsync(ledger, yahoo);
        await RunAsync(ledger, yahoo);

        Assert.Equal(["GONEF", "GONEF"], yahoo.Asked);
        Assert.Equal(unheld, Assert.Single(outcome.Errors).SecurityId);
        Assert.Null(await ConfirmedAtAsync(unheld));
        Assert.Equal("USD", (await RowsAsync(unheld))[OldDay1].CurrencyCode);
    }

    [Fact]
    public async Task A_minor_unit_symbols_pre_fix_rows_are_converted_the_way_a_new_fetch_is_stored()
    {
        // Its pre-fix rows hold pence figures stamped USD: re-stamping them GBP at the same
        // number would read 4125 pence as 4125 pounds. So price, high and low are divided
        // by the factor and rounded once to 4dp, GBP is stamped, and volume is left alone.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var unheld = await ledger.AddSecurityAsync("London Fund", ticker: "LSEF.L");
        await SeedRowsAsync(ledger, unheld);
        var tieDay = new DateOnly(2026, 3, 6);
        var majorDay = new DateOnly(2026, 3, 9);
        await using (var db = Db.NewDbContext())
        {
            // A pence figure that is half-way at the fourth place once in pounds, rounded as
            // the fetch path rounds (decimal.Round, to even), and a row already in pounds.
            var tie = Row(ledger, unheld, tieDay, 1234.565m, PriceSource.Fetch, 1240.1235m, 1230.0001m, 7);
            var major = Row(ledger, unheld, majorDay, 12.5m, PriceSource.Fetch, 12.75m, 12.25m, 9);
            major.CurrencyCode = "GBP";
            db.SecurityPrices.AddRange(tie, major);
            await db.SaveChangesAsync();
        }
        await EnableYahooAsync(ledger);
        var yahoo = new FakeYahoo(new() { ["LSEF.L"] = "GBp" });

        var outcome = await RunAsync(ledger, yahoo);

        Assert.Equal(["LSEF.L"], yahoo.Asked);
        Assert.Empty(outcome.Errors);
        Assert.NotNull(await ConfirmedAtAsync(unheld));
        var rows = await RowsAsync(unheld);
        AssertRow(rows[OldDay1], "GBP", 0.4125m, 0.42m, 0.40m, 1000);
        AssertRow(rows[OldDay2], "GBP", 0.415m, 0.425m, 0.405m, 2000);
        AssertRow(rows[tieDay], "GBP", decimal.Round(1234.565m / 100m, 4), 12.4012m, 12.3000m, 7);
        Assert.Equal(12.3456m, rows[tieDay].Price);
        // Already in the major unit: not converted again.
        AssertRow(rows[majorDay], "GBP", 12.5m, 12.75m, 12.25m, 9);
        // Another source's rows are not Yahoo's to name: untouched, figures and all.
        AssertRow(rows[ManualDay], "USD", 39m, null, null, null);
        AssertRow(rows[ImportDay], "USD", 38m, null, null, null);
        Assert.Equal(3, outcome.RowsRestamped);
        Assert.Equal(1, outcome.CurrenciesConfirmed);
    }

    [Fact]
    public async Task A_minor_unit_symbol_whose_fetch_rows_already_agree_is_confirmed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var held = await HeldSecurityAsync(ledger, "LSEF.L");
        await EnableYahooAsync(ledger);
        var yahoo = new FakeYahoo(new() { ["LSEF.L"] = "GBp" });

        var outcome = await RunAsync(ledger, yahoo);

        Assert.Empty(outcome.Errors);
        Assert.Equal(1, outcome.CurrenciesConfirmed);
        Assert.NotNull(await ConfirmedAtAsync(held));
        Assert.Equal(0.505m, (await RowsAsync(held))[new DateOnly(2026, 6, 1)].Price);
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<Guid> HeldSecurityAsync(SyntheticLedger ledger, string ticker)
    {
        var brokerage = await ledger.AddInvestmentAccountAsync($"Brokerage {ticker}");
        var id = await ledger.AddSecurityAsync($"Fund {ticker}", ticker: ticker);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, id, 10m, 400m);
        return id;
    }

    /// <summary>Two pre-fix fetch rows stamped USD (with high, low and volume, so a test can
    /// see they are left as they were), a manual row and an import row.</summary>
    private async Task SeedRowsAsync(SyntheticLedger ledger, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        db.SecurityPrices.AddRange(
            Row(ledger, securityId, OldDay1, 41.25m, PriceSource.Fetch, 42m, 40m, 1000),
            Row(ledger, securityId, OldDay2, 41.5m, PriceSource.Fetch, 42.5m, 40.5m, 2000),
            Row(ledger, securityId, ManualDay, 39m, PriceSource.Manual),
            Row(ledger, securityId, ImportDay, 38m, PriceSource.Import));
        await db.SaveChangesAsync();
    }

    private static SecurityPriceRow Row(
        SyntheticLedger ledger, Guid securityId, DateOnly day, decimal price, string source,
        decimal? high = null, decimal? low = null, long? volume = null) => new()
    {
        Id = Guid.NewGuid(),
        LedgerId = ledger.LedgerId,
        SecurityId = securityId,
        Price = price,
        CurrencyCode = "USD",
        PriceDate = day,
        High = high,
        Low = low,
        Volume = volume,
        Source = source,
    };

    private async Task EnableYahooAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        await new UserPreferencesRepository(db, new TestClock(Now)).SetQuotesAsync(
            ledger.UserId, ledger.LedgerId, new QuotesPrefs { EnabledProviders = [YahooFinanceQuoteProvider.Key] });
    }

    private async Task<QuoteRunOutcome> RunAsync(SyntheticLedger ledger, FakeYahoo yahoo)
    {
        var clock = new TestClock(Now);
        var http = new HttpClient(yahoo) { BaseAddress = new Uri("https://query1.finance.yahoo.com") };
        var provider = new YahooFinanceQuoteProvider(
            http, Options.Create(new YahooQuoteOptions { RequestDelayMs = 0, MaxBackoffSeconds = 0 }),
            NullLogger<YahooFinanceQuoteProvider>.Instance, clock);
        await using var db = Db.NewDbContext();
        var orchestrator = new QuoteOrchestrator(
            db, [provider], [], new UserPreferencesRepository(db, clock),
            NullLogger<QuoteOrchestrator>.Instance, clock);
        return await orchestrator.RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);
    }

    private async Task<Dictionary<DateOnly, SecurityPriceRow>> RowsAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .ToDictionaryAsync(p => p.PriceDate);
    }

    private async Task<DateTime?> ConfirmedAtAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.Securities.AsNoTracking()
            .Where(s => s.Id == securityId)
            .Select(s => s.QuoteCurrencyConfirmedAt)
            .SingleAsync();
    }

    private async Task<QuoteRunDetails> RunDetailsAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        var run = await db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledger.LedgerId && r.Family == "quote")
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync();
        return LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
    }

    private static void AssertRow(
        SecurityPriceRow row, string currency, decimal price, decimal? high, decimal? low, long? volume)
    {
        Assert.Equal(currency, row.CurrencyCode);
        Assert.Equal(price, row.Price);
        Assert.Equal(high, row.High);
        Assert.Equal(low, row.Low);
        Assert.Equal(volume, row.Volume);
    }
}
