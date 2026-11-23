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
/// Moneydance stores money as <c>long</c> minor units at its currency's own decimal
/// places, and <see cref="MdAmounts.ToMoney(long, int)"/> divides by ten to that power —
/// one line. A wrong divisor breaks downstream tests only by making their figures
/// absurd; these tests assert the scale itself.
/// </remarks>
public sealed class MoneyMappingBoundaryTests
{
    [Fact]
    public void The_largest_importable_two_decimal_amount_fits_the_money_column()
    {
        // The importer's ceiling is its own input type: long minor units at 2 places.
        var ceiling = MdAmounts.ToMoney(long.MaxValue, 2);

        // It must fit NUMERIC(19,2), or a real export could import a value the money
        // column cannot store. It does: long.MaxValue/100 and the column's maximum
        // both have a 17-digit integer part, and the long bound sits just under the
        // column's. This assertion pins that headroom. A 0-place currency (the yen)
        // has no such headroom: its ceiling is long.MaxValue itself, which a Moneydance
        // amount never approaches and the column would refuse.
        Assert.True(ceiling <= Boundary.MaxMoney,
            $"importer ceiling {ceiling} exceeds the money column maximum {Boundary.MaxMoney}");

        // And it is exact: a division that went through double would lose the low
        // digits at this magnitude.
        Assert.Equal(92_233_720_368_547_758.07m, ceiling);
        Assert.Equal(decimal.Round(ceiling, Boundary.MoneyScale), ceiling);
    }

    [Theory]
    [InlineData(0L, 2, 0)]
    [InlineData(1L, 2, 0.01)]
    [InlineData(-1L, 2, -0.01)]
    [InlineData(100L, 2, 1)]
    [InlineData(-123_456_789L, 2, -1_234_567.89)]
    // A currency with no minor unit (the yen, dec 0): the integer IS the amount.
    [InlineData(1500L, 0, 1500)]
    [InlineData(-100_000L, 0, -100_000)]
    // A 3-place currency (ADR-0114 D15): thousandths, not hundredths.
    [InlineData(1L, 3, 0.001)]
    [InlineData(-1_234_567L, 3, -1_234.567)]
    public void Minor_units_convert_exactly_at_their_currencys_places(long minorUnits, int decimals, double expected)
    {
        // The scale is the assertion: each currency's own places, and nothing else.
        // A fixed /100 turns a whole-yen figure into a hundredth of itself and is caught
        // here directly.
        Assert.Equal((decimal)expected, MdAmounts.ToMoney(minorUnits, decimals));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Places_Coffer_cannot_store_are_refused(int decimals)
    {
        // The database keeps leg money to at most 3 places (ADR-0114 D15).
        Assert.Throws<ArgumentOutOfRangeException>(() => MdAmounts.ToMoney(1L, decimals));
    }

    [Fact]
    public void The_largest_importable_three_decimal_amount_is_exact()
    {
        // A 3-place currency's ceiling, long.MaxValue thousandths, keeps every digit.
        var ceiling = MdAmounts.ToMoney(long.MaxValue, 3);

        Assert.Equal(9_223_372_036_854_775.807m, ceiling);
        Assert.True(ceiling <= Boundary.MaxMoney);
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
                Assert.Equal(money, MdAmounts.ToMoney(minor, 2));
            }
        }
    }
}
