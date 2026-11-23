using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The one rule for which rate a dated figure uses (<see cref="RateDay.Of(DateOnly, DateOnly)"/>,
/// ADR-0114 D7, D9), on the surfaces that convert a figure dated today or later: a day before
/// the server's UTC today converts at its own rate; today, or a scheduled day, converts at the
/// latest rate, which is readable while the pair is covered through UTC yesterday. So none of
/// these figures goes null only because today's rate is not published yet.
/// </summary>
/// <remarks>
/// The server's clock is the system clock, as the API runs on it, so every date is relative to
/// its UTC today. Spending is written through <c>POST /transactions</c>, the trades and prices
/// through the seeds the investment suites use, and the rate as the ledger's owner, the role
/// the rate fetcher writes with. The only EUR rate is yesterday's, and coverage ends there: the
/// state a ledger is in between midnight and the day's refresh.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TodayDatedConversionTests : DatabaseTest
{
    public TodayDatedConversionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const decimal EurYesterday = 1.5m;

    private static DateOnly ServerToday => DateOnly.FromDateTime(DateTime.UtcNow);

    private static DateTime Midnight(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>EUR to USD yesterday only, fetched through yesterday; an older rate too, so a
    /// figure that read the wrong one would show.</summary>
    private async Task SeedYesterdayOnlyAsync(SyntheticLedger ledger, DateOnly today)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        foreach (var (day, rate) in new[] { (today.AddDays(-30), 1.1m), (today.AddDays(-1), EurYesterday) })
        {
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = day, Rate = rate, Source = FxRateSource.Frankfurter, Upstream = "Source A",
            });
        }
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = today.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return client;
    }

    private sealed record Spending(
        SyntheticLedger Ledger, AccountRow Euros, AccountRow Category, DateOnly Today, DateOnly Scheduled,
        Guid TodayRow, Guid ScheduledRow);

    /// <summary>A USD ledger whose EUR account spends today and again in three days, a
    /// scheduled row, on one category.</summary>
    private async Task<Spending> SeedSpendingAsync()
    {
        var today = ServerToday;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("Account A");
        var euros = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        await SeedYesterdayOnlyAsync(ledger, today);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        async Task<Guid> SpendAsync(DateOnly day, decimal amount)
        {
            using var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Midnight(day),
                    SourceAccountId = euros.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -amount }],
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        }
        var scheduled = today.AddDays(3);
        var todayRow = await SpendAsync(today, 10m);
        var scheduledRow = await SpendAsync(scheduled, 20m);
        return new Spending(ledger, euros, category, today, scheduled, todayRow, scheduledRow);
    }

    /// <summary>
    /// Budget progress, and the summary report it reads, convert today's spending and a
    /// scheduled row at the latest rate: the month's actual and its scheduled part both have a
    /// figure.
    /// </summary>
    [Fact]
    public async Task Budget_progress_converts_todays_and_scheduled_spending_at_the_latest_rate()
    {
        var s = await SeedSpendingAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, s.Ledger);

        async Task<BudgetProgressDto> ProgressAsync(DateOnly month) =>
            (await client.GetFromJsonAsync<BudgetProgressDto>(
                $"/api/ledgers/{s.Ledger.LedgerId}/budget/progress?month={month:yyyy-MM}&window=3&today={s.Today:yyyy-MM-dd}"))!;

        var sameMonth = s.Scheduled.Month == s.Today.Month;
        var current = await ProgressAsync(s.Today);
        var row = Assert.Single(current.Rows, r => r.CategoryId == s.Category.Id);
        Assert.Null(row.RateUnavailable);
        Assert.Null(current.RateUnavailable);
        Assert.Equal(10m * EurYesterday + (sameMonth ? 20m * EurYesterday : 0m), row.Actual);
        Assert.Equal(sameMonth ? 20m * EurYesterday : 0m, current.ScheduledTotal);

        if (!sameMonth)
        {
            var next = await ProgressAsync(s.Scheduled);
            var scheduledRow = Assert.Single(next.Rows, r => r.CategoryId == s.Category.Id);
            Assert.Equal(20m * EurYesterday, scheduledRow.Actual);
            Assert.Equal(20m * EurYesterday, next.ScheduledTotal);
            Assert.Null(next.RateUnavailable);
        }
    }

    /// <summary>
    /// A category's register converts its running Balance at each row's date: today's row and
    /// the scheduled one at the latest rate, so neither balance is null.
    /// </summary>
    [Fact]
    public async Task The_category_register_converts_todays_and_scheduled_rows_at_the_latest_rate()
    {
        var s = await SeedSpendingAsync();

        await using var db = Db.NewRequestDbContextAsUser(s.Ledger.UserId);
        var page = await new RegisterRepository(db, TimeProvider.System).GetPageAsync(
            s.Ledger.LedgerId, s.Category.Id, null, RegisterRepository.DirectionBefore, null, 50, hidden: false);
        var rows = page.Entries.SelectMany(e => e.Txn is { } t ? [t] : e.Legs!).ToDictionary(r => r.HeaderId);

        Assert.Equal(10m * EurYesterday, rows[s.TodayRow].BalanceAfter);
        Assert.Null(rows[s.TodayRow].BalanceRateUnavailable);
        Assert.Equal(30m * EurYesterday, rows[s.ScheduledRow].BalanceAfter);
        Assert.Null(rows[s.ScheduledRow].BalanceRateUnavailable);
    }

    /// <summary>
    /// Net-worth history's last point is the caller's today, a current value: it converts at
    /// the latest rate, and agrees with the overview's net worth for the same today.
    /// </summary>
    [Fact]
    public async Task The_net_worth_history_last_point_converts_at_the_latest_rate()
    {
        var s = await SeedSpendingAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, s.Ledger);

        var from = Midnight(s.Today.AddDays(-60));
        var to = Midnight(s.Today.AddDays(40));
        var history = (await client.GetFromJsonAsync<NetWorthHistory>(
            $"/api/ledgers/{s.Ledger.LedgerId}/reports/net-worth-history?from={from:yyyy-MM-ddTHH:mm:ssZ}"
            + $"&to={to:yyyy-MM-ddTHH:mm:ssZ}&interval=Month&today={s.Today:yyyy-MM-dd}"))!;
        var last = history.Points[^1];
        Assert.Equal(s.Today, DateOnly.FromDateTime(last.AsOf));
        // The scheduled row is not counted yet: only today's spend.
        Assert.Equal(-10m * EurYesterday, last.NetWorth);
        Assert.Empty(last.RateUnavailable);

        var overview = (await client.GetFromJsonAsync<LedgerOverviewDto>(
            $"/api/ledgers/{s.Ledger.LedgerId}/overview?today={s.Today:yyyy-MM-dd}"))!;
        Assert.Equal(overview.NetWorth, last.NetWorth);
    }

    /// <summary>
    /// A lot bought today, in a brokerage in another currency than the figure's, has its cost
    /// converted at its acquisition day by <c>LotCosts</c>: today, so at the latest rate. The
    /// ledger's snapshot and the overview's portfolio both have a cost basis.
    /// </summary>
    [Fact]
    public async Task A_lot_acquired_today_converts_its_cost_at_the_latest_rate()
    {
        var today = ServerToday;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var fund = await ledger.AddSecurityAsync("Fund One", "FNDA");
        // The position's projection row, as the trade writer keeps one (the overview reads it).
        await ledger.AddHoldingAsync(eur.HoldingsAccountId!.Value, fund, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(eur.Id, eur.HoldingsAccountId!.Value, fund, 10m, 100m, Midnight(today));
        await ledger.AddSecurityPriceAsync(fund, 120m, Midnight(today), currencyCode: "EUR");
        await ledger.RecomputeHoldingsAsync();
        await SeedYesterdayOnlyAsync(ledger, today);

        await using (var db = Db.NewDbContext())
        {
            var snap = await new InvestmentReportingRepository(db, TimeProvider.System)
                .HoldingsSnapshotAsync(ledger.LedgerId);
            Assert.Empty(snap.RateUnavailable);
            var row = Assert.Single(snap.Holdings);
            Assert.Equal(1000m * EurYesterday, row.CostBasis);
            Assert.Equal(1200m * EurYesterday, row.MarketValue);
            Assert.Equal(1000m * EurYesterday, snap.TotalCostBasis);
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var overview = (await client.GetFromJsonAsync<LedgerOverviewDto>(
            $"/api/ledgers/{ledger.LedgerId}/overview?today={today:yyyy-MM-dd}"))!;
        Assert.Equal(1000m * EurYesterday, overview.Portfolio.CostBasis);
        Assert.Equal(1200m * EurYesterday, overview.Portfolio.Value);
        Assert.Empty(overview.Portfolio.RateUnavailable);
    }
}
