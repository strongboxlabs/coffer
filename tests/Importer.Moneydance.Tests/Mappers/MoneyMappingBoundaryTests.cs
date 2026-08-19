using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Money entering the system from a Moneydance export survives the magnitudes the
/// rest of the system is built to hold.
/// </summary>
/// <remarks>
/// The importer is where a money figure is first created, so a defect here is
/// invisible to every downstream test: they all agree happily on a wrong number.
/// Moneydance stores money as <c>long</c> minor units and
/// <see cref="AccountMapper.MinorUnitsToDecimal"/> divides by 100 — one line, no
/// tests before this one, and a mutation of `/100` to `/10` was caught by 31 tests
/// only because the resulting figures were absurd rather than because anything
/// asserted the scale.
/// </remarks>
public sealed class MoneyMappingBoundaryTests
{
    [Fact]
    public void The_largest_importable_amount_fits_the_money_column()
    {
        // The importer's ceiling is its own input type: long minor units / 100.
        var ceiling = AccountMapper.MinorUnitsToDecimal(long.MaxValue);

        // It must fit NUMERIC(19,2), or a real export could import a value the money
        // column cannot store. It does — with room to spare, which is WHY nobody has
        // hit this: long.MaxValue/100 is about 92.2 quadrillion against the column's
        // 99.9 quadrillion. That headroom is a fact worth pinning rather than
        // rediscovering.
        Assert.True(ceiling <= Boundary.MaxMoney,
            $"importer ceiling {ceiling} exceeds the money column maximum {Boundary.MaxMoney}");

        // And it is exact: a division that went through double would lose the low
        // digits at this magnitude.
        Assert.Equal(92_233_720_368_547_758.07m, ceiling);
        Assert.Equal(decimal.Round(ceiling, Boundary.MoneyScale), ceiling);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 0.01)]
    [InlineData(-1L, -0.01)]
    [InlineData(100L, 1)]
    [InlineData(-123_456_789L, -1_234_567.89)]
    public void Minor_units_convert_exactly(long minorUnits, double expected)
    {
        // The scale is the assertion: /100 lands on the cent, and nothing else does.
        // A /10 or /1000 mutation changes the value by an order of magnitude and is
        // caught here directly rather than by downstream figures looking odd.
        Assert.Equal((decimal)expected, AccountMapper.MinorUnitsToDecimal(minorUnits));
    }

    [Fact]
    public void Every_boundary_money_total_round_trips_through_minor_units()
    {
        // Each shared magnitude, pushed out to minor units and back — the trip a
        // figure makes when a ledger is exported and re-imported.
        foreach (var c in Boundary.All)
        {
            foreach (var money in new[] { c.Basis, c.Proceeds })
            {
                var minor = (long)decimal.Round(money * 100m, 0);
                Assert.Equal(money, AccountMapper.MinorUnitsToDecimal(minor));
            }
        }
    }
}
