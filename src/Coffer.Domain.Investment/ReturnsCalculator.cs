namespace Coffer.Domain.Investment;

/// <summary>
/// Pure investment-return math (ADR-0063 v2). Money-weighted (XIRR) and
/// time-weighted (TWR) returns, computed here — never by the model (ADR-0063 D4).
/// Actual/365 day count. Returns are annualized fractions (0.1 = 10%/yr); null
/// when the inputs can't yield a meaningful figure (single-signed flows, no
/// convergence, or — for TWR — a sub-period that can't be valued).
/// </summary>
public static class ReturnsCalculator
{
    /// <summary>A dated cash flow from the investor's perspective: negative =
    /// money in (start value + contributions), positive = money out (withdrawals
    /// + ending value).</summary>
    public readonly record struct CashFlow(DateTime Date, decimal Amount);

    /// <summary>
    /// Money-weighted (internal) rate of return, annualized. Solves
    /// Σ amount / (1+r)^(years) = 0 via Newton with a bisection fallback. Null when
    /// there aren't both inflows and outflows, or it doesn't converge.
    /// </summary>
    public static double? Xirr(IReadOnlyList<CashFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        if (flows.Count < 2) return null;

        var hasPos = false;
        var hasNeg = false;
        foreach (var f in flows)
        {
            if (f.Amount > 0m) hasPos = true;
            else if (f.Amount < 0m) hasNeg = true;
        }
        if (!hasPos || !hasNeg) return null;

        var t0 = flows[0].Date;
        for (var i = 1; i < flows.Count; i++)
            if (flows[i].Date < t0) t0 = flows[i].Date;

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
                if (Math.Abs(Npv(r)) < 1e-6) return r;
                break;
            }
            r = next;
        }

        // Bisection fallback over a wide bracket.
        double lo = -0.9999, hi = 100.0;
        var flo = Npv(lo);
        var fhi = Npv(hi);
        if (flo * fhi > 0) return null;   // no sign change → no root in range
        for (var i = 0; i < 300; i++)
        {
            var mid = (lo + hi) / 2.0;
            var fm = Npv(mid);
            if (Math.Abs(fm) < 1e-7) return mid;
            if (flo * fm < 0) { hi = mid; fhi = fm; }
            else { lo = mid; flo = fm; }
        }
        return (lo + hi) / 2.0;
    }

    /// <summary>One sub-period boundary for TWR: the portfolio value just before
    /// the external flow at that date, and the flow amount (investor perspective:
    /// negative = money in). The first segment is the start (flow = 0, value =
    /// start MV); the last is the end (flow = 0, value = end MV).</summary>
    public readonly record struct Boundary(DateTime Date, decimal ValueBeforeFlow, decimal Flow);

    /// <summary>
    /// True time-weighted return over the boundaries, annualized. Chains
    /// sub-period returns r_k = V_k / (V_{k-1} − flow_{k-1}) (flow is investor-
    /// signed: money-in is negative, so subtracting adds it to the invested base).
    /// Null when any sub-period base is non-positive (can't value a segment).
    /// </summary>
    public static double? Twr(IReadOnlyList<Boundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count < 2) return null;

        var cumulative = 1.0;
        for (var k = 1; k < boundaries.Count; k++)
        {
            // Base = previous value adjusted for the flow that happened at the
            // previous boundary (money-in lowers the investor flow sign, so the
            // invested base = prevValue − prevFlow).
            var baseValue = (double)(boundaries[k - 1].ValueBeforeFlow - boundaries[k - 1].Flow);
            if (baseValue <= 0) return null;
            var subReturn = (double)boundaries[k].ValueBeforeFlow / baseValue;
            cumulative *= subReturn;
        }

        var totalDays = (boundaries[^1].Date - boundaries[0].Date).TotalDays;
        if (totalDays <= 0) return null;
        // Annualize the cumulative growth.
        return Math.Pow(cumulative, 365.0 / totalDays) - 1.0;
    }
}
