using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// The home screen's figures agree with each other: the overview's net worth then
/// (<c>?since=</c>) is the net-worth history's point for that month-end, the history's
/// last point is the overview's net worth for the caller's today, and the returns end
/// value is the overview's investments value for the same today.
/// </summary>
/// <remarks>
/// Each comparison reads both sides through the API, as the screen does, and pins one
/// side to a figure worked out by hand, so the two cannot agree by being wrong the same
/// way.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OverviewSinceAndHistoryTests : DatabaseTest
{
    public OverviewSinceAndHistoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d, int hour = 0) => new(y, m, d, hour, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<LedgerOverviewDto> OverviewAsync(HttpClient client, SyntheticLedger ledger, string query)
    {
        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LedgerOverviewDto>())!;
    }

    private static async Task<NetWorthHistory> HistoryAsync(
        HttpClient client, SyntheticLedger ledger, DateTime from, DateTime to, DateOnly? today)
    {
        var query = $"from={from:yyyy-MM-ddTHH:mm:ssZ}&to={to:yyyy-MM-ddTHH:mm:ssZ}&interval=Month"
                    + (today is { } d ? $"&today={d:yyyy-MM-dd}" : string.Empty);
        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/reports/net-worth-history?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NetWorthHistory>())!;
    }

    private sealed record Seed(SyntheticLedger Ledger, Guid BankId, Guid BrokerageId);

    // A bank account funds a brokerage in January; spending and a buy land before
    // the end of February, and more of each after it, so net worth at the end of
    // February is neither today's nor zero.
    private async Task<Seed> SeedWinterAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -100m, Utc(2026, 1, 10));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2026, 1, 15));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -200m, Utc(2026, 2, 5));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -300m, Utc(2026, 3, 3));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -50m, Utc(2026, 3, 20));

        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 100m, Utc(2026, 2, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 5m, 120m, Utc(2026, 3, 5));
        await ledger.RecomputeHoldingsAsync();

        await ledger.AddSecurityPriceAsync(security, 110m, Utc(2026, 2, 20));
        await ledger.AddSecurityPriceAsync(security, 130m, Utc(2026, 3, 10));

        return new Seed(ledger, bank.Id, brokerage.Id);
    }

    [Fact]
    public async Task Net_worth_then_is_the_history_point_at_the_month_end()
    {
        var seed = await SeedWinterAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var overview = await OverviewAsync(client, seed.Ledger, "today=2026-03-25&since=2026-02-28");
        var comparison = Assert.IsType<OverviewComparisonDto>(overview.Comparison);
        Assert.Equal(new DateOnly(2026, 2, 28), comparison.Since);
        Assert.False(comparison.NoHistory);

        // By hand: the bank's opening balance less its two payments and the
        // transfer; the brokerage's cash less the buy; the shares at February's price.
        Assert.Equal(4_700m + 4_000m + 1_100m, comparison.NetWorth.Then);
        Assert.Equal(overview.NetWorth - comparison.NetWorth.Then, comparison.NetWorth.Change);
        // The rows after the month-end moved it, so the two are not the same reading.
        Assert.NotEqual(0m, comparison.NetWorth.Change);

        var history = await HistoryAsync(
            client, seed.Ledger, Utc(2026, 1, 1), Utc(2026, 3, 31, 23), new DateOnly(2026, 3, 25));
        var february = Assert.Single(history.Points, p => DateOnly.FromDateTime(p.AsOf) == comparison.Since);
        Assert.Equal(comparison.NetWorth.Then, february.NetWorth);
    }

    [Fact]
    public async Task Before_the_first_dated_fact_there_is_no_history_rather_than_a_zero()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -100m, Utc(2026, 3, 5));

        // A ledger with nothing dated at all has no history either.
        var empty = await SyntheticLedger.CreateAsync(Db);

        // An account's opening date is a fact too, even with no transaction yet.
        var opened = await SyntheticLedger.CreateAsync(Db);
        await opened.AddInvestmentAccountAsync(
            "Brokerage A", withHoldingsSibling: false, openingBalance: 500m, openedOn: new DateOnly(2026, 2, 10));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();

        using (var client = await AuthedClientAsync(factory, ledger))
        {
            var before = (await OverviewAsync(client, ledger, "today=2026-03-25&since=2026-02-28")).Comparison!;
            Assert.True(before.NoHistory);
            Assert.Null(before.NetWorth.Then);
            Assert.Null(before.NetWorth.Change);

            // The anchor: the same ledger compared with a month-end after its first
            // fact has a history.
            var after = (await OverviewAsync(client, ledger, "today=2026-04-25&since=2026-03-31")).Comparison!;
            Assert.False(after.NoHistory);
            Assert.Equal(900m, after.NetWorth.Then);

            // The history flags the same months.
            var history = await HistoryAsync(client, ledger, Utc(2026, 1, 1), Utc(2026, 3, 31, 23), new DateOnly(2026, 3, 25));
            Assert.Equal([true, true, false], history.Points.Select(p => p.NoHistory).ToArray());
        }

        using (var client = await AuthedClientAsync(factory, empty))
        {
            Assert.True((await OverviewAsync(client, empty, "today=2026-03-25&since=2026-02-28")).Comparison!.NoHistory);
        }

        using (var client = await AuthedClientAsync(factory, opened))
        {
            var comparison = (await OverviewAsync(client, opened, "today=2026-03-25&since=2026-02-28")).Comparison!;
            Assert.False(comparison.NoHistory);
            Assert.NotNull(comparison.NetWorth.Then);
        }
    }

    [Fact]
    public async Task Since_must_be_a_month_end_before_today_and_comes_with_today()
    {
        var seed = await SeedWinterAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // The anchor: a valid pair is answered.
        Assert.NotNull((await OverviewAsync(client, seed.Ledger, "today=2026-03-25&since=2026-02-28")).Comparison);
        // Without since there is no comparison at all.
        Assert.Null((await OverviewAsync(client, seed.Ledger, "today=2026-03-25")).Comparison);

        foreach (var query in new[]
                 {
                     "today=2026-03-25&since=2026-02-27",   // not a month-end
                     "today=2026-02-28&since=2026-02-28",   // not before today
                     "today=2026-03-25&since=2026-03-31",   // after today
                     "since=2026-02-28",                    // no today
                 })
        {
            var response = await client.GetAsync($"/api/ledgers/{seed.Ledger.LedgerId}/overview?{query}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("overview-since-invalid", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task The_portfolio_carries_its_cash_and_currency_and_accounts_carry_whether_they_are_open()
    {
        var seed = await SeedWinterAsync();
        var ledger = seed.Ledger;
        // A second brokerage in another currency, holding cash only.
        var euro = await ledger.AddInvestmentAccountAsync("Brokerage B", withHoldingsSibling: false);
        var bank = await ledger.AddBankAccountAsync("Account B", openingBalance: 250m);
        await ledger.AddTransactionPairAsync(euro.Id, bank.Id, 200m, Utc(2026, 2, 2));
        await ledger.SetIsActiveAsync(bank.Id, false);
        await using (var db = Db.NewDbContext())
        {
            await db.Accounts.Where(a => a.Id == euro.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "EUR"));
        }
        // The EUR brokerage's cash converts into the ledger's USD at the latest rate
        // (ADR-0114 D7), seeded as the rate fetcher writes it.
        var serverToday = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var writer = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            writer.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = serverToday.AddDays(-2), Rate = 1.5m, Source = FxRateSource.Frankfurter,
            });
            writer.FxPairCoverage.Add(new FxPairCoverageRow
            {
                LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = serverToday,
            });
            await writer.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var overview = await OverviewAsync(client, ledger, "today=2026-03-25");

        Assert.Equal(overview.InvestmentsValue - overview.Portfolio.Value, overview.Portfolio.CashBalance);
        // By hand: the first brokerage's cash less the March buy, and the cash
        // transferred to the second, converted.
        Assert.Equal(3_400m + 200m * 1.5m, overview.Portfolio.CashBalance);
        Assert.Equal("USD", overview.Portfolio.CurrencyCode);
        Assert.Equal("USD", overview.CurrencyCode);

        var accounts = overview.AccountGroups.SelectMany(g => g.Accounts).ToDictionary(a => a.Id);
        Assert.False(accounts[bank.Id].IsActive);
        Assert.True(accounts[seed.BankId].IsActive);
        // A closed account still counts.
        Assert.Equal(50m, accounts[bank.Id].Balance);
    }

    // ---- the history's last point, and returns, against the overview's now --------

    private static readonly DateTime T = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    // Two callers: one whose date is a day behind the server's UTC date, one a day
    // ahead. Around each caller's today sit a row posted later that day (counted) and
    // a row and a price dated the day after (not counted), so a last point cut at the
    // server's clock, or at the caller's midnight, misses one of them.
    private async Task<Seed> SeedAroundTodayAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, T.AddDays(-40));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -100m, T.AddDays(-10));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 100m, T.AddDays(-30));

        foreach (var today in new[] { T.AddDays(-1), T.AddDays(1) })
        {
            await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -40m, today.AddHours(15));
            await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -70m, today.AddDays(1));
            await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 1m, 100m, today.AddDays(1));
        }
        await ledger.RecomputeHoldingsAsync();

        await ledger.AddSecurityPriceAsync(security, 110m, T.AddDays(-20));
        await ledger.AddSecurityPriceAsync(security, 150m, T);
        await ledger.AddSecurityPriceAsync(security, 190m, T.AddDays(2));

        return new Seed(ledger, bank.Id, brokerage.Id);
    }

    [Fact]
    public async Task The_history_ends_at_the_overview_net_worth_for_the_callers_today()
    {
        var seed = await SeedAroundTodayAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var netWorths = new List<decimal?>();
        foreach (var today in new[] { DateOnly.FromDateTime(T.AddDays(-1)), DateOnly.FromDateTime(T.AddDays(1)) })
        {
            var overview = await OverviewAsync(client, seed.Ledger, $"today={today:yyyy-MM-dd}");
            var history = await HistoryAsync(client, seed.Ledger, T.AddDays(-60), T.AddDays(40), today);

            var last = history.Points[^1];
            Assert.Equal(ScheduledBoundary.LastCountedInstant(today, TimeProvider.System), last.AsOf);
            Assert.Equal(overview.NetWorth, last.NetWorth);
            netWorths.Add(overview.NetWorth);
        }

        // The two callers see different nows, so the agreement above is about each.
        Assert.NotEqual(netWorths[0], netWorths[1]);
    }

    [Fact]
    public async Task Returns_end_at_the_overview_investments_value_for_the_same_today()
    {
        var seed = await SeedAroundTodayAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var values = new List<decimal?>();
        foreach (var today in new[] { DateOnly.FromDateTime(T.AddDays(-1)), DateOnly.FromDateTime(T.AddDays(1)) })
        {
            var overview = await OverviewAsync(client, seed.Ledger, $"today={today:yyyy-MM-dd}");
            var returns = (await client.GetFromJsonAsync<ReturnsResult>(
                $"/api/ledgers/{seed.Ledger.LedgerId}/reports/returns?today={today:yyyy-MM-dd}"))!;
            var allocation = (await client.GetFromJsonAsync<AllocationResult>(
                $"/api/ledgers/{seed.Ledger.LedgerId}/reports/allocation?today={today:yyyy-MM-dd}"))!;

            Assert.Equal("ledger", returns.Scope);
            Assert.Equal(overview.InvestmentsValue, returns.EndValue);
            // Allocation values the same instant: its securities and the cash beside them.
            Assert.Equal(returns.EndValue, allocation.TotalMarketValue + allocation.ExcludedBrokerageCash);
            values.Add(overview.InvestmentsValue);
        }

        Assert.NotEqual(values[0], values[1]);
    }
}
