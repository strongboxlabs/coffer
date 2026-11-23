namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// A fixed-rate amortizing loan in decimal, rounded the way the generated rows are.
/// </summary>
/// <remarks>
/// The balance is iterated, not taken from the closed form: each payment's interest
/// is rounded to the cent on the decimal balance before the principal is taken off,
/// exactly as the emitted rows do it, so an opening balance plus the generated
/// payments lands on the figure the schedule implies. Powers are repeated
/// multiplication. Every rounding is to the cent, half away from zero.
/// </remarks>
public sealed class DemoAmortization
{
    /// <summary>A loan of <paramref name="principal"/> cents at
    /// <paramref name="annualRatePercent"/>, repaid in <paramref name="payments"/>
    /// payments, <paramref name="paymentsPerYear"/> a year.</summary>
    public DemoAmortization(long principal, decimal annualRatePercent, int payments, int paymentsPerYear)
    {
        if (principal <= 0) throw new ArgumentOutOfRangeException(nameof(principal), principal, "a loan lends something");
        if (annualRatePercent < 0) throw new ArgumentOutOfRangeException(nameof(annualRatePercent), annualRatePercent, "a rate is not negative");
        if (payments <= 0) throw new ArgumentOutOfRangeException(nameof(payments), payments, "a loan has payments");
        if (paymentsPerYear <= 0) throw new ArgumentOutOfRangeException(nameof(paymentsPerYear), paymentsPerYear, "payments fall in a year");

        Principal = principal;
        AnnualRatePercent = annualRatePercent;
        Payments = payments;
        PaymentsPerYear = paymentsPerYear;
        PeriodRate = annualRatePercent / 100m / paymentsPerYear;
        Payment = ComputePayment();
    }

    /// <summary>The amount lent, in cents.</summary>
    public long Principal { get; }

    /// <summary>The annual rate as a percent, as Moneydance stores it.</summary>
    public decimal AnnualRatePercent { get; }

    /// <summary>The number of payments.</summary>
    public int Payments { get; }

    /// <summary>Payments per year.</summary>
    public int PaymentsPerYear { get; }

    /// <summary>The rate per payment period.</summary>
    public decimal PeriodRate { get; }

    /// <summary>The level payment, in cents.</summary>
    public long Payment { get; }

    /// <summary>Interest on <paramref name="balance"/> cents for one period, in cents.</summary>
    public long InterestOn(long balance) => RoundCents(balance * PeriodRate);

    /// <summary>The principal still owed after <paramref name="paymentsMade"/>
    /// payments, in cents; never below zero.</summary>
    public long BalanceAfter(int paymentsMade)
    {
        if (paymentsMade < 0) throw new ArgumentOutOfRangeException(nameof(paymentsMade), paymentsMade, "payments made are not negative");
        var owed = Principal;
        for (var i = 0; i < paymentsMade && owed > 0; i++)
        {
            owed -= Payment - InterestOn(owed);
        }
        return owed < 0 ? 0 : owed;
    }

    /// <summary>A decimal amount of cents rounded to a whole cent, half away from zero.</summary>
    public static long RoundCents(decimal cents) =>
        decimal.ToInt64(decimal.Round(cents, 0, MidpointRounding.AwayFromZero));

    private long ComputePayment()
    {
        if (PeriodRate == 0) return RoundCents((decimal)Principal / Payments);
        var growth = 1m;
        for (var i = 0; i < Payments; i++) growth *= 1m + PeriodRate;
        // P * r / (1 - (1 + r)^-n), written with the positive power.
        return RoundCents(Principal * PeriodRate * growth / (growth - 1m));
    }
}
