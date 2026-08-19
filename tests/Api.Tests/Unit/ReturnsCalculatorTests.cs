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

    /// <summary>
    /// The invariant every Xirr case is held to: a rate is present exactly when the
    /// outcome is Solved, and absent exactly when it is not. Callers pair the null
    /// with a reason string, so a rate without an outcome — or an outcome without a
    /// rate — would leave the API reporting a blank it cannot explain.
    /// </summary>
    private static void AssertRateAndOutcomeAgree(ReturnsCalculator.XirrResult result)
    {
        if (result.Outcome == ReturnsCalculator.XirrOutcome.Solved)
            Assert.NotNull(result.Rate);
        else
            Assert.Null(result.Rate);
    }

    [Fact]
    public void Xirr_simple_one_year_ten_percent()
    {
        // Invest 1000 (money in → negative), get back 1100 a year later.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 1100m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, result.Outcome);
        Assert.Equal(0.10, result.Rate!.Value, 3);   // ~10%/yr
    }

    [Fact]
    public void Xirr_with_a_mid_period_contribution()
    {
        // -1000 at start, -1000 mid-year, +2200 at end. Positive return.
        var mid = new DateTime(2025, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(mid, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 2200m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.True(result.Rate!.Value is > 0.05 and < 0.25, $"unexpected irr {result.Rate}");
    }

    [Fact]
    public void Xirr_null_when_all_flows_same_sign()
    {
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, -500m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.SingleSignedFlows, result.Outcome);
    }

    [Fact]
    public void Xirr_null_when_fewer_than_two_flows()
    {
        var result = ReturnsCalculator.Xirr(new[] { new ReturnsCalculator.CashFlow(Y0, -1000m) });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.TooFewFlows, result.Outcome);
    }

    // ---- degenerate windows must be null, never a fabricated rate ---------
    //
    // The solver's bisection bracket is [-0.9999, 100.0]. When NPV is identically
    // zero the first iteration satisfies |NPV(mid)| < 1e-7 and returns mid — the
    // untouched bracket midpoint, (-0.9999 + 100) / 2 = 49.50005. That is not a
    // computed answer, it is the search space's centre, and it reached the API as a
    // confident "4950%/yr". These pin it to null, each with its own outcome so the
    // caller can say WHY the figure is blank.

    /// <summary>The exact sentinel a fabricated result collapses to.</summary>
    private const double BracketMidpoint = (-0.9999 + 100.0) / 2.0;

    [Fact]
    public void Xirr_null_when_every_flow_shares_one_instant()
    {
        // A same-instant round trip: no elapsed time, so no annualized rate exists.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y0, 1000m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.ZeroLengthWindow, result.Outcome);
    }

    [Fact]
    public void Xirr_null_when_a_zero_length_window_has_equal_start_and_end_value()
    {
        // Exactly what ReturnsAsync builds for an account with no qualifying flows:
        // start value in, identical end value out, both stamped "now". This is the
        // shape that produced 49.50005 in production.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -2_984_260.08m),
            new ReturnsCalculator.CashFlow(Y0, 2_984_260.08m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.ZeroLengthWindow, result.Outcome);
        Assert.NotEqual(BracketMidpoint, result.Rate ?? 0.0);
    }

    [Fact]
    public void Xirr_null_when_the_curve_is_flat_across_an_open_window()
    {
        // The non-zero flows share one instant but a later zero-amount boundary
        // holds the window open — a same-day in-and-out on an account that is empty
        // at the end. Span is 365 days, so the elapsed-time guard does not catch
        // this one; the flat-curve guard does, and reports a different outcome.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y0, 1000m),
            new ReturnsCalculator.CashFlow(Y1, 0m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public void Xirr_never_returns_the_bare_bracket_midpoint_for_a_real_solve()
    {
        // Guard the sentinel itself: a genuine solve must not coincide with it.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 1100m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.True(Math.Abs(result.Rate!.Value - BracketMidpoint) > 1.0,
            $"solved rate {result.Rate} collided with the bracket midpoint sentinel");
    }

    [Fact]
    public void Xirr_still_solves_a_one_day_window()
    {
        // The guard keys on zero elapsed time, not on "short" — a one-day window is
        // still a real window, and its annualized rate is legitimately enormous.
        var oneDay = Y0.AddDays(1);
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(oneDay, 1001m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, result.Outcome);
        Assert.True(result.Rate!.Value > 0, $"expected a positive rate, got {result.Rate}");
    }

    [Fact]
    public void Xirr_solves_when_flows_share_an_instant_but_others_do_not()
    {
        // Several flows on one date is normal and must keep working; only ALL of
        // them sharing one date is degenerate.
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -600m),
            new ReturnsCalculator.CashFlow(Y0, -400m),
            new ReturnsCalculator.CashFlow(Y1, 1100m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(ReturnsCalculator.XirrOutcome.Solved, result.Outcome);
        Assert.Equal(0.10, result.Rate!.Value, 3);
    }

    [Fact]
    public void Xirr_negative_return_when_value_falls()
    {
        var result = ReturnsCalculator.Xirr(new[]
        {
            new ReturnsCalculator.CashFlow(Y0, -1000m),
            new ReturnsCalculator.CashFlow(Y1, 900m),
        });
        AssertRateAndOutcomeAgree(result);
        Assert.Equal(-0.10, result.Rate!.Value, 3);
    }

    /// <summary>
    /// The TWR counterpart of <see cref="AssertRateAndOutcomeAgree"/>, with one
    /// extra clause: a solved rate must also carry a positive covered span, because
    /// the span is what stops an annualized ten-month figure being read as a
    /// five-year one. A rate without a span is a rate the caller cannot label.
    /// </summary>
    private static void AssertTwrSelfConsistent(ReturnsCalculator.TwrResult result)
    {
        if (result.Outcome == ReturnsCalculator.TwrOutcome.Solved)
        {
            Assert.NotNull(result.Rate);
            Assert.True(result.CoveredYears > 0.0, "a solved TWR must cover some time");
            Assert.NotNull(result.CoveredFrom);
            Assert.NotNull(result.CoveredTo);
        }
        else
        {
            Assert.Null(result.Rate);
        }
    }

    private static readonly DateTime Mid = new(2025, 7, 2, 0, 0, 0, DateTimeKind.Utc); // Y0 + 182d

    [Fact]
    public void Twr_no_flows_is_just_price_growth()
    {
        // 1000 → 1100 over a year, no external flows.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, 1100m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.Solved, result.Outcome);
        Assert.Equal(0.10, result.Rate!.Value, 3);
        // Invested the whole window, so the covered span IS the window.
        Assert.Equal(1.0, result.CoveredYears, 3);
        Assert.Equal(Y0, result.CoveredFrom);
        Assert.Equal(Y1, result.CoveredTo);
    }

    [Fact]
    public void Twr_neutralizes_a_contribution()
    {
        // Period 1: 1000 → 1100 (10%). Then +500 in. Period 2: 1600 → 1760 (10%).
        // TWR chains 1.1 × 1.1 = 1.21 → 21% over the year (a money-weighted figure
        // would be skewed by the timing of the 500).
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Mid, 1100m, -500m),
            new ReturnsCalculator.Boundary(Y1, 1760m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(0.21, result.Rate!.Value, 2);
        Assert.Equal(1.0, result.CoveredYears, 3);
    }

    // ---- Partial coverage: the account is empty for part of the window --------
    //
    // These are the cases the old "return null on any non-positive base" threw
    // away. On the ledger this was found against they were six accounts of nine —
    // every rollover destination (empty until funded) and every rollover source
    // (empty afterwards), which is to say both halves of the largest movements in
    // the book. Each has a perfectly well-defined time-weighted return over the
    // stretch it actually held money.

    [Fact]
    public void Twr_starts_the_chain_at_first_funding_when_the_account_begins_empty()
    {
        // Empty at the window open; funded with 500 mid-year; 500 → 550 after.
        // The dead first half contributes no return and no time.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 0m, 0m),
            new ReturnsCalculator.Boundary(Mid, 0m, -500m),
            new ReturnsCalculator.Boundary(Y1, 550m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.Solved, result.Outcome);
        // 10% over 183 days, annualized — NOT 10% over the full year.
        Assert.Equal(Math.Pow(1.1, 365.0 / 183.0) - 1.0, result.Rate!.Value, 6);
        Assert.Equal(183.0 / 365.0, result.CoveredYears, 6);
        Assert.Equal(Mid, result.CoveredFrom);
    }

    [Fact]
    public void Twr_ends_the_chain_at_the_withdrawal_that_empties_the_account()
    {
        // 1000 → 1100 over the first half, then the whole 1100 is rolled out and
        // the account sits at zero to the window close. The 10% earned before the
        // rollover is kept; the dead tail neither adds nor annualizes.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Mid, 1100m, 1100m),
            new ReturnsCalculator.Boundary(Y1, 0m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(Math.Pow(1.1, 365.0 / 182.0) - 1.0, result.Rate!.Value, 6);
        Assert.Equal(182.0 / 365.0, result.CoveredYears, 6);
        Assert.Equal(Y0, result.CoveredFrom);
        Assert.Equal(Mid, result.CoveredTo);
    }

    [Fact]
    public void Twr_chains_across_an_interior_gap_and_annualizes_over_invested_time_only()
    {
        // Two years. Invested and +10% in year 1, emptied, dormant for six months,
        // refunded and +10% again over the last six. Cumulative 1.21 across 1.5
        // invested years — the six dormant months must not dilute the rate.
        var y2 = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var refunded = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, 1100m, 1100m),      // emptied
            new ReturnsCalculator.Boundary(refunded, 0m, -1000m),  // refunded
            new ReturnsCalculator.Boundary(y2, 1100m, 0m),
        });
        AssertTwrSelfConsistent(result);
        var investedDays = 365.0 + 183.0;
        Assert.Equal(Math.Pow(1.21, 365.0 / investedDays) - 1.0, result.Rate!.Value, 6);
        Assert.Equal(investedDays / 365.0, result.CoveredYears, 6);
    }

    [Fact]
    public void Twr_covered_bounds_enclose_more_time_than_covered_years_when_there_is_a_gap()
    {
        // The contract the DTO doc states: with an interior gap, CoveredFrom/To are
        // the OUTER bounds and enclose dormant time, so a caller that annualizes
        // from their difference gets a different — wrong — answer. CoveredYears is
        // the authority. Nail it down so nobody "simplifies" the two into one.
        var y2 = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var refunded = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, 1100m, 1100m),
            new ReturnsCalculator.Boundary(refunded, 0m, -1000m),
            new ReturnsCalculator.Boundary(y2, 1100m, 0m),
        });
        var enclosedYears = (result.CoveredTo!.Value - result.CoveredFrom!.Value).TotalDays / 365.0;
        Assert.True(
            enclosedYears > result.CoveredYears + 0.4,
            $"expected the enclosed span ({enclosedYears:F3}y) to exceed invested time " +
            $"({result.CoveredYears:F3}y) by the dormant half-year");
    }

    [Fact]
    public void Twr_annualization_magnifies_a_short_covered_span()
    {
        // A rollover destination funded three months before the window closes. The
        // holdings gained 10%; annualized that is ~47%/yr. The number is correct and
        // it is also the number that makes an unlabelled table lie — which is why
        // the covered span travels with it.
        var funded = new DateTime(2025, 10, 3, 0, 0, 0, DateTimeKind.Utc);   // Y1 - 90d
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 0m, 0m),
            new ReturnsCalculator.Boundary(funded, 0m, -1000m),
            new ReturnsCalculator.Boundary(Y1, 1100m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(Math.Pow(1.1, 365.0 / 90.0) - 1.0, result.Rate!.Value, 6);
        Assert.True(result.Rate!.Value > 0.46 && result.Rate!.Value < 0.48);
        Assert.Equal(90.0 / 365.0, result.CoveredYears, 6);
    }

    // ---- The cases that must still refuse -------------------------------------

    [Fact]
    public void Twr_null_when_the_account_held_nothing_all_window()
    {
        // Value appears with no flow to explain it: incoherent, and there is no
        // invested base anywhere. Still refused — skipping non-positive bases must
        // not become "invent a chain out of nothing".
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 0m, 0m),
            new ReturnsCalculator.Boundary(Y1, 100m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.NoInvestedSubPeriod, result.Outcome);
        Assert.Equal(0.0, result.CoveredYears);
    }

    [Fact]
    public void Twr_a_total_market_loss_is_minus_one_hundred_percent_not_a_skipped_period()
    {
        // The distinction the skip rule must preserve: holdings that go to ZERO with
        // no withdrawal have a positive base and a 0 ending value — a real −100%
        // factor to be chained, not a dormant stretch to be dropped. Getting this
        // wrong would silently erase the worst outcome a portfolio can have.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, 0m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.Solved, result.Outcome);
        Assert.Equal(-1.0, result.Rate!.Value, 6);
        Assert.Equal(1.0, result.CoveredYears, 3);
    }

    [Fact]
    public void Twr_null_when_cumulative_growth_goes_negative()
    {
        // Ended below zero — borrowed against the position. Math.Pow of a negative
        // base to a fractional exponent is NaN, and NaN is not representable in
        // JSON: without this guard the API would either fail to serialize or emit a
        // blank with no reason. Must be a null with an outcome, never a NaN.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y1, -50m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.NegativeCumulativeGrowth, result.Outcome);
        Assert.Null(result.Rate);
    }

    [Fact]
    public void Twr_null_with_fewer_than_two_boundaries()
    {
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.TooFewBoundaries, result.Outcome);
    }

    [Fact]
    public void Twr_null_when_the_invested_subperiods_span_no_time()
    {
        // Both boundaries on one instant: a base to chain, but nothing to
        // annualize over. Same refusal the window-length guard always made.
        var result = ReturnsCalculator.Twr(new[]
        {
            new ReturnsCalculator.Boundary(Y0, 1000m, 0m),
            new ReturnsCalculator.Boundary(Y0, 1100m, 0m),
        });
        AssertTwrSelfConsistent(result);
        Assert.Equal(ReturnsCalculator.TwrOutcome.ZeroLengthCoverage, result.Outcome);
    }
}
