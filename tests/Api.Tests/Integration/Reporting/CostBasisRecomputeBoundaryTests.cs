using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The FIFO recompute preserves quantity and cost basis at every magnitude.
/// </summary>
/// <remarks>
/// This is the path that failed in production: the recompute stores
/// <c>quantity × unit_cost</c>, both <c>NUMERIC(25,12)</c>, so a fractional lot
/// produces up to 24 decimal places — ~30 significant digits at a seven-figure
/// position, past <c>System.Decimal</c>. Postgres NUMERIC stored it happily and
/// Npgsql threw <c>OverflowException</c> on the way back. Migrations 182 / 204 / 205
/// bound the stored scale; this asserts the bound holds from the seeding side rather
/// than from a schema guard.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CostBasisRecomputeBoundaryTests
{
    private readonly PostgresFixture _fixture;

    public CostBasisRecomputeBoundaryTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Recompute_preserves_quantity_and_basis(Boundary.Position p)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await ledger.AddBoundaryPositionAsync(
            brokerage.Id, holdings, sec, p, new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        await using var db = _fixture.NewDbContext();
        var row = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdings && h.SecurityId == sec);

        // Quantity survives at full 12dp — a truncation here would silently
        // under-report the position rather than throwing.
        Assert.Equal(p.Quantity, row.Quantity);

        // Basis is money: bounded, and equal to the expected total.
        Assert.Equal(decimal.Round(row.CostBasis, Boundary.MoneyScale), row.CostBasis);
        Assert.True(Math.Abs(row.CostBasis - p.Basis) <= p.Tolerance,
            $"{p.Name}: basis {row.CostBasis}, expected {p.Basis}");

        // Re-running must be a no-op. The recompute re-derives from legs, so a scale
        // bug that rounded on each pass would drift a little further every time.
        await ledger.RecomputeHoldingsAsync();
        await using var db2 = _fixture.NewDbContext();
        var again = await db2.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdings && h.SecurityId == sec);
        Assert.Equal(row.Quantity, again.Quantity);
        Assert.Equal(row.CostBasis, again.CostBasis);
    }
}
