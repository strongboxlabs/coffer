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
public static class LoanMapper
{
    public static LoanTermsRow? Map(
        Guid accountId,
        Guid ledgerId,
        MdLoanFields loan,
        Guid? interestAccountId,
        Guid? escrowAccountId)
    {
        ArgumentNullException.ThrowIfNull(loan);

        // Required for a usable amortization. MD stores principal in minor
        // units (e.g. 100000000 = $1,000,000.00); rate is already a percent.
        if (loan.InitPrincipalMinor is not { } principalMinor || principalMinor <= 0) return null;
        if (loan.AnnualRatePercent is not { } rate || rate < 0) return null;
        if (loan.PaymentCount is not { } count || count <= 0) return null;
        if (loan.PaymentsPerYear is not { } ppy || ppy <= 0) return null;

        var computed = loan.PaymentIsComputed ?? true;
        return new LoanTermsRow(
            AccountId: accountId,
            LedgerId: ledgerId,
            OriginalPrincipal: principalMinor / 100m,
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
}
