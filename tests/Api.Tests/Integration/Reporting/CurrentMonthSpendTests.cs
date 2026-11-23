using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The current month's spending, read two ways (ADR-0109 D10): budget progress counts
/// the whole month, rows the register shows as Scheduled included, and says how much
/// of it is scheduled; the cash-flow report stops at the caller's today. The two differ
/// by exactly that scheduled amount.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CurrentMonthSpendTests : DatabaseTest
{
    public CurrentMonthSpendTests(PostgresFixture fixture, ITestOutputHelper output)
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

    // The caller's today, mid-month. Spending before it, later on it, and after it.
    private static readonly DateOnly Today = new(2026, 3, 12);

    private async Task<SyntheticLedger> SeedMarchAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var food = await ledger.AddCategoryAsync("Category A");
        var rent = await ledger.AddCategoryAsync("Category B");
        var salary = await ledger.AddCategoryAsync("Category C", "income");

        await ledger.AddTransactionPairAsync(bank.Id, food.Id, -80m, Utc(2026, 1, 20));
        await ledger.AddTransactionPairAsync(bank.Id, food.Id, -100m, Utc(2026, 3, 2));
        await ledger.AddTransactionPairAsync(bank.Id, food.Id, -40m, Utc(2026, 3, 12, 15));
        await ledger.AddTransactionPairAsync(bank.Id, food.Id, -70m, Utc(2026, 3, 13));
        await ledger.AddTransactionPairAsync(bank.Id, rent.Id, -1_450m, Utc(2026, 3, 28));
        await ledger.AddTransactionPairAsync(bank.Id, salary.Id, 2_000m, Utc(2026, 3, 1));
        return ledger;
    }

    private static async Task<BudgetProgressDto> BudgetAsync(HttpClient client, SyntheticLedger ledger, string month, DateOnly today)
    {
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month={month}&today={today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BudgetProgressDto>())!;
    }

    // The home screen's cash flow: income against expense, twelve whole months.
    private static async Task<ReportSummaryDto> CashFlowAsync(HttpClient client, SyntheticLedger ledger, DateOnly today)
    {
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/reports/summary?measure=Net&groupBy=CategoryKind&bucket=Month"
            + $"&dateMode=Sliding&windowCount=12&windowUnit=Month&windowAlign=true&today={today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReportSummaryDto>())!;
    }

    [Fact]
    public async Task Budget_spent_is_the_cash_flow_expense_plus_the_scheduled_rows()
    {
        var ledger = await SeedMarchAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var budget = await BudgetAsync(client, ledger, "2026-03", Today);
        var cashFlow = await CashFlowAsync(client, ledger, Today);

        // By hand: the whole month is all four expense rows; after today, the two dated later.
        Assert.Equal(1_660m, budget.ActualTotal);
        Assert.Equal(1_520m, budget.ScheduledTotal);
        Assert.Equal(12, budget.ElapsedDays);

        var marchExpense = Assert.Single(cashFlow.Rows, r => r.Period == "2026-03" && r.GroupName == "expense");
        Assert.Equal(budget.ActualTotal, -marchExpense.Amount + budget.ScheduledTotal);
    }

    [Fact]
    public async Task A_past_month_has_nothing_scheduled_and_a_future_month_is_all_scheduled()
    {
        var ledger = await SeedMarchAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var past = await BudgetAsync(client, ledger, "2026-03", new DateOnly(2026, 4, 2));
        Assert.Equal(0m, past.ScheduledTotal);
        Assert.Equal(31, past.ElapsedDays);

        var future = await BudgetAsync(client, ledger, "2026-03", new DateOnly(2026, 2, 27));
        Assert.Equal(future.ActualTotal, future.ScheduledTotal);
        Assert.Equal(0, future.ElapsedDays);

        // The month's last day: nothing left to come, the whole month elapsed.
        var lastDay = await BudgetAsync(client, ledger, "2026-03", new DateOnly(2026, 3, 31));
        Assert.Equal(0m, lastDay.ScheduledTotal);
        Assert.Equal(31, lastDay.ElapsedDays);
    }

    [Fact]
    public async Task Cash_flow_totals_every_month_of_its_window_and_every_group()
    {
        var ledger = await SeedMarchAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var cashFlow = await CashFlowAsync(client, ledger, Today);

        // Twelve whole months, April through March, zero where nothing posted.
        Assert.Equal(new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc), cashFlow.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc), cashFlow.ToUtc);
        Assert.Equal(12, cashFlow.PeriodTotals.Count);
        Assert.Equal("2025-04", cashFlow.PeriodTotals[0].Period);
        Assert.Equal("2026-03", cashFlow.PeriodTotals[^1].Period);
        Assert.Equal(0m, cashFlow.PeriodTotals.Single(p => p.Period == "2025-12").Amount);
        // Net: income up, spending down. March counts the salary and the spending to
        // date; January the one purchase.
        Assert.Equal(2_000m - 140m, cashFlow.PeriodTotals.Single(p => p.Period == "2026-03").Amount);
        Assert.Equal(-80m, cashFlow.PeriodTotals.Single(p => p.Period == "2026-01").Amount);
        Assert.Equal(cashFlow.Total, cashFlow.PeriodTotals.Sum(p => p.Amount));

        Assert.Equal(-220m, cashFlow.GroupTotals.Single(g => g.GroupName == "expense").Amount);
        Assert.Equal(2_000m, cashFlow.GroupTotals.Single(g => g.GroupName == "income").Amount);

        Assert.Equal("USD", cashFlow.CurrencyCode);
        Assert.Null(cashFlow.RateUnavailable);
    }

    [Fact]
    public async Task Top_n_cuts_the_rows_but_not_the_totals()
    {
        var ledger = await SeedMarchAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/reports/summary?measure=Spending&groupBy=Category&bucket=Month"
            + "&from=2026-01-01T00:00:00Z&to=2026-04-01T00:00:00Z&topN=1");
        var summary = (await response.Content.ReadFromJsonAsync<ReportSummaryDto>())!;

        Assert.All(summary.Rows, r => Assert.Equal("Category B", r.GroupName));
        Assert.Equal(1_740m, summary.Total);
        Assert.Equal(1_740m, summary.PeriodTotals.Sum(p => p.Amount));
        Assert.Equal(2, summary.GroupTotals.Count);
        Assert.Equal(290m, summary.GroupTotals.Single(g => g.GroupName == "Category A").Amount);
    }
}
