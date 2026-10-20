using Dapper;
using Coffer.Importer.Moneydance.Db;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

[Collection(DbCollection.Name)]
public sealed class LedgersRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public LedgersRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetById_finds_the_fixture_seeded_ledger()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        var row = await repo.GetByIdAsync(TestLedger.Id);

        Assert.NotNull(row);
        Assert.Equal(TestLedger.Name, row!.Name);
    }

    [Fact]
    public async Task GetById_returns_null_for_unknown_id()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        var row = await repo.GetByIdAsync(Guid.NewGuid());

        Assert.Null(row);
    }

    [Fact]
    public async Task Create_inserts_a_named_ledger_and_round_trips_by_name()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetExtraLedgersAsync(connection);
        var repo = new LedgersRepository(connection);

        var name = $"book-{Guid.NewGuid():N}";
        var created = await repo.CreateAsync(name);
        Assert.NotEqual(Guid.Empty,           created.Id);
        Assert.NotEqual(TestLedger.Id,  created.Id);
        Assert.Equal(name, created.Name);

        var byName = await repo.GetByNameAsync(name);
        Assert.NotNull(byName);
        Assert.Equal(created.Id, byName!.Id);
    }

    // (ResolveOrCreate_falls_back_to_default_when_neither_id_nor_name_given was
    //  removed in ADR-0088 — the fallback it asserted is gone. The replacement,
    //  ResolveOrCreate_throws_when_no_ledger_is_specified, is below.)

    [Fact]
    public async Task ResolveOrCreate_creates_named_ledger_and_grants_owner_to_system_user()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetExtraLedgersAsync(connection);
        var repo = new LedgersRepository(connection);

        var name = $"resolve-create-{Guid.NewGuid():N}";
        var resolved = await repo.ResolveOrCreateAsync(
            explicitId: null, explicitName: name, ownerUserId: LedgerRow.SystemUserId);

        Assert.Equal(name, resolved.Name);
        var ownerRole = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT role FROM user_ledger_grants
             WHERE ledger_id = @id
               AND user_id = '00000000-0000-0000-0000-000000000001';
            """,
            new { id = resolved.Id });
        Assert.Equal("owner", ownerRole);
    }

    [Fact]
    public async Task ResolveOrCreate_grants_owner_to_the_supplied_user()
    {
        // ADR-0071 D2: a UI-driven import owns its new ledger as the importing
        // human, not the system user. Exercise the ownerUserId parameter with an
        // arbitrary user row.
        await using var connection = _fixture.OpenConnection();
        await ResetExtraLedgersAsync(connection);
        var repo = new LedgersRepository(connection);

        var ownerId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });

        var name = $"owned-{Guid.NewGuid():N}";
        var resolved = await repo.ResolveOrCreateAsync(
            explicitId: null, explicitName: name, ownerUserId: ownerId);

        var ownerRole = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT role FROM user_ledger_grants
             WHERE ledger_id = @id AND user_id = @ownerId;
            """,
            new { id = resolved.Id, ownerId });
        Assert.Equal("owner", ownerRole);
    }

    [Fact]
    public async Task ResolveOrCreate_returns_existing_ledger_when_name_already_taken()
    {
        await using var connection = _fixture.OpenConnection();
        await ResetExtraLedgersAsync(connection);
        var repo = new LedgersRepository(connection);

        var name = $"second-{Guid.NewGuid():N}";
        var first  = await repo.ResolveOrCreateAsync(
            explicitId: null, explicitName: name, ownerUserId: LedgerRow.SystemUserId);
        var second = await repo.ResolveOrCreateAsync(
            explicitId: null, explicitName: name, ownerUserId: LedgerRow.SystemUserId);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ResolveOrCreate_throws_when_explicit_id_does_not_exist()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ResolveOrCreateAsync(
                explicitId: Guid.NewGuid(), explicitName: null, ownerUserId: LedgerRow.SystemUserId));
    }

    [Fact]
    public async Task ResolveForValidation_throws_for_unknown_name_instead_of_creating()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ResolveForValidationAsync(
                explicitId: null,
                explicitName: $"never-imported-{Guid.NewGuid():N}"));
    }

    /// <summary>
    /// ADR-0088: there is no implicit target ledger. These used to fall back to the
    /// seeded …0001 "Default" row, which migration 186 removes — and silently
    /// choosing a destination for a bulk financial import would be wrong even if
    /// the row still existed. Both resolvers must refuse rather than guess.
    /// </summary>
    [Fact]
    public async Task ResolveOrCreate_throws_when_no_ledger_is_specified()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ResolveOrCreateAsync(
                explicitId: null, explicitName: null, ownerUserId: LedgerRow.SystemUserId));
        Assert.Contains("--ledger-name", ex.Message);
    }

    [Fact]
    public async Task ResolveForValidation_throws_when_no_ledger_is_specified()
    {
        await using var connection = _fixture.OpenConnection();
        var repo = new LedgersRepository(connection);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ResolveForValidationAsync(explicitId: null, explicitName: null));
        Assert.Contains("--ledger-name", ex.Message);
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
        await using var connection = _fixture.OpenConnection();
        await ResetExtraLedgersAsync(connection);
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

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

    /// <summary>
    /// Drop every ledger except the seeded default. Test ledgers are scoped
    /// per-test via random names, but we keep the population bounded so
    /// shared-fixture tests see a clean slate.
    /// </summary>
    private static async Task ResetExtraLedgersAsync(NpgsqlConnection connection)
    {
        // Ledger deletion is RESTRICTed by anchor FKs, so empty the anchors
        // (except those tied to the default ledger) before removing extras.
        await connection.ExecuteAsync(
            """
            TRUNCATE accounts, securities, txn_headers, txn_legs, tags, txn_header_tags,
                     feed_connections CASCADE;
            DELETE FROM user_ledger_grants WHERE ledger_id <> @defaultId;
            DELETE FROM ledgers           WHERE id        <> @defaultId;
            """,
            new { defaultId = TestLedger.Id });
    }
}
