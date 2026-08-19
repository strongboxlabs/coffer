using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The returns engine values a position and reports its return at every magnitude.
/// </summary>
/// <remarks>
/// Returns is an aggregation over valuations — each sub-period boundary values the
/// whole account — so a position whose <c>quantity × price</c> runs to 16 decimal
/// places is multiplied and summed repeatedly. That is the shape that broke
/// <c>realized_gains</c> and <c>holdings_snapshot</c> in production, reached here
/// through a third path.
/// <para>
/// One thing to know before reading the expectation: TWR is NOT the raw price move.
/// It is <b>annualized over the covered days and returned as a fraction rather than a
/// percentage</b> (<c>ReturnsCalculator</c>), so a 10/9 move over 144 days reads as
/// <c>0.3061</c>. It is derived from the case's own ratio and the engine's OWN
/// reported covered days, so it stays correct if the window ever changes.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsBoundaryTests
{
    private readonly PostgresFixture _fixture;

    public ReturnsBoundaryTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Returns_value_a_position_at_any_magnitude(Boundary.Position p)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Fund exactly the cost, so the account holds the position and no stray cash:
        // the end value is then the position alone.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, p.Basis, Utc(2024, 1, 10));
        await ledger.AddBoundaryPositionAsync(brokerage.Id, holdings, sec, p, Utc(2024, 1, 10));
        await ledger.AddSecurityPriceAsync(sec, p.SellPrice, Utc(2024, 6, 1));

        await using var db = _fixture.NewDbContext();
        var r = await new InvestmentReportingRepository(db).ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 6, 2));

        // The position is valued at the quoted price, at the feeder's 4dp scale. A
        // 12dp quantity that overflowed or was dropped fails here first.
        var expectedEnd = decimal.Round(p.Quantity * p.SellPrice, 4);
        Assert.True(Math.Abs(r.EndValue - expectedEnd) <= p.Tolerance,
            $"{p.Name}: end value {r.EndValue}, expected {expectedEnd}");

        // The return the prices imply, annualized over the engine's own covered days
        // on a 365-day year. Asserting the VALUE matters: a valuation that silently
        // dropped the position would still return some number.
        Assert.NotNull(r.TimeWeightedReturn);
        Assert.NotNull(r.TimeWeightedCoveredDays);
        var ratio = (double)(p.SellPrice / p.BuyPrice);
        var expectedTwr = Math.Pow(ratio, 365.0 / r.TimeWeightedCoveredDays!.Value) - 1.0;
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value - expectedTwr) <= 1e-6,
            $"{p.Name}: TWR {r.TimeWeightedReturn}, expected {expectedTwr} " +
            $"({ratio} over {r.TimeWeightedCoveredDays} days)");
    }

    /// <summary>
    /// The same price move yields the same return at every magnitude.
    /// </summary>
    /// <remarks>
    /// This is the property that needs NO model of the engine at all — a return is a
    /// percentage, and a percentage is scale-free. It works only because every
    /// <see cref="Boundary"/> case shares one price ratio, which
    /// <c>BoundaryFixtureTests</c> enforces; an earlier fixture had 1.25 in one case
    /// and 1.111 in the other, and a test written against this property then failed in
    /// a way that looked like an engine bug and was not.
    /// <para>
    /// What it catches that the per-case assertion above cannot: a valuation that
    /// degrades gradually with scale rather than failing outright. Both cases would
    /// still satisfy their own end-value check while their returns drift apart.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_same_price_move_returns_the_same_at_every_magnitude()
    {
        var byCase = new List<(string Name, double Twr)>();

        foreach (var p in Boundary.All)
        {
            var ledger = await SyntheticLedger.CreateAsync(_fixture);
            var bank = await ledger.AddBankAccountAsync("Checking");
            var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
            var holdings = brokerage.HoldingsAccountId!.Value;
            var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, p.Basis, Utc(2024, 1, 10));
            await ledger.AddBoundaryPositionAsync(brokerage.Id, holdings, sec, p, Utc(2024, 1, 10));
            await ledger.AddSecurityPriceAsync(sec, p.SellPrice, Utc(2024, 6, 1));

            await using var db = _fixture.NewDbContext();
            var r = await new InvestmentReportingRepository(db).ReturnsAsync(
                ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
                nowUtc: Utc(2024, 6, 2));

            Assert.NotNull(r.TimeWeightedReturn);
            byCase.Add((p.Name, r.TimeWeightedReturn!.Value));
        }

        var baseline = byCase[0];
        foreach (var other in byCase.Skip(1))
        {
            Assert.True(Math.Abs(other.Twr - baseline.Twr) <= 1e-9,
                $"TWR diverged by magnitude: {baseline.Name} {baseline.Twr} vs " +
                $"{other.Name} {other.Twr}");
        }
    }
}
