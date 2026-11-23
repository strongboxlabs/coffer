using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
/// A naive "did the trade seed a row?" check reports drift that is not there, because the per-day source-priority rule (migration 177) makes
/// a MISSING trade price legitimate whenever a <c>fetch</c> close or a <c>manual</c>
/// gap-fill already owns that day.
/// </para>
/// <para>
/// So the false-positive cases below are not extras — they are the reason the check
/// has the shape it does, and a regression in either direction would make it
/// untrustworthy. A checker that cries wolf trains you to ignore it, which is
/// strictly worse than not having one.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyTradePricesTests : DatabaseTest
{
    public ConsistencyTradePricesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

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

        await using var db = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Raw SQL: no EF save, so TradePriceFromLegInterceptor never runs.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        Assert.False(before.Healthy);
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(security, mismatch.SecurityId);
        Assert.Contains("no price", mismatch.Field);
        Assert.Equal(0m, mismatch.Stored);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = (await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(after.Healthy, "repair left drift behind: " + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.25m, stored.Price);
        Assert.Equal(PriceSource.Trade, stored.Source);
        Assert.Equal(new DateOnly(2024, 3, 15), stored.PriceDate);
    }

    /// <summary>
    /// A reported row must name a security the reader recognises. A bare GUID would
    /// mean looking up every id by hand before deciding whether to repair — a report
    /// nobody can act on is not a report.
    /// </summary>
    [Fact]
    public async Task A_reported_row_names_the_security_not_its_id()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Broad Market Index Fund", "BMIX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = Db.NewDbContext();
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(trade.Mismatches);

        Assert.Contains("BMIX", mismatch.Scope);
        Assert.Contains("Broad Market Index Fund", mismatch.Scope);
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
        await using var read = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));
        await ledger.AddSecurityPriceAsync(security, 99.0000m, Utc(2024, 3, 15), source);

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        Assert.False(before.Healthy);
        var mismatch = Assert.Single(before.Mismatches);
        Assert.DoesNotContain("no price", mismatch.Field);
        Assert.Equal(99m, mismatch.Stored);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = (await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(after.Healthy, "repair left drift behind: " + Describe(after));

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(137.25m, stored.Price);
    }

    /// <summary>
    /// THE case a naive check gets wrong. A <c>fetch</c> close or a
    /// <c>manual</c> gap-fill OUTRANKS a trade, so migration 177's writer deliberately
    /// leaves it alone — a trade price missing from such a day is correct behaviour,
    /// not drift, and the two disagreeing is the design rather than a defect.
    /// </summary>
    [Theory]
    [InlineData(PriceSource.Fetch)]
    [InlineData(PriceSource.Manual)]
    public async Task A_higher_ranked_price_on_a_trade_day_is_not_drift(string source)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));
        // Deliberately far from the trade price: an intraday fill is not the close,
        // and the close is the truer figure.
        await ledger.AddSecurityPriceAsync(security, 141.80m, Utc(2024, 3, 15), source);

        await using var db = Db.NewDbContext();
        var trade = TradePrices(await Consistency(db).CheckAsync(ledger.LedgerId));

        Assert.True(
            trade.Healthy,
            $"a {source} price on a trade day was reported as drift: " + Describe(trade));
    }

    /// <summary>
    /// The day's price is the LAST trade by <c>seq</c> — not the first, and not the
    /// largest. Getting this wrong is not academic: an aggregate over multi-fill
    /// days produces a confident discrepancy that does not exist.
    /// </summary>
    [Fact]
    public async Task The_expected_price_is_the_last_trade_of_the_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
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

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(137.25m, mismatch.Expected);

        var after = (await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // 6dp — two digits more than security_prices.price can keep. The 5th digit
        // is 8, so rounding is visible rather than a truncation that happens to match.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.256789m, Utc(2024, 3, 15));

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);

        var before = TradePrices(await consistency.CheckAsync(ledger.LedgerId));
        var mismatch = Assert.Single(before.Mismatches);
        Assert.Equal(137.2568m, mismatch.Expected);

        var after = (await consistency.RepairAsync(
            ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
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
    /// Migration 234. When one leg of a day with several is corrected by hand, an
    /// interceptor that writes that leg's price straight through ignores a
    /// fractional-share fee fragment posted later the same day, while the consistency
    /// check — correctly applying "last trade of the day" — derives the fragment's
    /// price and reports the difference as drift. Neither side is buggy on its own
    /// terms; they run different rules, and a sound ledger reads as broken. So the
    /// writer applies the same rule as the check.
    /// </remarks>
    [Fact]
    public async Task A_write_seeds_the_price_the_rule_names_not_the_leg_written()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // Two trades on one day. The LATER one by seq is the tiny fragment — the
        // shape that makes a wrong aggregate report drift.
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 400m, 25.1234m, Utc(2024, 3, 15));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 0.125m, 0.5678m, Utc(2024, 3, 15));

        await using var db = Db.NewDbContext();

        // Re-seed naming only the DAY, as the interceptor now does.
        await new TradePriceRecomputeService(db)
            .ReseedAsync([(ledger.LedgerId, security, new DateOnly(2024, 3, 15))]);

        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(0.5678m, stored.Price);

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 5m, 120.1234m, Utc(2024, 3, 15));

        // Two more security legs on the SAME header, at different prices. The
        // largest by value is the middle one, so neither min nor max nor
        // insertion order gives the right answer by accident.
        await using var db = Db.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                                  amount, security_id, quantity, unit_price, posting_role)
            SELECT gen_random_uuid(), l.header_id, l.ledger_id, l.account_id, 1,
                   -1067.50, l.security_id, 10.125, 105.4321, 'security'
              FROM txn_legs l WHERE l.id = {legId}
            UNION ALL
            SELECT gen_random_uuid(), l.header_id, l.ledger_id, l.account_id, 2,
                   -7.52, l.security_id, 0.075, 100.2468, 'security'
              FROM txn_legs l WHERE l.id = {legId};");
        // Raw SQL reaches no writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(legId);

        var service = new TradePriceRecomputeService(db);
        var key = (ledger.LedgerId, security, new DateOnly(2024, 3, 15));

        await service.ReseedAsync([key]);
        var first = (await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).SingleAsync()).Price;

        // The largest leg by absolute amount: the first leg the INSERT above adds.
        Assert.Equal(105.4321m, first);

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
    /// Reported as defects they would mark a sound ledger broken with rows none of
    /// which is actionable from the panel.
    /// <para>
    /// Grouped per security, because that is the unit a person acts on, and
    /// ordered by what the holding is worth so what still carries money leads.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unbacked_trade_price_is_advisory_not_a_finding()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        // A trade-source price on a day with no trade at all behind it.
        await ledger.AddSecurityPriceAsync(
            security, 88.4321m, Utc(2019, 6, 10), PriceSource.Trade);

        await using var db = Db.NewDbContext();
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
        Assert.Equal(new DateOnly(2019, 6, 10), advisory.Earliest);
        Assert.Equal(new DateOnly(2019, 6, 10), advisory.Latest);
        Assert.Contains("IDX", advisory.Security);

        // Nothing holds it, so it sorts last rather than being hidden.
        Assert.Equal(0m, advisory.HoldingValue);

        // And the repair leaves it alone — there is nothing to derive.
        var stored = Assert.Single(await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == security).ToListAsync());
        Assert.Equal(88.4321m, stored.Price);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        await ledger.AddSecurityPriceAsync(
            security, 88.4321m, Utc(2019, 6, 10), PriceSource.Manual);

        await using var db = Db.NewDbContext();
        var report = await Consistency(db).CheckAsync(ledger.LedgerId);

        Assert.Empty(report.UnbackedPrices);
        Assert.True(report.Healthy);
    }

    /// <summary>
    /// The advisory reads each security's unbacked dates in one pass (migration 273) and
    /// lists exactly what asking the rule once per stored price lists: the same securities,
    /// counts, first and last dates, holding values, in the same order.
    /// </summary>
    /// <remarks>
    /// <para>The per-row form is migration 259's statement of the advisory, kept here as SQL
    /// with migration 240's per-row test (<c>fn_price_is_unbacked</c>, dropped by 273) written
    /// out in its place: a trade-source price on a day the rule
    /// (<c>fn_trade_execution_for_day</c>, migration 274) prices nothing. It asks the day's rule for each stored price. The new form is read the
    /// way the consistency report reads it.</para>
    /// <para>The ledger holds what either form could count differently: a day whose later
    /// execution is hidden and one whose later execution is merged away, each still priced
    /// by the execution that remains; a day whose only execution is hidden and one whose only
    /// execution is merged away; trade-source prices no execution ever backed; a fetched and
    /// a manual price on days no trade prices, which are not the advisory's; a held security
    /// whose every price is backed; and two securities nobody holds, which sort last at 0,
    /// by how many prices each has unbacked.</para>
    /// </remarks>
    [Fact]
    public async Task The_one_pass_advisory_lists_what_the_per_row_advisory_lists()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var withHidden = await ledger.AddSecurityAsync("Fund A", "FNDA");
        var withMerged = await ledger.AddSecurityAsync("Fund B", "FNDB");
        var allBacked = await ledger.AddSecurityAsync("Fund C", "FNDC");
        var unheldTwo = await ledger.AddSecurityAsync("Fund D", "FNDD");
        var unheldOne = await ledger.AddSecurityAsync("Fund E", "FNDE");
        foreach (var held in new[] { withHidden, withMerged, allBacked })
            await ledger.AddHoldingAsync(holdings, held, 0m, 0m);

        // Fund A: a day whose later execution is then hidden, and a day whose only one is.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, withHidden, 10m, 20m, Utc(2024, 3, 4));
        var hiddenLater = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, withHidden, 5m, 30m, Utc(2024, 3, 4));
        var hiddenOnly = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, withHidden, 5m, 25m, Utc(2024, 3, 11));
        // Fund B: the same with merges into the first execution of the first day.
        var winner = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, withMerged, 4m, 50m, Utc(2024, 3, 5));
        var mergedLater = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, withMerged, 4m, 55m, Utc(2024, 3, 5));
        var mergedOnly = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, withMerged, 4m, 52m, Utc(2024, 3, 12));
        // Fund C: one execution, its price backed.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, allBacked, 2m, 70m, Utc(2024, 3, 6));
        await ledger.RecomputeTradePricesAsync();

        // Hidden and merged after their days were priced, then the days re-derived as the
        // interceptor does on a hide or a merge: the remaining execution prices the first
        // day, and the day with none keeps its price.
        await ledger.HideTransactionAsync(hiddenLater);
        await ledger.HideTransactionAsync(hiddenOnly);
        await ledger.MarkTransactionMergedAsync(mergedLater, winner);
        await ledger.MarkTransactionMergedAsync(mergedOnly, winner);
        await ledger.RecomputeTradePricesAsync();
        await ledger.RecomputeHoldingsAsync();

        // Trade-source prices no execution backs, and fetched and manual prices on days no
        // trade prices; the latest price of each held security sets its holding value.
        await ledger.AddSecurityPriceAsync(withHidden, 22m, Utc(2024, 4, 1), PriceSource.Trade);
        await ledger.AddSecurityPriceAsync(withHidden, 21m, Utc(2024, 4, 15), PriceSource.Fetch);
        await ledger.AddSecurityPriceAsync(withMerged, 60m, Utc(2024, 4, 16), PriceSource.Manual);
        await ledger.AddSecurityPriceAsync(allBacked, 71m, Utc(2024, 4, 17), PriceSource.Fetch);
        await ledger.AddSecurityPriceAsync(allBacked, 72m, Utc(2024, 4, 18), PriceSource.Manual);
        await ledger.AddSecurityPriceAsync(unheldTwo, 9m, Utc(2024, 2, 1), PriceSource.Trade);
        await ledger.AddSecurityPriceAsync(unheldTwo, 9.5m, Utc(2024, 2, 2), PriceSource.Trade);
        await ledger.AddSecurityPriceAsync(unheldTwo, 9.25m, Utc(2024, 2, 3), PriceSource.Fetch);
        await ledger.AddSecurityPriceAsync(unheldOne, 8m, Utc(2024, 2, 1), PriceSource.Trade);

        var today = new DateOnly(2026, 6, 15);
        await using var db = Db.NewDbContext();
        var oneDayDerived = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == withHidden && p.PriceDate == new DateOnly(2024, 3, 4))
            .Select(p => p.Price)
            .SingleAsync();
        Assert.Equal(20m, oneDayDerived);   // the premise: the hidden execution prices no day

        var oneByOne = await PerRowAdvisoryAsync(db, ledger.LedgerId, ScheduledBoundary.ScheduledFrom(today));
        var onePass = (await Consistency(db).CheckAsync(ledger.LedgerId, today)).UnbackedPrices
            .Select(u => (u.SecurityId, (long)u.Count, u.Earliest, u.Latest, u.HoldingValue!.Value))
            .ToList();

        Assert.Equal(oneByOne, onePass);

        // Not two empty lists: what both are, by held value, then by count. The hidden and
        // merged-away executions hold no shares.
        var expected = new List<(Guid, long, DateOnly, DateOnly, decimal)>
        {
            (withMerged, 1, new DateOnly(2024, 3, 12), new DateOnly(2024, 3, 12), 4m * 60m),
            (withHidden, 2, new DateOnly(2024, 3, 11), new DateOnly(2024, 4, 1), 10m * 21m),
            (unheldTwo, 2, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 2), 0m),
            (unheldOne, 1, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), 0m),
        };
        Assert.Equal(expected, onePass);
    }

    /// <summary>Migration 259's advisory, which asked <c>fn_price_is_unbacked</c> per stored
    /// price, as SQL so the one-pass form can be compared with it. That function is dropped
    /// (migration 273), so its definition is written out here: a read, and nothing is
    /// written.</summary>
    private static async Task<List<(Guid, long, DateOnly, DateOnly, decimal)>> PerRowAdvisoryAsync(
        AppDbContext db, Guid ledgerId, DateTime scheduledFrom)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            WITH held AS (
                SELECT n.security_id,
                       sum(CASE WHEN n.price IS NULL THEN n.cost_basis
                                ELSE round(n.quantity * n.price, 4)
                           END) AS value_now
                  FROM holdings_as_of_now(@l, @b, NULL) n
                 GROUP BY n.security_id
            )
            SELECT p.security_id,
                   count(*)                         AS unbacked_count,
                   min(p.price_date)                AS earliest,
                   max(p.price_date)                AS latest,
                   coalesce(max(held.value_now), 0) AS holding_value
            FROM security_prices p
            LEFT JOIN held ON held.security_id = p.security_id
            WHERE p.ledger_id = @l
              -- fn_price_is_unbacked's body (migration 240), per stored price.
              AND p.source = 'trade'
              AND NOT EXISTS (SELECT 1 FROM fn_trade_execution_for_day(p.ledger_id, p.security_id, p.price_date))
            GROUP BY p.security_id
            ORDER BY holding_value DESC, unbacked_count DESC, p.security_id
            """;
        command.Parameters.Add(new NpgsqlParameter("l", ledgerId));
        command.Parameters.Add(new NpgsqlParameter("b", scheduledFrom));
        var rows = new List<(Guid, long, DateOnly, DateOnly, decimal)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateOnly>(2),
                reader.GetFieldValue<DateOnly>(3), reader.GetDecimal(4)));
        return rows;
    }

    /// <summary>
    /// The per-row test is gone (migration 273): nothing called it, and a second statement
    /// of the advisory's test that nothing calls is one nothing keeps in step. A
    /// <c>DROP FUNCTION IF EXISTS</c> naming a signature that does not match drops nothing
    /// and says nothing, so the catalog is asked.
    /// </summary>
    [Fact]
    public async Task The_per_row_unbacked_test_is_dropped()
    {
        await using var db = Db.NewDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT to_regprocedure('fn_price_is_unbacked(uuid, uuid, date, text)') IS NULL,
                   to_regprocedure('fn_unbacked_price_dates(uuid, uuid)') IS NULL
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        // Anchor: the one-pass form, which the advisory reads, is there to be found.
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(0));
    }

    /// <summary>
    /// A recurring TEMPLATE is never a live cash event (ADR-0047), so it seeds no
    /// price and must not be reported as a missing one.
    /// </summary>
    [Fact]
    public async Task A_recurring_template_is_not_expected_to_have_a_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        var legId = await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, security, 10m, 137.25m, Utc(2024, 3, 15));

        await using var db = Db.NewDbContext();
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
        var noisy = await SyntheticLedger.CreateAsync(Db);
        var noisyBrokerage = await noisy.AddInvestmentAccountAsync("Brokerage");
        var noisySecurity = await noisy.AddSecurityAsync("Index Fund", "IDX");
        await noisy.AddInvestmentBuyAsync(
            noisyBrokerage.Id, noisyBrokerage.HoldingsAccountId!.Value,
            noisySecurity, 10m, 137.25m, Utc(2024, 3, 15));

        var quiet = await SyntheticLedger.CreateAsync(Db);

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);

        // The noisy ledger is genuinely drifted — otherwise this proves nothing.
        Assert.False(TradePrices(await consistency.CheckAsync(noisy.LedgerId)).Healthy);

        var trade = TradePrices(await consistency.CheckAsync(quiet.LedgerId));
        Assert.True(
            trade.Healthy,
            "another ledger's drift leaked into this report: " + Describe(trade));
    }
}
