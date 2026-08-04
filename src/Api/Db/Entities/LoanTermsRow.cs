namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>loan_terms</c> (migration 127, ADR-0050):
/// amortization parameters 1:1 with a loan account. Read-only at the API layer
/// — the importer (Dapper) seeds it; the API only reads it to compute the
/// per-occurrence loan split (<see cref="Repositories.RemindersRepository"/>).
/// FKs are enforced by the DB; no EF navigations are configured because the API
/// never inserts this row (so there's no INSERT-order concern).
/// </summary>
public sealed class LoanTermsRow
{
    public Guid AccountId { get; init; }
    public Guid LedgerId { get; init; }
    public decimal OriginalPrincipal { get; init; }
    public decimal AnnualInterestRate { get; init; }   // percent, e.g. 3.65
    public decimal Points { get; init; }
    public int PaymentCount { get; init; }
    public int PaymentsPerYear { get; init; }
    public DateOnly? FirstPaymentDate { get; init; }
    public decimal EscrowAmount { get; init; }
    public Guid? InterestAccountId { get; init; }
    public Guid? EscrowAccountId { get; init; }
    public bool PaymentIsComputed { get; init; }
    public decimal? FixedPayment { get; init; }
    public DateTime CreatedAt { get; init; }
}
