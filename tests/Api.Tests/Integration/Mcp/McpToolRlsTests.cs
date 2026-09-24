using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP read tools run under the caller's RLS scope (ADR-0063 §D2/§D4): a tool
/// is a thin wrapper over a <c>coffer_app</c> repository, so a caller-supplied
/// <c>ledgerId</c> the caller has no grant on yields EMPTY, not another user's
/// data. This closes the audit gap that the write-surface repo tests exercise the
/// SERVICE role (BYPASSRLS) — here the tools are driven through an RLS-scoped
/// context (<see cref="PostgresFixture.NewAppDbContextAsUser"/>), so the DB, not a
/// WHERE clause, is what denies the cross-ledger read.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpToolRlsTests
{
    private readonly PostgresFixture _fixture;

    public McpToolRlsTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime When = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Read_tools_are_RLS_scoped_and_deny_cross_ledger_reads()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("alice-bank");
        var aliceCat = await alice.AddCategoryAsync("alice-groceries", "expense");
        var bobBank = await bob.AddBankAccountAsync("bob-bank");
        var bobCat = await bob.AddCategoryAsync("bob-groceries", "expense");
        await alice.AddTransactionPairAsync(aliceBank.Id, aliceCat.Id, -10m, When);
        await bob.AddTransactionPairAsync(bobBank.Id, bobCat.Id, -20m, When);

        // The tool's repository, bound to coffer_app with app.user_id = alice
        // (RLS on) — exactly the posture an MCP bearer for alice runs under.
        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var repo = new ReportingRepository(aliceDb);

        // Positive control: alice reading HER ledger sees her spending, so an
        // empty cross-ledger result below is RLS denial, not a broken tool.
        var own = await ReportingTools.TransactionSummary(repo, alice.LedgerId);
        Assert.NotEmpty(own.Rows);

        // Cross-ledger: alice passing BOB's ledgerId gets nothing from either the
        // summary or the drill-down. RLS is the boundary, not the caller-supplied id.
        var crossSummary = await ReportingTools.TransactionSummary(repo, bob.LedgerId);
        Assert.Empty(crossSummary.Rows);
        Assert.Equal(0m, crossSummary.Total);

        var crossList = await ReportingTools.ListTransactions(repo, bob.LedgerId);
        Assert.Empty(crossList.Lines);
    }

    [Fact]
    public async Task Budget_progress_is_RLS_scoped_and_denies_cross_ledger_reads()
    {
        // The budget was the one subsystem with no MCP tool at all: an agent could
        // read every account and transaction and still not answer "how is the month
        // tracking". This pins the tool to the same boundary as its siblings — the
        // DB denies the cross-ledger read, not a WHERE clause the tool remembers.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("alice-bank");
        var aliceCat = await alice.AddCategoryAsync("alice-food", "expense");
        var bobBank = await bob.AddBankAccountAsync("bob-bank");
        var bobCat = await bob.AddCategoryAsync("bob-food", "expense");
        await alice.AddTransactionPairAsync(aliceBank.Id, aliceCat.Id, -40m, When);
        await bob.AddTransactionPairAsync(bobBank.Id, bobCat.Id, -90m, When);

        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var repo = new BudgetProgressRepository(new ReportingRepository(aliceDb), aliceDb);

        // Positive control FIRST, so the empty cross-ledger result below is RLS
        // denial rather than a tool that returns nothing for everyone.
        var own = await BudgetTools.BudgetProgress(repo, alice.LedgerId, "2026-05");
        Assert.Equal("2026-05", own.Month);
        var row = Assert.Single(own.Rows, r => r.CategoryId == aliceCat.Id);
        Assert.Equal(40m, row.Actual);
        Assert.Equal(40m, own.ActualTotal);
        // Nothing typed a target, so the row falls back to its rolled normal
        // (ADR-0099 D1) — and `mark` is what a caller judges against either way.
        Assert.Null(row.Target);

        // Cross-ledger: alice passing BOB's ledgerId sees none of his spending.
        var cross = await BudgetTools.BudgetProgress(repo, bob.LedgerId, "2026-05");
        Assert.DoesNotContain(cross.Rows, r => r.CategoryId == bobCat.Id);
        Assert.Equal(0m, cross.ActualTotal);
    }

    [Fact]
    public async Task Budget_progress_rejects_a_month_that_is_really_a_date()
    {
        // A MONTH, not a date. Accepting "2026-05-17" would silently land the
        // window wherever that date's month happens to be, and a model producing
        // an ISO date here is the likely case, not the exotic one.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewAppDbContextAsUser(alice.UserId);
        var repo = new BudgetProgressRepository(new ReportingRepository(db), db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => BudgetTools.BudgetProgress(repo, alice.LedgerId, "2026-05-17"));

        // …and an omitted month is the current one, not an error.
        var now = await BudgetTools.BudgetProgress(repo, alice.LedgerId);
        Assert.Equal(
            DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            now.Month);
    }
}
