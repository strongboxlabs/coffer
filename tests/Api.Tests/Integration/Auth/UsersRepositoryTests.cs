using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

[Collection(ApiCollection.Name)]
public sealed class UsersRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public UsersRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetById_finds_the_seeded_system_user()
    {
        // System user is part of the schema (migration 014/015), not
        // ledger-scoped — but every test still arranges via SyntheticLedger
        // so the per-test isolation pattern is uniform.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        var row = await repo.GetByIdAsync(UserRow.SystemUserId);
        Assert.NotNull(row);
        Assert.Equal("system", row!.DisplayName);
        Assert.Equal("system", row.Username);
        Assert.False(row.IsDisabled);
    }

    [Fact]
    public async Task GetById_returns_null_for_unknown_id()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByUsername_finds_the_per_test_user()
    {
        // The synthetic ledger already creates a fresh user; just look it up.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        var found = await repo.GetByUsernameAsync(ledger.Username);
        Assert.NotNull(found);
        Assert.Equal(ledger.UserId, found!.Id);
    }

    [Fact]
    public async Task GetByUsername_returns_null_for_unknown_username()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        Assert.Null(await repo.GetByUsernameAsync($"never-{Guid.NewGuid():N}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetByUsername_returns_null_for_empty_input(string? username)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        Assert.Null(await repo.GetByUsernameAsync(username!));
    }

    [Fact]
    public async Task Create_persists_supplied_fields()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());
        var username = $"alice-{Guid.NewGuid():N}";

        var row = await repo.CreateAsync(
            displayName: "Alice Z.", username: username, createdBy: "bootstrap-token");

        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal("Alice Z.",        row.DisplayName);
        Assert.Equal(username,           row.Username);
        Assert.Equal("bootstrap-token",  row.CreatedBy);
        Assert.False(row.IsDisabled);
        Assert.Null(row.LastOpenedLedgerId);
    }

    [Fact]
    public async Task Create_rejects_duplicate_username()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        // EF Core wraps Postgres errors in DbUpdateException; the
        // unique-constraint violation is the InnerException's
        // PostgresException, but we assert at the EF layer the repo
        // exposes (matches what callers see in production).
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            repo.CreateAsync("dup", ledger.Username, "test"));
        Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
    }

    [Fact]
    public async Task SetLastOpenedLedger_updates_users_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var repo = new UsersRepository(db, _fixture.NewServiceFactory());

        await repo.SetLastOpenedLedgerAsync(ledger.UserId, ledger.LedgerId);
        var refreshed = await repo.GetByIdAsync(ledger.UserId);
        Assert.Equal(ledger.LedgerId, refreshed!.LastOpenedLedgerId);

        // Clearing it back to null also works.
        await repo.SetLastOpenedLedgerAsync(ledger.UserId, null);
        var cleared = await repo.GetByIdAsync(ledger.UserId);
        Assert.Null(cleared!.LastOpenedLedgerId);
    }
}
