using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// The rules the investment and budget steps compute their writes by: which month-end
/// values become hand-entered prices, how the manifest's weights become a look-through,
/// and which months get budget targets.
/// </summary>
public sealed class DemoSeedContentTests
{
    /// <summary>Every month-end of one year and the next January, each worth its month.</summary>
    private static IReadOnlyList<DemoUnitValue> MonthEnds(int year) =>
        Enumerable.Range(1, 13)
            .Select(i => new DateOnly(year, 1, 1).AddMonths(i).AddDays(-1))
            .Select(d => new DemoUnitValue(d, d.Month))
            .ToList();

    [Fact]
    public void Prices_are_the_quarter_ends_from_the_first_buy_s_quarter_through_AsOf()
    {
        var prices = DemoSeedContent.QuarterEndPrices(
            MonthEnds(2025), firstBuy: new DateOnly(2025, 4, 14), asOf: new DateOnly(2025, 12, 30));

        // March ends before the first buy, and December after AsOf.
        Assert.Equal(
            [new DateOnly(2025, 6, 30), new DateOnly(2025, 9, 30)],
            prices.Select(p => p.Date));
        Assert.Equal([6m, 9m], prices.Select(p => p.UnitValue));
    }

    [Fact]
    public void A_quarter_end_on_the_first_buy_or_on_AsOf_is_priced_and_a_mid_month_value_never_is()
    {
        var values = MonthEnds(2025).Append(new DemoUnitValue(new DateOnly(2025, 6, 15), 99m)).ToList();

        var prices = DemoSeedContent.QuarterEndPrices(
            values, firstBuy: new DateOnly(2025, 3, 31), asOf: new DateOnly(2025, 12, 31));

        Assert.Equal(
            [new DateOnly(2025, 3, 31), new DateOnly(2025, 6, 30), new DateOnly(2025, 9, 30), new DateOnly(2025, 12, 31)],
            prices.Select(p => p.Date));
    }

    [Fact]
    public void The_look_through_has_one_sleeve_per_component_at_its_weight()
    {
        var components = DemoSeedContent.LookThrough(DemoSecurities.TargetDateWeights);

        Assert.Equal(
            new[]
            {
                ("equity", "us", DemoSecurities.TargetDateWeights[DemoSecurities.Vti]),
                ("equity", "developed_ex_us", DemoSecurities.TargetDateWeights[DemoSecurities.Vxus]),
                ("fixed_income", "us", DemoSecurities.TargetDateWeights[DemoSecurities.Bnd]),
            },
            components.Select(c => (c.AssetClass, c.Region!, c.Weight)));
    }

    [Theory]
    [InlineData("VTI", "0.5", "BND", "0.4", "sum to 0.9, not 1")]
    [InlineData("VTI", "1.1", "BND", "-0.1", "BND is not positive")]
    [InlineData("VTI", "0.5", "NVDA", "0.5", "NVDA has no sleeve")]
    public void A_look_through_the_manifest_gets_wrong_is_refused(
        string first, string firstWeight, string second, string secondWeight, string refusal)
    {
        var weights = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [first] = decimal.Parse(firstWeight, System.Globalization.CultureInfo.InvariantCulture),
            [second] = decimal.Parse(secondWeight, System.Globalization.CultureInfo.InvariantCulture),
        };

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoSeedContent.LookThrough(weights));
        Assert.Contains(refusal, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_months_are_the_twelve_ending_in_AsOf_s_month_across_a_year_end()
    {
        Assert.Equal(
            Enumerable.Range(0, 12).Select(i => new DateOnly(2025, 12, 1).AddMonths(i)),
            DemoLedgerSteps.BudgetMonthsEndingIn(new DateOnly(2026, 11, 18)));
        Assert.Equal(new DateOnly(2026, 1, 1), DemoLedgerSteps.BudgetMonthsEndingIn(new DateOnly(2026, 12, 31))[0]);
    }
}
