using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Migration 263 section 4b (ADR-0108 D18): an in-kind transfer with no source brokerage
/// is rebuilt as the sale with a transfer the importer writes, priced at the cost of the
/// shares it takes, and the pre-upgrade check predicts every refusal.
/// </summary>
/// <remarks>
/// <para><b>How the earlier state is built.</b> The current importer imports a Moneydance
/// export whose share transfers out are priced at Moneydance's $1-per-share placeholder;
/// its rows are copied into a database staged at 259, and the post-import step release
/// 0.36.9 shipped (commit 4caaffdd, reverted in 0.36.10) runs there verbatim. That step
/// left the transfers this migration rebuilds: the holdings leg at the carried cost and,
/// where the cash side was not a brokerage, that leg; every other brokerage leg deleted.
/// The export is synthetic.</para>
/// <para><b>What is expected.</b> The same export with each transfer out priced at its
/// cost, imported by the current importer: the sale it writes, its zero gain, the
/// holdings, the brokerage's balances and every stored presentation.</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class Migration263InKindSaleTests : DatabaseTest
{
    public Migration263InKindSaleTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Mig263 = "263_originating_account_tag_name_key_and_saved_report_repoint.sql";

    // Moneydance amounts: samt in 1e-4 shares, pamt in cents. A transfer out carries
    // Moneydance's one-per-share placeholder, or the cost of the shares it moves.
    private const int Placeholder = 1000;
    private const int AtCost = 10000;

    private static string Export(params string[] outs) => $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-sec","type":"s","ticker":"SYN","dec":"4","name":"Synthetic Fund","currid":"^SYN","rate":"0.1","rrate":"0.1"},
            {"obj_type":"curr","id":"c-sec2","type":"s","ticker":"SEC","dec":"4","name":"Second Fund","currid":"^SEC","rate":"0.1","rrate":"0.1"},
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brkA","type":"v","name":"Brokerage A","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"s-A","type":"s","name":"Synthetic Fund","parentid":"a-brkA","currid":"c-sec","sbal":"0"},
            {"obj_type":"acct","id":"s-A2","type":"s","name":"Second Fund","parentid":"a-brkA","currid":"c-sec2","sbal":"0"},
            {"obj_type":"acct","id":"a-brkB","type":"v","name":"Brokerage B","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"s-B","type":"s","name":"Synthetic Fund","parentid":"a-brkB","currid":"c-sec","sbal":"0"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-xfer","type":"e","name":"Transfers Clearing","parentid":"a-root","currid":"c-usd"},

            {"obj_type":"txn","id":"t-buy","acctid":"a-brkA","desc":"Buy","dt":"20190115","td":"20190115",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"t-buy-0","0.acctid":"s-A","0.invest.splittype":"sec","0.samt":"1000000","0.pamt":"-100000"},
            {"obj_type":"txn","id":"t-buy2a","acctid":"a-brkA","desc":"Buy","dt":"20200115","td":"20200115",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"t-buy2a-0","0.acctid":"s-A2","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"txn","id":"t-buy2b","acctid":"a-brkA","desc":"Buy","dt":"20210515","td":"20210515",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"t-buy2b-0","0.acctid":"s-A2","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-20000"}
            {{string.Concat(outs)}}
          ]
        }
        """;

    /// <summary>A Moneydance share transfer out of brokerage A, 10 shares unless given.</summary>
    private static string Out(string id, string date, string to, int pamt, string sec = "s-A", int samt = -100000) => $$"""
            ,{"obj_type":"txn","id":"{{id}}","acctid":"a-brkA","desc":"Out","memo":"Moved out","dt":"{{date}}","td":"{{date}}",
             "invest.txntype":"sellx","xfer_type":"xfrtp_buysellxfr","qif_invst_action":"ShrsOut",
             "0.id":"{{id}}-0","0.acctid":"{{sec}}","0.invest.splittype":"sec","0.samt":"{{samt}}","0.pamt":"{{pamt}}",
             "1.id":"{{id}}-1","1.acctid":"{{to}}","1.invest.splittype":"xfr","1.samt":"{{pamt}}","1.pamt":"{{-pamt}}"}
        """;

    /// <summary>A Moneydance buy of the synthetic fund into brokerage A.</summary>
    private static string Buy(string id, string date, int samt, int pamt) => $$"""
            ,{"obj_type":"txn","id":"{{id}}","acctid":"a-brkA","desc":"Buy","dt":"{{date}}","td":"{{date}}",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"{{id}}-0","0.acctid":"s-A","0.invest.splittype":"sec","0.samt":"{{samt}}","0.pamt":"{{pamt}}"}
        """;

    // ---- 0.36.9 InKindTransferMitigationStep (commit 4caaffdd), verbatim ----------

    private const string StepCandidates = """
        SELECT h.id
          FROM txn_headers h
         WHERE h.ledger_id = @ledgerId
           AND h.action = 'sellx'
           AND EXISTS (SELECT 1 FROM txn_legs t
                        WHERE t.header_id = h.id AND t.posting_role = 'transfer')
           AND EXISTS (SELECT 1 FROM txn_legs hl
                        WHERE hl.header_id = h.id AND hl.security_id IS NOT NULL
                          AND hl.quantity IS NOT NULL AND hl.quantity <> 0
                          AND abs(hl.amount) <= 1.01 * abs(hl.quantity));
        """;

    private static readonly string[] StepWrites =
    [
        """
        UPDATE txn_legs l
           SET amount = -rg.cost_basis_sold
          FROM realized_gains rg
         WHERE rg.sell_leg_id = l.id
           AND l.header_id = ANY(@ids)
           AND l.security_id IS NOT NULL;
        """,
        """
        UPDATE txn_legs c
           SET amount = -hl.amount
          FROM txn_legs hl, accounts a
         WHERE c.header_id = ANY(@ids)
           AND c.posting_role = 'transfer'
           AND a.id = c.account_id
           AND a.holdings_account_id IS NULL
           AND hl.header_id = c.header_id
           AND hl.security_id IS NOT NULL;
        """,
        """
        DELETE FROM txn_legs l
         USING accounts a
         WHERE l.header_id = ANY(@ids)
           AND l.account_id = a.id
           AND l.security_id IS NULL
           AND a.holdings_account_id IS NOT NULL;
        """,
        "UPDATE txn_legs SET posting_index = 0 WHERE header_id = ANY(@ids);",
        "UPDATE txn_headers SET action = 'transfer_shares' WHERE id = ANY(@ids);",
    ];

    // ---- tests ------------------------------------------------------------------

    [Fact]
    public async Task Transfers_to_a_category_or_a_bank_upgrade_to_the_sale_the_importer_writes_at_their_cost()
    {
        // t-out-bank is deleted (hidden) after the import, in both ledgers. The walk
        // leaves a hidden header out, so the step found no gain to price it from and it
        // kept the placeholder; the rebuilt sale keeps that amount and records no gain.
        var (_, expected) = await ImportAsync(
            Export(Out("t-out-cat", "20210601", "a-xfer", AtCost), Out("t-out-bank", "20210602", "a-bank", Placeholder)),
            hide: "t-out-bank");
        var ledgerId = (await ImportAsync(
            Export(Out("t-out-cat", "20210601", "a-xfer", Placeholder), Out("t-out-bank", "20210602", "a-bank", Placeholder)),
            hide: "t-out-bank")).LedgerId;

        var (old, work) = await InstallAt259.CreateAsync(Db, "inkind_sale", ledgerId, derived: true);
        Assert.Equal(2, await RunTheStepAsync(old, ledgerId));
        Assert.Equal(
            ["p0 holdings -10.000000000000 -100.0000 security", "p0 category 100.0000 transfer"],
            await LegShapeAsync(old, ledgerId, "t-out-cat"));
        Assert.Equal(new Blockers(0, 0, 0), await PrecheckAsync(old));

        // Up to 263, then 263 again: the rebuilt headers are sales, so it finds nothing.
        InstallAt259.UpgradeFrom259(old, work, before: "264_");
        var once = await DigestAsync(old, ledgerId);
        await using (var conn = new NpgsqlConnection(old))
            await conn.ExecuteAsync($"DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%{Mig263}'");
        MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(old), work, NullLogger.Instance);
        Assert.Equal(once, await DigestAsync(old, ledgerId));

        InstallAt259.UpgradeFrom259(old, work);
        var actual = await SnapshotAsync(old, ledgerId);
        Assert.True(expected.SequenceEqual(actual), Diff(expected, actual));

        // The new legs are derived from the header alone.
        await using var check = new NpgsqlConnection(old);
        var derived = (await check.QueryAsync<(string Role, bool IdFromHeader, bool StampFromHoldingsLeg)>("""
            SELECT l.posting_role,
                   l.id = md5(h.id::text || CASE l.posting_index WHEN 0 THEN ':263:sale-cash'
                                                                  ELSE ':263:transfer-cash' END)::uuid,
                   l.created_at = (SELECT s.created_at FROM txn_legs s WHERE s.header_id = h.id AND s.quantity < 0)
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE h.ledger_id = @L AND h.external_id LIKE 't-out-%' AND a.external_id = 'a-brkA'
             ORDER BY h.external_id, l.posting_index
            """, new { L = ledgerId })).ToList();
        Assert.Equal(4, derived.Count);
        Assert.All(derived, d => Assert.True(d.IdFromHeader && d.StampFromHoldingsLeg, $"a {d.Role} leg was not derived from its header"));
    }

    [Fact]
    public async Task A_transfer_to_another_brokerage_keeps_no_leg_to_rebuild_from_and_refuses_the_upgrade()
    {
        var ledgerId = (await ImportAsync(Export(Out("t-out-brk", "20210601", "a-brkB", Placeholder)))).LedgerId;
        var (old, work) = await InstallAt259.CreateAsync(Db, "inkind_brokerage", ledgerId, derived: true);
        Assert.Equal(1, await RunTheStepAsync(old, ledgerId));
        Assert.Equal(["p0 holdings -10.000000000000 -100.0000 security"], await LegShapeAsync(old, ledgerId, "t-out-brk"));

        Assert.Equal(new Blockers(0, 1, 0), await PrecheckAsync(old));
        await AssertRefusedAsync(old, work, ledgerId, "t-out-brk", "(c) transfer_shares not owned by its source brokerage: 1");
    }

    [Fact]
    public async Task A_sale_across_the_long_term_line_nets_to_zero_with_offsetting_terms_as_the_importer_writes_it()
    {
        // Both lots of the second fund: one held over a year, one under, the second
        // bought dearer. At the carried cost the long-term part takes its share of the
        // proceeds by share count but its cost by lot, so it books a gain and the
        // short-term part an equal loss: zero in total, as on the importer's sale.
        var (_, expected) = await ImportAsync(Export(
            Out("t-out-terms", "20210601", "a-xfer", AtCost * 3, sec: "s-A2", samt: -200000)));
        var ledgerId = (await ImportAsync(Export(
            Out("t-out-terms", "20210601", "a-xfer", Placeholder * 2, sec: "s-A2", samt: -200000)))).LedgerId;
        var (old, work) = await InstallAt259.CreateAsync(Db, "inkind_terms", ledgerId, derived: true);
        Assert.Equal(1, await RunTheStepAsync(old, ledgerId));
        Assert.Equal(
            ["p0 holdings -20.000000000000 -300.0000 security", "p0 category 300.0000 transfer"],
            await LegShapeAsync(old, ledgerId, "t-out-terms"));

        Assert.Equal(new Blockers(0, 0, 0), await PrecheckAsync(old));
        InstallAt259.UpgradeFrom259(old, work);
        var actual = await SnapshotAsync(old, ledgerId);
        Assert.True(expected.SequenceEqual(actual), Diff(expected, actual));

        var (action, gain, gainLt) = (await GainsAsync(old, ledgerId)).Single();
        Assert.Equal(("sellx", 0m), (action, gain));
        Assert.NotEqual(0m, gainLt);
    }

    [Theory]
    [InlineData("dearer", 1000.50, 100.05, 0.00)]
    [InlineData("cheaper", 999.50, 99.95, 0.00)]
    [InlineData("half_cent", 1000.05, 100.01, 0.01)]
    public async Task A_carried_cost_the_lots_no_longer_add_up_to_is_repriced_at_their_cost(
        string edit, decimal buyCost, decimal price, decimal gain)
    {
        // The buy is edited after the step to cost slightly more or less, so the shares
        // the transfer took no longer cost what it carries. The sale is priced at what
        // they cost now, to the cent: no gain, except where that cost ends in exactly
        // half a cent, which rounds to a one-cent gain as on any sale at that cost.
        var ledgerId = (await ImportAsync(Export(Out("t-out-cat", "20190601", "a-xfer", Placeholder)))).LedgerId;
        var (old, work) = await InstallAt259.CreateAsync(Db, "inkind_drift_" + edit, ledgerId, derived: true);
        Assert.Equal(1, await RunTheStepAsync(old, ledgerId));
        await using (var conn = new NpgsqlConnection(old))
        {
            await conn.ExecuteAsync("""
                UPDATE txn_legs l SET amount = sign(l.amount) * @Cost
                  FROM txn_headers h
                 WHERE h.id = l.header_id AND h.ledger_id = @L AND h.external_id = 't-buy';
                SELECT recompute_holdings_cost_basis(@L);
                SELECT fn_recompute_balances_for_account(l.account_id, h.posted_at)
                  FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
                 WHERE h.ledger_id = @L AND h.external_id = 't-buy';
                """, new { L = ledgerId, Cost = buyCost });
        }
        Assert.Equal(new Blockers(0, 0, 0), await PrecheckAsync(old));

        InstallAt259.UpgradeFrom259(old, work);
        Assert.Equal([("sellx", gain)], (await GainsAsync(old, ledgerId)).Select(g => (g.Action, g.Gain)));
        await using var check = new NpgsqlConnection(old);
        var legs = (await check.QueryAsync<(decimal Amount, decimal? UnitPrice)>("""
            SELECT l.amount, l.unit_price
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @L AND h.external_id = 't-out-cat'
            """, new { L = ledgerId })).ToList();
        Assert.Equal(4, legs.Count);
        Assert.All(legs, l => Assert.Equal(price, Math.Abs(l.Amount)));
        Assert.Equal(0m, legs.Sum(l => l.Amount));
        Assert.Contains(legs, l => l.UnitPrice == Math.Round(price / 10m, 6));
        await AssertDerivedStateCurrentAsync(old, ledgerId);
    }

    [Fact]
    public async Task Transfers_at_one_instant_with_a_buy_are_each_priced_at_the_lots_they_take()
    {
        // A dearer buy at the transfers' instant sorts before them. The walk orders the
        // two transfers by leg id: the first takes the older lot and part of the new
        // one, the second the rest of the new one, so the two carry different costs.
        var ledgerId = (await ImportAsync(Export(
            Buy("t-buy-now", "20210601", samt: 2000000, pamt: -600000),
            Out("t-out-one", "20210601", "a-xfer", Placeholder * 15, samt: -1500000),
            Out("t-out-two", "20210601", "a-xfer", Placeholder * 15, samt: -1500000)))).LedgerId;
        var (old, work) = await InstallAt259.CreateAsync(Db, "inkind_buy_instant", ledgerId, derived: true);
        Assert.Equal(2, await RunTheStepAsync(old, ledgerId));
        var carried = await CarriedCostsAsync(old, ledgerId);
        Assert.Equal(2, carried.Distinct().Count());

        InstallAt259.UpgradeFrom259(old, work);
        Assert.Equal([("sellx", 0m), ("sellx", 0m)], (await GainsAsync(old, ledgerId)).Select(g => (g.Action, g.Gain)));
        Assert.Equal(carried.Order(), (await CarriedCostsAsync(old, ledgerId)).Order());
        await AssertDerivedStateCurrentAsync(old, ledgerId);
    }

    // ---- the two databases --------------------------------------------------------

    private async Task<(Guid LedgerId, List<string> Snapshot)> ImportAsync(string export, string? hide = null)
    {
        await using var conn = Db.OpenConnection();
        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(export), ledgerName: $"In-kind {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
        if (hide is not null)
        {
            // As the delete endpoint does: the header is hidden, and its holding and the
            // balances of its accounts are recomputed, in one transaction that takes the
            // ledger's write lock first (migration 275).
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync(
                    "SELECT locked FROM fn_ledger_write_lock(@L);", new { L = result.LedgerId }, tx);
                await conn.ExecuteAsync("""
                    UPDATE txn_headers SET is_hidden = TRUE WHERE ledger_id = @L AND external_id = @X;
                    SELECT recompute_holdings_cost_basis(@L);
                    SELECT fn_recompute_balances_for_account(l.account_id, h.posted_at)
                      FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
                     WHERE h.ledger_id = @L AND h.external_id = @X;
                    """, new { L = result.LedgerId, X = hide }, tx);
                await tx.CommitAsync();
            }
            await StoredNotesSeed.BackfillNotesAsync(conn, result.LedgerId);
        }
        return (result.LedgerId, await SnapshotAsync(Db.ConnectionString, result.LedgerId));
    }

    /// <summary>The 0.36.9 step over the ledger, as the import ran it: realized gains
    /// current first, the holdings recomputed after. Returns how many rows it reshaped.</summary>
    private static async Task<int> RunTheStepAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT recompute_holdings_cost_basis(@L);", new { L = ledgerId });
        var ids = (await conn.QueryAsync<Guid>(StepCandidates, new { ledgerId })).ToArray();
        foreach (var sql in StepWrites) await conn.ExecuteAsync(sql, new { ids });
        await conn.ExecuteAsync("SELECT recompute_holdings_cost_basis(@L);", new { L = ledgerId });
        return ids.Length;
    }

    /// <summary>The ledger's balances, realized gains and holdings are what a fresh
    /// recompute of every account and position writes.</summary>
    private static async Task AssertDerivedStateCurrentAsync(string cs, Guid ledgerId)
    {
        var stored = await SnapshotAsync(cs, ledgerId);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.ExecuteAsync("""
                SELECT recompute_holdings_cost_basis(@L);
                SELECT fn_recompute_balances_for_account(a.id, '1900-01-01'::timestamptz)
                  FROM accounts a WHERE a.ledger_id = @L;
                """, new { L = ledgerId });
        }
        var fresh = await SnapshotAsync(cs, ledgerId);
        Assert.True(fresh.SequenceEqual(stored), Diff(fresh, stored));
    }

    /// <summary>The upgrade stops at 263 with <paramref name="message"/>, and the transfer
    /// and its legs are as the step left them.</summary>
    private static async Task AssertRefusedAsync(string cs, string work, Guid ledgerId, string externalId, string message)
    {
        var before = await DigestAsync(cs, ledgerId);
        var shape = await LegShapeAsync(cs, ledgerId, externalId);
        var ex = Assert.ThrowsAny<Exception>(() => InstallAt259.UpgradeFrom259(cs, work));
        Assert.Contains(message, Flatten(ex), StringComparison.Ordinal);
        Assert.Equal(before, await DigestAsync(cs, ledgerId));
        Assert.Equal(shape, await LegShapeAsync(cs, ledgerId, externalId));
        await using var conn = new NpgsqlConnection(cs);
        Assert.Equal("transfer_shares", await conn.ExecuteScalarAsync<string>(
            "SELECT action FROM txn_headers WHERE ledger_id = @L AND external_id = @X", new { L = ledgerId, X = externalId }));
    }

    // ---- the pre-upgrade check ------------------------------------------------------

    private readonly record struct Blockers(long Unowned, long Unsourced, long Functions);

    /// <summary>
    /// <c>scripts/maintainer/release-checks/0128-precheck.sql</c>, run as written: its one
    /// statement, in a READ ONLY transaction rolled back after.
    /// </summary>
    private static async Task<Blockers> PrecheckAsync(string cs)
    {
        var repo = Path.GetFullPath(Path.Combine(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), "..", ".."));
        var text = await File.ReadAllTextAsync(Path.Combine(repo, "scripts", "maintainer", "release-checks", "0128-precheck.sql"));
        var start = text.IndexOf("WITH", text.IndexOf("SET LOCAL lock_timeout", StringComparison.Ordinal), StringComparison.Ordinal);
        var end = text.LastIndexOf(";", text.LastIndexOf("ROLLBACK;", StringComparison.Ordinal) - 1, StringComparison.Ordinal);
        var statement = text[start..end];

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: tx);
        var rows = (await conn.QueryAsync<(string Section, string Bucket, long Count, string Expected)>(statement, transaction: tx)).ToList();
        await tx.RollbackAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("blocker", r.Section));
        return new Blockers(rows[0].Count, rows[1].Count, rows[2].Count);
    }

    // ---- reads ------------------------------------------------------------------

    /// <summary>Each transfer out's action and realized gain, total and long-term.</summary>
    private static async Task<List<(string Action, decimal Gain, decimal GainLt)>> GainsAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        return (await conn.QueryAsync<(string Action, decimal Gain, decimal GainLt)>("""
            SELECT h.action, g.realized_gain, g.realized_gain_lt
              FROM txn_headers h
              JOIN txn_legs l ON l.header_id = h.id AND l.quantity < 0
              JOIN realized_gains g ON g.sell_leg_id = l.id
             WHERE h.ledger_id = @L AND h.external_id LIKE 't-out-%'
             ORDER BY h.external_id
            """, new { L = ledgerId })).ToList();
    }

    /// <summary>The carried cost of each transfer out: its holdings leg's amount.</summary>
    private static async Task<List<decimal>> CarriedCostsAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        return (await conn.QueryAsync<decimal>("""
            SELECT l.amount
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @L AND h.external_id LIKE 't-out-%' AND l.quantity < 0
            """, new { L = ledgerId })).ToList();
    }

    /// <summary>One transfer's legs: posting, kind, quantity, amount, role.</summary>
    private static async Task<List<string>> LegShapeAsync(string cs, Guid ledgerId, string externalId)
    {
        await using var conn = new NpgsqlConnection(cs);
        return (await conn.QueryAsync<string>("""
            SELECT 'p' || l.posting_index || ' '
                   || CASE WHEN a.holdings_account_id IS NOT NULL THEN 'brokerage'
                           WHEN EXISTS (SELECT 1 FROM accounts b WHERE b.holdings_account_id = a.id) THEN 'holdings'
                           ELSE a.account_type END
                   || COALESCE(' ' || l.quantity::text, '') || ' ' || l.amount || ' ' || COALESCE(l.posting_role, 'none')
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE h.ledger_id = @L AND h.external_id = @X
             ORDER BY l.posting_index, a.account_type DESC, l.amount
            """, new { L = ledgerId, X = externalId })).ToList();
    }

    /// <summary>
    /// Every leg of the ledger's transfers out (posting, account, amount, quantity, unit
    /// price, role, memo and stored presentation), each header's action and memo, every
    /// realized gain, the holdings, and the brokerage's running balances: everything a
    /// rebuilt sale must share with the one the importer writes.
    /// </summary>
    private static async Task<List<string>> SnapshotAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        var lines = (await conn.QueryAsync<string>("""
            SELECT h.external_id || ' H ' || COALESCE(h.action, 'none') || ' ' || h.is_hidden
                   || ' ' || COALESCE('''' || h.memo || '''', 'NULL')
              FROM txn_headers h WHERE h.ledger_id = @L
            UNION ALL
            SELECT h.external_id || ' L p' || l.posting_index || ' ' || COALESCE(a.external_id, 'named ' || a.name)
                   || ' ' || l.amount || ' ' || COALESCE(l.quantity::text, '-') || ' ' || COALESCE(l.unit_price::text, '-')
                   || ' ' || COALESCE(l.posting_role, 'none') || ' ' || COALESCE('''' || l.leg_memo || '''', 'NULL')
                   || ' ' || l.account_postings_on_header || '/' || l.header_total_postings
                   || ' ' || COALESCE(l.notes_kind, 'unfilled') || ' ' || COALESCE('''' || l.notes_memo || '''', 'NULL')
                   || ' ' || COALESCE(l.notes_memo_inherited::text, '-') || ' ' || COALESCE('''' || l.notes_header_memo || '''', 'NULL')
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE l.ledger_id = @L AND h.external_id LIKE 't-out-%'
            UNION ALL
            SELECT h.external_id || ' G ' || g.quantity || ' ' || g.proceeds || ' ' || g.cost_basis_sold
                   || ' ' || g.realized_gain || ' ' || g.realized_gain_lt
              FROM realized_gains g
              JOIN txn_legs l ON l.id = g.sell_leg_id
              JOIN txn_headers h ON h.id = l.header_id
             WHERE g.ledger_id = @L
            UNION ALL
            SELECT 'holding ' || COALESCE(a.external_id, 'named ' || a.name) || ' ' || s.name
                   || ' ' || x.quantity || ' ' || x.cost_basis
              FROM holdings x
              JOIN accounts a ON a.id = x.account_id
              JOIN securities s ON s.id = x.security_id
             WHERE x.ledger_id = @L
            UNION ALL
            SELECT h.external_id || ' B ' || COALESCE(a.external_id, 'named ' || a.name) || ' ' || b.balance_after
                   || ' ' || b.net_amount
              FROM txn_header_account_balances b
              JOIN txn_headers h ON h.id = b.header_id
              JOIN accounts a ON a.id = b.account_id
             WHERE b.ledger_id = @L
            """, new { L = ledgerId })).ToList();
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>Every header and leg of the ledger, ids included, for "nothing changed".</summary>
    private static async Task<string> DigestAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        return await conn.ExecuteScalarAsync<string>("""
            SELECT md5(string_agg(x, E'\n' ORDER BY x))
              FROM (SELECT to_jsonb(h)::text AS x FROM txn_headers h WHERE h.ledger_id = @L
                    UNION ALL
                    SELECT to_jsonb(l)::text FROM txn_legs l WHERE l.ledger_id = @L) d
            """, new { L = ledgerId }) ?? "";
    }

    private static string Diff(List<string> expected, List<string> actual) =>
        "expected only:\n  " + string.Join("\n  ", expected.Except(actual, StringComparer.Ordinal))
        + "\nactual only:\n  " + string.Join("\n  ", actual.Except(expected, StringComparer.Ordinal));

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message + (e is PostgresException p ? " " + p.MessageText : ""));
        return string.Join(" | ", parts);
    }
}
