using Coffer.Domain.Investment;

using Boundary = Coffer.Domain.Investment.ReturnsCalculator.Boundary;
using State = Coffer.Domain.Investment.ReturnsCalculator.TwrReadState;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The time-weighted chain the returns series reads (<see cref="ReturnsCalculator.TwrChain"/>):
/// its final factor is the one <see cref="ReturnsCalculator.Twr"/> annualizes, and a
/// reading at an instant never moves the chain or another reading.
/// </summary>
/// <remarks>
/// Boundaries are written as the engine writes them: the value just before the flow at
/// the instant, and the flow investor-signed (money in is negative), so a sub-period's
/// base is the previous value less the previous flow.
/// </remarks>
public sealed class TwrChainTests
{
    private static readonly DateTime Origin = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Day(int n) => Origin.AddDays(n);

    private static Boundary B(int day, decimal valueBeforeFlow, decimal flow = 0m) =>
        new(Day(day), valueBeforeFlow, flow);

    private static double Annualized(ReturnsCalculator.TwrChain chain) =>
        Math.Pow(chain.FinalFactor, 365.0 / chain.FinalCoveredDays) - 1.0;

    [Fact]
    public void The_final_factor_annualizes_to_what_Twr_reports()
    {
        // Start 1000; 10% over 60 days; 500 in; the 1600 base earns 5% over 90 days;
        // 200 out; then the 1480 base earns about 1.4% over 100 days.
        Boundary[] boundaries =
        [
            B(0, 1000m),
            B(60, 1100m, -500m),
            B(150, 1680m, 200m),
            B(250, 1500m),
        ];
        var twr = ReturnsCalculator.Twr(boundaries);
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(ReturnsCalculator.TwrOutcome.Solved, twr.Outcome);
        Assert.Equal(1100.0 / 1000.0 * (1680.0 / 1600.0) * (1500.0 / 1480.0), chain.FinalFactor, 12);
        Assert.Equal(250.0, chain.FinalCoveredDays, 9);
        Assert.Equal(twr.Rate!.Value, Annualized(chain), 12);
        Assert.Equal(Day(0), chain.CoveredFrom);
        Assert.Equal(Day(250), chain.CoveredTo);
    }

