using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The overview and the net-worth-history report compute net worth by DIFFERENT
/// routes, and nothing asserted that they agree.
/// </summary>
/// <remarks>
/// The overview reads current balances and values holdings at the latest price;
/// net-worth-history values every point through the as-of feeder. Both are correct
/// on their own terms and each has tests pinning its own numbers — which is exactly
/// why a divergence between them was invisible. A mutation sweep cannot surface this
/// either: break one side and that side's own tests fail, so the missing agreement
/// never shows up. It has to be written.
/// <para>
/// This is the same shape as the $5,339 discrepancy between an allocation total and
/// a returns total: two paths to one number, agreeing by habit rather than by
/// construction, with nothing to notice when they stop.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NetWorthReconciliationTests
{
    private readonly PostgresFixture _fixture;

    public NetWorthReconciliationTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Cash left in the brokerage after the buy, identical at every magnitude.</summary>
    private const decimal CashFloat = 20_000m;

    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Overview_net_worth_matches_the_history_series_at_the_same_instant(
        Boundary.Position p)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var savings = await ledger.AddBankAccountAsync("Savings", openingBalance: 1_500m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW", assetClass: "equity");

        // Cash plus an invested position at the magnitude under test. The seeder does
        // the holdings row + buy + recompute together — the overview reads the
        // PROJECTION while the history series replays legs, so a seed that populates
        // only legs makes the two disagree for a reason unrelated to the invariant.
        // Fund the brokerage with the position's cost PLUS a fixed cash float, so the
        // cash left after the buy is the same at every magnitude and the expected
        // total below is exact rather than magnitude-dependent.
        await ledger.AddTransactionPairAsync(
            brokerage.Id, bank.Id, p.Basis + CashFloat, Utc(2024, 1, 10));
        await ledger.AddBoundaryPositionAsync(
            brokerage.Id, holdings, sec, p, Utc(2024, 1, 10));
        // Priced above cost, at PriceScale — the valuation both routes must agree on.
        await ledger.AddSecurityPriceAsync(sec, p.SellPrice, Utc(2024, 6, 1));

        await using var db = _fixture.NewDbContext();
        var balances = new AccountBalancesRepository(db);
        var investment = new InvestmentReportingRepository(db);
        var overviewRepo = new OverviewRepository(db, balances, investment);
        var overview = await overviewRepo.GetAsync(ledger.LedgerId);

        // The history series clamps its final point to `to`, so ask for a window
        // ending now and compare against the overview's current figure.
        var now = DateTime.UtcNow;
        var history = await new AccountsReportingRepository(db, balances, investment, overviewRepo)
            .NetWorthHistoryAsync(ledger.LedgerId, Utc(2024, 1, 1), now, ReportTimeBucket.Year);

        Assert.NotEmpty(history.Points);
        var last = history.Points[^1];

        Assert.Equal(overview.NetWorth, last.NetWorth);

        // Agreement alone is satisfiable by two paths that both value the position at
        // nothing, so pin the valuation too. InvestmentsValue is the brokerage total —
        // the untouched cash float plus the position at quantity × price. This is what
        // proves a 12dp quantity survived the multiplication rather than being dropped
        // or overflowing.
        var marketValue = decimal.Round(p.Quantity * p.SellPrice, Boundary.MoneyScale);
        Assert.Equal(
            CashFloat + marketValue,
            decimal.Round(overview.InvestmentsValue, Boundary.MoneyScale));
    }
}
