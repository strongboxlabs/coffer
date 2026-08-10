using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The deployment-level admin audit (ADR-0092 D2, migration 191). Append-only, RLS
/// deny-all to <c>coffer_app</c>, and never pruned.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminAuditRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public AdminAuditRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private AdminAuditRepository NewRepo() => new(_fixture.NewServiceFactory());

    [Fact]
    public async Task Appends_an_event_with_actor_and_detail()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var id = await NewRepo().AppendAsync(
            AdminAuditActions.MasterKeyRevealed, ledger.UserId, "credential abc");

        await using var db = _fixture.NewDbContext();
        var row = await db.AdminAuditEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal("master-key.revealed", row.Action);
        Assert.Equal(ledger.UserId, row.ActorUserId);
        Assert.Equal("credential abc", row.Detail);
        Assert.NotEqual(default, row.OccurredAt);
    }

    [Fact]
    public async Task Accepts_a_null_actor()
    {
        // The adopt-on-boot path has no authenticated user to attribute.
        var id = await NewRepo().AppendAsync(
            AdminAuditActions.MasterKeyAdopted, actorUserId: null, detail: "boot-time adoption");

        await using var db = _fixture.NewDbContext();
        var row = await db.AdminAuditEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Null(row.ActorUserId);
    }

    [Fact]
    public async Task Survives_deletion_of_the_actor()
    {
        // The FK is ON DELETE SET NULL on purpose: removing a user must not erase the
        // record of what they did.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var id = await NewRepo().AppendAsync(
            AdminAuditActions.MasterKeyRotated, ledger.UserId, "'v1' -> 'v2'");

        await using (var db = _fixture.NewDbContext())
            await db.Users.Where(u => u.Id == ledger.UserId).ExecuteDeleteAsync();

        await using var check = _fixture.NewDbContext();
        var row = await check.AdminAuditEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Null(row.ActorUserId);
        Assert.Equal("'v1' -> 'v2'", row.Detail);
    }

    [Fact]
    public async Task Recent_returns_newest_first()
    {
        var repo = NewRepo();
        var first = await repo.AppendAsync(AdminAuditActions.MasterKeyRevealed, null, "older");
        await Task.Delay(5);
        var second = await repo.AppendAsync(AdminAuditActions.MasterKeyRevealed, null, "newer");

        var recent = await repo.RecentAsync(limit: 50);

        var firstIndex = recent.ToList().FindIndex(e => e.Id == first);
        var secondIndex = recent.ToList().FindIndex(e => e.Id == second);
        Assert.True(secondIndex < firstIndex,
            $"expected the newer event earlier in the list (newer={secondIndex}, older={firstIndex})");
    }

    [Fact]
    public async Task Recent_with_a_non_positive_limit_returns_nothing()
    {
        Assert.Empty(await NewRepo().RecentAsync(limit: 0));
        Assert.Empty(await NewRepo().RecentAsync(limit: -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_action(string action)
        => await Assert.ThrowsAsync<ArgumentException>(
            () => NewRepo().AppendAsync(action, null));

    [Fact]
    public async Task The_app_role_cannot_read_the_audit()
    {
        // Admin is a deployment-global capability, so RequireAdmin is the boundary and
        // the runtime role has no business reading this at all. Note the failure mode:
        // REVOKE ALL means a hard 42501 permission-denied, not an RLS-filtered empty
        // set — the grant is gone, so no policy is even consulted. Stronger than
        // filtering, and worth pinning as the actual behaviour.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await NewRepo().AppendAsync(AdminAuditActions.MasterKeyRevealed, null, "rls-probe");

        await using var appDb = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => appDb.AdminAuditEvents.AsNoTracking().ToListAsync());
        Assert.Equal("42501", ex.SqlState);
    }
}
