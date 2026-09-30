using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The trade-derived <c>security_prices</c> projection (migration 232) — the one
/// projection written by raw SQL rather than through an EF interceptor.
/// </summary>
/// <remarks>
/// <para>
/// This check was left out of the consistency report for a long time with a stated
/// reason that was sound: a naive "did the trade seed a row?" check reports drift
/// that is not there, because the per-day source-priority rule (migration 177) makes
/// a MISSING trade price legitimate whenever a <c>fetch</c> close or a <c>manual</c>
/// gap-fill already owns that day.
/// </para>
/// <para>
/// So the false-positive cases below are not extras — they are the reason the check
/// took the shape it did, and a regression in either direction is what would make it
/// unshippable again. A checker that cries wolf trains you to ignore it, which is
/// strictly worse than not having one.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyTradePricesTests
{
    private readonly PostgresFixture _fixture;

    public ConsistencyTradePricesTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db), new HoldingsRecomputeService(db));

    private static ProjectionConsistency TradePrices(LedgerConsistencyReport report) =>
        Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.TradePrices);

    private static string Describe(ProjectionConsistency p) =>
        string.Join("; ", p.Mismatches.Select(
            m => $"{m.Scope}: {m.Field} stored {m.Stored} expected {m.Expected}"));

    /// <summary>
    /// The ordinary write path must leave the projection healthy. If this fails the
    /// check is useless no matter what else it catches: every real ledger would sit
    /// permanently red.
    /// </summary>
    [Fact]
    public async Task A_trade_written_through_the_api_leaves_the_projection_healthy()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                Action = "buy",
                SecurityId = security,
                Shares = 10m,
                Price = 137.25m,
                PostedAt = Utc(2024, 3, 15),
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));

        Assert.True(trade.Healthy, "an API-written trade was reported as drift: " + Describe(trade));
    }

    /// <summary>
    /// A write that bypasses the interceptor — a raw-SQL seed, a scrub, a hand-run
    /// fix — leaves no price row, which is exactly the silent desync this projection
    /// was added to catch.
    /// </summary>
    [Fact]
    public async Task A_trade_that_seeded_no_price_is_reported_and_repaired()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw SQL: no EF save, so TradePriceFromLegInterceptor never runs.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        Assert.False(before.Healthy);
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(security, mismatch.SecurityId);
        Assert.Contains("no price", mismatch.Field);
        Assert.Equal(0m, mismatch.Stored);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices);
        Assert.True(after.Healthy, "repair left drift behind: " + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.25m, stored.Price);
        Assert.Equal(PriceSource.Trade, stored.Source);
        Assert.Equal(new DateOnly(2024, 3, 15), stored.PriceDate);
    }

    /// <summary>
    /// A reported row must name a security the reader recognises. The first version
    /// printed a bare GUID, so deciding whether to repair meant looking up six ids by
    /// hand — a report nobody can act on is not a report.
    /// </summary>
    [Fact]
    public async Task A_reported_row_names_the_security_not_its_id()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Total Stock Market Index", "VTSAX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(trade.Mismatches);

        Assert.Contains("VTSAX", mismatch.Scope);
        Assert.Contains("Total Stock Market Index", mismatch.Scope);
        Assert.Contains("2024-03-15", mismatch.Scope);
        Assert.DoesNotContain(security.ToString(), mismatch.Scope);

        // The account as the reader knows it — the BROKERAGE, not the holdings
        // sibling the security leg actually sits on, which is a system account
        // nobody navigates to.
        Assert.Contains("Brokerage", mismatch.Scope);
        Assert.DoesNotContain("Holdings", mismatch.Scope);
        // The id follows the name: a link built from it opens the brokerage
        // register, where the trade is actually read, not the holdings sibling.
        Assert.Equal(brokerage.Id, mismatch.AccountId);
        Assert.NotEqual(holdings, mismatch.AccountId);

        // And the trade itself, so a link can target it exactly.
        Assert.NotNull(mismatch.HeaderId);
        await using var read = _fixture.NewDbContext();
        var legHeader = await read.TxnLegs.AsNoTracking()
            .Where(l => l.Id == legId).Select(l => l.HeaderId).SingleAsync();
        Assert.Equal(legHeader, mismatch.HeaderId);

        // And the field says what is wrong, not which column it lives in.
        Assert.DoesNotContain("security_prices", mismatch.Field);
        Assert.Contains("no price", mismatch.Field);
    }

    /// <summary>
    /// A stale price in a rank-COMPARABLE row is drift: migration 177's writer would
    /// have overwritten it, so its surviving with the wrong value means a write was
    /// lost.
    /// </summary>
    [Theory]
    [InlineData(PriceSource.Trade)]
    [InlineData(PriceSource.Import)]
    [InlineData(PriceSource.Simplefin)]
    public async Task A_stale_rank_comparable_price_is_reported_and_repaired(string source)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));
        await ledger.AddSecurityPriceAsync(security, 99.0000m, Utc(2024, 3, 15), source);

        await using var db = _fixture.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        Assert.False(before.Healthy);
        var mismatch = Assert.Single(before.Mismatches);
        Assert.DoesNotContain("no price", mismatch.Field);
        Assert.Equal(99m, mismatch.Stored);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices);
        Assert.True(after.Healthy, "repair left drift behind: " + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.25m, stored.Price);
    }

    /// <summary>
    /// THE case that made the naive check unshippable. A <c>fetch</c> close or a
    /// <c>manual</c> gap-fill OUTRANKS a trade, so migration 177's writer deliberately
    /// leaves it alone — a trade price missing from such a day is correct behaviour,
    /// not drift, and the two disagreeing is the design rather than a defect.
    /// </summary>
    [Theory]
    [InlineData(PriceSource.Fetch)]
    [InlineData(PriceSource.Manual)]
    public async Task A_higher_ranked_price_on_a_trade_day_is_not_drift(string source)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));
        // Deliberately far from the trade price: an intraday fill is not the close,
        // and the close is the truer figure.
        await ledger.AddSecurityPriceAsync(security, 141.80m, Utc(2024, 3, 15), source);

        await using var db = _fixture.NewDbContext();
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));

        Assert.True(
            trade.Healthy,
            $"a {source} price on a trade day was reported as drift: " + Describe(trade));
    }

    /// <summary>
    /// The day's price is the LAST trade by <c>seq</c> — not the first, and not the
    /// largest. Getting this wrong is not academic: an aggregate over a real ledger's
    /// multi-fill days produced a confident discrepancy that did not exist.
    /// </summary>
    [Fact]
    public async Task The_expected_price_is_the_last_trade_of_the_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Three fills on one day. The last by seq is neither the highest nor the
        // lowest, so max() and min() both give a different answer from the rule.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 150.00m, Utc(2024, 3, 15));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 120.00m, Utc(2024, 3, 15));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices);
        Assert.True(after.Healthy, "repair left drift behind: " + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.25m, stored.Price);
    }

    /// <summary>
    /// <c>txn_legs.unit_price</c> is NUMERIC(25,12) and <c>security_prices.price</c>
    /// is NUMERIC(19,4), so a fill priced finer than the destination column can hold
    /// must be compared at the DESTINATION scale.
    /// </summary>
    /// <remarks>
    /// Compare at the source scale and the repair cannot win: it writes the only
    /// value the column can hold, the next check re-derives the finer one, and the
    /// ledger is permanently, unfixably red. A check that disagrees with its own
    /// repair is worse than no check — so this asserts on the state AFTER a repair,
    /// which is the only place that failure shows up.
    /// </remarks>
    [Fact]
    public async Task A_fill_priced_finer_than_the_column_repairs_to_healthy()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // 6dp — two digits more than security_prices.price can keep. The 5th digit
        // is 8, so rounding is visible rather than a truncation that happens to match.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.256789m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(137.2568m, mismatch.Expected);

        var after = await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices);
        Assert.True(
            after.Healthy,
            "the repair wrote what the column can hold and the check still disagreed: "
            + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.2568m, stored.Price);
    }

    /// <summary>
    /// Writing one leg of a multi-leg day must seed the price the RULE names, not
    /// the price of the leg that happened to be written.
    /// </summary>
    /// <remarks>
    /// This is the production failure that migration 234 exists for. A 506-share
    /// execution was corrected by hand; the interceptor wrote that leg's price
    /// straight through, ignoring a 0.152-share fee fragment posted later the same
    /// day. The consistency check — correctly applying "last trade of the day" —
    /// derived the fragment's price and reported the difference as drift. Neither
    /// side was buggy on its own terms; they were running different rules, and the
    /// ledger was reported broken when it was not.
    /// </remarks>
    [Fact]
    public async Task A_write_seeds_the_price_the_rule_names_not_the_leg_written()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Two trades on one day. The LATER one by seq is the tiny fragment — the
        // shape that made the real ledger report drift.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 506m, 20.3898m, Utc(2024, 3, 15));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 0.152m, 0.9868m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();

        // Re-seed naming only the DAY, as the interceptor now does.
        await new TradePriceRecomputeService(db)
            .ReseedAsync([(ledger.LedgerId, security, new DateOnly(2024, 3, 15))]);

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(0.9868m, stored.Price);

        // And the checker agrees with what the writer wrote — which is the whole
        // point. Before 234 these two disagreed by construction.
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));
        Assert.True(trade.Healthy, "writer and checker disagree: " + Describe(trade));
    }

    /// <summary>
    /// Within one header, the largest leg by value wins — and the answer never
    /// varies between runs over identical data.
    /// </summary>
    /// <remarks>
    /// A share transfer moves each lot at its own basis, so ONE header can carry
    /// several security legs at different prices. <c>DISTINCT ON ... ORDER BY
    /// h.seq DESC</c> alone ties there and Postgres picks arbitrarily, which is
    /// how a stored row and the check came to hold two different legs of the same
    /// header and call it drift.
    /// </remarks>
    [Fact]
    public async Task A_header_with_several_priced_legs_resolves_the_same_way_every_time()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 5m, 121.9295m, Utc(2024, 3, 15));

        // Two more security legs on the SAME header, at different prices. The
        // largest by value is the middle one, so neither min nor max nor
        // insertion order gives the right answer by accident.
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                                  amount, security_id, quantity, unit_price, posting_role)
            SELECT gen_random_uuid(), l.header_id, l.ledger_id, l.account_id, 1,
                   -1100.31, l.security_id, 10.588, 103.9204, 'security'
              FROM txn_legs l WHERE l.id = {legId}
            UNION ALL
            SELECT gen_random_uuid(), l.header_id, l.ledger_id, l.account_id, 2,
                   -7.95, l.security_id, 0.078, 101.9231, 'security'
              FROM txn_legs l WHERE l.id = {legId};");

        var service = new TradePriceRecomputeService(db);
        var key = (ledger.LedgerId, security, new DateOnly(2024, 3, 15));

        await service.ReseedAsync([key]);
        var first = (await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).SingleAsync()).Price;

        // The largest leg by absolute amount: -1100.31 at 103.9204.
        Assert.Equal(103.9204m, first);

        // Stable: re-deriving over unchanged data cannot produce a different
        // answer. Without the tie-break this is exactly what varied.
        for (var i = 0; i < 3; i++)
        {
            await service.ReseedAsync([key]);
            var again = (await db.SecurityPrices.AsNoTracking()
                .Where(p => p.SecurityId == security).SingleAsync()).Price;
            Assert.Equal(first, again);
        }

        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));
        Assert.True(trade.Healthy, "checker picked a different leg than the writer: " + Describe(trade));
    }

    /// <summary>
    /// A stored trade price nothing derives any more is ADVISORY — it is not a
    /// finding, does not make the projection unhealthy, and is not repairable.
    /// </summary>
    /// <remarks>
    /// It cannot be a finding. <c>security_prices</c> records THAT a trade wrote
    /// a row and never WHICH one, so "nothing derives this" can only be inferred
    /// by re-deriving and finding nothing — which cannot tell a deleted trade
    /// (ADR-0084 D4 keeps its price on purpose) from a rule change from an edit.
    /// Reported as defects they marked a sound ledger broken: 130 rows on one
    /// install, none of them actionable from the panel.
    /// <para>
    /// Grouped per security, because that is the unit a person acts on, and
    /// ordered by what the holding is worth so what still carries money leads.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unbacked_trade_price_is_advisory_not_a_finding()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // A trade-source price on a day with no trade at all behind it.
        await ledger.AddSecurityPriceAsync(
            security, 103.9204m, Utc(2019, 11, 22), PriceSource.Trade);

        await using var db = _fixture.NewDbContext();
        var report = await Consistency(db).CheckAsync(ledger.LedgerId);

        // Not a finding: the projection is clean and the report is healthy.
        var trade = TradePrices(report);
        Assert.True(trade.Healthy, "an unbacked price was reported as drift: " + Describe(trade));
        Assert.Empty(trade.Mismatches);
        Assert.True(report.Healthy);

        // Advisory instead, naming the security rather than the date.
        var advisory = Assert.Single(report.UnbackedPrices);
        Assert.Equal(security, advisory.SecurityId);
        Assert.Equal(1, advisory.Count);
        Assert.Equal(new DateOnly(2019, 11, 22), advisory.Earliest);
        Assert.Equal(new DateOnly(2019, 11, 22), advisory.Latest);
        Assert.Contains("IDX", advisory.Security);

        // Nothing holds it, so it sorts last rather than being hidden.
        Assert.Equal(0m, advisory.HoldingValue);

        // And the repair leaves it alone — there is nothing to derive.
        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(103.9204m, stored.Price);
        Assert.Equal(PriceSource.Trade, stored.Source);
    }

    /// <summary>
    /// A price someone entered by hand is never advisory, whatever it replaced.
    /// </summary>
    /// <remarks>
    /// The whole category is scoped to <c>source = 'trade'</c>, and editing a
    /// price sets it to <c>manual</c>. So acting on an advisory clears it: that
    /// is what stops the list accumulating rows already dealt with.
    /// </remarks>
    [Fact]
    public async Task A_manual_price_is_never_advisory()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        await ledger.AddSecurityPriceAsync(
            security, 103.9204m, Utc(2019, 11, 22), PriceSource.Manual);

        await using var db = _fixture.NewDbContext();
        var report = await Consistency(db).CheckAsync(ledger.LedgerId);

        Assert.Empty(report.UnbackedPrices);
        Assert.True(report.Healthy);
    }

    /// <summary>
    /// A recurring TEMPLATE is never a live cash event (ADR-0047), so it seeds no
    /// price and must not be reported as a missing one.
    /// </summary>
    [Fact]
    public async Task A_recurring_template_is_not_expected_to_have_a_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_headers SET is_recurring_template = TRUE
            WHERE id = (SELECT header_id FROM txn_legs WHERE id = {legId});");

        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));

        Assert.True(
            trade.Healthy,
            "a recurring template was reported as a missing price: " + Describe(trade));
    }

    /// <summary>
    /// One ledger's drift must not surface in another's report. The function scopes
    /// by <c>txn_legs.ledger_id</c>, while the price join is on <c>security_id</c>
    /// alone — correct only because securities are per-ledger.
    /// </summary>
    [Fact]
    public async Task Drift_is_scoped_to_the_ledger_being_checked()
    {
        var noisy = await SyntheticLedger.CreateAsync(_fixture);
        var noisyBrokerage = await noisy.AddInvestmentAccountAsync("Brokerage");
        var noisySecurity = await noisy.AddSecurityAsync("Index Fund", "IDX");
        await noisy.AddInvestmentBuyAsync(
            noisyBrokerage.Id, noisyBrokerage.HoldingsAccountId!.Value,
            noisySecurity, 10m, 137.25m, Utc(2024, 3, 15));

        var quiet = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewDbContext();
        var consistency = Consistency(db);

        // The noisy ledger is genuinely drifted — otherwise this proves nothing.
        Assert.False(TradePrices(await consistency.CheckAsync(noisy.LedgerId)).Healthy);

        var trade = TradePrices(await consistency.CheckAsync(quiet.LedgerId));
        Assert.True(
            trade.Healthy,
            "another ledger's drift leaked into this report: " + Describe(trade));
    }
}
