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
        // window must be. Twr refuses this too (totalDays <= 0 below). Without this
        // guard the result is not a wrong number but a FABRICATED one: with a start
        // value that equals the end value the constant is 0, the Newton loop breaks
        // on a zero derivative, and bisection's first iteration finds
        // |NPV(mid)| < 1e-7 and returns mid — the untouched midpoint of the initial
        // bracket, halfway between the bisection's lo and hi below, which reads as a
        // confident "4950%/yr" on any account whose flows all share one instant.
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
        // both lo and hi is not a thing, and null beats the bracket
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
        // untouched bracket centre that the degenerate cases above would return
        // without their own exits.
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
    /// answer: "return null on any non-positive base" would blank the figure for
    /// every account funded after the window opened and every account emptied
    /// before it closed, including both sides of every rollover. The rate is annualized over the covered time only, and the
    /// caller is handed that span so a ten-month figure can never be presented in a
    /// column headed five years.
    /// </para>
    /// </summary>
    public static TwrResult Twr(IReadOnlyList<Boundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count < 2) return new(null, TwrOutcome.TooFewBoundaries, null, null, 0.0);

        var chain = TwrChain.Build(boundaries);
        var cumulative = chain.FinalFactor;
        var coveredDays = chain.FinalCoveredDays;
        var from = chain.CoveredFrom;
        var to = chain.CoveredTo;

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

    /// <summary>What a <see cref="TwrChain"/> says about one instant.</summary>
    public enum TwrReadState
    {
        /// <summary>The account was invested, and the reading carries a growth factor.</summary>
        Value = 0,

        /// <summary>Nothing has been invested yet: no sub-period at or before the
        /// instant had a positive base.</summary>
        BeforeFirstFunding,

        /// <summary>Something was invested earlier, but the stretch the instant sits
        /// in has no positive base (the account was emptied and is not funded again
        /// yet). Its calendar time is not investment time and its factor is 1, so
        /// there is no value to report, and the last one is not carried over it.</summary>
        NoInvestedBase,

        /// <summary>The cumulative growth is below zero (the holdings were worth less
        /// than nothing), which is not a growth factor.</summary>
        NegativeCumulativeGrowth,
    }

    /// <summary>
    /// A reading of a <see cref="TwrChain"/>: the cumulative growth factor and the
    /// invested days up to the instant, when <see cref="State"/> is
    /// <see cref="TwrReadState.Value"/>. For any other state the factor and the days
    /// are what the chain holds there and are not a result.
    /// </summary>
    public readonly record struct TwrReading(TwrReadState State, double Factor, double CoveredDays);

    /// <summary>
    /// The time-weighted chain behind <see cref="Twr"/>, kept at every boundary: the
    /// running cumulative growth factor and invested days after each one, so the
    /// factor can be READ at any instant without changing it.
    /// </summary>
    /// <remarks>
    /// <para>The chain runs on the boundaries it is given, which are the instants money
    /// crossed the scope's edge (plus the window's own ends) and nowhere else: a
    /// sub-period's return is its end value over its base, and between flows the value
    /// moves only by the holdings' own return, so no other instant needs valuing to
    /// chain. <see cref="ReadAt"/> reads the factor at an instant without making it a
    /// boundary, from the last boundary at or before it and the portfolio's value at the
    /// instant. A reader that samples more or fewer instants therefore cannot change any
    /// number the others read, which sampling the chain itself would.</para>
    /// <para><see cref="Twr"/> is this chain's final factor annualized over its invested
    /// days: one implementation of the chaining rule, with the skipped sub-periods
    /// (a non-positive base) and the real zero of a total wipeout defined once.</para>
    /// </remarks>
    public sealed class TwrChain
    {
        private readonly Boundary[] _boundaries;
        private readonly double[] _factor;
        private readonly double[] _covered;
        private readonly bool[] _started;
        private readonly bool[] _arrivedInvested;

        private TwrChain(Boundary[] boundaries)
        {
            _boundaries = boundaries;
            _factor = new double[boundaries.Length];
            _covered = new double[boundaries.Length];
            _started = new bool[boundaries.Length];
            _arrivedInvested = new bool[boundaries.Length];
        }

        /// <summary>The cumulative growth factor after the last boundary: 1 when no
        /// sub-period was invested, 0 after a total wipeout.</summary>
        public double FinalFactor => _factor.Length == 0 ? 1.0 : _factor[^1];

        /// <summary>The days the chain was invested, summed over its invested
        /// sub-periods.</summary>
        public double FinalCoveredDays => _covered.Length == 0 ? 0.0 : _covered[^1];

        /// <summary>The start of the first invested sub-period; null when none was.</summary>
        public DateTime? CoveredFrom { get; private set; }

        /// <summary>The end of the last invested sub-period; null when none was.</summary>
        public DateTime? CoveredTo { get; private set; }

        /// <summary>
        /// Every invested sub-period, in order: from one boundary to the next, where the
        /// base the first left is positive. The stretches between them, before the first
        /// funding and wherever nothing was invested, are not investment time.
        /// </summary>
        /// <remarks>Another series chained over the same invested time (a benchmark) takes
        /// its growth over each of these and over the open one <see cref="OpenSpanFrom"/>
        /// names, and nowhere else, so it pauses where this chain pauses.</remarks>
        public IReadOnlyList<(DateTime From, DateTime To)> InvestedSpans => _investedSpans;

        private readonly List<(DateTime From, DateTime To)> _investedSpans = [];

        /// <summary>
        /// Where the invested sub-period that <paramref name="instant"/> sits inside began,
        /// as <see cref="ReadAt"/> grows it: the last boundary before the instant, when the
        /// base it left is positive. Null at a boundary (the sub-period ending there is one
        /// of <see cref="InvestedSpans"/>), before the first boundary, and in a stretch with
        /// no base.
        /// </summary>
        public DateTime? OpenSpanFrom(DateTime instant)
        {
            var at = LastAtOrBefore(instant);
            if (at < 0 || _boundaries[at].Date == instant) return null;
            var baseValue = (double)(_boundaries[at].ValueBeforeFlow - _boundaries[at].Flow);
            return baseValue > 0 ? _boundaries[at].Date : null;
        }

        private int LastAtOrBefore(DateTime instant)
        {
            int lo = 0, hi = _boundaries.Length - 1, at = -1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_boundaries[mid].Date <= instant) { at = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return at;
        }

        /// <summary>
        /// Chains the sub-periods between <paramref name="boundaries"/>, which must be
        /// in ascending date order, as <see cref="Twr"/> reads them.
        /// </summary>
        public static TwrChain Build(IReadOnlyList<Boundary> boundaries)
        {
            ArgumentNullException.ThrowIfNull(boundaries);
            var chain = new TwrChain([.. boundaries]);
            var b = chain._boundaries;
            if (b.Length == 0) return chain;

            chain._factor[0] = 1.0;
            for (var k = 1; k < b.Length; k++)
            {
                // Base = previous value adjusted for the flow that happened at the
                // previous boundary (money-in lowers the investor flow sign, so the
                // invested base = prevValue − prevFlow).
                var baseValue = (double)(b[k - 1].ValueBeforeFlow - b[k - 1].Flow);

                chain._factor[k] = chain._factor[k - 1];
                chain._covered[k] = chain._covered[k - 1];
                chain._started[k] = chain._started[k - 1];

                // Nothing invested across this stretch: no return, and its calendar
                // time is not investment time. An account emptied by a withdrawal
                // lands here on the sub-period AFTER the withdrawal — the
                // withdrawal's own sub-period still has the pre-flow value as its
                // base, so the return it earned is kept. Note that a total market
                // wipeout does NOT land here: there the base stays positive and the
                // ending value is 0, which is a real −100% factor and is chained as
                // one.
                if (baseValue <= 0) continue;

                chain._factor[k] *= (double)b[k].ValueBeforeFlow / baseValue;
                chain._covered[k] += Math.Max(0.0, (b[k].Date - b[k - 1].Date).TotalDays);
                chain._started[k] = true;
                chain._arrivedInvested[k] = true;
                chain._investedSpans.Add((b[k - 1].Date, b[k].Date));
                chain.CoveredFrom ??= b[k - 1].Date;
                chain.CoveredTo = b[k].Date;
            }
            return chain;
        }

        /// <summary>
        /// The chain's state at <paramref name="instant"/>, given the portfolio's
        /// <paramref name="valueAtInstant"/> there (the value INCLUDING any flow that
        /// lands on the instant). Reads only: the chain is not changed, so a reading
        /// never moves another.
        /// </summary>
        /// <remarks>
        /// At a boundary the reading is the chain as it stands there: the sub-period
        /// that ends at it earned its return on its own pre-flow value, and the flow at
        /// the instant earns nothing in no time. Between two boundaries it is the
        /// earlier boundary's factor grown by the value at the instant over the base
        /// that boundary left invested, which is the sub-period's return so far.
        /// </remarks>
        public TwrReading ReadAt(DateTime instant, decimal valueAtInstant)
        {
            // The last boundary at or before the instant.
            var at = LastAtOrBefore(instant);
            if (at < 0) return new(TwrReadState.BeforeFirstFunding, 1.0, 0.0);

            if (_boundaries[at].Date == instant)
            {
                if (!_started[at]) return new(TwrReadState.BeforeFirstFunding, _factor[at], _covered[at]);
                if (!_arrivedInvested[at]) return new(TwrReadState.NoInvestedBase, _factor[at], _covered[at]);
                return Classify(_factor[at], _covered[at]);
            }

            var baseValue = (double)(_boundaries[at].ValueBeforeFlow - _boundaries[at].Flow);
            if (baseValue <= 0)
                return new(
                    _started[at] ? TwrReadState.NoInvestedBase : TwrReadState.BeforeFirstFunding,
                    _factor[at], _covered[at]);

            return Classify(
                _factor[at] * ((double)valueAtInstant / baseValue),
                _covered[at] + Math.Max(0.0, (instant - _boundaries[at].Date).TotalDays));
        }

        // A cumulative factor that changes sign through negative holdings reads as
        // negative growth where it is below zero, the same exposure Twr has.
        private static TwrReading Classify(double factor, double coveredDays) =>
            new(factor < 0.0 ? TwrReadState.NegativeCumulativeGrowth : TwrReadState.Value, factor, coveredDays);
    }
}
