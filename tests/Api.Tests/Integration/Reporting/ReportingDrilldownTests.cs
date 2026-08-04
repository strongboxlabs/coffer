using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// MCP v2 account/category drill-down (ADR-0063 §D5): the generalized summary
/// (group-by account, category-tree rollup), the transaction line-drill,
/// investment income, and the account catalog + net worth (MV-aware balances).
/// These also guard against LINQ-translation regressions in the new queries.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReportingDrilldownTests
{
    private readonly PostgresFixture _fixture;

    public ReportingDrilldownTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Spending_by_account_attributes_to_the_paying_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, May);
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 50m, May);

        var result = await new ReportingRepository(_fixture.NewDbContext()).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Account,
        });

        // Both expense postings paid from Checking → one account row, total 150.
        var row = Assert.Single(result.Rows);
        Assert.Equal(bank.Id, row.GroupId);
        Assert.Equal(150m, row.Amount);
    }

    [Fact]
    public async Task Category_rollup_folds_children_into_the_parent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food", "expense");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense", parentId: food.Id);
        var dining = await ledger.AddCategoryAsync("Dining", "expense", parentId: food.Id);
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, May);
        await ledger.AddTransactionPairAsync(dining.Id, bank.Id, 40m, May);

        var rolled = await new ReportingRepository(_fixture.NewDbContext()).SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            Measure = ReportMeasure.Spending,
            TimeBucket = ReportTimeBucket.None,
            GroupBy = ReportGroupBy.Category,
            Rollup = true,
        });

        // Parent Food = 140 (100 + 40); children carry their own; total of
        // underlying postings stays 140 (parent rows are subtotals).
        Assert.Equal(140m, rolled.Rows.Single(r => r.GroupId == food.Id).Amount);
        Assert.Equal(100m, rolled.Rows.Single(r => r.GroupId == groceries.Id).Amount);
        Assert.Equal(140m, rolled.Total);
        Assert.Equal(food.Id, rolled.Rows.Single(r => r.GroupId == groceries.Id).ParentId);
    }

    [Fact]
    public async Task List_transactions_filters_by_account_and_direction()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var salary = await ledger.AddCategoryAsync("Salary", "income");
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, May);   // bank −100
        await ledger.AddTransactionPairAsync(bank.Id, salary.Id, 5000m, May);     // bank +5000

        var repo = new ReportingRepository(_fixture.NewDbContext());

        var inflow = await repo.ListTransactionsAsync(new TransactionQuery
        {
            LedgerId = ledger.LedgerId,
            AccountId = bank.Id,
            Direction = TransactionDirection.Inflow,
        });
        var line = Assert.Single(inflow.Lines);
        Assert.Equal(5000m, line.Amount);
        Assert.False(inflow.HasMore);

        var outflow = await repo.ListTransactionsAsync(new TransactionQuery
        {
            LedgerId = ledger.LedgerId,
            AccountId = bank.Id,
            Direction = TransactionDirection.Outflow,
        });
        Assert.Equal(-100m, Assert.Single(outflow.Lines).Amount);
    }

    [Fact]
    public async Task List_accounts_and_net_worth_use_market_value_aware_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund", "FND");
        // 10 shares cost 1000, latest price 150 → market value 1500.
        await ledger.AddHoldingAsync(holdings, security, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(security, 150m, May);

        var db = _fixture.NewDbContext();
        var balances = new AccountBalancesRepository(db);
        var invest = new InvestmentReportingRepository(db);
        var overview = new OverviewRepository(db, balances, invest);
        var repo = new AccountsReportingRepository(db, balances, invest, overview);

        var accounts = await repo.ListAccountsAsync(
            ledger.LedgerId, includeCategories: false, includeInactive: false, type: null);
        var brokRow = accounts.Single(a => a.Id == brokerage.Id);
        Assert.Equal("asset", brokRow.Class);
        Assert.Equal(1500m, brokRow.Balance);   // cash 0 + holdings MV 1500

        var nw = await repo.NetWorthAsync(ledger.LedgerId);
        Assert.Equal(1500m, nw.TotalAssets);
        Assert.Equal(1500m, nw.NetWorth);
    }

    [Fact]
    public async Task Investment_income_sums_dividends_per_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Fund", "FND");
        var divCategory = await ledger.AddCategoryAsync("Dividends", "income");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "dividend_cash",
                SecurityId = security,
                Amount = 75m,
                CategoryAccountId = divCategory.Id,
                PostedAt = May,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var income = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .IncomeAsync(ledger.LedgerId, null, null, null, null,
                InvestmentIncomeGroupBy.Security, ReportTimeBucket.None);

        var row = Assert.Single(income.Rows);
        Assert.Equal(security, row.GroupId);
        Assert.Equal(75m, row.Amount);
        Assert.Equal(75m, income.Total);
    }
}
