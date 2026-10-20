using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;
using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Migration 187 / ADR-0089: <c>users.username</c> carries the ICU
/// <c>username_ci</c> collation, so <c>=</c> and <c>uq_users_username</c> fold
/// case in the database rather than in application code.
/// </summary>
/// <remarks>
/// These assert against real Postgres deliberately. The whole point of putting
/// case-insensitivity in the column is that it cannot be bypassed — an in-memory
/// or mocked test would prove nothing about the actual guarantee, and a C#-side
/// <c>ToLower()</c> would have been the wrong fix (culture-dependent, and one
/// forgotten call site from breaking).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class UsernameCaseInsensitivityTests
{
    private readonly PostgresFixture _fixture;

    public UsernameCaseInsensitivityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Lookup_by_a_different_case_finds_the_row()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stored = $"Ada_{suffix}";

        await using (var seed = _fixture.NewDbContext())
        {
            seed.Users.Add(new UserRow
            {
                Id = Guid.NewGuid(),
                Username = stored,
                DisplayName = "case probe",
                CreatedBy = "integration-test",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.NewDbContext();

        // The bug this fixes: registering as `Ada` then typing `ada` at sign-in
        // returned nothing — "user not found" for an account that exists. With a
        // passkey the username is often the only thing the user types.
        foreach (var typed in new[] { stored.ToUpperInvariant(), stored.ToLowerInvariant() })
        {
            var found = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == typed);
            Assert.NotNull(found);
            // Stored form keeps the capitalisation the user chose: "store as
            // typed, compare folded".
            Assert.Equal(stored, found!.Username);
        }
    }

    [Fact]
    public async Task Two_usernames_differing_only_by_case_cannot_coexist()
    {
        var suffix = Guid.NewGuid().ToString("N");

        await using (var seed = _fixture.NewDbContext())
        {
            seed.Users.Add(new UserRow
            {
                Id = Guid.NewGuid(),
                Username = $"Dup_{suffix}",
                DisplayName = "first",
                CreatedBy = "integration-test",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.NewDbContext();
        db.Users.Add(new UserRow
        {
            Id = Guid.NewGuid(),
            Username = $"dup_{suffix}",   // same name, different case
            DisplayName = "second",
            CreatedBy = "integration-test",
        });

        // uq_users_username was rebuilt under username_ci by the ALTER, so the
        // index itself refuses the near-duplicate. Two accounts differing only by
        // case are an impersonation/confusion vector — and with email usernames
        // they are the same person as far as any mail provider is concerned.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((Npgsql.PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Folding_is_not_limited_to_ASCII()
    {
        // The reason this uses an ICU collation rather than `lower(username
        // COLLATE "C")`: C-collation folding only touches A-Z, so accented and
        // non-Latin names would silently stay case-sensitive. That matters more
        // with per-user language selection planned.
        var suffix = Guid.NewGuid().ToString("N");
        var stored = $"JOSÉ_{suffix}";

        await using (var seed = _fixture.NewDbContext())
        {
            seed.Users.Add(new UserRow
            {
                Id = Guid.NewGuid(),
                Username = stored,
                DisplayName = "unicode probe",
                CreatedBy = "integration-test",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.NewDbContext();
        var typed = $"josé_{suffix}";
        var found = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == typed);

        Assert.NotNull(found);
        Assert.Equal(stored, found!.Username);
    }
}
