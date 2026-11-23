using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

[Collection(DbCollection.Name)]
public sealed class LedgersRepositoryTests : DatabaseTest
{
    public LedgersRepositoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Create_inserts_a_named_ledger()
    {
        await using var connection = Db.OpenConnection();
        var repo = new LedgersRepository(connection);

        var name = $"book-{Guid.NewGuid():N}";
        var created = await repo.CreateAsync(name);
        Assert.NotEqual(Guid.Empty,           created.Id);
        Assert.NotEqual(TestLedger.Id,  created.Id);
        Assert.Equal(name, created.Name);
        Assert.Equal(name, await connection.ExecuteScalarAsync<string>(
            "SELECT name FROM ledgers WHERE id = @id;", new { id = created.Id }));
    }

    [Fact]
    public async Task CreateOwned_grants_owner_to_the_supplied_user_only()
    {
        // ADR-0071 D2: an import owns its new ledger as the importing person.
        await using var connection = Db.OpenConnection();
        var repo = new LedgersRepository(connection);

        var ownerId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });

        var created = await repo.CreateOwnedAsync($"owned-{Guid.NewGuid():N}", ownerId);

        var grants = (await connection.QueryAsync<(Guid, string)>(
            "SELECT user_id, role FROM user_ledger_grants WHERE ledger_id = @id;",
            new { id = created.Id })).ToList();
        Assert.Equal([(ownerId, "owner")], grants);
    }

    [Fact]
    public async Task CreateOwned_creates_a_new_ledger_when_the_name_is_taken()
    {
        await using var connection = Db.OpenConnection();
        var repo = new LedgersRepository(connection);

        var name = $"second-{Guid.NewGuid():N}";
        var first  = await repo.CreateOwnedAsync(name, LedgerRow.SystemUserId);
        var second = await repo.CreateOwnedAsync(name, LedgerRow.SystemUserId);

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// ADR-0088: there is no implicit target ledger, so an import with no name for
    /// its new ledger is refused rather than given one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateOwned_refuses_a_blank_name(string name)
    {
        await using var connection = Db.OpenConnection();
        var repo = new LedgersRepository(connection);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.CreateOwnedAsync(name, LedgerRow.SystemUserId));
    }

    // Owner_constraint_blocks_demoting_the_last_owner_at_commit and
    // Owner_constraint_allows_swap_when_a_new_owner_is_added_in_the_same_transaction
    // (formerly here) exercised the DEFERRED constraint trigger
    // trg_user_ledger_grants_owner_present via raw SQL. Migration 087
    // dropped the trigger per ADR-0032 — when a future API endpoint
    // adds revoke / role-change support, that endpoint will own the
    // owner-count check itself and return a friendly 422 instead of
    // surfacing a Postgres exception. The invariant ("every ledger
    // has >= 1 owner") is intact; only its enforcement mechanism moves.

    [Fact]
    public async Task Two_ledgers_can_each_hold_an_account_with_the_same_external_id()
    {
        // Phase A's per-ledger unique index lets each ledger keep an
        // independent (ledger_id, external_id) namespace. Re-importing the
        // same MD export into two ledgers must not collide.
        await using var connection = Db.OpenConnection();

        var ledgersRepo = new LedgersRepository(connection);
        var first  = await ledgersRepo.CreateAsync($"book-a-{Guid.NewGuid():N}");
        var second = await ledgersRepo.CreateAsync($"book-b-{Guid.NewGuid():N}");

        var accountsRepo = new AccountsRepository(connection);
        var sharedExternalId = "md-shared-bank";
        var aId = await accountsRepo.UpsertByExternalIdAsync(MakeBank(first.Id,  sharedExternalId, "A Checking"));
        var bId = await accountsRepo.UpsertByExternalIdAsync(MakeBank(second.Id, sharedExternalId, "B Checking"));

        Assert.NotEqual(aId, bId);
        var inA = await accountsRepo.GetByExternalIdAsync(first.Id,  sharedExternalId);
        var inB = await accountsRepo.GetByExternalIdAsync(second.Id, sharedExternalId);
        Assert.NotNull(inA);
        Assert.NotNull(inB);
        Assert.Equal("A Checking", inA!.Name);
        Assert.Equal("B Checking", inB!.Name);
    }

    private static AccountRow MakeBank(Guid ledgerId, string externalId, string name) =>
        new(Id: Guid.NewGuid(), LedgerId: ledgerId,
            ParentId: null, Name: name, AccountType: "bank",
            CategoryKind: null, CurrencyCode: "USD", OpeningBalance: 0m,
            IsActive: true, ExternalId: externalId,
            IsSystem: false, HoldingsAccountId: null,
            Notes: null, AccountNumber: null,
            InstitutionName: null, RoutingNumber: null, AccountUrl: null);
}
