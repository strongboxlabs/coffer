using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// A tag merge repoints the saved reports that filter on the merged-away tag, every
/// member's, through the merge endpoint, and the echo counts only the ones the merging
/// user can see.
/// </summary>
/// <remarks>
/// The world is <see cref="SavedReportWorld"/>. Ledger A also carries one spending
/// transaction tagged S and one tagged T, so a report can be run before and after.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TagMergeSavedReportsTests : DatabaseTest
{
    public TagMergeSavedReportsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private const decimal OnSAmount = 80m;
    private const decimal OnTAmount = 30m;

    private async Task SeedSpendingAsync(SavedReportWorld w)
    {
        var bank = await w.A.AddBankAccountAsync("Checking");
        var groceries = await w.A.AddCategoryAsync("Groceries", "expense");
        var onS = await w.A.AddTransactionPairAsync(groceries.Id, bank.Id, OnSAmount, May);
        var onT = await w.A.AddTransactionPairAsync(groceries.Id, bank.Id, OnTAmount, May);
        await w.A.SetSideTagsAsync(onS.FromTxnId, bank.Id, ["Tag S"], groceries.Id);
        await w.A.SetSideTagsAsync(onT.FromTxnId, bank.Id, ["Tag T"], groceries.Id);
    }

    [Fact]
    public async Task An_editors_merge_repoints_every_members_reports_and_echoes_only_the_visible_ones()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        await SeedSpendingAsync(w);
        var before = await w.ReportsAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var editor = await ClientForAsync(factory, w.Editor);
        var response = await editor.PostAsJsonAsync(
            $"/api/ledgers/{w.A.LedgerId}/tags/{w.S}/merge", new MergeTagRequest(w.T));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.Content.ReadFromJsonAsync<MergeTagResponse>();
        // The editor's own report and the two shared ones; the four private reports of
        // X and Y are repointed but not counted.
        Assert.Equal(1 + w.SharedOnS.Count, echo!.SavedReportsRepointed);
        Assert.Equal(1, echo.TransactionsRepointed);
        w.AssertRepointedOnly(before, await w.ReportsAsync(Db));
    }

    [Fact]
    public async Task Each_member_runs_their_repointed_report_and_gets_the_merged_tags_rows()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        await SeedSpendingAsync(w);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using (var editor = await ClientForAsync(factory, w.Editor))
        {
            var merged = await editor.PostAsJsonAsync(
                $"/api/ledgers/{w.A.LedgerId}/tags/{w.S}/merge", new MergeTagRequest(w.T));
            Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        }

        // Every report that was on S, each read and run by its own owner.
        foreach (var reportId in w.OnS)
        {
            using var member = await ClientForAsync(factory, w.OwnerOf[reportId]);
            var report = await member.GetFromJsonAsync<JsonElement>(
                $"/api/ledgers/{w.A.LedgerId}/reports/saved/{reportId}");
            var tagId = report.GetProperty("spec").GetProperty("tagId").GetString();
            Assert.Equal(w.T.ToString(), tagId);

            var summary = await member.GetFromJsonAsync<JsonElement>(
                $"/api/ledgers/{w.A.LedgerId}/reports/summary?measure=Spending&groupBy=Category&tagId={tagId}");
            Assert.Equal(OnSAmount + OnTAmount, summary.GetProperty("total").GetDecimal());
        }
    }

    [Fact]
    public async Task A_viewers_merge_is_refused_by_the_endpoint_and_changes_nothing()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        await SeedSpendingAsync(w);
        var before = await w.ReportsAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var viewer = await ClientForAsync(factory, w.Viewer);
        var response = await viewer.PostAsJsonAsync(
            $"/api/ledgers/{w.A.LedgerId}/tags/{w.S}/merge", new MergeTagRequest(w.T));

        Assert.False(response.IsSuccessStatusCode, $"a viewer's merge returned {response.StatusCode}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await w.ReportsAsync(Db));
        await using var db = Db.NewServiceDbContext();
        Assert.True(await db.Tags.AnyAsync(t => t.Id == w.S), "the source tag is still there");
    }

    [Fact]
    public async Task A_merge_whose_source_delete_fails_rolls_the_report_repoint_back_with_it()
    {
        var w = await SavedReportWorld.BuildAsync(Db);
        await SeedSpendingAsync(w);
        var before = await w.ReportsAsync(Db);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.AppConnectionString)
            .AddInterceptors(new FixedUserConnectionInterceptor(w.Editor), new FailTagDelete())
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new TagsRepository(db).MergeAsync(w.A.LedgerId, w.S, w.T));
        }

        Assert.Equal(before, await w.ReportsAsync(Db));
        await using var check = Db.NewServiceDbContext();
        Assert.True(await check.Tags.AnyAsync(t => t.Id == w.S), "the source tag is still there");
    }

    /// <summary>Fails the statement that deletes a tag, after everything before it ran.</summary>
    private sealed class FailTagDelete : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM tags", StringComparison.Ordinal))
                throw new InvalidOperationException("the source tag's delete failed");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<HttpClient> ClientForAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();
        await using (var db = Db.NewServiceDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash, ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }
}
