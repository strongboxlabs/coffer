using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Tests for <see cref="LoanTermsRepository"/> seed behavior. Seed-once
/// (ADR-0052 D2): the importer only ever seeds an EMPTY ledger, so seeding a
/// loan account's amortization parameters is a plain INSERT.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class LoanTermsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public LoanTermsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Seeds_loan_terms()
    {
        await using var conn = _fixture.OpenConnection();
        await ResetAsync(conn);
        var accounts = new AccountsRepository(conn);
        var repo = new LoanTermsRepository(conn);

        var loanAccountId = await accounts.UpsertByExternalIdAsync(MakeLoan("md-loan"));

        var seeded = await repo.SeedAsync(Terms(loanAccountId, principal: 500000m));
        Assert.True(seeded);

        var principal = await conn.ExecuteScalarAsync<decimal>(
            "SELECT original_principal FROM loan_terms WHERE account_id = @Id;",
            new { Id = loanAccountId });
        Assert.Equal(500000m, principal);

        var rows = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM loan_terms WHERE account_id = @Id;",
            new { Id = loanAccountId });
        Assert.Equal(1, rows);
    }

    private static LoanTermsRow Terms(Guid accountId, decimal principal) =>
        new(
            AccountId:           accountId,
            LedgerId:            TestLedger.Id,
            OriginalPrincipal:   principal,
            AnnualInterestRate:  4.00m,
            Points:              0m,
            PaymentCount:        360,
            PaymentsPerYear:     12,
            FirstPaymentDate:    new DateOnly(2020, 1, 1),
            EscrowAmount:        500.00m,
            InterestAccountId:   null,
            EscrowAccountId:     null,
            PaymentIsComputed:   true,
            FixedPayment:        null);

    private static AccountRow MakeLoan(string externalId) =>
        new(
            Id:                Guid.NewGuid(),
            LedgerId:          TestLedger.Id,
            ParentId:          null,
            Name:              "Mortgage",
            AccountType:       "loan",
            CategoryKind:      null,
            CurrencyCode:      "USD",
            OpeningBalance:    0m,
            IsActive:          true,
            ExternalId:        externalId,
            IsSystem:          false,
            HoldingsAccountId: null,
            Notes:             null,
            AccountNumber:     null,
            InstitutionName:   null,
            RoutingNumber:     null,
            AccountUrl:        null);

    private static async Task ResetAsync(Npgsql.NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            TRUNCATE loan_terms, security_splits, lots, holdings, txn_legs, txn_headers,
                     security_prices, securities,
                     account_external_ids, accounts
                     RESTART IDENTITY CASCADE;");
    }
}
