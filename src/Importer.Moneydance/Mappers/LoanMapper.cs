using Coffer.Domain.Money;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Translates a Moneydance loan account's <see cref="MdLoanFields"/> into a
/// Coffer <see cref="LoanTermsRow"/> (ADR-0050). Pure logic; account references
/// are resolved to Coffer ids by the caller. Returns <c>null</c> when MD didn't
/// provide a usable amortization (so the importer simply skips seeding rather
/// than persisting a row the DB CHECK constraints would reject).
/// </summary>
/// <remarks>
/// Loan terms are checked at the loan currency's places (ADR-0114 D15). The
/// principal is minor units at the file's <c>dec</c>, which the currency check
/// already holds to the code's places, so it always fits. The escrow and the
/// fixed payment are plain decimals as Moneydance writes them, so each is checked
/// on its own: one that carries more places than the loan's currency is a
/// problem the preview refuses with (<see cref="Problem"/>), and <see cref="Map"/>
/// throws if it is reached with one. A computed payment reads no
/// <c>monthly_pmt</c>, so that figure is not checked.
/// </remarks>
public static class LoanMapper
{
    /// <param name="currency">The loan account's resolved currency
    /// (<see cref="MdCurrencies.RequireAccount"/>): its <c>dec</c> scales the
    /// principal, and its code's places bound the escrow and the fixed payment.</param>
    public static LoanTermsRow? Map(
        Guid accountId,
        Guid ledgerId,
        MdLoanFields loan,
        Guid? interestAccountId,
        Guid? escrowAccountId,
        AccountCurrency currency)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(currency);

        // Required for a usable amortization. MD stores principal in the loan
        // account's currency's minor units, at that currency's own decimal places
        // (its curr item's dec); rate is already a percent.
        if (!IsUsable(loan, out var principalMinor, out var rate, out var count, out var ppy)) return null;

        // The preview refuses a loan whose terms do not fit (MdCurrencies.Problems);
        // reaching here with one means a caller skipped that check.
        if (Problem(loan, currency) is { } problem)
            throw new InvalidDataException($"Loan account {accountId} {problem}.");

        var computed = loan.PaymentIsComputed ?? true;
        return new LoanTermsRow(
            AccountId: accountId,
            LedgerId: ledgerId,
            OriginalPrincipal: MdAmounts.ToMoney(principalMinor, currency.Decimals),
            AnnualInterestRate: rate,
            Points: loan.Points ?? 0m,
            PaymentCount: count,
            PaymentsPerYear: ppy,
            FirstPaymentDate: loan.FirstPaymentDate,
            EscrowAmount: loan.EscrowPayment ?? 0m,
            InterestAccountId: interestAccountId,
            EscrowAccountId: escrowAccountId,
            PaymentIsComputed: computed,
            FixedPayment: computed ? null : loan.MonthlyPayment);
    }

    /// <summary>
    /// Why <paramref name="loan"/>'s terms cannot be seeded in
    /// <paramref name="currency"/>, phrased to follow the account's name: its escrow
    /// payment, or its fixed payment when the payment is not computed, carries more
    /// places than Coffer keeps for the loan's currency (ADR-0114 D15). Null when they
    /// fit, and for a loan with no usable amortization, which is not seeded at all.
    /// </summary>
    public static string? Problem(MdLoanFields loan, AccountCurrency currency)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(currency);
        if (!IsUsable(loan, out _, out _, out _, out _)) return null;

        var places = CurrencyMinorUnits.PlacesOf(currency.Code);
        if (loan.EscrowPayment is { } escrow && !CurrencyMinorUnits.Fits(escrow, currency.Code))
            return TooFine("escrow payment", escrow, currency.Code, places);
        if (loan.PaymentIsComputed == false
            && loan.MonthlyPayment is { } payment
            && !CurrencyMinorUnits.Fits(payment, currency.Code))
            return TooFine("fixed payment", payment, currency.Code, places);
        return null;
    }

    private static bool IsUsable(
        MdLoanFields loan, out long principalMinor, out decimal rate, out int count, out int ppy)
    {
        principalMinor = 0;
        rate = 0m;
        count = 0;
        ppy = 0;
        if (loan.InitPrincipalMinor is not { } p || p <= 0) return false;
        if (loan.AnnualRatePercent is not { } r || r < 0) return false;
        if (loan.PaymentCount is not { } c || c <= 0) return false;
        if (loan.PaymentsPerYear is not { } y || y <= 0) return false;
        (principalMinor, rate, count, ppy) = (p, r, c, y);
        return true;
    }

    private static string TooFine(string figure, decimal value, string code, int places)
    {
        var carried = PlacesIn(value);
        return $"has a loan {figure} with {carried} decimal {(carried == 1 ? "place" : "places")}, "
            + $"and Coffer keeps {code} at {places}";
    }

    /// <summary>The decimal places <paramref name="value"/> carries, trailing zeros
    /// left out.</summary>
    private static int PlacesIn(decimal value)
    {
        var places = 0;
        while (value != Math.Round(value, places)) places++;
        return places;
    }
}
