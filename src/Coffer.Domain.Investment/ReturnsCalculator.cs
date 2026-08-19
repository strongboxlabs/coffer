namespace Coffer.Domain.Investment;

/// <summary>
/// Pure investment-return math (ADR-0063 v2). Money-weighted (XIRR) and
/// time-weighted (TWR) returns, computed here — never by the model (ADR-0063 D4).
/// Actual/365 day count. Returns are annualized fractions (0.1 = 10%/yr); null
/// when the inputs can't yield a meaningful figure (single-signed flows, no
/// convergence, or — for TWR — no invested sub-period at all). TWR is annualized
/// over the time actually invested, which can be shorter than the requested
/// window; see <see cref="TwrResult.CoveredYears"/>.
/// </summary>
public static class ReturnsCalculator
{
    /// <summary>A dated cash flow from the investor's perspective: negative =
    /// money in (start value + contributions), positive = money out (withdrawals
    /// + ending value).</summary>
    public readonly record struct CashFlow(DateTime Date, decimal Amount);

    /// <summary>
    /// Why <see cref="Xirr"/> did or did not produce a rate. Every non-solved case
    /// is a distinct, explainable condition rather than a bare null — the caller
    /// turns these into the reason string the API reports alongside the missing
    /// figure, so a blank return always says why it is blank.
    /// </summary>
    public enum XirrOutcome
    {
        /// <summary>A rate was found.</summary>
        Solved = 0,

        /// <summary>Fewer than two dated flows — nothing to solve between.</summary>
        TooFewFlows,

        /// <summary>All flows share a sign: no rate discounts money-in to money-out
        /// when one side is missing entirely.</summary>
        SingleSignedFlows,

        /// <summary>Every flow falls on one instant. All discount exponents are
        /// zero, so NPV is a constant and there is no elapsed time to annualize
        /// over — the same condition <see cref="Twr"/> refuses.</summary>
        ZeroLengthWindow,

        /// <summary>NPV is flat at zero: the flows offset exactly, so every rate
        /// satisfies them equally and none is more correct than another.</summary>
        Indeterminate,

        /// <summary>No sign change across the search bracket, so no root exists in
        /// the range the solver can reach.</summary>
        NoRootInRange,
    }

    /// <summary>
    /// The outcome of an <see cref="Xirr"/> solve. <see cref="Rate"/> is non-null
    /// if and only if <see cref="Outcome"/> is <see cref="XirrOutcome.Solved"/>.
    /// </summary>
    public readonly record struct XirrResult(double? Rate, XirrOutcome Outcome);

