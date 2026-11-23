using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Presentation;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The transaction listing with no account, on the stress ledger: it answers over MCP,
/// and its lines are the view's money-side rows.
/// </summary>
/// <remarks>
/// <para>A listing with no account reads the most of the memo-and-tags rule: each line
/// picks the leg it is presented from, and a text search or a tag filter reads the rule
/// over the whole money side. Small fixtures never make the statement expensive enough
/// for its cost to show, so this runs at the stress lane's default scale, through the
/// production path: <c>/mcp</c> with a connected-apps bearer, the role the API runs as,
/// and the command timeout it has. A call that fails answers 200 with a tool error,
/// which <see cref="AnnotationApiTiming.McpLineCount"/> turns into a failure.</para>
/// <para>The presentation changes which notes a line shows, never which lines there are
/// or their order: the lines are the view's rows of the ledger's real accounts that are
/// neither hidden nor merged away, once each, in the listing's order.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NoAccountListingStressTests : DatabaseTest
{
    private const int Page = 500;   // the listing's largest page

    public NoAccountListingStressTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task The_no_account_listing_answers_and_lists_every_money_side_row_once()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Default);
        var ledgerId = ledger.LedgerId;

        await using var factory = new ApiFactory(Db).WithoutDevAuth().WithMcpEnabled();
        using var mcp = await StressMcpClient.CreateAsync(Db, factory, ledger, "no-account-listing");

        // Unfiltered, by text and by tag: the three calls that read the rule over the
        // whole money side.
        foreach (var (name, arguments) in new (string, object)[]
        {
            ("all", new { ledgerId, limit = 200 }),
            ("text", new { ledgerId, text = "note-17", limit = 200 }),
            ("tag", new { ledgerId, tagId = StressLedger.TagId(ledgerId, 7), limit = 200 }),
        })
        {
            using var request = StressMcpClient.ToolCall("list_transactions", arguments);
            using var response = await mcp.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode}");
            var lines = AnnotationApiTiming.McpLineCount(await response.Content.ReadAsStringAsync());
            Assert.True(lines > 0, $"list_transactions {name} returned no lines");
        }

        await using var db = Db.NewDbContext();
        var viewRows =
            from r in db.ResolvedTransactions.AsNoTracking()
            join a in db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == ledgerId && !r.IsHidden && r.IsMergedInto == null && a.AccountType != "category"
            select r;
        var listed =
            from l in StoredNotesQuery.ListingLegs(db, ListingScope.Ledger)
            join a in db.Accounts.AsNoTracking() on l.Row.AccountId equals a.Id
            where a.LedgerId == ledgerId && !l.Row.IsHidden && l.Row.IsMergedInto == null && a.AccountType != "category"
            select l.Row.Id;

        // Every listed line is a view row, so as many lines as rows, each once, is the
        // same set.
        var rows = await viewRows.CountAsync();
        Assert.True(rows > Page, $"the seed has {rows} money-side rows; the comparison needs more than a page");
        Assert.Equal(rows, await listed.CountAsync());
        Assert.Equal(rows, await listed.Distinct().CountAsync());

        // The first page in the listing's order, against the view's rows in that order.
        var page = await new ReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .ListTransactionsAsync(new TransactionQuery { LedgerId = ledgerId, Limit = Page });
        var expected = await viewRows
            .OrderByDescending(r => r.PostedAt).ThenByDescending(r => r.HeaderSeq)
            .ThenByDescending(r => r.LegIndex).ThenByDescending(r => r.Id)
            .Take(Page)
            .Select(r => new { r.HeaderId, r.AccountId, r.Amount, r.PostedAt })
            .ToListAsync();
        Assert.Equal(
            expected.Select(e => (e.HeaderId, e.AccountId, e.Amount, e.PostedAt)),
            page.Lines.Select(l => (l.HeaderId, l.AccountId, l.Amount, l.PostedAt)));
    }
}
