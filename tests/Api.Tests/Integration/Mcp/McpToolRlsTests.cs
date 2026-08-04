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
}