    /// <summary>
    /// Money-weighted (internal) rate of return, annualized. Solves
    /// Σ amount / (1+r)^(years) = 0 via Newton with a bisection fallback. Returns
    /// the rate with <see cref="XirrOutcome.Solved"/>, or a null rate with the
    /// specific <see cref="XirrOutcome"/> that prevented one — every blank figure
    /// can be explained rather than merely reported.
    /// </summary>
    public static XirrResult Xirr(IReadOnlyList<CashFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        if (flows.Count < 2) return new(null, XirrOutcome.TooFewFlows);

        var hasPos = false;
        var hasNeg = false;
        foreach (var f in flows)
        {
            if (f.Amount > 0m) hasPos = true;
            else if (f.Amount < 0m) hasNeg = true;
        }
        if (!hasPos || !hasNeg) return new(null, XirrOutcome.SingleSignedFlows);

        var t0 = flows[0].Date;
        var tLast = flows[0].Date;
        for (var i = 1; i < flows.Count; i++)
        {
            if (flows[i].Date < t0) t0 = flows[i].Date;
            if (flows[i].Date > tLast) tLast = flows[i].Date;
        }

        // Every flow at the same instant: all discount exponents are zero, so NPV
        // is the constant Σ amount and NO rate is more correct than any other — the
        // IRR is undetermined, exactly as an annualized rate over a zero-length
        // window must be. Twr has always refused this (totalDays <= 0 below); Xirr
        // did not, and the consequence was not a wrong number but a FABRICATED one:
        // with a start value that equals the end value the constant is 0, the
        // Newton loop breaks on a zero derivative, and bisection's first iteration
        // finds |NPV(mid)| < 1e-7 and returns mid — the untouched midpoint of the
        // initial bracket, (-0.9999 + 100) / 2 = 49.50005. That surfaced as a
        // confident "4950%/yr" on any account whose flows all shared one instant.
        if ((tLast - t0).TotalDays <= 0) return new(null, XirrOutcome.ZeroLengthWindow);

        double Npv(double rate)
        {
            var sum = 0.0;
            foreach (var f in flows)
            {
                var years = (f.Date - t0).TotalDays / 365.0;
                sum += (double)f.Amount / Math.Pow(1.0 + rate, years);
            }
            return sum;
        }

        // Newton-Raphson from a 10% guess.
        var r = 0.1;
        for (var i = 0; i < 100; i++)
        {
            var f0 = Npv(r);
            const double h = 1e-6;
            var deriv = (Npv(r + h) - Npv(r - h)) / (2 * h);
            if (Math.Abs(deriv) < 1e-12) break;
            var next = r - f0 / deriv;
            if (double.IsNaN(next) || double.IsInfinity(next) || next <= -0.9999) break;
            if (Math.Abs(next - r) < 1e-9)
            {
                r = next;
                if (Math.Abs(Npv(r)) < 1e-6) return new(r, XirrOutcome.Solved);
                break;
            }
            r = next;
        }

        // Bisection fallback over a wide bracket.
        double lo = -0.9999, hi = 100.0;
        var flo = Npv(lo);
        var fhi = Npv(hi);
        // No sign change → no root in range.
        if (flo * fhi > 0) return new(null, XirrOutcome.NoRootInRange);

        // NPV zero at both ends of a 101-wide bracket means the curve is flat at
        // zero, so every rate "solves" it — undetermined, not solved. The span
        // guard above misses this shape: the NON-ZERO flows can share one instant
        // while a later zero-amount boundary (an emptied account's ending value)
        // keeps the window open. Conservative — a real curve with exact roots at
        // both -0.9999 and 100.0 is not a thing, and null beats the bracket
        // midpoint that the |NPV(mid)| < 1e-7 test would otherwise hand back.
        if (flo == 0.0 && fhi == 0.0) return new(null, XirrOutcome.Indeterminate);
        for (var i = 0; i < 300; i++)
        {
            var mid = (lo + hi) / 2.0;
            var fm = Npv(mid);
            if (Math.Abs(fm) < 1e-7) return new(mid, XirrOutcome.Solved);
            if (flo * fm < 0) { hi = mid; fhi = fm; }
            else { lo = mid; flo = fm; }
        }
        // 300 halvings of a 101-wide bracket leaves an interval far below double
        // precision, so this midpoint IS the root — a converged answer, not the
        // untouched bracket centre that the degenerate cases above used to return.
        return new((lo + hi) / 2.0, XirrOutcome.Solved);
    }

    /// <summary>One sub-period boundary for TWR: the portfolio value just before
    /// the external flow at that date, and the flow amount (investor perspective:
    /// negative = money in). The first segment is the start (flow = 0, value =
    /// start MV); the last is the end (flow = 0, value = end MV).</summary>
    public readonly record struct Boundary(DateTime Date, decimal ValueBeforeFlow, decimal Flow);

    /// <summary>
    /// Why <see cref="Twr"/> did or did not produce a rate — the TWR counterpart of
    /// <see cref="XirrOutcome"/>, so a blank time-weighted figure explains itself
    /// with the same rigour a blank money-weighted one does.
    /// </summary>
    public enum TwrOutcome
    {
        /// <summary>A rate was found, covering <see cref="TwrResult.CoveredYears"/>.</summary>
        Solved = 0,

        /// <summary>Fewer than two boundaries — no sub-period to chain.</summary>
        TooFewBoundaries,

        /// <summary>No sub-period had a positive invested base: the account held
        /// nothing for the whole window, so there is no performance to measure.</summary>
        NoInvestedSubPeriod,

        /// <summary>Invested sub-periods exist but span no elapsed time, so there is
        /// nothing to annualize over.</summary>
        ZeroLengthCoverage,

        /// <summary>Cumulative growth went negative — the holdings ended worth less
        /// than nothing (margin debt). A fractional power of a negative number is
        /// not real, so no annualized rate exists.</summary>
        NegativeCumulativeGrowth,
    }

