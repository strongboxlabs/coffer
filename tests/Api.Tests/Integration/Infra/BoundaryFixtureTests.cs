namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Guards the properties every boundary case must hold, so a case added later cannot
/// silently break the suites that rely on them.
/// </summary>
/// <remarks>
/// These are fixture invariants rather than product behaviour, but they are load
/// bearing: a matrix whose cases disagree about the price move makes cross-magnitude
/// invariance false, and a test written against that property then fails in a way
/// that looks like a bug in the code under test. That happened once already.
/// </remarks>
public sealed class BoundaryFixtureTests
{
    /// <summary>
    /// Every case moves the price by the same factor, so any percentage a report
    /// derives is identical at every magnitude.
    /// </summary>
    [Fact]
    public void Every_position_shares_the_same_price_ratio()
    {
        var cases = Boundary.All;
        Assert.True(cases.Count >= 2, "the matrix needs at least two magnitudes to compare");

        var first = cases[0];
        foreach (var c in cases.Skip(1))
        {
            // Compared at 12dp: 10/9 is a repeating decimal, so exact equality would
            // depend on how each pair happens to round.
            Assert.True(
                Math.Abs(c.Ratio - first.Ratio) < 0.000000000001m,
                $"'{c.Name}' moves the price by {c.Ratio} but '{first.Name}' moves it by " +
                $"{first.Ratio}. Every case must share one ratio — see Position.Ratio.");
        }
    }

    /// <summary>
    /// The declared money totals are what the arithmetic actually produces, so a test
    /// asserting against <c>Basis</c> or <c>Proceeds</c> is asserting a real figure.
    /// </summary>
    [Fact]
    public void Declared_totals_match_the_arithmetic()
    {
        foreach (var c in Boundary.All)
        {
            Assert.Equal(decimal.Round(c.Quantity * c.BuyPrice, Boundary.MoneyScale), c.Basis);
            Assert.Equal(decimal.Round(c.Quantity * c.SellPrice, Boundary.MoneyScale), c.Proceeds);
        }
    }
}
