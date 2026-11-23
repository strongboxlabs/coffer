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
        // The textbook one-year loan: twelve monthly payments at 12%/yr (1%/mo),
        // whose published payment is known to the cent.
        Assert.Equal(88.85m, LoanAmortization.PeriodicPayment(1000m, 12m, 12, 12, "USD"));
    }

    [Fact]
    public void Periodic_payment_rounds_up_to_the_cent_servicer_convention()
    {
        // A 30-year loan at 3.5% amortizes to a payment with a sub-cent
        // remainder below half a cent — servicers (and MD) round the payment
        // UP so the loan fully retires within the term; round-to-nearest
        // would bill the cent below, a cent short every month.
        Assert.Equal(1347.14m, LoanAmortization.PeriodicPayment(300000m, 3.5m, 360, 12, "USD"));
    }

    [Fact]
    public void Zero_rate_amortizes_linearly()
    {
        Assert.Equal(100.00m, LoanAmortization.PeriodicPayment(1200m, 0m, 12, 12, "USD"));
    }

    [Fact]
    public void Period_split_is_interest_plus_principal_summing_to_payment()
    {
        var (interest, principal) = LoanAmortization.PeriodSplit(1000m, 88.85m, 12m, 12, "USD");
        Assert.Equal(10.00m, interest);          // principal × the 1%/mo rate
        Assert.Equal(78.85m, principal);
        Assert.Equal(88.85m, interest + principal);
    }

    [Fact]
    public void Real_scale_mortgage_payment_is_in_range()
    {
        // A 30-year mortgage at a realistic principal and 4% → a four-figure
        // monthly principal+interest payment, checked against a narrow range.
        var payment = LoanAmortization.PeriodicPayment(500000m, 4.00m, 360, 12, "USD");
        Assert.InRange(payment, 2380m, 2395m);
    }

    [Fact]
    public void Invalid_terms_return_zero()
    {
        Assert.Equal(0m, LoanAmortization.PeriodicPayment(0m, 3.65m, 360, 12, "USD"));
        Assert.Equal(0m, LoanAmortization.PeriodicPayment(500000m, 4.00m, 0, 12, "USD"));
    }

    // ---- each currency's own places (ADR-0114 D15) ---------------------------------------
    //
    // The textbook loan above, scaled: its exact payment carries a fraction below half of
    // one minor unit at two places, so each currency's own rounding up shows.

    [Fact]
    public void A_whole_unit_currencys_payment_rounds_up_to_the_next_whole_unit()
    {
        // The exact payment has a fractional part; a whole-unit currency has none.
        Assert.Equal(8885m, LoanAmortization.PeriodicPayment(100000m, 12m, 12, 12, "JPY"));
        Assert.Equal(100m, LoanAmortization.PeriodicPayment(1200m, 0m, 12, 12, "JPY"));
        Assert.Equal(101m, LoanAmortization.PeriodicPayment(1201m, 0m, 12, 12, "JPY"));
    }

    [Fact]
    public void A_three_place_currencys_payment_rounds_up_to_the_next_thousandth()
    {
        Assert.Equal(88.849m, LoanAmortization.PeriodicPayment(1000m, 12m, 12, 12, "KWD"));
    }

    [Fact]
    public void A_code_the_table_does_not_accept_keeps_two_places()
    {
        // A stored code from before the rule keeps working at two places.
        Assert.Equal(88.85m, LoanAmortization.PeriodicPayment(1000m, 12m, 12, 12, "XAU"));
    }

    [Fact]
    public void Interest_rounds_half_away_from_zero_at_the_loans_places()
    {
        // The balance times the periodic rate lands exactly on a half of one minor unit:
        // away from zero rounds it up where half to even would round it down.
        var (wholeInterest, wholePrincipal) = LoanAmortization.PeriodSplit(250m, 100m, 12m, 12, "JPY");
        Assert.Equal(3m, wholeInterest);
        Assert.Equal(97m, wholePrincipal);

        var (thousandthInterest, thousandthPrincipal) = LoanAmortization.PeriodSplit(0.25m, 1m, 12m, 12, "KWD");
        Assert.Equal(0.003m, thousandthInterest);
        Assert.Equal(0.997m, thousandthPrincipal);

        var (centInterest, _) = LoanAmortization.PeriodSplit(2.5m, 1m, 12m, 12, "USD");
        Assert.Equal(0.03m, centInterest);
    }
}
