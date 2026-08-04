using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Pure math for investment returns (ADR-0063 v2). No DB — exercises the XIRR
/// solver + TWR chaining against hand-computed expectations.
/// </summary>
public sealed class ReturnsCalculatorTests
{
    private static readonly DateTime Y0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Y1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); // +365d

    [Fact]
    public void Xirr_simple_one_year_ten_percent()
    {
        // Invest 1000 (money in → negative), get back 1100 a year later.
        var irr = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 1100m),
        });
        Assert.NotNull(irr);
        Assert.Equal(0.10, irr!.Value, 3);   // ~10%/yr
    }

    [Fact]
    public void Xirr_with_a_mid_period_contribution()
    {
        // -1000 at start, -1000 mid-year, +2200 at end. Positive return.
        var mid = new DateTime(2025, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var irr = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(mid, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 2200m),
        });
        Assert.NotNull(irr);
        Assert.True(irr!.Value is > 0.05 and < 0.25, $"unexpected irr {irr}");
    }

    [Fact]
    public void Xirr_null_when_all_flows_same_sign()
    {
        Assert.Null(ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, -500m),
        }));
    }

    [Fact]
    public void Xirr_null_when_fewer_than_two_flows()
    {
        Assert.Null(ReturnsCalculator.Xirr(new[] { new ReturnsCalculator.CashFlow(Y0, -1000m) }));
    }

    [Fact]
    public void Xirr_negative_return_when_value_falls()
    {
        var irr = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 900m),
        });
        Assert.NotNull(irr);
        Assert.Equal(-0.10, irr!.Value, 3);
    }

    [Fact]
    public void Twr_no_flows_is_just_price_growth()
    {
        // 1000 → 1100 over a year, no external flows.
        var twr = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, 1100m, 0m),
        });
        Assert.NotNull(twr);
        Assert.Equal(0.10, twr!.Value, 3);
    }

    [Fact]
    public void Twr_neutralizes_a_contribution()
    {
        // Period 1: 1000 → 1100 (10%). Then +500 in. Period 2: 1600 → 1760 (10%).
        // TWR chains 1.1 × 1.1 = 1.21 → 21% over the year (a money-weighted figure
        // would be skewed by the timing of the 500).
        var twr = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(new DateTime(2025, 7, 2, 0, 0, 0, DateTimeKind.Utc), 1100m, -500m),
            new ReturnsCalculator.Boundary(Y1, 1760m, 0m),
        });
        Assert.NotNull(twr);
        Assert.Equal(0.21, twr!.Value, 2);
    }

    [Fact]
    public void Twr_null_when_a_subperiod_base_is_nonpositive()
    {
        Assert.Null(ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 0m, 0m),
            new ReturnsCalculator.Boundary(Y1, 100m, 0m),
        }));
    }
}
