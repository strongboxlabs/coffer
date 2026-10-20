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
        var investment = new InvestmentReportingRepository(db, TimeProvider.System);
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

        // 2024-01-10: income into checking (the income category leg is excluded
        // from net worth, so only checking's side counts).
        await ledger.AddTransactionPairAsync(checking.Id, income.Id, 2000m, Utc(2024, 1, 10));
        // 2024-01-20: a buy from an unfunded brokerage → its cash goes negative by
        // quantity times unitPrice, and the holdings carry the shares at that trade
        // price.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, quantity: 100m, unitPrice: 10m, postedAt: Utc(2024, 1, 20));
        // 2024-02-01: a feed close above the trade price revalues the position.
        await ledger.AddSecurityPriceAsync(sec, 15m, Utc(2024, 2, 1));

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        var history = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, Utc(2024, 1, 1), Utc(2024, 2, 15), ReportTimeBucket.Month);

        Assert.Equal("month", history.Interval);
        Assert.Equal(2, history.Points.Count);

        // End of January: checking's income plus the brokerage, whose negative cash
        // and trade-priced holdings cancel exactly, so net worth is the income alone.
        // No feed price yet → priced from the trade, so nothing is unpriced.
        Assert.Equal(2000m, history.Points[0].NetWorth);
        Assert.Equal(0, history.Points[0].UnpricedSecurityCount);

        // Clamped to 2024-02-15: the feed close (2024-02-01) revalues the position,
        // so net worth is the income plus the position's gain over its cost
        // (quantity times the rise from the trade price to the close).
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

        // 2024-01-10: income into checking, and a larger amount into the account that
        // later closes.
        await ledger.AddTransactionPairAsync(checking.Id, income.Id, 2000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(rolledOver.Id, income.Id, 50000m, Utc(2024, 1, 10));

        // Closed NOW (after the reporting window) — must NOT erase the history.
        await ledger.SetIsActiveAsync(rolledOver.Id, isActive: false);

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        var history = await repository.NetWorthHistoryAsync(
            ledger.LedgerId, Utc(2024, 1, 1), Utc(2024, 1, 31), ReportTimeBucket.Month);

        // End of January: both accounts' balances together. Pre-ADR-0085 the closed
        // account was dropped and this understated to checking's balance alone.
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
        await Assert.ThrowsAsync<ReportRequestException>(() =>
            repository.NetWorthHistoryAsync(
                ledger.LedgerId, Utc(1990, 1, 1), Utc(2050, 1, 1), ReportTimeBucket.Month));
    }

    [Fact]
    public async Task NetWorthHistory_refuses_a_daily_interval_rather_than_answering_in_months()
    {
        // `NextPeriodStart` has no Day arm, so Day fell through to the month
        // default and a daily request came back as MONTHLY points presented as
        // a daily series. Nothing in the response said so, and it was already
        // reachable: the `net_worth_history` MCP tool documents
        // month/quarter/year while its parser accepts the whole enum.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewDbContext();
        var repository = NewRepository(db);

        var ex = await Assert.ThrowsAsync<ReportRequestException>(() =>
            repository.NetWorthHistoryAsync(
                ledger.LedgerId, Utc(2026, 1, 1), Utc(2026, 3, 1), ReportTimeBucket.Day));
        Assert.Contains("month, quarter or year", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetWorthHistory_still_treats_an_unspecified_interval_as_monthly()
    {
        // The guard must not catch None, which is the documented "unspecified"
        // default and the value every caller that omits the argument sends.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddBankAccountAsync("Checking", openingBalance: 500m);

        await using var db = _fixture.NewDbContext();
        var history = await NewRepository(db).NetWorthHistoryAsync(
            ledger.LedgerId, Utc(2026, 1, 1), Utc(2026, 4, 1), ReportTimeBucket.None);

        Assert.Equal("month", history.Interval, ignoreCase: true);
        Assert.NotEmpty(history.Points);
    }
}
