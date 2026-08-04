namespace Coffer.Api.Loans;

/// <summary>
/// Pure loan-amortization math (ADR-0050 D3/D4). Computes a fixed-rate loan's
/// periodic payment from its terms, and the per-occurrence interest/principal
/// split from the current balance owed. All <c>decimal</c> (money), no
/// floating point — the power term is accumulated by repeated multiplication.
/// </summary>
public static class LoanAmortization
{
    /// <summary>
    /// The fixed periodic principal+interest payment for a fully-amortizing
    /// loan: <c>P·r / (1 − (1+r)^−n)</c>, where <c>r</c> is the periodic rate
    /// (<paramref name="annualRatePercent"/>/100 ÷ <paramref name="paymentsPerYear"/>)
    /// and <c>n</c> is <paramref name="paymentCount"/>. Zero-rate loans amortize
    /// linearly (<c>P/n</c>). Rounded <b>up</b> to the cent: mortgage servicers
    /// round the periodic payment up so the schedule fully retires the principal
    /// within the term (the final payment absorbs the small remainder). Rounding
    /// to nearest bills real payments a cent short — e.g. $300k / 3.5% / 360 is
    /// $1,347.1341…, which servicers (and Moneydance) charge as $1,347.14, not .13.
    /// </summary>
    public static decimal PeriodicPayment(
        decimal originalPrincipal, decimal annualRatePercent, int paymentCount, int paymentsPerYear)
    {
        if (paymentCount <= 0 || paymentsPerYear <= 0 || originalPrincipal <= 0m) return 0m;

        var r = annualRatePercent / 100m / paymentsPerYear;
        if (r <= 0m) return RoundUpToCent(originalPrincipal / paymentCount);

        // factor = (1+r)^n by repeated multiplication (decimal — no double).
        var factor = 1m;
        for (var i = 0; i < paymentCount; i++) factor *= 1m + r;

        return RoundUpToCent(originalPrincipal * r * factor / (factor - 1m));
    }

    /// <summary>
    /// Split one occurrence's payment into interest + principal given the
    /// current balance still owed (a positive amount). Interest =
    /// balance × periodic rate; principal = payment − interest. The principal
    /// is the remainder so the two always sum to the payment.
    /// </summary>
    public static (decimal Interest, decimal Principal) PeriodSplit(
        decimal currentBalanceOwed, decimal periodicPayment, decimal annualRatePercent, int paymentsPerYear)
    {
        if (paymentsPerYear <= 0) return (0m, periodicPayment);

        var r = annualRatePercent / 100m / paymentsPerYear;
        var interest = Round(currentBalanceOwed * r);
        if (interest < 0m) interest = 0m;
        return (interest, periodicPayment - interest);
    }

    /// <summary>Round to the nearest cent — used for the interest portion of a
    /// split (interest accrues to the nearest cent on the balance).</summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Round <b>up</b> to the next cent — the servicer amortization
    /// convention for the periodic payment (see <see cref="PeriodicPayment"/>).</summary>
    private static decimal RoundUpToCent(decimal value) =>
        Math.Ceiling(value * 100m) / 100m;
}
