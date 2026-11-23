using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The quote_currency projection (ADR-0114 D5, amendment): a security with fetched prices
/// whose currency Yahoo has not confirmed, and a confirmed security with fetched prices in
/// another currency, are reported; the repair asks Yahoo, under the repairing user's opt-in,
/// and re-stamps fetch rows only, prices unchanged.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyQuoteCurrencyTests : DatabaseTest
{
    public ConsistencyQuoteCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly Day1 = new(2026, 3, 2);
    private static readonly DateOnly Day2 = new(2026, 3, 3);
    private static readonly DateOnly Day3 = new(2026, 3, 4);

    [Fact]
    public async Task A_security_with_unconfirmed_fetched_prices_is_reported_and_one_without_fetched_prices_is_not()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var fetched = await ledger.AddSecurityAsync("Fetched Fund", ticker: "FETF");
        var manualOnly = await ledger.AddSecurityAsync("Manual Fund", ticker: "MANF");
        await SeedAsync(ledger, fetched, (Day1, PriceSource.Fetch, "USD"), (Day2, PriceSource.Fetch, "USD"));
        await SeedAsync(ledger, manualOnly, (Day1, PriceSource.Manual, "USD"));

        var p = await ProjectionAsync(ledger);

        Assert.False(p.Healthy);
        Assert.Equal(1, p.Checked);
        var m = Assert.Single(p.Mismatches);
        Assert.Equal(fetched, m.SecurityId);
        Assert.Equal(ConsistencyReasons.CurrencyUnconfirmed, m.Reason);
        Assert.Contains("2 fetched prices in USD", m.Field, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmed_security_with_a_fetched_price_in_another_currency_is_reported()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        await SeedAsync(ledger, security,
            (Day1, PriceSource.Fetch, "USD"), (Day2, PriceSource.Fetch, "EUR"), (Day3, PriceSource.Manual, "USD"));
        await ConfirmAsync(security);

        var p = await ProjectionAsync(ledger);

        var m = Assert.Single(p.Mismatches);
        Assert.Equal(ConsistencyReasons.ValueDiffers, m.Reason);
        Assert.Equal("1 fetched price in USD, but Yahoo quotes it in EUR", m.Field);
    }

    [Fact]
    public async Task A_confirmed_security_whose_fetched_prices_agree_is_healthy()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        await SeedAsync(ledger, security,
            (Day1, PriceSource.Fetch, "EUR"), (Day2, PriceSource.Fetch, "EUR"), (Day3, PriceSource.Import, "USD"));
        await ConfirmAsync(security);

        var p = await ProjectionAsync(ledger);

        Assert.True(p.Healthy);
        Assert.Equal(1, p.Checked);
    }

    [Fact]
    public async Task The_repair_asks_the_lookup_and_restamps_fetch_rows_only_prices_unchanged()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        await SeedAsync(ledger, security,
            (Day1, PriceSource.Fetch, "USD"), (Day2, PriceSource.Fetch, "USD"), (Day3, PriceSource.Manual, "USD"));
        var lookup = new FakeLookup(new() { ["EURF"] = "EUR" });

        await using (var db = Db.NewDbContext())
            await Repository(db).RewriteAsync(
                ledger.LedgerId, ConsistencyProjections.QuoteCurrency, ledger.UserId, lookup, CancellationToken.None);

        Assert.Equal(["EURF"], lookup.Asked);
        var rows = await RowsAsync(security);
        Assert.Equal(("EUR", 10m), (rows[Day1].CurrencyCode, rows[Day1].Price));
        Assert.Equal(("EUR", 11m), (rows[Day2].CurrencyCode, rows[Day2].Price));
        Assert.Equal(("USD", 12m), (rows[Day3].CurrencyCode, rows[Day3].Price));
        Assert.True((await ProjectionAsync(ledger)).Healthy);
    }

    [Fact]
    public async Task The_repair_names_each_security_it_could_not_rewrite_and_why()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var repaired = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        var failing = await ledger.AddSecurityAsync("Gone Fund", ticker: "GONEF");
        var feedOnly = await ledger.AddSecurityAsync(
            "Plan Portfolio", ticker: null, quoteSymbol: "0000", quoteSymbolPublic: false);
        foreach (var id in new[] { repaired, failing, feedOnly })
            await SeedAsync(ledger, id, (Day1, PriceSource.Fetch, "USD"));
        var lookup = new FakeLookup(new() { ["EURF"] = "EUR" });

        RewriteOutcomeView outcome;
        await using (var db = Db.NewDbContext())
        {
            var r = await Repository(db).RewriteAsync(
                ledger.LedgerId, ConsistencyProjections.QuoteCurrency, ledger.UserId, lookup, CancellationToken.None);
            outcome = new(r.Repaired.MismatchedCount, r.NotRepaired);
        }

        Assert.Equal(["EURF", "GONEF"], lookup.Asked.Order(StringComparer.Ordinal));   // never the private symbol
        Assert.Equal(3, outcome.Reported);
        Assert.Equal(2, outcome.NotRepaired.Count);
        var gone = Assert.Single(outcome.NotRepaired, m => m.SecurityId == failing);
        Assert.Equal("fetch-failed", gone.Reason);
        Assert.Equal("GONEF: HTTP 404", gone.Field);
        Assert.Equal("GONEF — Gone Fund", gone.Scope);   // named as the check names it
        var plan = Assert.Single(outcome.NotRepaired, m => m.SecurityId == feedOnly);
        Assert.Equal(ConsistencyReasons.QuoteSymbolNotPublic, plan.Reason);
        Assert.Equal("EUR", (await RowsAsync(repaired))[Day1].CurrencyCode);
        Assert.Equal("USD", (await RowsAsync(failing))[Day1].CurrencyCode);
    }

    private sealed record RewriteOutcomeView(int Reported, IReadOnlyList<ConsistencyMismatch> NotRepaired);

    [Fact]
    public async Task The_repair_endpoints_answer_lists_a_failed_lookup_under_not_repaired()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var failing = await ledger.AddSecurityAsync("Gone Fund", ticker: "GONEF");
        await SeedAsync(ledger, failing, (Day1, PriceSource.Fetch, "USD"));
        await using (var db = Db.NewDbContext())
        {
            await new UserPreferencesRepository(db, TimeProvider.System).SetQuotesAsync(
                ledger.UserId, ledger.LedgerId, new QuotesPrefs { EnabledProviders = [YahooFinanceQuoteProvider.Key] });
        }
        var yahoo = new FakeYahoo([]);   // 404 for every symbol
        await using var factory = FactoryWith(yahoo);
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(RepairPath(ledger), content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(["GONEF"], yahoo.Asked);
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var notRepaired = Assert.Single(result.GetProperty("notRepaired").EnumerateArray());
        Assert.Equal(failing.ToString(), notRepaired.GetProperty("securityId").GetString());
        Assert.Equal("fetch-failed", notRepaired.GetProperty("reason").GetString());
        Assert.Equal("GONEF — Gone Fund", notRepaired.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task The_repair_without_a_lookup_writes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        await SeedAsync(ledger, security, (Day1, PriceSource.Fetch, "USD"));

        await using (var db = Db.NewDbContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Repository(db).RewriteAsync(
                    ledger.LedgerId, ConsistencyProjections.QuoteCurrency, ledger.UserId, CancellationToken.None));
        }

        Assert.Equal("USD", (await RowsAsync(security))[Day1].CurrencyCode);
    }

    [Fact]
    public async Task The_repair_endpoint_refuses_a_user_without_the_yahoo_opt_in()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Fund", ticker: "EURF");
        await SeedAsync(ledger, security, (Day1, PriceSource.Fetch, "USD"));
        var yahoo = new FakeYahoo(new() { ["EURF"] = "EUR" });
        await using var factory = FactoryWith(yahoo);
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(RepairPath(ledger), content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consistency-repair-needs-yahoo", problem.GetProperty("code").GetString());
        Assert.Empty(yahoo.Asked);
        Assert.Equal("USD", (await RowsAsync(security))[Day1].CurrencyCode);
    }

    [Fact]
    public async Task The_repair_endpoint_with_the_opt_in_reaches_yahoo_and_repairs()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Unheld Fund", ticker: "EURF");
        await SeedAsync(ledger, security, (Day1, PriceSource.Fetch, "USD"), (Day2, PriceSource.Import, "USD"));
        await using (var db = Db.NewDbContext())
        {
            await new UserPreferencesRepository(db, TimeProvider.System).SetQuotesAsync(
                ledger.UserId, ledger.LedgerId, new QuotesPrefs { EnabledProviders = [YahooFinanceQuoteProvider.Key] });
        }
        var yahoo = new FakeYahoo(new() { ["EURF"] = "EUR" });
        await using var factory = FactoryWith(yahoo);
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(RepairPath(ledger), content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(["EURF"], yahoo.Asked);   // delivered through the registered client
        var rows = await RowsAsync(security);
        Assert.Equal(("EUR", 10m), (rows[Day1].CurrencyCode, rows[Day1].Price));
        Assert.Equal(("USD", 11m), (rows[Day2].CurrencyCode, rows[Day2].Price));
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var after = result.GetProperty("after").GetProperty("projections").EnumerateArray()
            .Single(p => p.GetProperty("projection").GetString() == ConsistencyProjections.QuoteCurrency);
        Assert.True(after.GetProperty("healthy").GetBoolean());
    }

    // ---- helpers ----------------------------------------------------------------

    private sealed class FakeLookup : IQuoteCurrencyLookup
    {
        private readonly Dictionary<string, string> _bySymbol;
        public FakeLookup(Dictionary<string, string> bySymbol) { _bySymbol = bySymbol; }
        public List<string> Asked { get; } = [];
        public string ProviderKey => YahooFinanceQuoteProvider.Key;

        public Task<QuoteCurrencyLookupResult> LookupCurrenciesAsync(
            IReadOnlyList<QuoteSecurityRef> securities, CancellationToken cancellationToken)
        {
            Asked.AddRange(securities.Select(s => s.Ticker));
            return Task.FromResult(new QuoteCurrencyLookupResult(
                securities.Where(s => _bySymbol.ContainsKey(s.Ticker))
                    .Select(s => new QuoteCurrency(s.SecurityId, _bySymbol[s.Ticker])).ToList(),
                securities.Where(s => !_bySymbol.ContainsKey(s.Ticker))
                    .Select(s => new QuoteError(s.SecurityId, s.Ticker, "fetch-failed", $"{s.Ticker}: HTTP 404"))
                    .ToList()));
        }
    }

    /// <summary>Yahoo's chart endpoint, faked at the handler: each symbol's currency, and
    /// every symbol asked for.</summary>
    private sealed class FakeYahoo : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bySymbol;
        public FakeYahoo(Dictionary<string, string> bySymbol) { _bySymbol = bySymbol; }
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var symbol = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/')[^1]);
            lock (Asked) Asked.Add(symbol);
            if (!_bySymbol.TryGetValue(symbol, out var currency))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = $$"""
                { "chart": { "result": [ { "meta": { "currency": "{{currency}}" },
                  "timestamp": [1780321800], "indicators": { "quote": [ { "close": [10] } ] } } ], "error": null } }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ApiFactory FactoryWith(FakeYahoo yahoo) =>
        new ApiFactory(Db).WithoutDevAuth().WithServices(services =>
            services.AddHttpClient<YahooFinanceQuoteProvider>().ConfigurePrimaryHttpMessageHandler(() => yahoo));

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string RepairPath(SyntheticLedger ledger) =>
        $"/api/ledgers/{ledger.LedgerId}/balances/consistency/{ConsistencyProjections.QuoteCurrency}/repair";

    private static LedgerConsistencyRepository Repository(Coffer.Api.Db.AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System);

    private async Task<ProjectionConsistency> ProjectionAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        var report = await Repository(db).CheckAsync(ledger.LedgerId);
        return report.Projections.Single(p => p.Projection == ConsistencyProjections.QuoteCurrency);
    }

    /// <summary>One row per (day, source, currency), priced 10, 11, 12… in seed order.</summary>
    private async Task SeedAsync(SyntheticLedger ledger, Guid securityId, params (DateOnly Day, string Source, string Currency)[] rows)
    {
        await using var db = Db.NewDbContext();
        var price = 10m;
        foreach (var (day, source, currency) in rows)
        {
            db.SecurityPrices.Add(new SecurityPriceRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                SecurityId = securityId,
                Price = price++,
                CurrencyCode = currency,
                PriceDate = day,
                Source = source,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>A security a Yahoo read has confirmed, as the quote refresh leaves it.</summary>
    private async Task ConfirmAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        var row = await db.Securities.SingleAsync(s => s.Id == securityId);
        row.QuoteCurrencyConfirmedAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<DateOnly, SecurityPriceRow>> RowsAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .ToDictionaryAsync(p => p.PriceDate);
    }
}
