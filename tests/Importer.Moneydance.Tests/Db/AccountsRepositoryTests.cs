using Dapper;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Db;

[Collection(DbCollection.Name)]
public sealed class AccountsRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public AccountsRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static AccountRow Bank(string externalId, string name = "test bank") =>
        new(
            Id: Guid.NewGuid(),
            LedgerId: TestLedger.Id,
            ParentId: null,
            Name: name,
            AccountType: "bank",
            CategoryKind: null,
            CurrencyCode: "USD",
            OpeningBalance: 0m,
            IsActive: true,
            ExternalId: externalId,
            IsSystem: false,
            HoldingsAccountId: null,
            Notes: null,
            AccountNumber: null,
            InstitutionName: null,
            RoutingNumber: null,
            AccountUrl: null);

    private static AccountRow Category(string externalId, string kind, string name = "test category") =>
        new(
            Id: Guid.NewGuid(),
            LedgerId: TestLedger.Id,
            ParentId: null,
            Name: name,
            AccountType: "category",
            CategoryKind: kind,
            CurrencyCode: "USD",
            OpeningBalance: 0m,
            IsActive: true,
            ExternalId: externalId,
            IsSystem: false,
            HoldingsAccountId: null,
            Notes: null,
            AccountNumber: null,
            InstitutionName: null,
            RoutingNumber: null,
            AccountUrl: null);

    private static AccountRow Brokerage(string externalId, string name = "Brokerage A") =>
        new(
            Id: Guid.NewGuid(),
            LedgerId: TestLedger.Id,
            ParentId: null,
            Name: name,
            AccountType: "investment",
            CategoryKind: null,
            CurrencyCode: "USD",
            OpeningBalance: 0m,
            IsActive: true,
            ExternalId: externalId,
            IsSystem: false,
            HoldingsAccountId: null,
            Notes: null,
            AccountNumber: null,
            InstitutionName: null,
            RoutingNumber: null,
            AccountUrl: null);

    [Fact]
    public async Task Upsert_round_trips_account_metadata()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        // Mig 106 collapsed is_hidden into is_active — MD's `hide`
        // and `is_inactive` both land as IsActive=false.
        var row = Bank("md-bank-meta", "Northwind Checking") with
        {
            IsActive         = false,
            Notes            = "household joint account",
            AccountNumber    = "123456789",
            InstitutionName  = "Northwind Bank, N.A.",
            RoutingNumber    = "031176110",
            AccountUrl       = "https://northwind.example",
        };

        await repo.UpsertByExternalIdAsync(row);
        var roundTrip = await repo.GetByExternalIdAsync(TestLedger.Id,"md-bank-meta");

        Assert.NotNull(roundTrip);
        Assert.False(roundTrip!.IsActive);
        Assert.Equal("household joint account",  roundTrip.Notes);
        Assert.Equal("123456789",                roundTrip.AccountNumber);
        Assert.Equal("Northwind Bank, N.A.",        roundTrip.InstitutionName);
        Assert.Equal("031176110",                roundTrip.RoutingNumber);
        Assert.Equal("https://northwind.example",   roundTrip.AccountUrl);
    }

    [Fact]
    public async Task Upsert_inserts_a_bank_account()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var row = Bank("md-bank-1", "Northwind Checking") with { OpeningBalance = 8503.17m };

        var id = await repo.UpsertByExternalIdAsync(row);

        Assert.Equal(row.Id, id);
        var roundTrip = await repo.GetByExternalIdAsync(TestLedger.Id,"md-bank-1");
        Assert.NotNull(roundTrip);
        Assert.Equal("bank", roundTrip!.AccountType);
        Assert.Null(roundTrip.CategoryKind);
        Assert.Equal(8503.17m, roundTrip.OpeningBalance);
        Assert.False(roundTrip.IsSystem);
        Assert.Null(roundTrip.HoldingsAccountId);
    }

    [Fact]
    public async Task Upsert_persists_category_with_kind()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var row = Category("md-cat-1", "expense", "Groceries");

        await repo.UpsertByExternalIdAsync(row);

        var roundTrip = await repo.GetByExternalIdAsync(TestLedger.Id,"md-cat-1");
        Assert.NotNull(roundTrip);
        Assert.Equal("category", roundTrip!.AccountType);
        Assert.Equal("expense",  roundTrip.CategoryKind);
    }

    [Fact]
    public async Task UpdateParent_links_a_child_category_to_its_parent()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var parentId = await repo.UpsertByExternalIdAsync(Category("md-parent", "expense", "Bills"));
        await repo.UpsertByExternalIdAsync(Category("md-child",  "expense", "Electric"));

        var rowsAffected = await repo.UpdateParentByExternalIdAsync(TestLedger.Id,"md-child", parentId);

        Assert.Equal(1, rowsAffected);
        var child = await repo.GetByExternalIdAsync(TestLedger.Id,"md-child");
        Assert.NotNull(child);
        Assert.Equal(parentId, child!.ParentId);
    }

    [Fact]
    public async Task UpdateParent_rejected_when_target_is_non_category()
    {
        // The accounts_parent_only_for_categories CHECK constraint fires on
        // any attempt to set parent_id on a non-category row.
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var parentBank = await repo.UpsertByExternalIdAsync(Bank("md-bank-parent"));
        await repo.UpsertByExternalIdAsync(Bank("md-bank-child"));

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            repo.UpdateParentByExternalIdAsync(TestLedger.Id,"md-bank-child", parentBank));
    }

    [Fact]
    public async Task Upsert_rejects_row_with_empty_external_id()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new AccountsRepository(connection);

        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertByExternalIdAsync(
            Bank("dummy") with { ExternalId = null }));
    }

    [Fact]
    public async Task EnsureHoldingsSibling_creates_system_account_and_wires_brokerage()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var brokerageId = await repo.UpsertByExternalIdAsync(
            Brokerage("md-broker", "Brokerage A"));

        var holdingsId = await repo.EnsureHoldingsSiblingAsync(
            brokerageId, "Brokerage A", "USD", TestLedger.Id);

        Assert.NotEqual(Guid.Empty, holdingsId);
        Assert.NotEqual(brokerageId, holdingsId);

        // Sibling exists, is system-managed, has no external_id, sits at root.
        var sibling = await connection.QuerySingleAsync<(string Name, bool IsSystem, string AccountType, Guid? ParentId, string? ExternalId)>(
            """
            SELECT name AS "Name", is_system AS "IsSystem", account_type AS "AccountType",
                   parent_id AS "ParentId", external_id AS "ExternalId"
              FROM accounts WHERE id = @holdingsId;
            """,
            new { holdingsId });
        Assert.Equal("Brokerage A Holdings", sibling.Name);
        Assert.True(sibling.IsSystem);
        Assert.Equal("investment", sibling.AccountType);
        Assert.Null(sibling.ParentId);
        Assert.Null(sibling.ExternalId);

        // Brokerage now points at the sibling via holdings_account_id.
        var wiredId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT holdings_account_id FROM accounts WHERE id = @brokerageId;",
            new { brokerageId });
        Assert.Equal(holdingsId, wiredId);
    }

    [Fact]
    public async Task EnsureHoldingsSibling_is_idempotent()
    {
        await using var connection = _fixture.OpenConnection();
        await TruncateAsync(connection);

        var repo = new AccountsRepository(connection);
        var brokerageId = await repo.UpsertByExternalIdAsync(Brokerage("md-broker"));

        var first  = await repo.EnsureHoldingsSiblingAsync(brokerageId, "Brokerage A", "USD", TestLedger.Id);
        var second = await repo.EnsureHoldingsSiblingAsync(brokerageId, "Brokerage A", "USD", TestLedger.Id);

        Assert.Equal(first, second);
        var siblingCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE is_system = TRUE;");
        Assert.Equal(1, siblingCount);
    }

    private static async Task TruncateAsync(Npgsql.NpgsqlConnection connection)
    {
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");
    }
}
