using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// <c>saved_reports_repoint_tag</c> (migration 263), called the way the tag
/// merge calls it: as <c>coffer_app</c>, with <c>app.user_id</c> set, through its EF
/// binding.
/// </summary>
/// <remarks>
/// <para><b>Why it is tested this hard.</b> It runs as its owner, past RLS, so the
/// grant check and the ledger filter inside it are the whole boundary around other
/// members' reports. Each refusal below is a way that boundary could leak: a viewer, a
/// member of another ledger, a stranger, a caller with no user at all, a tag from the
/// wrong ledger. Every refusal must change nothing.</para>
///
/// <para>The world is <see cref="SavedReportWorld"/>, which the merge's own tests
/// share.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SavedReportsRepointTagTests : DatabaseTest
{
    public SavedReportsRepointTagTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task An_editor_repoints_every_report_of_the_ledger_and_nothing_else()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        var before = await w.ReportsAsync(Db);

        var result = await RepointAsAsync(w.Editor, w.A.LedgerId, w.S, w.T);

        // Every member's, private and shared; counted in full, and the visible part is
        // the editor's own plus the shared ones.
        Assert.Equal(w.OnS.Count, result.Repointed);
        Assert.Equal(1 + w.SharedOnS.Count, result.RepointedVisible);
        w.AssertRepointedOnly(before, await w.ReportsAsync(Db));
    }

    [Fact]
    public async Task Callers_without_an_owner_or_editor_grant_on_the_ledger_are_refused_and_change_nothing()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        var before = await w.ReportsAsync(Db);

        await AssertRefusedAsync(() => RepointAsAsync(w.Viewer, w.A.LedgerId, w.S, w.T), PostgresErrorCodes.InsufficientPrivilege);
        await AssertRefusedAsync(() => RepointAsAsync(w.B.UserId, w.A.LedgerId, w.S, w.T), PostgresErrorCodes.InsufficientPrivilege);
        await AssertRefusedAsync(() => RepointAsAsync(w.Stranger, w.A.LedgerId, w.S, w.T), PostgresErrorCodes.InsufficientPrivilege);
        // No user on the connection at all.
        await AssertRefusedAsync(() => RepointWithoutUserAsync(w.A.LedgerId, w.S, w.T), PostgresErrorCodes.InsufficientPrivilege);
        // B's owner naming B's ledger, with A's tags: the grant holds, the tags do not.
        await AssertRefusedAsync(() => RepointAsAsync(w.B.UserId, w.B.LedgerId, w.S, w.T), PostgresErrorCodes.InvalidParameterValue);

        Assert.Equal(before, await w.ReportsAsync(Db));
    }

    [Fact]
    public async Task Tags_that_are_not_two_distinct_tags_of_the_ledger_are_refused_and_change_nothing()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        var before = await w.ReportsAsync(Db);

        await AssertRefusedAsync(() => RepointAsAsync(w.Editor, w.A.LedgerId, w.S, w.S), PostgresErrorCodes.InvalidParameterValue);
        await AssertRefusedAsync(() => RepointAsAsync(w.Editor, w.A.LedgerId, w.TagOfB, w.T), PostgresErrorCodes.InvalidParameterValue);
        await AssertRefusedAsync(() => RepointAsAsync(w.Editor, w.A.LedgerId, w.S, w.TagOfB), PostgresErrorCodes.InvalidParameterValue);
        await AssertRefusedAsync(() => RepointAsAsync(w.Editor, w.A.LedgerId, Guid.NewGuid(), w.T), PostgresErrorCodes.InvalidParameterValue);
        await AssertRefusedAsync(() => RepointAsAsync(w.Editor, w.A.LedgerId, w.S, Guid.NewGuid()), PostgresErrorCodes.InvalidParameterValue);

        Assert.Equal(before, await w.ReportsAsync(Db));
    }

    // Who may call it, its pinned search_path and its owner's BYPASSRLS are catalog
    // facts, held in RlsCatalogTests with the other definer.

    // ----- calls -----

    private async Task<SavedReportsRepointTagRow> RepointAsAsync(Guid userId, Guid ledgerId, Guid from, Guid to)
    {
        await using var db = Db.NewAppDbContextAsUser(userId);
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.SavedReportsRepointTag(ledgerId, from, to).SingleAsync();
        await tx.CommitAsync();
        return row;
    }

    private async Task<SavedReportsRepointTagRow> RepointWithoutUserAsync(Guid ledgerId, Guid from, Guid to)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Db.AppConnectionString).Options;
        await using var db = new AppDbContext(options);
        return await db.SavedReportsRepointTag(ledgerId, from, to).SingleAsync();
    }

    private static async Task AssertRefusedAsync(Func<Task> call, string sqlState)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(call);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.True(pg is not null, $"expected a PostgresException, got {ex}");
        Assert.Equal(sqlState, pg!.SqlState);
    }
}
