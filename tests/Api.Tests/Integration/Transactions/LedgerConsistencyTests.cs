using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every derived projection is checkable without writing to it.
/// </summary>
/// <remarks>
/// Balances are one projection of several. Four interceptors
/// maintain denormalised state — balances, posting counts, holdings/lots/cost
/// basis/realized gains, and trade-derived prices — and a write that bypasses the
/// ChangeTracker skips all of them. A scrub that bypasses it does exactly that: it recomputes
/// the FIFO side correctly and never touches balances, and the register stays wrong
/// for as long as nothing asks.
/// <para>
/// Each test corrupts one projection the way an out-of-band write would, then
/// asserts the report NAMES it and leaves it alone.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerConsistencyTests : DatabaseTest
{
    public LedgerConsistencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private async Task<SyntheticLedger> InvestedLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        // A fetched close whose currency a Yahoo read named, through the confirmation the
        // quote refresh runs (ADR-0114 D5, amendment), so the quote_currency projection has
        // a security to check. On a day with no trade, so no trade price is in question.
        await ledger.AddSecurityPriceAsync(sec, 101m, Utc(2026, 5, 11), PriceSource.Fetch);
        await using (var db = Db.NewDbContext())
        {
            await Coffer.Api.Quotes.QuoteCurrencyConfirmation.ConfirmAsync(
                db, ledger.LedgerId, [new Coffer.Api.Quotes.QuoteCurrency(sec, "USD")],
                Utc(2026, 5, 12), CancellationToken.None);
        }
        return ledger;
    }

    private LedgerConsistencyRepository RepoFor(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

    [Fact]
    public async Task A_consistent_ledger_reports_healthy_across_every_projection()
    {
        var ledger = await InvestedLedgerAsync();

        await using var db = Db.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.True(report.Healthy,
            "unhealthy: " + string.Join(", ", report.Projections
                .Where(p => !p.Healthy)
                .Select(p => p.Projection + "=" + p.MismatchedCount)));

        // Sound positions are not oversold (FU-097).
        Assert.Empty(report.Oversold);

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
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Holdings.AsNoTracking()
                .FirstAsync(h => h.LedgerId == ledger.LedgerId && h.Quantity != 0m);
            corrupted = row.CostBasis + 500m;
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = {corrupted} WHERE id = {row.Id};");
        }

        await using var db = Db.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.False(report.Healthy);
        var holdings = Assert.Single(report.Projections, p => p.Projection == "holdings");
        Assert.Contains(holdings.Mismatches, m => m.Field == "cost_basis" && m.Stored == corrupted
                                                  && m.Reason == ConsistencyReasons.ValueDiffers);

        // Read-only: the corruption is still there for a human to look at.
        await using var after = Db.NewDbContext();
        Assert.Contains(await after.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId)
            .Select(h => h.CostBasis)
            .ToListAsync(), v => v == corrupted);
    }

    [Fact]
    public async Task Corrupted_posting_counts_are_named()
    {
        var ledger = await InvestedLedgerAsync();

        await using (var seed = Db.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_legs SET header_total_postings = header_total_postings + 7
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = Db.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        Assert.False(report.Healthy);
        var counts = Assert.Single(report.Projections, p => p.Projection == "posting_counts");
        Assert.True(counts.MismatchedCount > 0, "posting-count drift went unreported");
    }

    [Fact]
    public async Task Corrupted_realized_gains_are_named()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Through the API, so lots and realized_gains are produced by the real
        // write path rather than seeded into a shape the walk would not agree with.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

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

        await using (var seed = Db.NewDbContext())
        {
            var changed = await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE realized_gains SET realized_gain = realized_gain + 321.00
                 WHERE ledger_id = {ledger.LedgerId};");
            Assert.True(changed > 0, "no realized_gains row to corrupt — fixture is wrong");
        }

        await using var db = Db.NewDbContext();
        var report = await RepoFor(db).CheckAsync(ledger.LedgerId);

        var gains = Assert.Single(report.Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(gains.MismatchedCount > 0, "realized-gain drift went unreported");
        Assert.Contains(gains.Mismatches, m => m.Field == "realized_gain");

        // ... and repairing it makes the re-check clean. Every projection the report
        // names has a repair; this is that rule for realized gains.
        await RepoFor(db).RepairAsync(ledger.LedgerId, ConsistencyProjections.RealizedGains);

        await using var recheckDb = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sec = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

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
        // walk has something to carry forward; four close it.
        var lot = Boundary.Typical.Quantity / 4m;
        for (var i = 0; i < sells; i++)
        {
            await TradeAsync("sell", -lot, Boundary.Typical.SellPrice, Utc(2026, 6, 10 + i));
        }

        return ledger.LedgerId;
    }

    // ------------------------------------------------------------------
    // Holdings: what the writer and the walk should both hold.
    //
    // The recompute creates a position's row the first time a write touches it and
    // keeps it at quantity 0 once the position closes; the walk returns only the
    // positions holding shares. So a walked position with no stored row is drift, and
    // a stored row the walk does not return must hold quantity 0.
    // ------------------------------------------------------------------

    /// <summary>A position bought and then sold down to nothing, through the API.</summary>
    private Task<Guid> SeedClosedPositionAsync() => SeedDisposalsAsync(4);

    private sealed record StoredHolding(
        Guid Id, Guid AccountId, Guid SecurityId, decimal Quantity, decimal CostBasis);

    /// <summary>The ledger's one stored holdings row, or null when none is stored.</summary>
    private async Task<StoredHolding?> StoredHoldingAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new StoredHolding(h.Id, h.AccountId, h.SecurityId, h.Quantity, h.CostBasis))
            .SingleOrDefaultAsync();
    }

    /// <summary>
    /// Remove the ledger's one stored holdings row: what a write that skipped the
    /// recompute leaves for a position it added. Deliberate corruption for the checker,
    /// written through EF past the writers that recompute, as the balances check's
    /// missing-row tests remove a balance row.
    /// </summary>
    private async Task<StoredHolding> RemoveTheHoldingAsync(Guid ledgerId)
    {
        var row = await StoredHoldingAsync(ledgerId);
        Assert.NotNull(row);
        await using var db = Db.NewDbContext();
        var removed = await db.Holdings.Where(h => h.Id == row.Id).ExecuteDeleteAsync();
        Assert.Equal(1, removed);
        return row;
    }

    private static ProjectionConsistency HoldingsOf(LedgerConsistencyReport report) =>
        Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.Holdings);

    private static string Describe(ProjectionConsistency p) =>
        string.Join("; ", p.Mismatches.Select(
            m => $"{m.Scope}: {m.Field} stored {m.Stored} expected {m.Expected} ({m.Reason})"));

    [Fact]
    public async Task A_walked_position_with_no_stored_holding_is_named_as_missing_and_left_missing()
    {
        var ledger = await SeedOneDisposalAsync();
        await using var db = Db.NewDbContext();
        var repo = RepoFor(db);

        // Anchor: the open position checks clean while its row is stored.
        var clean = HoldingsOf(await repo.CheckAsync(ledger));
        Assert.True(clean.Healthy, "an API-written position was reported as drift: " + Describe(clean));
        var removed = await RemoveTheHoldingAsync(ledger);
        Assert.NotEqual(0m, removed.Quantity);

        var holdings = HoldingsOf(await repo.CheckAsync(ledger));
        Assert.False(holdings.Healthy, "a position with no stored holding checked healthy");
        // Still examined: the position counts though nothing is stored for it.
        Assert.Equal(clean.Checked, holdings.Checked);

        // One mismatch per figure, each said as not stored, with Stored 0 and the
        // walk's figure, which the row held before it was removed.
        Assert.Equal(2, holdings.MismatchedCount);
        Assert.All(holdings.Mismatches, m =>
        {
            Assert.Equal((removed.AccountId, removed.SecurityId), (m.AccountId, m.SecurityId));
            Assert.Equal(ConsistencyReasons.NotStored, m.Reason);
            Assert.Equal(0m, m.Stored);
        });
        Assert.Equal(removed.Quantity,
            Assert.Single(holdings.Mismatches, m => m.Field == "quantity, not stored").Expected);
        Assert.Equal(removed.CostBasis,
            Assert.Single(holdings.Mismatches, m => m.Field == "cost_basis, not stored").Expected);

        // Read-only: nothing was written back.
        Assert.Null(await StoredHoldingAsync(ledger));
    }

    [Fact]
    public async Task Repairing_holdings_writes_back_a_missing_holding_and_reports_it()
    {
        var ledger = await SeedOneDisposalAsync();
        await using var db = Db.NewDbContext();
        var repo = RepoFor(db);
        Assert.True(HoldingsOf(await repo.CheckAsync(ledger)).Healthy);
        var removed = await RemoveTheHoldingAsync(ledger);

        // The repair reports what it found: the position, as not stored.
        var repaired = (await repo.RepairAsync(ledger, ConsistencyProjections.Holdings)).Repaired;
        Assert.False(repaired.Healthy);
        Assert.Equal(2, repaired.MismatchedCount);
        Assert.All(repaired.Mismatches, m =>
        {
            Assert.Equal((removed.AccountId, removed.SecurityId), (m.AccountId, m.SecurityId));
            Assert.Equal(ConsistencyReasons.NotStored, m.Reason);
        });

        // ... and wrote the row back with the walk's figures.
        var restored = await StoredHoldingAsync(ledger);
        Assert.NotNull(restored);
        Assert.Equal(
            (removed.AccountId, removed.SecurityId, removed.Quantity, removed.CostBasis),
            (restored.AccountId, restored.SecurityId, restored.Quantity, restored.CostBasis));

        await using var recheck = Db.NewDbContext();
        var after = HoldingsOf(await RepoFor(recheck).CheckAsync(ledger));
        Assert.True(after.Healthy, "holdings still drifted after repair: " + Describe(after));
    }

    [Fact]
    public async Task A_closed_position_checks_clean_and_shares_stored_for_it_are_drift()
    {
        var ledger = await SeedClosedPositionAsync();
        await using var db = Db.NewDbContext();
        var repo = RepoFor(db);

        // The closed position stays stored at quantity 0, and the walk returns nothing
        // for it: a check that took the walk's silence as drift would fail here.
        var closed = await StoredHoldingAsync(ledger);
        Assert.NotNull(closed);
        Assert.Equal(0m, closed.Quantity);
        var clean = HoldingsOf(await repo.CheckAsync(ledger));
        Assert.True(clean.Healthy, "a closed position was reported as drift: " + Describe(clean));
        Assert.Equal(1, clean.Checked);

        // Shares stored for it, past the writers that recompute: the stored row still
        // holds a position the transactions have sold.
        var corrupted = Boundary.Typical.Quantity;
        await using (var seed = Db.NewDbContext())
        {
            var changed = await seed.Holdings.Where(h => h.Id == closed.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Quantity, corrupted));
            Assert.Equal(1, changed);
        }

        var holdings = HoldingsOf(await repo.CheckAsync(ledger));
        var mismatch = Assert.Single(holdings.Mismatches);
        Assert.Equal((closed.AccountId, closed.SecurityId), (mismatch.AccountId, mismatch.SecurityId));
        Assert.Equal("quantity", mismatch.Field);
        Assert.Equal(corrupted, mismatch.Stored);
        Assert.Equal(0m, mismatch.Expected);
        Assert.Equal(ConsistencyReasons.ValueDiffers, mismatch.Reason);

        await repo.RepairAsync(ledger, ConsistencyProjections.Holdings);
        Assert.Equal(0m, (await StoredHoldingAsync(ledger))!.Quantity);
        await using var recheck = Db.NewDbContext();
        var after = HoldingsOf(await RepoFor(recheck).CheckAsync(ledger));
        Assert.True(after.Healthy, "holdings still drifted after repair: " + Describe(after));
    }

    /// <summary>
    /// Drift in the LONG-TERM columns is reported (mig 217).
    /// </summary>
    /// <remarks>
    /// A check that compares <c>SUM(realized_gain)</c> and nothing else, against a walk
    /// that does not return the long-term columns, reports healthy a ledger whose entire
    /// short/long tax split has been zeroed. Those columns are <c>NOT NULL DEFAULT 0</c>,
    /// which means the realistic way to destroy them is an INSERT that omits them:
    /// silent, not an error.
    /// </remarks>
    [Fact]
    public async Task Drift_in_the_long_term_split_is_named()
    {
        var ledger = await SeedOneDisposalAsync();

        await using (var seed = Db.NewDbContext())
        {
            // ONLY the long-term column. realized_gain is left correct, so the old
            // check has nothing at all to notice.
            var changed = await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE realized_gains SET realized_gain_lt = realized_gain_lt + 321.00
                 WHERE ledger_id = {ledger};");
            Assert.True(changed > 0, "no realized_gains row to corrupt — fixture is wrong");
        }

        await using var db = Db.NewDbContext();
        var gains = Assert.Single(
            (await RepoFor(db).CheckAsync(ledger)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);

        Assert.False(gains.Healthy, "long-term drift went unreported");
        Assert.Contains(gains.Mismatches, m => m.Field == "realized_gain_lt");
        // And it is not being reported as some other column having moved.
        Assert.DoesNotContain(gains.Mismatches, m => m.Field == "realized_gain");

        await RepoFor(db).RepairAsync(ledger, ConsistencyProjections.RealizedGains);

        await using var recheck = Db.NewDbContext();
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
    /// The other blind spot of a per-pair sum: a cent up on one row and a cent down on
    /// another cancel exactly, and the position reports healthy while both rows are wrong. This
    /// is not a contrived shape — a rounding defect that pushes values in whichever
    /// direction the tail falls produces it naturally, which is the class of defect mig
    /// 209 fixed.
    /// </remarks>
    [Fact]
    public async Task Offsetting_drift_on_two_disposals_does_not_cancel()
    {
        var ledger = await SeedTwoDisposalsAsync();

        await using (var seed = Db.NewDbContext())
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

        await using var db = Db.NewDbContext();
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
    /// Reproduces the state a scrub leaves: headers whose stored count said two
    /// postings while the legs said one, because a scrub collapsed them and never
    /// recomputed. The repair is targeted — only the disagreeing headers — so this
    /// also pins that a consistent header is left alone.
    /// </remarks>
    [Fact]
    public async Task Repairing_posting_counts_fixes_only_the_disagreeing_headers()
    {
        var ledger = await InvestedLedgerAsync();

        Guid corrupted;
        await using (var seed = Db.NewDbContext())
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

        await using var db = Db.NewDbContext();
        var repo = RepoFor(db);

        var found = await repo.CheckAsync(ledger.LedgerId);
        var counts = Assert.Single(found.Projections, p => p.Projection == "posting_counts");
        Assert.Equal(1, counts.MismatchedCount);

        var repaired = await repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.PostingCounts);
        Assert.Equal(1, repaired.Repaired.MismatchedCount);
        Assert.True(repaired.ProjectionAfter().Healthy);

        var after = await repo.CheckAsync(ledger.LedgerId);
        Assert.True(after.Healthy,
            "still unhealthy after repair: " + string.Join(", ", after.Projections
                .Where(p => !p.Healthy).Select(p => p.Projection)));
    }

    // ------------------------------------------------------------------
    // A repair must fix everything it found, not everything it DISPLAYED.
    //
    // ProjectionConsistency.Mismatches is capped at 100 because it is what the
    // client renders. A repair that iterated that capped list would fix a ledger
    // with more than a hundred disagreements a hundred at a time — and each pass
    // would return the repair it had just performed, showing the reader a success
    // and no indication that more clicks were needed. These tests
    // drift MORE than the cap and demand one call clears it.
    // ------------------------------------------------------------------

    /// <summary>The display cap. Mirrors the private constant in the repository.</summary>
    private const int DisplayCap = 100;

    [Fact]
    public async Task Repairing_posting_counts_past_the_display_cap_takes_one_call()
    {
        const int Headers = DisplayCap + 5;

        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        for (var i = 0; i < Headers; i++)
        {
            await ledger.AddTransactionPairAsync(
                bank.Id, groceries.Id, -25m, Utc(2026, 5, 10).AddMinutes(i));
        }
        await ledger.RecomputePostingCountsAsync();

        await using (var seed = Db.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_legs SET header_total_postings = header_total_postings + 1
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = Db.NewDbContext();
        var repo = RepoFor(db);

        var found = Assert.Single(
            (await repo.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.PostingCounts);

        // The report is honest about the total while showing only a page of it —
        // and the gap between these two numbers is what a capped repair would lose.
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

        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw seeds bypass the interceptor, so each day is a trade with no price.
        for (var i = 0; i < Days; i++)
        {
            await ledger.AddInvestmentBuyAsync(
                brokerage.Id, holdings, security, 1m, 100m + i, Utc(2026, 1, 1).AddDays(i));
        }

        await using var db = Db.NewDbContext();
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

        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        for (var i = 0; i < Positions; i++)
        {
            var sec = await ledger.AddSecurityAsync($"Fund {i}", $"F{i}");
            await ledger.AddBoundaryPositionAsync(
                brokerage.Id, holdings, sec, Boundary.Typical, Utc(2026, 1, 10));
        }

        await using (var seed = Db.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = cost_basis + 500
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = Db.NewDbContext();
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

        await using var db = Db.NewDbContext();
        var repaired = await RepoFor(db).RepairAsync(ledger.LedgerId, ConsistencyProjections.PostingCounts);

        Assert.True(repaired.Repaired.Healthy);
        Assert.Equal(0, repaired.Repaired.MismatchedCount);
        Assert.NotNull(repaired.After);
        Assert.True(repaired.After.Healthy);
    }

    /// <summary>
    /// Repairing holdings rebuilds the disagreeing pair.
    /// </summary>
    /// <remarks>
    /// Each projection has to be corrupted on its own terms: repairing realized_gains
    /// when realized gains are healthy correctly does nothing, so a [Theory] that
    /// corrupted only holdings and asserted both repairs fixed it would be asserting
    /// something false.
    /// </remarks>
    [Fact]
    public async Task Repairing_holdings_rebuilds_the_disagreeing_pair()
    {
        var ledger = await InvestedLedgerAsync();

        await using (var seed = Db.NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE holdings SET cost_basis = cost_basis + 500
                 WHERE ledger_id = {ledger.LedgerId};");
        }

        await using var db = Db.NewDbContext();
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
        // Comparing ConsistencyProjections.All against a second hardcoded copy of
        // itself would fail when the roster changes but prove nothing about
        // repairability: adding a projection and its repair in one commit would be
        // just as red as adding it without one.
        var ledger = await InvestedLedgerAsync();

        await using var db = Db.NewDbContext();
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

        await using var db = Db.NewDbContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => RepoFor(db).RepairAsync(ledger.LedgerId, "not_a_projection"));
    }

    /// <summary>
    /// Each check whose stored rows and walk must read one state refuses to run inside a
    /// caller's READ COMMITTED transaction, where each of its reads would see what committed
    /// before it, and leaves that transaction open: the balances (the register's check),
    /// the holdings (reached alone through their repair) and the stored notes.
    /// </summary>
    [Fact]
    public async Task A_check_that_reads_one_state_refuses_a_weaker_transaction()
    {
        var ledger = await InvestedLedgerAsync();

        await using var db = Db.NewDbContext();
        await using var owner = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var repo = RepoFor(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RegisterRepository(db, TimeProvider.System).CheckBalancesAsync(ledger.LedgerId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.RepairAsync(ledger.LedgerId, ConsistencyProjections.Holdings));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.CheckStoredNotesAsync(ledger.LedgerId, CancellationToken.None));
        Assert.Same(owner, db.Database.CurrentTransaction);

        // Outside it, the same checks run and find the ledger healthy.
        await owner.RollbackAsync();
        Assert.True((await repo.CheckAsync(ledger.LedgerId)).Healthy);
    }
}