    [Fact]
    public void A_sub_period_with_no_base_is_skipped_and_the_factor_stays_one()
    {
        // Nothing held until money arrives on day 30; the 1000 then earns 10%.
        Boundary[] boundaries = [B(0, 0m), B(30, 0m, -1000m), B(60, 1100m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(1.1, chain.FinalFactor, 12);
        Assert.Equal(30.0, chain.FinalCoveredDays, 9);
        Assert.Equal(Day(30), chain.CoveredFrom);
        Assert.Equal(ReturnsCalculator.Twr(boundaries).Rate!.Value, Annualized(chain), 12);
    }

    [Fact]
    public void A_total_wipeout_is_a_real_zero_factor_and_not_a_skip()
    {
        // 1000 held, worth nothing at day 40; money in at day 40 then earns on the same zero.
        Boundary[] boundaries = [B(0, 1000m), B(40, 0m, -500m), B(80, 600m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(0.0, chain.FinalFactor);
        var atWipeout = chain.ReadAt(Day(40), 500m);
        Assert.Equal(State.Value, atWipeout.State);
        Assert.Equal(0.0, atWipeout.Factor);
        Assert.Equal(40.0, atWipeout.CoveredDays, 9);
    }

    [Fact]
    public void The_chain_resumes_from_its_last_value_after_a_gap_and_the_gap_reads_as_no_base()
    {
        // Funded on day 0 and +10% by day 50, where a withdrawal empties it. Nothing is held
        // until day 120, when 1000 returns; it earns 20% by day 200.
        Boundary[] boundaries =
        [
            B(0, 0m),
            B(10, 0m, -1000m),
            B(50, 1100m, 1100m),
            B(120, 0m, -1000m),
            B(200, 1200m),
        ];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(1.1 * 1.2, chain.FinalFactor, 12);
        Assert.Equal((40.0) + 80.0, chain.FinalCoveredDays, 9);

        // At the withdrawal the return it earned is kept; in the gap there is no base, and
        // the last value is neither carried nor zeroed; at the refund instant the stretch
        // before it was still empty.
        var atWithdrawal = chain.ReadAt(Day(50), 0m);
        Assert.Equal(State.Value, atWithdrawal.State);
        Assert.Equal(1.1, atWithdrawal.Factor, 12);
        Assert.Equal(State.NoInvestedBase, chain.ReadAt(Day(80), 0m).State);
        Assert.Equal(State.NoInvestedBase, chain.ReadAt(Day(120), 1000m).State);

        // After the refund it grows from where it stopped.
        var resumed = chain.ReadAt(Day(160), 1100m);
        Assert.Equal(State.Value, resumed.State);
        Assert.Equal(1.1 * 1.1, resumed.Factor, 12);
        Assert.Equal(40.0 + 40.0, resumed.CoveredDays, 9);
    }

    [Fact]
    public void Before_money_arrives_there_is_nothing_to_read()
    {
        Boundary[] boundaries = [B(0, 0m), B(30, 0m, -1000m), B(60, 1100m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(State.BeforeFirstFunding, chain.ReadAt(Day(10), 0m).State);
        // On the funding instant itself no invested time has passed.
        Assert.Equal(State.BeforeFirstFunding, chain.ReadAt(Day(30), 1000m).State);
        Assert.Equal(State.BeforeFirstFunding, chain.ReadAt(Day(-5), 0m).State);
        var inside = chain.ReadAt(Day(45), 1050m);
        Assert.Equal(State.Value, inside.State);
        Assert.Equal(1.05, inside.Factor, 12);
        Assert.Equal(15.0, inside.CoveredDays, 9);
    }

    [Fact]
    public void A_reading_inside_a_sub_period_equals_chaining_through_that_instant()
    {
        // The growth factor read at day 100 is the one a chain with a boundary there
        // (no flow, the same value) holds at it: reading is the sub-period's return so far.
        Boundary[] sparse = [B(0, 1000m), B(60, 1100m, -400m), B(200, 1800m)];
        Boundary[] dense = [B(0, 1000m), B(60, 1100m, -400m), B(100, 1300m), B(200, 1800m)];

        var read = ReturnsCalculator.TwrChain.Build(sparse).ReadAt(Day(100), 1300m);
        var denseChain = ReturnsCalculator.TwrChain.Build(dense);
        var atBoundary = denseChain.ReadAt(Day(100), 1300m);

        Assert.Equal(atBoundary.Factor, read.Factor, 12);
        Assert.Equal(atBoundary.CoveredDays, read.CoveredDays, 9);
        // A boundary that carries no flow does not change what the chain ends at.
        Assert.Equal(ReturnsCalculator.TwrChain.Build(sparse).FinalFactor, denseChain.FinalFactor, 12);
    }

    [Fact]
    public void Reading_never_changes_the_chain_or_another_reading()
    {
        Boundary[] boundaries = [B(0, 1000m), B(60, 1100m, -400m), B(200, 1800m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);
        var finalBefore = chain.FinalFactor;
        var first = chain.ReadAt(Day(30), 1050m);
        var second = chain.ReadAt(Day(120), 1500m);

        // Sample many more instants, in a different order, then read the first two again.
        foreach (var day in new[] { 199, 5, 61, 100, 150, 59, 2 })
            chain.ReadAt(Day(day), 1000m + (day * 3));

        Assert.Equal(finalBefore, chain.FinalFactor);
        Assert.Equal(first, chain.ReadAt(Day(30), 1050m));
        Assert.Equal(second, chain.ReadAt(Day(120), 1500m));
    }

    [Fact]
    public void A_reading_on_a_flow_instant_is_the_chain_there_and_ignores_the_value_after_the_flow()
    {
        Boundary[] boundaries = [B(0, 1000m), B(60, 1100m, -400m), B(200, 1800m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        // 1100 before the 400 in, 1500 after it: the instant reads the 10% the sub-period
        // earned, whatever value is offered for the instant.
        Assert.Equal(1.1, chain.ReadAt(Day(60), 1500m).Factor, 12);
        Assert.Equal(1.1, chain.ReadAt(Day(60), 123_456m).Factor, 12);
    }

    [Fact]
    public void Holdings_worth_less_than_nothing_are_not_a_growth_factor()
    {
        Boundary[] boundaries = [B(0, 1000m), B(30, -200m), B(60, -100m)];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.True(chain.FinalFactor < 0.0);
        var reading = chain.ReadAt(Day(30), -200m);
        Assert.Equal(State.NegativeCumulativeGrowth, reading.State);
        Assert.Equal(State.NegativeCumulativeGrowth, chain.ReadAt(Day(10), -50m).State);
        Assert.Equal(ReturnsCalculator.TwrOutcome.NegativeCumulativeGrowth, ReturnsCalculator.Twr(boundaries).Outcome);
    }

    [Fact]
    public void An_empty_or_single_boundary_chain_is_not_invested()
    {
        var empty = ReturnsCalculator.TwrChain.Build([]);
        Assert.Equal(1.0, empty.FinalFactor);
        Assert.Equal(State.BeforeFirstFunding, empty.ReadAt(Day(1), 100m).State);

        var single = ReturnsCalculator.TwrChain.Build([B(0, 1000m)]);
        Assert.Null(single.CoveredFrom);
        Assert.Equal(0.0, single.FinalCoveredDays);
        Assert.Empty(single.InvestedSpans);
    }

    /// <summary>
    /// The invested time another series is chained over (a benchmark, ADR-0114 D3): the
    /// sub-periods with a positive base, and, inside one, where it began, exactly where
    /// <see cref="ReturnsCalculator.TwrChain.ReadAt"/> grows the factor. Nothing before the
    /// first funding, and nothing across a stretch the account sat empty.
    /// </summary>
    /// <remarks>Mutation caught: a span for a sub-period with no base, or an open span inside
    /// one (the benchmark would not pause); no open span inside an invested sub-period (a
    /// point between two flows would lose its growth since the last).</remarks>
    [Fact]
    public void The_invested_spans_are_the_sub_periods_with_a_base_and_skip_the_empty_stretches()
    {
        // Nothing until day 10; 1000 in; emptied on day 40 (1100 out); 500 in on day 70.
        Boundary[] boundaries =
        [
            B(0, 0m),
            B(10, 0m, -1000m),
            B(40, 1100m, 1100m),
            B(70, 0m, -500m),
            B(100, 550m),
        ];
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);

        Assert.Equal(new[] { (Day(10), Day(40)), (Day(70), Day(100)) }, chain.InvestedSpans);
        Assert.Equal(Day(10), chain.CoveredFrom);

        Assert.Null(chain.OpenSpanFrom(Day(5)));            // before the first funding
        Assert.Equal(Day(10), chain.OpenSpanFrom(Day(25)));  // inside the first span
        Assert.Null(chain.OpenSpanFrom(Day(40)));           // on a boundary
        Assert.Null(chain.OpenSpanFrom(Day(55)));           // emptied: no base
        Assert.Equal(Day(70), chain.OpenSpanFrom(Day(85)));  // funded again

        // Where a span is open, the reading has a value; where none is, it has none.
        Assert.Equal(State.Value, chain.ReadAt(Day(25), 1050m).State);
        Assert.Equal(State.NoInvestedBase, chain.ReadAt(Day(55), 0m).State);
    }
}
