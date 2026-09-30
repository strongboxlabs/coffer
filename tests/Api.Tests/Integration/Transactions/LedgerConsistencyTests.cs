using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every derived projection is checkable without writing to it.
/// </summary>
/// <remarks>
/// Balances were only the projection someone happened to check. Four interceptors
/// maintain denormalised state — balances, posting counts, holdings/lots/cost
/// basis/realized gains, and trade-derived prices — and a write that bypasses the
/// ChangeTracker skips all of them. A real scrub did exactly that: it recomputed
/// the FIFO side correctly and never touched balances, and the register was wrong
/// for months because nothing asked.
/// <para>
/// Each test corrupts one projection the way an out-of-band write would, then
/// asserts the report NAMES it and leaves it alone.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerConsistencyTests
{
    private readonly PostgresFixture _fixture;

    public LedgerConsistencyTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private async Task<SyntheticLedger> InvestedLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 10));

        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");
        await ledger.AddBoundaryPositionAsync(
            brokerage.Id, holdings, sec, Boundary.Typical, Utc(2026, 1, 10));
        // Raw seeds bypass the interceptors, so the posting counts would sit at the
        // column default and the trade legs would have seeded no prices — both of
        // which the check would flag, correctly. Recompute so the fixture matches
        // what a real write path leaves behind.
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeTradePricesAsync();
        return ledger;
    }

    private LedgerConsistencyRepository RepoFor(AppDbContext db) =>
        new(db, new RegisterRepository(db), new HoldingsRecomputeService(db));

    [Fact]
    public async Task A_consistent_ledger_reports_healthy_across_every_projection()
    {
        var ledger = await InvestedLedgerAsync();

        await using var db = _fixture.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.True(report.Healthy,
            "unhealthy: " + string.Join(", ", report.Projections
                .Where(p => !p.Healthy)
                .Select(p => p.Projection + "=" + p.MismatchedCount)));

        // Guards the opposite failure: a report that checks nothing is trivially
        // healthy and would never catch anything.
        Assert.Equal(ConsistencyProjections.All.Count, report.Projections.Count);
        Assert.All(report.Projections, p => Assert.True(p.Checked > 0,
            p.Projection + " checked nothing"));
    }

    [Fact]
    public async Task Corrupted_holdings_are_named_and_left_alone()
    {
        var ledger = await InvestedLedgerAsync();

        decimal corrupted;
        await using (var seed = _fixture.NewDbContext())
        {
            var row = await seed.Holdings.AsNoTracking()
                .FirstAsync(h => h.LedgerId == ledger.LedgerId && h.Quantity != 0m);
            corrupted = row.CostBasis + 500m;
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = {corrupted} WHERE id = {row.Id};");
        }

        await using var db = _fixture.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.False(report.Healthy);
        var holdings = Assert.Single(report.Projections, p => p.Projection == "holdings");
        Assert.Contains(holdings.Mismatches, m => m.Field == "cost_basis" && m.Stored == corrupted);

        // Read-only: the corruption is still there for a human to look at.
        await using var after = _fixture.NewDbContext();
        Assert.Contains(await after.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId)
            .Select(h => h.CostBasis)
            .ToListAsync(), v => v == corrupted);
    }

    [Fact]
    public async Task Corrupted_posting_counts_are_named()
    {
        var ledger = await InvestedLedgerAsync();

        await using (var seed = _fixture.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_legs SET header_total_postings = header_total_postings + 7
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = _fixture.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.False(report.Healthy);
        var counts = Assert.Single(report.Projections, p => p.Projection == "posting_counts");
        Assert.True(counts.MismatchedCount > 0, "posting-count drift went unreported");
    }

    [Fact]
    public async Task Corrupted_realized_gains_are_named()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Through the API, so lots and realized_gains are produced by the real
        // write path rather than seeded into a shape the walk would not agree with.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        async Task TradeAsync(string action, decimal shares, decimal price, DateTime at)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    Action = action,
                    SecurityId = sec,
                    Shares = shares,
                    Price = price,
                    PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        await TradeAsync("buy", Boundary.Typical.Quantity, Boundary.Typical.BuyPrice, Utc(2026, 1, 10));
        await TradeAsync("sell", -(Boundary.Typical.Quantity / 2m), Boundary.Typical.SellPrice, Utc(2026, 6, 10));

        await using (var seed = _fixture.NewDbContext())
        {
            var changed = await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE realized_gains SET realized_gain = realized_gain + 321.00
                 WHERE ledger_id = {ledger.LedgerId};");
            Assert.True(changed > 0, "no realized_gains row to corrupt — fixture is wrong");
        }

        await using var db = _fixture.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        var gains = Assert.Single(report.Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(gains.MismatchedCount > 0, "realized-gain drift went unreported");
        Assert.Contains(gains.Mismatches, m => m.Field == "realized_gain");

        // ... and repairing it makes the re-check clean. Every projection the report
        // names has a repair; this is that rule for realized gains.
        await RepoFor(db).RepairAsync(ledger.LedgerId, ConsistencyProjections.RealizedGains);

        await using var recheckDb = _fixture.NewDbContext();
        var after = Assert.Single(
            (await RepoFor(recheckDb).CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(after.Healthy,
            $"realized gains still has {after.MismatchedCount} mismatches after repair");
    }

    /// <summary>
    /// A ledger with one brokerage position and one disposal, written through the API
    /// so lots and realized_gains come from the real write path rather than a seeded
    /// shape the walk would disagree with.
    /// </summary>
    private async Task<Guid> SeedOneDisposalAsync() => await SeedDisposalsAsync(1);

    /// <summary>Two disposals, so a per-row check has two rows to disagree about.</summary>
    private async Task<Guid> SeedTwoDisposalsAsync() => await SeedDisposalsAsync(2);

    private async Task<Guid> SeedDisposalsAsync(int sells)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        async Task TradeAsync(string action, decimal shares, decimal price, DateTime at)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    Action = action,
                    SecurityId = sec,
                    Shares = shares,
                    Price = price,
                    PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        await TradeAsync("buy", Boundary.Typical.Quantity, Boundary.Typical.BuyPrice,
            Utc(2026, 1, 10));

        // A quarter per sell, so two disposals still leave the position open and the
        // walk has something to carry forward.
        var lot = Boundary.Typical.Quantity / 4m;
        for (var i = 0; i < sells; i++)
        {
            await TradeAsync("sell", -lot, Boundary.Typical.SellPrice, Utc(2026, 6, 10 + i));
        }

        return ledger.LedgerId;
    }

    /// <summary>
    /// Drift in the LONG-TERM columns is reported. It was invisible before mig 217.
    /// </summary>
    /// <remarks>
    /// The check compared <c>SUM(realized_gain)</c> and nothing else, and the walk it
    /// compares against did not even return the long-term columns — so a ledger whose
    /// entire short/long tax split had been zeroed reported healthy. Those columns are
    /// <c>NOT NULL DEFAULT 0</c>, which means the realistic way to destroy them is an
    /// INSERT that omits them: silent, not an error. This test fails on every commit
    /// before 217.
    /// </remarks>
    [Fact]
    public async Task Drift_in_the_long_term_split_is_named()
    {
        var ledger = await SeedOneDisposalAsync();

        await using (var seed = _fixture.NewDbContext())
        {
            // ONLY the long-term column. realized_gain is left correct, so the old
            // check has nothing at all to notice.
            var changed = await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE realized_gains SET realized_gain_lt = realized_gain_lt + 321.00
                 WHERE ledger_id = {ledger};");
            Assert.True(changed > 0, "no realized_gains row to corrupt — fixture is wrong");
        }

        await using var db = _fixture.NewDbContext();
        var gains = Assert.Single(
            (await RepoFor(db).CheckAsync(ledger)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);

        Assert.False(gains.Healthy, "long-term drift went unreported");
        Assert.Contains(gains.Mismatches, m => m.Field == "realized_gain_lt");
        // And it is not being reported as some other column having moved.
        Assert.DoesNotContain(gains.Mismatches, m => m.Field == "realized_gain");

        await RepoFor(db).RepairAsync(ledger, ConsistencyProjections.RealizedGains);

        await using var recheck = _fixture.NewDbContext();
        var after = Assert.Single(
            (await RepoFor(recheck).CheckAsync(ledger)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(after.Healthy,
            $"long-term drift survived the repair: {after.MismatchedCount} mismatches");
    }

    /// <summary>
    /// Two disposals drifting in OPPOSITE directions are both reported.
    /// </summary>
    /// <remarks>
    /// The other blind spot of a per-pair sum: +0.01 on one row and -0.01 on another
    /// cancel exactly, and the position reports healthy while both rows are wrong. This
    /// is not a contrived shape — a rounding defect that pushes values in whichever
    /// direction the tail falls produces it naturally, which is the class of defect mig
    /// 209 fixed.
    /// </remarks>
    [Fact]
    public async Task Offsetting_drift_on_two_disposals_does_not_cancel()
    {
        var ledger = await SeedTwoDisposalsAsync();

        await using (var seed = _fixture.NewDbContext())
        {
            var changed = await seed.Database.ExecuteSqlInterpolatedAsync($@"
                WITH numbered AS (
                    SELECT sell_leg_id, ROW_NUMBER() OVER (ORDER BY sell_leg_id) AS rn
                      FROM realized_gains
                     WHERE ledger_id = {ledger}
                )
                UPDATE realized_gains g
                   SET realized_gain =
                           g.realized_gain + CASE WHEN n.rn = 1 THEN 0.01 ELSE -0.01 END
                  FROM numbered n
                 WHERE g.sell_leg_id = n.sell_leg_id AND n.rn <= 2;");
            Assert.Equal(2, changed);
        }

        await using var db = _fixture.NewDbContext();
        var gains = Assert.Single(
            (await RepoFor(db).CheckAsync(ledger)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);

        Assert.False(gains.Healthy, "offsetting drift cancelled and reported healthy");
        Assert.Equal(2, gains.Mismatches.Count(m => m.Field == "realized_gain"));
    }

    /// <summary>
    /// Repairing posting counts fixes them and a re-check comes back clean.
    /// </summary>
    /// <remarks>
    /// Reproduces what production had: 17 headers whose stored count said two
    /// postings while the legs said one, because a scrub collapsed them and never
    /// recomputed. The repair is targeted — only the disagreeing headers — so this
    /// also pins that a consistent header is left alone.
    /// </remarks>
    [Fact]
    public async Task Repairing_posting_counts_fixes_only_the_disagreeing_headers()
    {
        var ledger = await InvestedLedgerAsync();

        Guid corrupted;
        await using (var seed = _fixture.NewDbContext())
        {
            corrupted = await seed.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .OrderBy(h => h.Id)
                .Select(h => h.Id)
                .FirstAsync();
            // Overstate it by one, the way a removed posting leaves it.
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_legs SET header_total_postings = header_total_postings + 1
                 WHERE header_id = {corrupted};");
        }

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        var found = await repo.CheckAsync(ledger.LedgerId);
        var counts = Assert.Single(found.Projections, p => p.Projection == "posting_counts");
        Assert.Equal(1, counts.MismatchedCount);

        var repaired = await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.PostingCounts);
        Assert.Equal(1, repaired.MismatchedCount);

        var after = await repo.CheckAsync(ledger.LedgerId);
        Assert.True(after.Healthy,
            "still unhealthy after repair: " + string.Join(", ", after.Projections
                .Where(p => !p.Healthy).Select(p => p.Projection)));
    }

    // ------------------------------------------------------------------
    // A repair must fix everything it found, not everything it DISPLAYED.
    //
    // ProjectionConsistency.Mismatches is capped at 100 because it is what the
    // client renders. Every repair used to iterate that capped list, so a ledger
    // with more than a hundred disagreements was repaired a hundred at a time —
    // and each pass returned the repair it had just performed, so the reader was
    // shown a success and no indication that more clicks were needed. These tests
    // drift MORE than the cap and demand one call clears it.
    // ------------------------------------------------------------------

    /// <summary>The display cap. Mirrors the private constant in the repository.</summary>
    private const int DisplayCap = 100;

    [Fact]
    public async Task Repairing_posting_counts_past_the_display_cap_takes_one_call()
    {
        const int Headers = DisplayCap + 5;

        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        for (var i = 0; i < Headers; i++)
        {
            await ledger.AddTransactionPairAsync(
                bank.Id, groceries.Id, -25m, Utc(2026, 5, 10).AddMinutes(i));
        }
        await ledger.RecomputePostingCountsAsync();

        await using (var seed = _fixture.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_legs SET header_total_postings = header_total_postings + 1
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        var found = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.PostingCounts);

        // The report is honest about the total while showing only a page of it —
        // and the gap between these two numbers is what the repair used to lose.
        Assert.True(found.MismatchedCount > DisplayCap,
            $"fixture drifted only {found.MismatchedCount}; it must exceed the cap to prove anything");
        Assert.Equal(DisplayCap, found.Mismatches.Count);

        await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.PostingCounts);

        var after = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.PostingCounts);
        Assert.True(after.Healthy,
            $"{after.MismatchedCount} of {found.MismatchedCount} survived ONE repair — "
            + "the repair is reading the truncated display list");
    }

    [Fact]
    public async Task Repairing_trade_prices_past_the_display_cap_takes_one_call()
    {
        const int Days = DisplayCap + 5;

        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw seeds bypass the interceptor, so each day is a trade with no price.
        for (var i = 0; i < Days; i++)
        {
            await ledger.AddInvestmentBuyAsync(
                brokerage.Id, holdings, security, 1m, 100m + i, Utc(2026, 1, 1).AddDays(i));
        }

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        var found = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.TradePrices);
        Assert.True(found.MismatchedCount > DisplayCap,
            $"fixture drifted only {found.MismatchedCount}; it must exceed the cap to prove anything");
        Assert.Equal(DisplayCap, found.Mismatches.Count);

        await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices);

        var after = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.TradePrices);
        Assert.True(after.Healthy,
            $"{after.MismatchedCount} of {found.MismatchedCount} survived ONE repair — "
            + "the repair is reading the truncated display list");
    }

    [Fact]
    public async Task Repairing_holdings_past_the_display_cap_takes_one_call()
    {
        const int Positions = DisplayCap + 5;

        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        for (var i = 0; i < Positions; i++)
        {
            var sec = await ledger.AddSecurityAsync($"Fund {i}", $"F{i}");
            await ledger.AddBoundaryPositionAsync(
                brokerage.Id, holdings, sec, Boundary.Typical, Utc(2026, 1, 10));
        }

        await using (var seed = _fixture.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = cost_basis + 500
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        var found = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.Holdings);
        Assert.True(found.MismatchedCount > DisplayCap,
            $"fixture drifted only {found.MismatchedCount}; it must exceed the cap to prove anything");
        Assert.Equal(DisplayCap, found.Mismatches.Count);

        await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.Holdings);

        var after = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.Holdings);
        Assert.True(after.Healthy,
            $"{after.MismatchedCount} of {found.MismatchedCount} survived ONE repair — "
            + "the repair is reading the truncated display list");
    }

    [Fact]
    public async Task Repair_is_a_no_op_when_nothing_disagrees()
    {
        var ledger = await InvestedLedgerAsync();

        await using var db = _fixture.NewDbContext();
        var repaired = await RepoFor(db).RepairAsync(ledger.LedgerId, ConsistencyProjections.PostingCounts);

        Assert.True(repaired.Healthy);
        Assert.Equal(0, repaired.MismatchedCount);
    }

    /// <summary>
    /// Repairing holdings rebuilds the disagreeing pair.
    /// </summary>
    /// <remarks>
    /// A first version of this was a [Theory] over holdings AND realized_gains that
    /// corrupted only holdings and asserted both repairs fixed it. Repairing
    /// realized_gains when realized gains are healthy correctly does nothing, so the
    /// assertion was false rather than the code being wrong — each projection has to
    /// be corrupted on its own terms.
    /// </remarks>
    [Fact]
    public async Task Repairing_holdings_rebuilds_the_disagreeing_pair()
    {
        var ledger = await InvestedLedgerAsync();

        await using (var seed = _fixture.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = cost_basis + 500
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        Assert.False((await repo.CheckAsync(ledger.LedgerId)).Healthy);
        await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.Holdings);

        var after = await repo.CheckAsync(ledger.LedgerId);
        var holdings = Assert.Single(after.Projections,
            p => p.Projection == ConsistencyProjections.Holdings);
        Assert.True(holdings.Healthy,
            $"holdings still has {holdings.MismatchedCount} mismatches after repair");
    }

    [Fact]
    public async Task Every_named_projection_is_repairable()
    {
        // The rule this file exists to enforce: nothing is reported that cannot be
        // fixed. A projection added to the report without a branch in the repair
        // dispatch throws ArgumentOutOfRangeException, and this fails until it is
        // added.
        //
        // This used to compare ConsistencyProjections.All against a second hardcoded
        // copy of itself, which fails when the roster changes but proves nothing
        // about repairability — the thing the comment claimed it enforced. Adding a
        // projection and its repair in one commit would have been just as red as
        // adding it without one.
        var ledger = await InvestedLedgerAsync();

        await using var db = _fixture.NewDbContext();
        var repo = RepoFor(db);

        Assert.NotEmpty(ConsistencyProjections.All);
        foreach (var projection in ConsistencyProjections.All)
        {
            var result = await repo.RepairAsync(ledger.LedgerId, projection);
            Assert.Equal(projection, result.Projection);
        }
    }

    [Fact]
    public async Task An_unknown_projection_is_refused()
    {
        // The other half of the guard above: RepairAsync must reject a name it does
        // not dispatch rather than silently reporting success, or the loop up there
        // would pass for a projection nobody implemented.
        var ledger = await InvestedLedgerAsync();

        await using var db = _fixture.NewDbContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => RepoFor(db).RepairAsync(ledger.LedgerId, "not_a_projection"));
    }
}
