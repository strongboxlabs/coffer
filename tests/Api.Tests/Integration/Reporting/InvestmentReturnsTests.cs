using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Investment returns (ADR-0063 §D5, Track-2 historical valuations). Both the
/// money-weighted (IRR) and the true time-weighted (TWR) figure value the
/// portfolio the same way at every boundary — brokerage cash
/// (<c>account_balance_as_of</c>) + split-adjusted holdings market value (the
/// migration-172 feeder). Covers the cash-inclusive basis (a contribution left as
/// cash must not read as a loss), the TWR chain across an intermediate flow
/// (time-weighted diverges from money-weighted), and the non-positive-base null
/// path. <c>ReturnsAsync</c> had no integration coverage before this.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentReturnsTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentReturnsTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Returns_value_cash_inclusive_so_an_uninvested_contribution_is_not_a_loss()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Deposit $1,000 into the brokerage; it stays as cash (never invested).
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 10));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 4, 10));

        // Value = cash + securities, so the $1,000 is still there at the end and the
        // start value (before the contribution) is 0.
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(1000m, result.EndValue);

        // The contribution was never invested → no gain, no loss. The old
        // securities-only basis would have reported roughly -100% here.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(Math.Abs(result.MoneyWeightedReturn!.Value) < 1e-6,
            $"expected ~0% IRR, got {result.MoneyWeightedReturn}");
    }

    [Fact]
    public async Task Returns_time_weighted_chains_across_an_intermediate_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // Year start: deposit $1,000, buy 100 @ $10 (portfolio $1,000).
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, Utc(2024, 1, 1));
        // Price has doubled to $20: deposit another $2,000 and buy 100 more @ $20
        // (portfolio $4,000 = 200 sh, cash 0). Pre-deposit value is $2,000.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 2000m, Utc(2024, 4, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 20m, Utc(2024, 4, 10));
        // Year end: a $22 feed close → 200 × $22 = $4,400.
        await ledger.AddSecurityPriceAsync(sec, 22m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));   // exactly 365 days → annualization factor 1

        Assert.Equal(0m, result.StartValue);
        Assert.Equal(4400m, result.EndValue);

        // Sub-periods: $1,000 -> $2,000 (×2.0), then base $4,000 -> $4,400 (×1.1).
        // Cumulative 2.2 over one year → 120%/yr, independent of the flow's timing.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 1.2) < 1e-6,
            $"expected 120% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);

        // Money-weighted is lower: the larger $2,000 was invested only through the
        // smaller-return second sub-period — the IRR/TWR divergence TWR exists to show.
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(result.MoneyWeightedReturn!.Value > 0
                    && result.MoneyWeightedReturn.Value < result.TimeWeightedReturn.Value,
            $"expected 0 < IRR < TWR, got IRR {result.MoneyWeightedReturn}, TWR {result.TimeWeightedReturn}");
    }

    [Fact]
    public async Task Returns_time_weighted_is_null_when_a_sub_period_base_is_non_positive()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Deposit $1,000 then withdraw all $1,000 → the invested base drops to 0, so
        // a sub-period can't be valued and TWR is null-with-reason.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, -1000m, Utc(2024, 2, 20));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 4, 10));

        Assert.Null(result.TimeWeightedReturn);
        Assert.False(string.IsNullOrEmpty(result.TimeWeightedUnavailableReason));
        // Money-weighted still computes ($1,000 in, $1,000 out → ~0%).
        Assert.NotNull(result.MoneyWeightedReturn);
    }
}
