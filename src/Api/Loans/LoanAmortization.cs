using Coffer.Domain.Money;

namespace Coffer.Api.Loans;

/// <summary>
/// Pure loan-amortization math (ADR-0050 D3/D4). Computes a fixed-rate loan's
/// periodic payment from its terms, and the per-occurrence interest/principal
/// split from the balance owed going into the payment. All <c>decimal</c> (money), no
/// floating point — the power term is accumulated by repeated multiplication.
/// </summary>
/// <remarks>
/// Every figure is computed in the loan's currency and rounded once, at that currency's
/// ISO 4217 places (ADR-0114 D15): a whole-unit currency's payment and interest are whole,
/// a three-place currency's carry three places. The places come from
/// <see cref="CurrencyMinorUnits.PlacesOf"/>, so a stored code that would now be refused
/// keeps 2, as before the rule.
/// </remarks>
public static class LoanAmortization
{
    /// <summary>
    /// The fixed periodic principal+interest payment for a fully-amortizing
    /// loan: <c>P·r / (1 − (1+r)^−n)</c>, where <c>r</c> is the periodic rate
    /// (<paramref name="annualRatePercent"/>/100 ÷ <paramref name="paymentsPerYear"/>)
    /// and <c>n</c> is <paramref name="paymentCount"/>. Zero-rate loans amortize
    /// linearly (<c>P/n</c>). Rounded <b>up</b> to one minor unit of
    /// <paramref name="currencyCode"/>: mortgage servicers round the periodic payment up
    /// so the schedule fully retires the principal within the term (the final payment
    /// absorbs the small remainder). Rounding to nearest bills real payments one unit
    /// short — e.g. a 360-payment fixed-rate loan whose exact payment carries a remainder
    /// under half a minor unit: servicers (and Moneydance) charge the next unit up, where
    /// rounding to nearest would charge the unit below (ADR-0114 D15).
    /// </summary>
    /// <param name="currencyCode">The loan account's currency, whose places the payment
    /// rounds up to.</param>
    public static decimal PeriodicPayment(
        decimal originalPrincipal, decimal annualRatePercent, int paymentCount, int paymentsPerYear,
        string currencyCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        if (paymentCount <= 0 || paymentsPerYear <= 0 || originalPrincipal <= 0m) return 0m;

        var r = annualRatePercent / 100m / paymentsPerYear;
        if (r <= 0m) return CurrencyMinorUnits.RoundUpToMinorUnit(originalPrincipal / paymentCount, currencyCode);

        // factor = (1+r)^n by repeated multiplication (decimal — no double).
        var factor = 1m;
        for (var i = 0; i < paymentCount; i++) factor *= 1m + r;

        return CurrencyMinorUnits.RoundUpToMinorUnit(originalPrincipal * r * factor / (factor - 1m), currencyCode);
    }

    /// <summary>
    /// Split one occurrence's payment into interest + principal given the
    /// balance still owed going into it (a positive amount; the caller prices it as of
    /// the payment's own date, ADR-0078). Interest =
    /// balance × periodic rate, rounded half away from zero at
    /// <paramref name="currencyCode"/>'s places (ADR-0114 D15); principal = payment −
    /// interest. The principal is the remainder so the two always sum to the payment.
    /// </summary>
    /// <param name="currencyCode">The loan account's currency, whose places the interest
    /// rounds at.</param>
    public static (decimal Interest, decimal Principal) PeriodSplit(
        decimal currentBalanceOwed, decimal periodicPayment, decimal annualRatePercent, int paymentsPerYear,
        string currencyCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        if (paymentsPerYear <= 0) return (0m, periodicPayment);

        var r = annualRatePercent / 100m / paymentsPerYear;
        var interest = CurrencyMinorUnits.RoundAwayFromZero(currentBalanceOwed * r, currencyCode);
        if (interest < 0m) interest = 0m;
        return (interest, periodicPayment - interest);
    }
}
