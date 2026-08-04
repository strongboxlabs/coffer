using Coffer.Api.Loans;

namespace Coffer.Api.Tests.Loans;

/// <summary>
/// Pure-math tests for <see cref="LoanAmortization"/> (ADR-0050). Uses a
/// textbook amortization as an exact oracle plus a real-scale range check.
/// </summary>
public sealed class LoanAmortizationTests
{
    [Fact]
    public void Periodic_payment_matches_a_known_amortization()
    {
        // $1,000 over 12 monthly payments at 12%/yr (1%/mo) → the textbook $88.85.
        Assert.Equal(88.85m, LoanAmortization.PeriodicPayment(1000m, 12m, 12, 12));
    }

    [Fact]
    public void Periodic_payment_rounds_up_to_the_cent_servicer_convention()
    {
        // $300k / 3.5% / 360 amortizes to $1,347.1341… — servicers (and MD)
        // round the payment UP so the loan fully retires within the term;
        // round-to-nearest would bill $1,347.13, a cent short every month.
        Assert.Equal(1347.14m, LoanAmortization.PeriodicPayment(300000m, 3.5m, 360, 12));
    }

    [Fact]
    public void Zero_rate_amortizes_linearly()
    {
        Assert.Equal(100.00m, LoanAmortization.PeriodicPayment(1200m, 0m, 12, 12));
    }

    [Fact]
    public void Period_split_is_interest_plus_principal_summing_to_payment()
    {
        var (interest, principal) = LoanAmortization.PeriodSplit(1000m, 88.85m, 12m, 12);
        Assert.Equal(10.00m, interest);          // 1000 * 1%/mo
        Assert.Equal(78.85m, principal);
        Assert.Equal(88.85m, interest + principal);
    }

    [Fact]
    public void Real_scale_mortgage_payment_is_in_range()
    {
        // 30-year, $500k, 4.00% → ~$2,387 principal+interest.
        var payment = LoanAmortization.PeriodicPayment(500000m, 4.00m, 360, 12);
        Assert.InRange(payment, 2380m, 2395m);
    }

    [Fact]
    public void Invalid_terms_return_zero()
    {
        Assert.Equal(0m, LoanAmortization.PeriodicPayment(0m, 3.65m, 360, 12));
        Assert.Equal(0m, LoanAmortization.PeriodicPayment(500000m, 4.00m, 0, 12));
    }
}
