namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>loan_terms</c> (migration 127, ADR-0050):
/// the amortization parameters 1:1 with a loan account. The importer seeds it
/// once; Coffer owns it thereafter (D10).
/// </summary>
public sealed record LoanTermsRow(
    Guid AccountId,
    Guid LedgerId,
    decimal OriginalPrincipal,
    decimal AnnualInterestRate,   // percent, not a fraction: a five-percent rate is 5
    decimal Points,
    int PaymentCount,
    int PaymentsPerYear,
    DateOnly? FirstPaymentDate,
    decimal EscrowAmount,
    Guid? InterestAccountId,
    Guid? EscrowAccountId,
    bool PaymentIsComputed,
    decimal? FixedPayment);
