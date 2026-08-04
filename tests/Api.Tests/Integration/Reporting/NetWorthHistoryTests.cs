using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>net_worth_history</c> (ADR-0063 §D5 v2, Track-2 historical valuations): the
/// repository assembles a net-worth-over-time series from the migration-172 as-of
/// feeder — cash balance as of each period end + split-adjusted holdings market
/// value — using the same Overview-consistent classification as <c>net_worth</c>
/// (investment accounts fold in holdings value; holdings-sibling shadow accounts
/// are never double-counted). Covers the cash+holdings fold across a feed
/// revaluation and the point-cap guard.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NetWorthHistoryTests
{
    private readonly PostgresFixture _fixture;

    public NetWorthHistoryTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // NetWorthHistoryAsync reads only through the db context (the mig-172 feeder
    // functions + the account catalog); the other repositories are ctor deps it
    // does not exercise, so real instances over the same context suffice.
    private static AccountsReportingRepository NewRepository(AppDbContext db)
    {
        var balances = new AccountBalancesRepository(db);
        var investment = new InvestmentReportingRepository(db);
        var overview = new OverviewRepository(db, balances, investment);
        return new AccountsReportingRepository(db, balances, investment, overview);
    }

    [Fact]
    public async Task NetWorthHistory_folds_cash_and_split_adjusted_holdings_per_period()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var income = await ledger.AddCategoryAsync("Income", kind: "income");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // 2024-01-10: +$2,000 into checking (the income category leg is excluded
        // from net worth, so only checking's +$2,000 counts).
        await ledger.AddTransactionPairAsync(checking.Id, income.Id, 2000m, Utc(2024, 1, 10));
        // 2024-01-20: buy 100 @ $10 → brokerage cash -$1,000, holdings 100 sh at
        // the trade price $10.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, quantity: 100m, unitPrice: 10m, postedAt: Utc(2024, 1, 20));
        // 2024-02-01: a $15 feed close revalues the position.
        await ledger.AddSecurityPriceAsync(sec, 15m, Utc(2024, 2, 1));

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        var history = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, Utc(2024, 1, 1), Utc(2024, 2, 15), ReportTimeBucket.Month);

        Assert.Equal("month", history.Interval);
        Assert.Equal(2, history.Points.Count);

        // End of January: checking $2,000 + brokerage (cash -$1,000 + holdings
        // 100 × $10 trade = $1,000) = $2,000. No feed price yet → priced from the
        // trade, so nothing is unpriced.
        Assert.Equal(2000m, history.Points[0].NetWorth);
        Assert.Equal(0, history.Points[0].UnpricedSecurityCount);

        // Clamped to 2024-02-15: the $15 feed close (2024-02-01) revalues the
        // position to $1,500 → $2,000 + (-$1,000 + $1,500) = $2,500.
        Assert.Equal(2500m, history.Points[1].NetWorth);
        Assert.Equal(0, history.Points[1].UnpricedSecurityCount);
    }

    [Fact]
    public async Task NetWorthHistory_includes_a_since_closed_account_for_periods_it_was_open()
    {
        // ADR-0085: a historical point values every account that was open THEN,
        // including one since closed (e.g. a 401k rolled over mid-window).
        // Filtering current is_active would drop it and understate history.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var income = await ledger.AddCategoryAsync("Income", kind: "income");
        var rolledOver = await ledger.AddBankAccountAsync("Old 401k (later closed)");

        // 2024-01-10: +$2,000 checking, +$50,000 into the account that later closes.
        await ledger.AddTransactionPairAsync(checking.Id, income.Id, 2000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(rolledOver.Id, income.Id, 50000m, Utc(2024, 1, 10));

        // Closed NOW (after the reporting window) — must NOT erase the history.
        await ledger.SetIsActiveAsync(rolledOver.Id, isActive: false);

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        var history = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, Utc(2024, 1, 1), Utc(2024, 1, 31), ReportTimeBucket.Month);

        // End of January: $2,000 + $50,000 = $52,000. Pre-ADR-0085 the closed
        // account was dropped and this understated to $2,000.
        var point = Assert.Single(history.Points);
        Assert.Equal(52000m, point.NetWorth);
    }

    [Fact]
    public async Task NetWorthHistory_rejects_a_range_that_would_exceed_the_point_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        // Monthly over six decades (~720 points) blows past the 600-point cap that
        // guards against an unbounded per-point feeder fan-out.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.NetWorthHistoryAsync(
                ledger.LedgerId, Utc(1990, 1, 1), Utc(2050, 1, 1), ReportTimeBucket.Month));
    }
}
