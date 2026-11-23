using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Pipeline;
using Coffer.Importer.Moneydance.Tests.Db;
using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The generated Demo, parsed through <see cref="DemoBuild.ToExport"/>, imports its
/// accounts through the importer's own account step: every real account arrives
/// with its type, opening balance, start date, active flag, institution and masked
/// number, and the two loans with their terms.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class DemoAccountsImportTests : DatabaseTest
{
    public DemoAccountsImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record ImportedAccount(
        string ExternalId, string Name, string AccountType, decimal OpeningBalance, bool IsActive,
        string? OpenedOn, string? AccountNumber, string? InstitutionName, string? RoutingNumber, string? TaxStatus);

    [Fact]
    public async Task Generated_accounts_import_with_their_fields_and_loan_terms()
    {
        var build = DemoExportGenerator.Generate(DemoSpec.Full(new DateOnly(2026, 11, 18)));
        using var export = build.ToExport();

        await using var conn = Db.OpenConnection();
        var ledgerId = await ProvisionLedgerAsync(conn);
        var context = new ImportContext(export, ledgerId);
        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);

        var imported = (await conn.QueryAsync<ImportedAccount>(
            """
            SELECT external_id AS ExternalId, name AS Name, account_type AS AccountType,
                   opening_balance AS OpeningBalance, is_active AS IsActive,
                   opened_on::text AS OpenedOn, account_number AS AccountNumber,
                   institution_name AS InstitutionName, routing_number AS RoutingNumber,
                   tax_status AS TaxStatus
              FROM accounts
             WHERE ledger_id = @LedgerId AND account_type <> 'category' AND external_id IS NOT NULL
            """,
            new { LedgerId = ledgerId })).ToDictionary(a => a.ExternalId, StringComparer.Ordinal);

        var ids = build.Manifest.AccountIds;
        Assert.Equal(ids.Count, imported.Count);
        Assert.All(ids.Values, id => Assert.Contains(id, imported.Keys));
        Assert.All(imported.Values, a => Assert.Null(a.RoutingNumber));

        ImportedAccount Role(string role) => imported[ids[role]];

        Assert.Equal("bank", Role(DemoAccountRoles.JointChecking).AccountType);
        Assert.Equal("credit_card", Role(DemoAccountRoles.RewardsCard).AccountType);
        Assert.Equal("investment", Role(DemoAccountRoles.Brokerage).AccountType);
        Assert.Equal("asset", Role(DemoAccountRoles.MortgageEscrow).AccountType);
        Assert.Equal("liability", Role(DemoAccountRoles.PaymentPlan).AccountType);
        Assert.Equal("loan", Role(DemoAccountRoles.Mortgage).AccountType);

        var cd = Role(DemoAccountRoles.CertificateOfDeposit);
        Assert.Equal(HouseholdTerms.CertificateOfDepositOpening / 100m, cd.OpeningBalance);
        Assert.Equal("2021-11-01", cd.OpenedOn);
        Assert.True(cd.IsActive);

        Assert.False(Role(DemoAccountRoles.ClassicCard).IsActive);
        Assert.False(Role(DemoAccountRoles.OldPlanA).IsActive);
        Assert.False(Role(DemoAccountRoles.CarLoan).IsActive);
        Assert.True(Role(DemoAccountRoles.Mortgage).IsActive);
        Assert.Equal("2024-05-01", Role(DemoAccountRoles.Mortgage).OpenedOn);

        // Tax status is the importer's name-based guess (ADR-0066), and it lands: the
        // retirement plans are tax-deferred, the HSA is "other", and nothing else in
        // the household names a tax treatment: only the retirement and HSA account names
        // carry a word the guess reads.
        foreach (var role in new[]
                 {
                     DemoAccountRoles.OldPlanA, DemoAccountRoles.PlanB, DemoAccountRoles.NewPlanA, DemoAccountRoles.RolloverIra,
                 })
        {
            Assert.Equal("tax_deferred", Role(role).TaxStatus);
        }
        Assert.Equal("other", Role(DemoAccountRoles.FamilyHsa).TaxStatus);
        var guessed = new HashSet<string>(StringComparer.Ordinal)
        {
            DemoAccountRoles.OldPlanA, DemoAccountRoles.PlanB, DemoAccountRoles.NewPlanA,
            DemoAccountRoles.RolloverIra, DemoAccountRoles.FamilyHsa,
        };
        Assert.All(ids.Where(kv => !guessed.Contains(kv.Key)), kv => Assert.Null(imported[kv.Value].TaxStatus));

        var checking = Role(DemoAccountRoles.JointChecking);
        Assert.Matches(@"^\*{4}[0-9]{4}$", checking.AccountNumber!);
        Assert.False(string.IsNullOrEmpty(checking.InstitutionName));

        var loans = (await conn.QueryAsync<(string ExternalId, decimal Escrow, bool Computed, decimal? Fixed, bool HasEscrowAccount, bool HasInterest)>(
            """
            SELECT a.external_id, t.escrow_amount, t.payment_is_computed, t.fixed_payment,
                   t.escrow_account_id IS NOT NULL, t.interest_account_id IS NOT NULL
              FROM loan_terms t JOIN accounts a ON a.id = t.account_id
             WHERE t.ledger_id = @LedgerId
            """,
            new { LedgerId = ledgerId })).ToDictionary(t => t.ExternalId, StringComparer.Ordinal);

        Assert.Equal(2, loans.Count);
        var mortgage = loans[ids[DemoAccountRoles.Mortgage]];
        Assert.Equal(HouseholdTerms.MonthlyEscrow / 100m, mortgage.Escrow);
        Assert.True(mortgage.HasEscrowAccount);
        Assert.True(mortgage.Computed);
        Assert.Null(mortgage.Fixed);
        Assert.True(mortgage.HasInterest);

        var carLoan = loans[ids[DemoAccountRoles.CarLoan]];
        Assert.False(carLoan.HasEscrowAccount);
        Assert.True(carLoan.HasInterest);
    }

    private static async Task<Guid> ProvisionLedgerAsync(NpgsqlConnection conn)
    {
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM ledgers WHERE id = @Id;",
            new { Id = TestLedger.Id });
        if (existing is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO ledgers (id, name) VALUES (@Id, 'Default');",
                new { Id = TestLedger.Id });
        }
        return TestLedger.Id;
    }
}
