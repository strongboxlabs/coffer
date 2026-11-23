using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Tests for the account-seed behavior of
/// <see cref="AccountsRepository.UpsertWithAdoptionAsync"/> under seed-once
/// (ADR-0052 D2). The importer only ever seeds an EMPTY ledger, so the method:
///   1. Looks up the account_external_ids junction by
///      (ledger_id, source, external_id) — within a single run, the same
///      account referenced twice resolves to one row.
///   2. Inserts a fresh account + junction row when no junction match exists.
/// The pre-0052 same-name ADOPTION path (cross-source linking on re-import)
/// has been removed — a seed-only import never meets an account a different
/// source already created.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class AccountAdoptionTests : DatabaseTest
{
    public AccountAdoptionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Inserts_new_account_and_junction_row_on_first_import()
    {
        await using var conn = Db.OpenConnection();
        var repo = new AccountsRepository(conn);

        var (id, inserted) = await repo.UpsertWithAdoptionAsync(
            MakeAccount(name: "Rewards Card", externalId: "md-rewards-1"),
            source: "moneydance");
        Assert.True(inserted);

        var totalAccounts = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @L;",
            new { L = TestLedger.Id });
        var junctionRows = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM account_external_ids
            WHERE account_id = @Id AND source = 'moneydance' AND external_id = 'md-rewards-1';",
            new { Id = id });

        Assert.Equal(1, totalAccounts);
        Assert.Equal(1, junctionRows);
    }

    [Fact]
    public async Task Inserts_the_tax_status_guess_with_the_account()
    {
        // ADR-0066: the importer seeds a tax-status guess, then Coffer owns it.
        await using var conn = Db.OpenConnection();
        var repo = new AccountsRepository(conn);

        var (id, _) = await repo.UpsertWithAdoptionAsync(
            MakeAccount(name: "Retirement 401(k)", externalId: "md-401k-1") with
            {
                AccountType = "investment",
                TaxStatus = "tax_deferred",
            },
            source: "moneydance");

        var stored = await conn.ExecuteScalarAsync<string?>(
            "SELECT tax_status::text FROM accounts WHERE id = @Id;", new { Id = id });
        Assert.Equal("tax_deferred", stored);
    }

    private static AccountRow MakeAccount(string name, string externalId) =>
        new(
            Id:                Guid.NewGuid(),
            LedgerId:          TestLedger.Id,
            ParentId:          null,
            Name:              name,
            AccountType:       "credit_card",
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
}