    /// <summary>
    /// The outcome of a <see cref="Twr"/> chain. <see cref="Rate"/> is non-null if
    /// and only if <see cref="Outcome"/> is <see cref="TwrOutcome.Solved"/>.
    /// <para>
    /// <see cref="CoveredYears"/> is the figure a caller must render alongside the
    /// rate: it is the SUM of the invested sub-periods, which is shorter than the
    /// requested window whenever the account was empty at either end or in the
    /// middle. <see cref="CoveredFrom"/>/<see cref="CoveredTo"/> are the outer
    /// bounds of that coverage and are informational only — with an interior gap
    /// they enclose more time than was actually invested, so CoveredYears, not
    /// their difference, is what the rate is annualized over.
    /// </para>
    /// </summary>
    public readonly record struct TwrResult(
        double? Rate,
        TwrOutcome Outcome,
        DateTime? CoveredFrom,
        DateTime? CoveredTo,
        double CoveredYears);

    /// <summary>
    /// True time-weighted return over the boundaries, annualized. Chains
    /// sub-period returns r_k = V_k / (V_{k-1} − flow_{k-1}) (flow is investor-
    /// signed: money-in is negative, so subtracting adds it to the invested base).
    /// <para>
    /// Sub-periods with a non-positive base are SKIPPED, not fatal. A stretch in
    /// which the account holds nothing has no return to contribute — its growth
    /// factor is 1 and its elapsed time is not investment time — so discarding the
    /// whole chain over one such stretch throws away a perfectly well-defined
    /// answer. That is what the previous "return null on any non-positive base"
    /// did, and on a real ledger it blanked the figure for every account funded
    /// after the window opened and every account emptied before it closed: six of
    /// nine accounts on the ledger this was found against, including both sides of
    /// every rollover. The rate is annualized over the covered time only, and the
    /// caller is handed that span so a ten-month figure can never be presented in a
    /// column headed five years.
    /// </para>
    /// </summary>
    public static TwrResult Twr(IReadOnlyList<Boundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count < 2) return new(null, TwrOutcome.TooFewBoundaries, null, null, 0.0);

        var cumulative = 1.0;
        var coveredDays = 0.0;
        DateTime? from = null;
        DateTime? to = null;

        for (var k = 1; k < boundaries.Count; k++)
        {
            // Base = previous value adjusted for the flow that happened at the
            // previous boundary (money-in lowers the investor flow sign, so the
            // invested base = prevValue − prevFlow).
            var baseValue = (double)(boundaries[k - 1].ValueBeforeFlow - boundaries[k - 1].Flow);

            // Nothing invested across this stretch: no return, and its calendar time
            // is not investment time. An account emptied by a withdrawal lands here
            // on the sub-period AFTER the withdrawal — the withdrawal's own
            // sub-period still has the pre-flow value as its base, so the return it
            // earned is kept. Note that a total market wipeout does NOT land here:
            // there the base stays positive and the ending value is 0, which is a
            // real −100% factor and is chained as one.
            if (baseValue <= 0) continue;

            cumulative *= (double)boundaries[k].ValueBeforeFlow / baseValue;
            coveredDays += Math.Max(0.0, (boundaries[k].Date - boundaries[k - 1].Date).TotalDays);
            from ??= boundaries[k - 1].Date;
            to = boundaries[k].Date;
        }

        if (from is null) return new(null, TwrOutcome.NoInvestedSubPeriod, null, null, 0.0);
        if (coveredDays <= 0) return new(null, TwrOutcome.ZeroLengthCoverage, from, to, 0.0);

        // A negative cumulative factor means an ending value below zero somewhere —
        // borrowed against the position. Math.Pow of a negative base to a fractional
        // exponent is NaN, and NaN is not JSON: it would surface as a serialization
        // failure or a silent null with no reason attached. Refuse it explicitly.
        // Cumulative EXACTLY zero is fine and deliberate — Math.Pow(0, x) is 0, so
        // the total loss annualizes to −100%/yr, which is the true answer.
        if (cumulative < 0.0)
            return new(null, TwrOutcome.NegativeCumulativeGrowth, from, to, coveredDays / 365.0);

        return new(
            Math.Pow(cumulative, 365.0 / coveredDays) - 1.0,
            TwrOutcome.Solved,
            from,
            to,
            coveredDays / 365.0);
    }
}
