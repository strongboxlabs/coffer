using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// A summary grouped by account on a ledger whose accounts are in two currencies
/// (ADR-0114 D7): each account's row is that account's own figure, in its own currency
/// and named on it; every figure that combines accounts (the total, a period's total,
/// the rest) is in the reporting currency, converted at each posting's date's rate, and
/// fails closed for a missing rate (ADR-0114 D9). The same answer reaches the app's
/// endpoint and the MCP tool. Grouped by category, the rows combine accounts and convert.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountGroupedReportCurrencyTests : DatabaseTest
{
    public AccountGroupedReportCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Spent = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Spent);

    private sealed record Seed(SyntheticLedger Ledger, AccountRow Home, AccountRow Abroad, AccountRow First, AccountRow Second);

    /// <summary>A USD ledger: Account A in USD spends 100 on Category A; Account B in EUR
    /// spends 40 on Category A and 20 on Category B, all on one day.</summary>
    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var home = await ledger.AddBankAccountAsync("Account A");
        var abroad = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var first = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");
        await ledger.AddTransactionPairAsync(first.Id, home.Id, 100m, Spent);
        await ledger.AddTransactionPairAsync(first.Id, abroad.Id, 40m, Spent);
        await ledger.AddTransactionPairAsync(second.Id, abroad.Id, 20m, Spent);
        return new Seed(ledger, home, abroad, first, second);
    }

    /// <summary>EUR to USD at 1.5 on the posting's day, as the rate fetcher stores it.</summary>
    private Task SeedRateAsync(SyntheticLedger ledger) =>
        BenchmarkSeed.SeedRatesAsync(Db, ledger, "EUR", "USD", Day, Day, Day.AddDays(20), _ => 1.5m);

    private Task<ReportSummaryDto> SummaryAsync(Guid ledgerId, ReportGroupBy groupBy, int? topN = null) =>
        new ReportingRepository(Db.NewDbContext(), TimeProvider.System).SummaryAsync(new ReportSpec
        {
            LedgerId = ledgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.Month,
            GroupBy = groupBy,
            FromUtc = From,
            ToUtc = To,
            TopN = topN,
        }, today: null);

    [Fact]
    public async Task By_account_each_row_stays_in_its_accounts_currency_and_the_total_converts()
    {
        var seed = await SeedAsync();
        await SeedRateAsync(seed.Ledger);

        var result = await SummaryAsync(seed.Ledger.LedgerId, ReportGroupBy.Account);

        Assert.Equal("USD", result.CurrencyCode);
        var home = Assert.Single(result.Rows, r => r.GroupId == seed.Home.Id);
        Assert.Equal((100m, "USD"), (home.Amount, home.CurrencyCode));
        // Account B's own euro figure, not its value in dollars.
        var abroad = Assert.Single(result.Rows, r => r.GroupId == seed.Abroad.Id);
        Assert.Equal((60m, "EUR"), (abroad.Amount, abroad.CurrencyCode));
        Assert.Null(abroad.RateUnavailable);

        var groupTotals = result.GroupTotals.ToDictionary(g => g.GroupId!.Value);
        Assert.Equal((100m, "USD"), (groupTotals[seed.Home.Id].Amount, groupTotals[seed.Home.Id].CurrencyCode));
        Assert.Equal((60m, "EUR"), (groupTotals[seed.Abroad.Id].Amount, groupTotals[seed.Abroad.Id].CurrencyCode));

        // The total and the month's total combine accounts: A's dollars plus B's euros at
        // the rate.
        Assert.Equal(190m, result.Total);
        Assert.Equal(190m, Assert.Single(result.PeriodTotals).Amount);
    }

    [Fact]
    public async Task By_account_a_missing_rate_withholds_only_the_figures_that_combine_accounts()
    {
        var seed = await SeedAsync();

        var result = await SummaryAsync(seed.Ledger.LedgerId, ReportGroupBy.Account);

        // Each account's own figure needs no rate.
        var abroad = Assert.Single(result.Rows, r => r.GroupId == seed.Abroad.Id);
        Assert.Equal((60m, "EUR"), (abroad.Amount, abroad.CurrencyCode));
        Assert.Null(abroad.RateUnavailable);
        Assert.Equal(60m, result.GroupTotals.Single(g => g.GroupId == seed.Abroad.Id).Amount);

        // The total is never the USD account's alone.
        var why = new RateUnavailable("EUR", "USD", Day);
        Assert.Null(result.Total);
        Assert.Equal([why], result.RateUnavailable);
        var month = Assert.Single(result.PeriodTotals);
        Assert.Null(month.Amount);
        Assert.Equal([why], month.RateUnavailable);
    }

    [Fact]
    public async Task By_account_with_a_cut_the_rest_combines_accounts_and_converts()
    {
        var seed = await SeedAsync();
        await SeedRateAsync(seed.Ledger);

        // A's dollars outrank B's converted figure, so A is kept; B goes to the rest.
        var result = await SummaryAsync(seed.Ledger.LedgerId, ReportGroupBy.Account, topN: 1);

        var kept = Assert.Single(result.Rows);
        Assert.Equal((seed.Home.Id, 100m, "USD"), (kept.GroupId!.Value, kept.Amount, kept.CurrencyCode));
        var rest = Assert.IsType<ReportRest>(result.Rest);
        Assert.Equal(90m, rest.Total);
        Assert.Equal(90m, Assert.Single(rest.ByPeriod!).Amount);
        Assert.Equal(seed.Abroad.Id, rest.Only!.GroupId);
    }

    [Fact]
    public async Task By_category_every_row_combines_accounts_and_converts()
    {
        var seed = await SeedAsync();
        await SeedRateAsync(seed.Ledger);

        var result = await SummaryAsync(seed.Ledger.LedgerId, ReportGroupBy.Category);

        var first = Assert.Single(result.Rows, r => r.GroupId == seed.First.Id);
        Assert.Equal((160m, "USD"), (first.Amount, first.CurrencyCode));
        var second = Assert.Single(result.Rows, r => r.GroupId == seed.Second.Id);
        Assert.Equal((30m, "USD"), (second.Amount, second.CurrencyCode));
        Assert.All(result.GroupTotals, g => Assert.Equal("USD", g.CurrencyCode));
        Assert.Equal(190m, result.Total);
    }

    [Fact]
    public async Task The_endpoint_and_the_mcp_tool_name_each_rows_currency()
    {
        var seed = await SeedAsync();
        await SeedRateAsync(seed.Ledger);

        await using var factory = new ApiFactory(Db);
        var cookie = await seed.Ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{seed.Ledger.LedgerId}/reports/summary?measure=Spending&groupBy=Account&bucket=Month"
            + "&from=2026-01-01T00:00:00Z&to=2026-02-01T00:00:00Z");
        Assert.Equal("USD", body.GetProperty("currencyCode").GetString());
        Assert.Equal(190m, body.GetProperty("total").GetDecimal());
        var row = body.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("groupId").GetGuid() == seed.Abroad.Id);
        Assert.Equal(60m, row.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", row.GetProperty("currencyCode").GetString());
        var groupTotal = body.GetProperty("groupTotals").EnumerateArray()
            .Single(r => r.GetProperty("groupId").GetGuid() == seed.Abroad.Id);
        Assert.Equal("EUR", groupTotal.GetProperty("currencyCode").GetString());

        var mcp = await ReportingTools.TransactionSummary(
            new ReportingRepository(Db.NewDbContext(), TimeProvider.System),
            seed.Ledger.LedgerId, groupBy: "account", fromUtc: From, toUtc: To);
        var mcpRow = Assert.Single(mcp.Rows, r => r.GroupId == seed.Abroad.Id);
        Assert.Equal((60m, "EUR"), (mcpRow.Amount, mcpRow.CurrencyCode));
        Assert.Equal(190m, mcp.Total);
    }
}
