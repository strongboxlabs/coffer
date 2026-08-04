using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ledgers;

[Collection(ApiCollection.Name)]
public sealed class LedgersRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public LedgersRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetVisible_returns_every_ledger_the_user_has_a_grant_on()
    {
        // Synthetic ledger comes with the test user already granted owner
        // on its fresh ledger; this asserts the read path lights up.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        var rows = await repo.GetVisibleAsync(ledger.UserId);
        Assert.Contains(rows, r => r.Id == ledger.LedgerId && r.Role == "owner");
    }

    [Fact]
    public async Task GetVisible_orders_by_ledger_name_ascending()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        // Add two more ledgers with deliberately ordered names so the
        // sort is observable.
        await repo.CreateWithOwnerAsync(ledger.UserId, $"a-ledger-{Guid.NewGuid():N}");
        await repo.CreateWithOwnerAsync(ledger.UserId, $"z-ledger-{Guid.NewGuid():N}");

        var rows = await repo.GetVisibleAsync(ledger.UserId);
        var names = rows.Select(r => r.Name).ToList();
        Assert.Equal(names, names.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetVisible_does_not_return_ledgers_the_user_has_no_grant_on()
    {
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);

        // first.UserId has no grant on second.LedgerId.
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());
        var rows = await repo.GetVisibleAsync(first.UserId);

        Assert.DoesNotContain(rows, r => r.Id == second.LedgerId);
    }

    [Fact]
    public async Task GetVisibleById_returns_the_row_when_grant_exists()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        var row = await repo.GetVisibleByIdAsync(ledger.UserId, ledger.LedgerId);
        Assert.NotNull(row);
        Assert.Equal(ledger.LedgerId, row!.Id);
        Assert.Equal("owner", row.Role);
    }

    [Fact]
    public async Task GetVisibleById_returns_null_when_user_has_no_grant()
    {
        var first = await SyntheticLedger.CreateAsync(_fixture);
        var second = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        Assert.Null(await repo.GetVisibleByIdAsync(first.UserId, second.LedgerId));
    }

    [Fact]
    public async Task CreateWithOwner_inserts_ledger_and_owner_grant_atomically()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        var name = $"new-{Guid.NewGuid():N}";
        var created = await repo.CreateWithOwnerAsync(ledger.UserId, name);

        Assert.Equal(name, created.Name);
        Assert.Equal("owner", created.Role);

        // Visible immediately after the call returns.
        var visible = await repo.GetVisibleByIdAsync(ledger.UserId, created.Id);
        Assert.NotNull(visible);

        // Single owner grant present (no orphan rows from the transaction).
        await using var assertDb = _fixture.NewDbContext();
        var grantCount = await assertDb.UserLedgerGrants
            .CountAsync(g => g.LedgerId == created.Id);
        Assert.Equal(1, grantCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateWithOwner_rejects_empty_name(string name)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.CreateWithOwnerAsync(ledger.UserId, name));
    }

    [Fact]
    public async Task CreateWithOwner_rejects_unknown_user_id_via_grant_FK()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        // The grants table FKs user_id → users; a non-existent user
        // breaks the constraint at INSERT-time, rolling back the freshly
        // inserted ledger row too. EF wraps Postgres errors in
        // DbUpdateException; the InnerException is the underlying
        // PostgresException.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repo.CreateWithOwnerAsync(Guid.NewGuid(), "should-rollback"));
        Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
    }

    /// <summary>
    /// The system user sees the ledgers it owns. This used to assert against the
    /// seeded …0001 "Default" ledger; ADR-0088 / migration 186 removed that row,
    /// so it now asserts against a ledger actually granted to the system user —
    /// which is what the test was really about.
    /// </summary>
    [Fact]
    public async Task System_user_remains_visible_owner_of_ledgers_it_owns()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new LedgersRepository(db, _fixture.NewServiceFactory(), _fixture.NewLedgerKeyService());

        // SyntheticLedger.CreateAsync grants owner to BOTH its minted user and the
        // system user, so the system user genuinely owns this ledger.
        var rows = await repo.GetVisibleAsync(UserRow.SystemUserId);
        Assert.Contains(rows, r => r.Id == ledger.LedgerId && r.Role == "owner");
    }
}
