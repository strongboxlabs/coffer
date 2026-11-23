using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP read tools run under the caller's RLS scope (ADR-0063 §D2/§D4): a tool
/// is a thin wrapper over a <c>coffer_app</c> repository, so a caller-supplied
/// <c>ledgerId</c> the caller has no grant on yields EMPTY, not another user's
/// data. The write-surface repo tests exercise the SERVICE role (BYPASSRLS), which
/// cannot show that — here the tools are driven through an RLS-scoped
/// context (<see cref="PostgresFixture.NewAppDbContextAsUser"/>), so the DB, not a
/// WHERE clause, is what denies the cross-ledger read.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpToolRlsTests : DatabaseTest
{
    public McpToolRlsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime When = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Read_tools_are_RLS_scoped_and_deny_cross_ledger_reads()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        var aliceBank = await alice.AddBankAccountAsync("alice-bank");
        var aliceCat = await alice.AddCategoryAsync("alice-groceries", "expense");
        var bobBank = await bob.AddBankAccountAsync("bob-bank");
        var bobCat = await bob.AddCategoryAsync("bob-groceries", "expense");
        await alice.AddTransactionPairAsync(aliceBank.Id, aliceCat.Id, -10m, When);
        await bob.AddTransactionPairAsync(bobBank.Id, bobCat.Id, -20m, When);

        // The tool's repository, bound to coffer_app with app.user_id = alice
        // (RLS on) — exactly the posture an MCP bearer for alice runs under.
        await using var aliceDb = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new ReportingRepository(aliceDb, TimeProvider.System);

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
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        var aliceBank = await alice.AddBankAccountAsync("alice-bank");
        var aliceCat = await alice.AddCategoryAsync("alice-food", "expense");
        var bobBank = await bob.AddBankAccountAsync("bob-bank");
        var bobCat = await bob.AddCategoryAsync("bob-food", "expense");
        await alice.AddTransactionPairAsync(aliceBank.Id, aliceCat.Id, -40m, When);
        await bob.AddTransactionPairAsync(bobBank.Id, bobCat.Id, -90m, When);

        await using var aliceDb = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new BudgetProgressRepository(new ReportingRepository(aliceDb, TimeProvider.System), aliceDb, TimeProvider.System);

        // Positive control FIRST, so the empty cross-ledger result below is RLS
        // denial rather than a tool that returns nothing for everyone.
        var own = await BudgetTools.BudgetProgress(repo, TimeProvider.System, alice.LedgerId, "2026-05");
        Assert.Equal("2026-05", own.Month);
        var row = Assert.Single(own.Rows, r => r.CategoryId == aliceCat.Id);
        Assert.Equal(40m, row.Actual);
        Assert.Equal(40m, own.ActualTotal);
        // Nothing typed a target, so the row falls back to its rolled normal
        // (ADR-0099 D1) — and `mark` is what a caller judges against either way.
        Assert.Null(row.Target);

        // Cross-ledger: alice passing BOB's ledgerId sees none of his spending.
        var cross = await BudgetTools.BudgetProgress(repo, TimeProvider.System, bob.LedgerId, "2026-05");
        Assert.DoesNotContain(cross.Rows, r => r.CategoryId == bobCat.Id);
        Assert.Equal(0m, cross.ActualTotal);
    }

    [Fact]
    public async Task Returns_series_is_RLS_scoped_and_denies_cross_ledger_reads()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        foreach (var (ledger, name) in new[] { (alice, "alice"), (bob, "bob") })
        {
            var bank = await ledger.AddBankAccountAsync($"{name}-bank");
            var brokerage = await ledger.AddInvestmentAccountAsync($"{name}-brokerage");
            var security = await ledger.AddSecurityAsync($"{name}-sec", $"{name[..3].ToUpperInvariant()}X");
            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, When);
            await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 10m, 100m, When);
            await ledger.AddSecurityPriceAsync(security, 110m, When.AddDays(10));
        }

        await using var aliceDb = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new InvestmentReportingRepository(aliceDb, TimeProvider.System);

        // Positive control: alice reading HER ledger gets an index, so an empty
        // cross-ledger result below is RLS denial, not a broken tool.
        var own = await InvestmentTools.ReturnsSeries(
            repo, alice.LedgerId, includeAccounts: true, interval: "month");
        Assert.Equal(2, own.Series.Count);
        Assert.Contains(own.Series[0].Points, p => p.Index is not null);

        // Cross-ledger: bob's ledger id yields no brokerage, so no account series, and
        // nothing is valued.
        var cross = await InvestmentTools.ReturnsSeries(
            repo, bob.LedgerId, includeAccounts: true, interval: "month");
        var ledgerLine = Assert.Single(cross.Series);
        Assert.All(ledgerLine.Points, p => Assert.Null(p.Index));
        Assert.Null(ledgerLine.AccountId);
    }

    [Fact]
    public async Task Returns_series_refuses_a_daily_interval_and_an_unknown_one()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new InvestmentReportingRepository(db, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(
            () => InvestmentTools.ReturnsSeries(repo, alice.LedgerId, interval: "fortnight"));
        await Assert.ThrowsAsync<Coffer.Api.Contracts.ReportRequestException>(
            () => InvestmentTools.ReturnsSeries(repo, alice.LedgerId, interval: "day"));
    }

    [Fact]
    public async Task Budget_progress_rejects_a_month_that_is_really_a_date()
    {
        // A MONTH, not a date. Accepting "2026-05-17" would silently land the
        // window wherever that date's month happens to be, and a model producing
        // an ISO date here is the likely case, not the exotic one.
        var alice = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new BudgetProgressRepository(new ReportingRepository(db, TimeProvider.System), db, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(
            () => BudgetTools.BudgetProgress(repo, TimeProvider.System, alice.LedgerId, "2026-05-17"));

        // …and an omitted month is the current one, not an error. The clock is pinned,
        // late on a month's last day: read off the wall clock this raced midnight.
        var clock = new TestClock(new DateTimeOffset(2031, 7, 31, 23, 59, 59, TimeSpan.Zero));
        var now = await BudgetTools.BudgetProgress(repo, clock, alice.LedgerId);
        Assert.Equal("2031-07", now.Month);
    }
}
