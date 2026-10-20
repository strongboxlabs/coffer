using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper gateway to <c>loan_terms</c>. ADR-0050 D10 (seed-only): the importer
/// seeds a loan account's amortization parameters ONCE; Coffer owns them
/// thereafter. Seed-once (ADR-0052 D2): the importer only ever runs against an
/// empty ledger, so the seed is a plain INSERT — there is no prior row to
/// conflict with.
/// </summary>
public sealed class LoanTermsRepository
{
    private readonly NpgsqlConnection _connection;

    public LoanTermsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Seed one <c>loan_terms</c> row. Seed-once (ADR-0052 D2): the importer
    /// runs only against an empty ledger, so this is a plain INSERT and always
    /// writes the row. Returns <c>true</c> (the row was inserted).
    /// </summary>
    public async Task<bool> SeedAsync(LoanTermsRow row, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO loan_terms (
                account_id, ledger_id, original_principal, annual_interest_rate,
                points, payment_count, payments_per_year, first_payment_date,
                escrow_amount, interest_account_id, escrow_account_id,
                payment_is_computed, fixed_payment)
            VALUES (
                @AccountId, @LedgerId, @OriginalPrincipal, @AnnualInterestRate,
                @Points, @PaymentCount, @PaymentsPerYear, @FirstPaymentDate,
                @EscrowAmount, @InterestAccountId, @EscrowAccountId,
                @PaymentIsComputed, @FixedPayment);
            """;
        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
