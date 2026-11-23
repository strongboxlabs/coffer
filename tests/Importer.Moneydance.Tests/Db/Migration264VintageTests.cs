using System.Globalization;
using System.Text;

using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Migration 264 on a ledger an earlier importer wrote: every importer vintage's
/// placement of the same Moneydance data, upgraded from 259, ends exactly as the
/// importer writes it now, stored values included.
/// </summary>
/// <remarks>
/// <para><b>How the earlier state is built.</b> The current importer imports the export
/// below into this test's database; that is the expected result, S. Its rows are then
/// copied into a database staged at 259 (no originating account, no stored notes), and
/// only the memo and tag fields are rewound to what one vintage wrote, from values
/// written out by hand per row below, never derived the way 264 derives them; the
/// brokerage cash move also loses the action 260 restores. The upgrade then runs
/// through MigrationRunner, 260 to 266 with the stored-notes step, and the result must
/// equal S: every leg's memo and tags, every header's, and every stored presentation.</para>
/// <para><b>The vintages.</b> V1, before 2026-09-30: every tag on the header, names
/// trimmed. V2: the transaction's tags on the header, each line's on its home leg,
/// names trimmed. V3, from 2026-10-07: as V2, names cut to 64. Every vintage kept the
/// memo (else the feed's) on the header verbatim and, on a split, each line's text on
/// both of its legs; none imported a template's or an investment row's tags.</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class Migration264VintageTests : DatabaseTest
{
    public Migration264VintageTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly string LongName = new('x', 70);
    private static readonly string LongNameCapped = new('x', 64);
    private static readonly string LongShared = new('y', 70);

    private static readonly string Export = $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-chk","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-sav","type":"b","name":"Savings","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-brk2","type":"v","name":"Other brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-gro","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-hh","type":"e","name":"Household","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-fee","type":"e","name":"Commission","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-inc","type":"i","name":"Dividends","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-blank","type":"e","name":"","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-gro2","type":"e","name":"Food","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-exp2","type":"e","name":"Other expenses","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},

            {"obj_type":"txn","id":"t-one","acctid":"a-chk","desc":"Grocer","memo":" weekly shop ","dt":"20260105",
             "tags":"Family","0.id":"s0","0.acctid":"a-gro","0.desc":"receipt 12","0.tags":"food",
             "0.samt":"500","0.pamt":"-500"},
            {"obj_type":"txn","id":"t-copy","acctid":"a-chk","desc":"Grocer","memo":"top up","dt":"20260106",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"Grocer","0.samt":"300","0.pamt":"-300"},
            {"obj_type":"txn","id":"t-multi","acctid":"a-chk","desc":"Store","memo":"weekend","dt":"20260107",
             "tags":"family","0.id":"s0","0.acctid":"a-gro","0.desc":"food","0.tags":"weekly","0.samt":"300","0.pamt":"-300",
             "1.id":"s1","1.acctid":"a-sav","1.desc":"to savings","1.samt":"200","1.pamt":"-200"},
            {"obj_type":"txn","id":"t-sym","acctid":"a-chk","desc":"Store","memo":"two lines","dt":"20260108",
             "0.id":"s0","0.acctid":"a-hh","0.desc":"soap","0.samt":"100","0.pamt":"-100",
             "1.id":"s1","1.acctid":"a-hh","1.desc":"towels","1.samt":"200","1.pamt":"-200"},
            {"obj_type":"txn","id":"t-xfr","acctid":"a-brk","desc":"Sweep","memo":"monthly","dt":"20260109",
             "tags":"cash","qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
             "0.id":"s0","0.acctid":"a-chk","0.desc":"into checking","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-fund","acctid":"a-chk","desc":"Fund","memo":"top up brokerage","dt":"20260110",
             "0.id":"s0","0.acctid":"a-brk","0.desc":"deposit","0.samt":"50000","0.pamt":"-50000"},
            {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"BUY FUND A","memo":"rebalance","dt":"20260111",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell","tags":"plan,PLAN",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-fee","1.invest.splittype":"fee","1.desc":"commission","1.tags":"cost",
             "1.samt":"500","1.pamt":"-500"},
            {"obj_type":"txn","id":"t-buyx","acctid":"a-brk","desc":"BUY FUND A","memo":"from the other","dt":"20260112",
             "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-brk2","1.invest.splittype":"xfr","1.desc":"funding","1.samt":"-10000","1.pamt":"10000"},
            {"obj_type":"txn","id":"t-buy1","acctid":"a-brk","desc":"BUY FUND A","memo":"single","dt":"20260113",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell","tags":"solo",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"50000","0.pamt":"-5000"},
            {"obj_type":"txn","id":"t-long","acctid":"a-chk","desc":"Long","memo":"long tag","dt":"20260114","tags":"{{LongShared}}",
             "0.id":"s0","0.acctid":"a-gro","0.tags":"{{LongName}}","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-blank","acctid":"a-chk","desc":"Feed","memo":"   ","ol.orig-memo":"feed memo","dt":"20260115",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-gap","acctid":"a-chk","desc":"Gap","memo":"gap","dt":"20260116","tags":"g",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"first","0.samt":"100","0.pamt":"-100",
             "1.id":"s1","1.acctid":"a-chk","1.desc":"self","1.samt":"5","1.pamt":"-5",
             "2.id":"s2","2.acctid":"a-hh","2.desc":"third","2.samt":"200","2.pamt":"-200"},

            {"obj_type":"txn","id":"t-eqmemo","acctid":"a-chk","desc":"Same","memo":"same text","dt":"20260117",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"same text","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-blankdesc","acctid":"a-chk","desc":"Blank desc","memo":"bd","dt":"20260118",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"   ","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-ws","acctid":"a-chk","desc":"Spaces","memo":"\t padded ","dt":"20260119",
             "tags":" a1　, b1\t","0.id":"s0","0.acctid":"a-hh","0.tags":" c1","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-comma","acctid":"a-chk","desc":"Commas","memo":"commas","dt":"20260120",
             "tags":"t1,t2, t3","0.id":"s0","0.acctid":"a-gro","0.tags":"t3,t4","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-many","acctid":"a-chk","desc":"Many","memo":"many tags","dt":"20260121",
             "tags":"m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"first line","0.tags":"n01,n02,n03,n04,n05,n06,n07,n08,n09,n10",
             "0.samt":"100","0.pamt":"-100",
             "1.id":"s1","1.acctid":"a-hh","1.desc":"second line","1.samt":"50","1.pamt":"-50"},
            {"obj_type":"txn","id":"t-cafe1","acctid":"a-chk","desc":"Cafe","memo":"c1","dt":"20260122","tags":"Café",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-cafe2","acctid":"a-chk","desc":"Cafe","memo":"c2","dt":"20260123","tags":"CAFÉ",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-blankcat","acctid":"a-chk","desc":"Blank category","memo":"bc","dt":"20260124",
             "0.id":"s0","0.acctid":"a-gro","0.samt":"100","0.pamt":"-100",
             "1.id":"s1","1.acctid":"a-blank","1.desc":"blank line","1.samt":"30","1.pamt":"-30"},
            {"obj_type":"txn","id":"t-brk2","acctid":"a-brk","desc":"Two lines","memo":"two","dt":"20260125",
             "0.id":"s0","0.acctid":"a-gro","0.desc":"g","0.samt":"100","0.pamt":"-100",
             "1.id":"s1","1.acctid":"a-hh","1.desc":"h","1.samt":"50","1.pamt":"-50"},
            {"obj_type":"txn","id":"t-ixfr","acctid":"a-brk","desc":"Sweep out","memo":"inv xfr","dt":"20260126","tags":"ix",
             "invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s0","0.acctid":"a-chk","0.invest.splittype":"xfr","0.desc":"to checking","0.samt":"700","0.pamt":"-700"},
            {"obj_type":"txn","id":"t-divx","acctid":"a-brk","desc":"Dividend","memo":"div","dt":"20260127","tags":"dv",
             "invest.txntype":"divx","xfer_type":"xfrtp_dividendxfr",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0",
             "1.id":"s1","1.acctid":"a-inc","1.invest.splittype":"inc","1.desc":"dividend","1.samt":"-500","1.pamt":"500",
             "2.id":"s2","2.acctid":"a-chk","2.invest.splittype":"xfr","2.desc":"sweep","2.samt":"500","2.pamt":"-500"},
            {"obj_type":"txn","id":"t-misc","acctid":"a-brk","desc":"Cap gain","memo":"misc","dt":"20260128",
             "invest.txntype":"inc","xfer_type":"xfrtp_miscincexp",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0",
             "1.id":"s1","1.acctid":"a-fee","1.invest.splittype":"fee","1.desc":"fee","1.samt":"20","1.pamt":"-20",
             "2.id":"s2","2.acctid":"a-inc","2.invest.splittype":"inc","2.samt":"-300","2.pamt":"300"},
            {"obj_type":"txn","id":"t-payee","acctid":"a-brk","desc":"BUY FUND A","memo":"rebal2","dt":"20260129",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000",
             "1.id":"s1","1.acctid":"a-fee","1.invest.splittype":"fee","1.desc":"BUY FUND A","1.samt":"500","1.pamt":"-500"},
            {"obj_type":"txn","id":"t-num","acctid":"a-chk","desc":"Numbers","memo":5,"ol.orig-memo":"feed","dt":"20260130","tags":3,
             "0.id":"s0","0.acctid":"a-gro","0.desc":"own text","0.tags":7,"0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-move","acctid":"a-chk","desc":"Move","memo":"to savings","dt":"20260131",
             "0.id":"s0","0.acctid":"a-sav","0.samt":"100","0.pamt":"-100"},
            {"obj_type":"txn","id":"t-cat","acctid":"a-gro2","desc":"Cat","memo":"cat to cat","dt":"20260201",
             "0.id":"s0","0.acctid":"a-hh","0.samt":"40","0.pamt":"-40"},
            {"obj_type":"txn","id":"t-dup","acctid":"a-brk","desc":"Dividend","memo":"d1","dt":"20260202",
             "invest.txntype":"divx","xfer_type":"xfrtp_dividendxfr",
             "0.id":"s0","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0",
             "1.id":"s1","1.acctid":"a-inc","1.invest.splittype":"inc","1.desc":"d2","1.samt":"-500","1.pamt":"500",
             "2.id":"s2","2.acctid":"a-fee","2.invest.splittype":"fee","2.desc":"d1","2.samt":"20","2.pamt":"-20",
             "3.id":"s3","3.acctid":"a-chk","3.invest.splittype":"xfr","3.desc":"d2","3.samt":"480","3.pamt":"-480"},

            {"obj_type":"reminder","id":"r-1","desc":"Rent","memo":"rent memo","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1","is_loan_reminder":"0",
             "txn.acctid":"a-chk","txn.desc":"Rent","txn.memo":"txn memo","txn.tags":"home",
             "txn.0.acctid":"a-hh","txn.0.desc":"landlord","txn.0.tags":"Fixed","txn.0.samt":"1000","txn.0.pamt":"-1000"},
            {"obj_type":"reminder","id":"r-2","desc":"Shop","memo":"","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1","is_loan_reminder":"0",
             "txn.acctid":"a-chk","txn.desc":"Shop","txn.memo":"txn two","txn.tags":"both",
             "txn.0.acctid":"a-hh","txn.0.desc":"line a","txn.0.samt":"100","txn.0.pamt":"-100",
             "txn.1.acctid":"a-gro","txn.1.desc":"line b","txn.1.samt":"200","txn.1.pamt":"-200"},
            {"obj_type":"reminder","id":"r-3","desc":"Move","memo":"move","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1","is_loan_reminder":"0",
             "txn.acctid":"a-chk","txn.desc":"Move","txn.memo":"move txn",
             "txn.0.acctid":"a-sav","txn.0.samt":"100","txn.0.pamt":"-100"},
            {"obj_type":"reminder","id":"r-inv","desc":"Sweep","memo":"inv rem","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1","is_loan_reminder":"0",
             "txn.acctid":"a-brk","txn.desc":"Sweep","txn.memo":"inv txn","txn.invest.txntype":"bank","txn.xfer_type":"xfrtp_bank",
             "txn.0.acctid":"a-chk","txn.0.invest.splittype":"xfr","txn.0.samt":"100","txn.0.pamt":"-100"}
          ]
        }
        """;

    /// <summary>
    /// What one vintage wrote for one row: the header's memo, each posting's leg memo
    /// (on both legs: the old importers wrote a split line's text on each), the
    /// header's tag names, and each posting's home-leg tag names.
    /// </summary>
    private sealed record OldRow(
        string ExternalId,
        string? HeaderMemo,
        Dictionary<int, string> LineMemos,
        string[] HeaderTags,
        Dictionary<int, string[]> HomeTags);

    private static Dictionary<int, string> NoLines => [];
    private static Dictionary<int, string[]> NoLegTags => [];

    /// <summary>Every row of the export as vintage <paramref name="v"/> wrote it, by hand.</summary>
    private static List<OldRow> Vintage(int v)
    {
        var split = v >= 2;
        string Cap(string name) => v == 3 && name.Length > 64 ? name[..64] : name;
        return
        [
            new("t-one", " weekly shop ", NoLines,
                split ? ["Family"] : ["Family", "food"],
                split ? new() { [0] = ["food"] } : NoLegTags),
            new("t-copy", "top up", NoLines, [], NoLegTags),
            new("t-multi", "weekend", new() { [0] = "food", [1] = "to savings" },
                split ? ["family"] : ["family", "weekly"],
                split ? new() { [0] = ["weekly"] } : NoLegTags),
            new("t-sym", "two lines", new() { [0] = "soap", [1] = "towels" }, [], NoLegTags),
            new("t-xfr", "monthly", NoLines, ["cash"], NoLegTags),
            new("t-fund", "top up brokerage", NoLines, [], NoLegTags),
            new("t-buy", "rebalance", NoLines, [], NoLegTags),
            new("t-buyx", "from the other", NoLines, [], NoLegTags),
            new("t-buy1", "single", NoLines, [], NoLegTags),
            new("t-long", "long tag", NoLines,
                split ? [Cap(LongShared)] : [LongName, LongShared],
                split ? new() { [0] = [Cap(LongName)] } : NoLegTags),
            new("t-blank", "feed memo", NoLines, [], NoLegTags),
            new("t-gap", "gap", new() { [0] = "first", [2] = "third" }, ["g"], NoLegTags),
            new("mdreminder:r-1", "rent memo", NoLines, [], NoLegTags),
            new("t-eqmemo", "same text", NoLines, [], NoLegTags),
            new("t-blankdesc", "bd", NoLines, [], NoLegTags),
            // The memo as Moneydance holds it, white space and all; names trimmed of
            // .NET white space (tab, no-break space, ideographic space).
            new("t-ws", "\t padded ", NoLines,
                split ? ["a1", "b1"] : ["a1", "b1", "c1"],
                split ? new() { [0] = ["c1"] } : NoLegTags),
            new("t-comma", "commas", NoLines,
                split ? ["t1", "t2", "t3"] : ["t1", "t2", "t3", "t4"],
                split ? new() { [0] = ["t3", "t4"] } : NoLegTags),
            new("t-many", "many tags", new() { [0] = "first line", [1] = "second line" },
                split ? [.. Many("m", 15)] : [.. Many("m", 15), .. Many("n", 10)],
                split ? new() { [0] = [.. Many("n", 10)] } : NoLegTags),
            new("t-cafe1", "c1", NoLines, ["Café"], NoLegTags),
            new("t-cafe2", "c2", NoLines, ["CAFÉ"], NoLegTags),
            new("t-blankcat", "bc", new() { [1] = "blank line" }, [], NoLegTags),
            new("t-brk2", "two", new() { [0] = "g", [1] = "h" }, [], NoLegTags),
            new("t-ixfr", "inv xfr", NoLines, [], NoLegTags),
            new("t-divx", "div", NoLines, [], NoLegTags),
            new("t-misc", "misc", NoLines, [], NoLegTags),
            new("t-payee", "rebal2", NoLines, [], NoLegTags),
            // Fields that are not strings are no value: the feed memo is the one written, and a
            // numeric tag list is no tags, under any vintage.
            new("t-num", "feed", NoLines, [], NoLegTags),
            new("t-move", "to savings", NoLines, [], NoLegTags),
            new("t-cat", "cat to cat", NoLines, [], NoLegTags),
            new("t-dup", "d1", NoLines, [], NoLegTags),
            new("mdreminder:r-3", "move", NoLines, [], NoLegTags),
            new("mdreminder:r-inv", "inv rem", NoLines, [], NoLegTags),
        ];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_vintage_upgrades_to_exactly_what_the_importer_writes_now(int vintage)
    {
        var (ledgerId, expected) = await ImportAsync();
        Assert.Contains(expected, l => l.Contains(LongNameCapped, StringComparison.Ordinal));

        var old = await OldInstallAsync($"v{vintage}", ledgerId);
        await RewindAsync(old, ledgerId, Vintage(vintage));
        UpgradeFrom259(old);

        var actual = await SnapshotAsync(old, ledgerId);
        Assert.True(expected.SequenceEqual(actual), Diff(expected, actual));
        await AssertStoredIsComputedAsync(old, ledgerId);

        // The tags no vintage imported (templates', investment rows') are created by the
        // upgrade from the data alone: the id from the ledger and the name, the
        // created_at of the earliest header that carries the tag.
        await using var conn = new NpgsqlConnection(old);
        await conn.OpenAsync();
        var created = (await conn.QueryAsync<(string Name, bool IdFromData, bool StampFromData)>("""
            SELECT t.name,
                   t.id = md5(t.ledger_id::text || '/' || t.name)::uuid,
                   t.created_at = (SELECT min(h.created_at) FROM txn_headers h
                                    WHERE h.ledger_id = t.ledger_id
                                      AND (EXISTS (SELECT 1 FROM txn_header_tags x WHERE x.header_id = h.id AND x.tag_id = t.id)
                                           OR EXISTS (SELECT 1 FROM txn_leg_tags x JOIN txn_legs l ON l.id = x.leg_id
                                                       WHERE l.header_id = h.id AND x.tag_id = t.id)))
              FROM tags t
             WHERE t.ledger_id = @L AND t.name = ANY(@Names)
             ORDER BY t.name
            """, new { L = ledgerId, Names = new[] { "Fixed", "both", "cost", "home", "plan", "solo" } })).ToList();
        Assert.Equal(6, created.Count);
        Assert.All(created, t => Assert.True(t.IdFromData && t.StampFromData, $"tag {t.Name} was not created from the data alone"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Every_register_shows_what_the_earlier_release_showed_but_the_intended_changes(int vintage)
    {
        var (ledgerId, _) = await ImportAsync();
        var old = await OldInstallAsync($"display_v{vintage}", ledgerId);
        await RewindAsync(old, ledgerId, Vintage(vintage));
        // The earlier release's read, on the database as that release left it.
        var before = await RegisterDisplay.BeforeAsync(old, ledgerId);
        Assert.True(before.Count > 60, $"the baseline read only {before.Count} register rows");

        UpgradeFrom259(old);

        AssertOnlyIntendedChanges(
            RegisterDisplay.Differences(before, await RegisterDisplay.AfterAsync(old, ledgerId)),
            vintage == 1 ? V1Changes : V3Changes);
    }

    /// <summary>The register rows' display changes are exactly <paramref name="intended"/>.</summary>
    private static void AssertOnlyIntendedChanges(
        IReadOnlySet<string> differences, IReadOnlyList<(string Change, string Reason)> intended)
    {
        var expected = intended.Select(c => c.Change).ToHashSet(StringComparer.Ordinal);
        var unexpected = differences.Where(d => !expected.Contains(d)).ToList();
        var missing = intended.Where(c => !differences.Contains(c.Change)).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Register display after 264 against the earlier release's:"
            + string.Concat(unexpected.Select(u => "\n  unexpected:            " + u))
            + string.Concat(missing.Select(m => $"\n  intended, not seen:    {m.Change} ({m.Reason})")));
    }

    private static readonly (string Change, string Reason)[] V1Changes =
    [
        ("t-num a-gro row: 'feed' [] → 'own text' []",
         "ADR-0108 D11 Step A: the other side's own text; the fields that are not strings are no value"),
        ("t-blankcat a-gro line0: 'bc' [] → — []",
         "ADR-0108 D1: a split's line seen from another account shows its own, else its line's other leg's"),
        ("t-buy a-brk event: 'rebalance' [] → 'rebalance · commission' [cost,plan]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy a-fee line1: 'rebalance' [] → 'rebalance · commission' [cost,plan]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy1 a-brk event: 'single' [] → 'single' [solo]",
         "ADR-0108 D11 Step B: an investment row's tags are imported"),
        ("t-buyx a-brk event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-buyx a-brk2 event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-cafe2 a-chk row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-cafe2 a-gro row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-comma a-chk row: 'commas' [t1,t2,t3,t4] → 'commas' [t3,t4]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-comma a-gro row: 'commas' [t1,t2,t3,t4] → 'commas' [t1,t2,t3]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-divx a-brk event: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-chk line1: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-inc line0: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-dup a-brk event: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-chk line2: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-fee line1: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-inc line0: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-fund a-brk event: 'top up brokerage' [] → 'deposit' []",
         "ADR-0108 D11 Step A: the other side's own text, which differs from the payee and the memo"),
        ("t-gap a-chk line0: 'first' [] → 'first' [g]",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-gap a-chk line2: 'third' [] → 'third' [g]",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-gap a-chk parent: 'gap' [g] → 'gap' []",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-ixfr a-brk event: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-ixfr a-chk row: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-long a-chk row: 'long tag' [xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx,yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] → 'long tag' [xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-long a-gro row: 'long tag' [xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx,yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] → 'long tag' [yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-many a-chk line0: 'first line' [] → 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk line1: 'second line' [] → 'second line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk parent: 'many tags' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10] → 'many tags' []",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-hh line1: 'second line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10] → 'second line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-misc a-brk event: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-misc a-fee line0: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-misc a-inc line1: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-multi a-chk line0: 'food' [] → 'food' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk line1: 'to savings' [] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk parent: 'weekend' [family,weekly] → 'weekend' []",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-gro line0: 'food' [family,weekly] → 'food' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-sav line1: 'to savings' [family,weekly] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-one a-chk row: ' weekly shop ' [Family,food] → 'weekly shop' [food]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-one a-gro row: ' weekly shop ' [Family,food] → 'receipt 12' [Family]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-sym a-hh parent: 'two lines' [] → (no row)",
         "ADR-0108 D14: the other side of a split never groups; one row per line"),
        ("t-ws a-chk row: '\t padded\u00a0' [a1,b1,c1] → 'padded' [c1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-ws a-hh row: '\t padded\u00a0' [a1,b1,c1] → 'padded' [a1,b1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-xfr a-brk event: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
        ("t-xfr a-chk row: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
    ];

    private static readonly (string Change, string Reason)[] V3Changes =
    [
        ("t-num a-gro row: 'feed' [] → 'own text' []",
         "ADR-0108 D11 Step A: the other side's own text; the fields that are not strings are no value"),
        ("t-blankcat a-gro line0: 'bc' [] → — []",
         "ADR-0108 D1: a split's line seen from another account shows its own, else its line's other leg's"),
        ("t-buy a-brk event: 'rebalance' [] → 'rebalance · commission' [cost,plan]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy a-fee line1: 'rebalance' [] → 'rebalance · commission' [cost,plan]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy1 a-brk event: 'single' [] → 'single' [solo]",
         "ADR-0108 D11 Step B: an investment row's tags are imported"),
        ("t-buyx a-brk event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-buyx a-brk2 event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-cafe2 a-chk row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-cafe2 a-gro row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-comma a-chk row: 'commas' [t1,t2,t3] → 'commas' [t3,t4]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-divx a-brk event: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-chk line1: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-inc line0: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-dup a-brk event: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-chk line2: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-fee line1: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-inc line0: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-fund a-brk event: 'top up brokerage' [] → 'deposit' []",
         "ADR-0108 D11 Step A: the other side's own text, which differs from the payee and the memo"),
        ("t-gap a-chk line0: 'first' [] → 'first' [g]",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-gap a-chk line2: 'third' [] → 'third' [g]",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-gap a-chk parent: 'gap' [g] → 'gap' []",
         "ADR-0108 D11 Step A, D15: each line carries its own tags with the transaction's, shown on the line, not the header"),
        ("t-ixfr a-brk event: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-ixfr a-chk row: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-long a-chk row: 'long tag' [yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] → 'long tag' [xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-many a-chk line0: 'first line' [] → 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk line1: 'second line' [] → 'second line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk parent: 'many tags' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15] → 'many tags' []",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-gro line0: 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15] → 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-misc a-brk event: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-misc a-fee line0: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-misc a-inc line1: 'misc' [] → 'misc · fee' []",
         "ADR-0108 D11 Step B: the fee text joins the event's one memo"),
        ("t-multi a-chk line0: 'food' [] → 'food' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk line1: 'to savings' [] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk parent: 'weekend' [family] → 'weekend' []",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-gro line0: 'food' [family] → 'food' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-sav line1: 'to savings' [family] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-one a-chk row: ' weekly shop ' [Family] → 'weekly shop' [food]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-one a-gro row: ' weekly shop ' [Family] → 'receipt 12' [Family]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-sym a-hh parent: 'two lines' [] → (no row)",
         "ADR-0108 D14: the other side of a split never groups; one row per line"),
        ("t-ws a-chk row: '\t padded\u00a0' [a1,b1] → 'padded' [c1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-ws a-hh row: '\t padded\u00a0' [a1,b1] → 'padded' [a1,b1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-xfr a-brk event: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
        ("t-xfr a-chk row: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
    ];

    private static readonly (string Change, string Reason)[] EditedChanges =
    [
        ("t-num a-gro row: 'feed' [] → 'own text' []",
         "ADR-0108 D11 Step A: the other side's own text; the fields that are not strings are no value"),
        ("occ-r3 a-chk line0: 'move' [] → — []",
         "ADR-0108 D1: an occurrence split from its other side; the old home's line shows its own, else its line's other leg's"),
        ("occ-r3 a-hh line1: 'move' [] → — []",
         "ADR-0108 D1: an occurrence split from its other side; the old home's line shows its own, else its line's other leg's"),
        ("t-blankcat a-gro line0: 'bc' [] → — []",
         "ADR-0108 D1: a split's line seen from another account shows its own, else its line's other leg's"),
        ("t-buy a-brk event: 'rebalance' [mine] → 'rebalance · commission' [mine]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy a-fee line1: 'rebalance' [mine] → 'rebalance · commission' [mine]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-buy1 a-brk event: 'single' [] → 'single' [solo]",
         "ADR-0108 D11 Step B: an investment row's tags are imported"),
        ("t-buyx a-brk event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-buyx a-brk2 event: 'from the other' [] → 'from the other · funding' []",
         "ADR-0108 D11 Step B: the funding text joins the event's one memo"),
        ("t-cafe2 a-chk row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-cafe2 a-gro row: 'c2' [CAFÉ] → 'c2' [Café]",
         "ADR-0108 D11 Step M: case variants merge into the earliest created"),
        ("t-divx a-brk event: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-chk line1: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-divx a-inc line0: 'div' [] → 'div · dividend · sweep' [dv]",
         "ADR-0108 D11 Step B: the event's split texts join its one memo; its tags are imported"),
        ("t-dup a-brk event: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-chk line2: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-fee line1: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-dup a-inc line0: 'd1' [] → 'd1 · d2' []",
         "ADR-0108 D11 Step B and the event rule: the event's split texts join its one memo, each distinct one once"),
        ("t-eqmemo a-gro line0: 'same text' [] → — []",
         "ADR-0108 D1: split from its home, its lines seen from another account show their own, else their line's other leg's"),
        ("t-eqmemo a-hh line1: 'same text' [] → — []",
         "ADR-0108 D1: split from its home, its lines seen from another account show their own, else their line's other leg's"),
        ("t-gap a-gro line0: 'first' [g renamed] → 'first' []",
         "ADR-0108 D1: with its tag renamed the tags are the user's; a line seen from another account no longer shows the transaction's"),
        ("t-gap a-hh line2: 'third' [g renamed] → 'third' []",
         "ADR-0108 D1: with its tag renamed the tags are the user's; a line seen from another account no longer shows the transaction's"),
        ("t-ixfr a-brk event: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-ixfr a-chk row: 'inv xfr' [] → 'inv xfr · to checking' [ix]",
         "ADR-0108 D11 Step B: the transfer text joins the event's one memo; its tags are imported"),
        ("t-long a-chk row: 'long tag' [yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] → 'long tag' [xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx]",
         "ADR-0108 D11 Step A: the home shows its split's own tags, the other side the transaction's"),
        ("t-many a-chk line0: 'first line' [] → 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk line1: 'second line' [] → 'second line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-chk parent: 'many tags' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15] → 'many tags' []",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-many a-gro line0: 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15] → 'first line' [m01,m02,m03,m04,m05,m06,m07,m08,m09,m10,m11,m12,m13,m14,m15,n01,n02,n03,n04,n05,n06,n07,n08,n09,n10]",
         "ADR-0108 D11 Step A, D12, D15: each line carries its own tags with the transaction's, uncapped, shown on the line"),
        ("t-move a-chk line0: 'to savings' [] → — []",
         "ADR-0108 D1: split from its other side, the old home's line shows its own, else its line's other leg's"),
        ("t-move a-gro line1: 'to savings' [] → — []",
         "ADR-0108 D1: split from its other side, the old home's line shows its own, else its line's other leg's"),
        ("t-multi a-chk line0: 'food edited' [] → 'food edited' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk line1: 'to savings' [] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-chk parent: 'weekend' [family] → 'weekend' []",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-gro line0: 'food' [family] → 'food' [Family,weekly]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-multi a-sav line1: 'to savings' [family] → 'to savings' [Family]",
         "ADR-0108 D11 Step A, Step M, D15: each line carries its own tags with the transaction's; the variant spelled as the survivor"),
        ("t-one a-chk row: ' weekly shop ' [Family,added] → 'weekly shop' [Family,added,food]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-one a-gro row: ' weekly shop ' [Family,added] → 'receipt 12' [Family,added]",
         "ADR-0108 D11 Step A, D13: the home shows its split's tags and the trimmed memo, the other side its own text and the transaction's tags"),
        ("t-sym a-chk row: 'soap' [] → 'two lines · soap' []",
         "ADR-0108 D16: collapsed to one line, the header's memo was shown on neither side; it joins the entered side"),
        ("t-ws a-chk row: '\t padded\u00a0' [a1,b1] → 'padded' [c1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-ws a-hh row: '\t padded\u00a0' [a1,b1] → 'padded' [a1,b1]",
         "ADR-0108 D11 Step A, D13: the trimmed memo; the home shows its split's tags, the other side the transaction's"),
        ("t-xfr a-brk event: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
        ("t-xfr a-chk row: 'monthly' [cash] → 'monthly · into checking' [cash]",
         "ADR-0108 D11 Step A: the cash move's other text joins its one memo"),
    ];


    [Fact]
    public async Task Rows_edited_after_the_import_keep_the_edit_and_a_rerun_of_264_changes_nothing()
    {
        var (ledgerId, _) = await ImportAsync();
        var old = await OldInstallAsync("edited", ledgerId);
        await RewindAsync(old, ledgerId, Vintage(3));
        await using (var conn = new NpgsqlConnection(old))
        {
            await conn.OpenAsync();
            // A memo changed in the register, a tag added to a one-split, a tag added
            // to an investment event: each field is the user's now and stays theirs.
            await conn.ExecuteAsync("""
                UPDATE txn_headers SET memo = 'edited by hand' WHERE ledger_id = @L AND external_id = 't-copy';
                """, new { L = ledgerId });
            await TagHeaderAsync(conn, ledgerId, "t-one", "added");
            await TagHeaderAsync(conn, ledgerId, "t-buy", "mine");
            await EditAsync(conn, ledgerId);
        }
        var before = await RegisterDisplay.BeforeAsync(old, ledgerId);
        // Up to 266, then 264 again over its own result, as after a stop between its
        // commit and its journal row (266 would run after it on that start).
        var work = UpgradeFrom259(old, before: "266_");
        var once = await SnapshotAsync(old, ledgerId);
        await using (var conn = new NpgsqlConnection(old))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%264_annotation_data.sql'");
        }
        MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(old), work, NullLogger.Instance);
        Assert.Equal(once, await SnapshotAsync(old, ledgerId));
        UpgradeFrom259(old);

        Assert.Equal(
            ["chk 0 'edited by hand' [] own", "gro 0 NULL [] inherited 'edited by hand'"],
            await LegsOfAsync(old, ledgerId, "t-copy"));
        Assert.Equal(
            ["chk 0 'weekly shop' [Family,added,food] own", "gro 0 'receipt 12' [Family,added] own"],
            await LegsOfAsync(old, ledgerId, "t-one"));
        Assert.Equal(("rebalance · commission", "mine"), await HeaderOfAsync(old, ledgerId, "t-buy"));
        await AssertStoredIsComputedAsync(old, ledgerId);

        // Converted to an event: owned by the brokerage, which holds the memo; the bank inherits.
        Assert.Equal(
            ["brk 0 'top up brokerage' [] own", "chk 0 NULL [] inherited 'top up brokerage'"],
            await LegsOfAsync(old, ledgerId, "t-fund"));
        // Split from the other side, live and fired: the splitter owns the split and the
        // header keeps its memo; the old home is a line of it.
        Assert.Equal(("to savings", ""), await HeaderOfAsync(old, ledgerId, "t-move"));
        Assert.Equal(("move", ""), await HeaderOfAsync(old, ledgerId, "occ-r3"));
        Assert.Equal("a-sav", await OwnerOfAsync(old, ledgerId, "t-move"));
        Assert.Equal("a-sav", await OwnerOfAsync(old, ledgerId, "occ-r3"));
        // Fired and left alone: the template's memo on the side it was posted from.
        Assert.Equal(
            ["chk 0 'rent memo' [] own", "hh 0 NULL [] inherited 'rent memo'"],
            await LegsOfAsync(old, ledgerId, "occ-r1"));
        // Home category merged away: one category takes the memo, the other inherits it.
        Assert.Equal((null, ""), await HeaderOfAsync(old, ledgerId, "t-cat"));
        // Payloads 264 cannot read are ignored, the rows treated as edited: the one-split
        // goes through step C, the investment row keeps what it holds.
        Assert.Equal(
            ["chk 0 'c2' [Café] own", "gro 0 NULL [] inherited 'c2'"],
            await LegsOfAsync(old, ledgerId, "t-cafe2"));
        Assert.Equal(("misc", ""), await HeaderOfAsync(old, ledgerId, "t-misc"));

        // Every register shows what it showed before, but the intended changes.
        AssertOnlyIntendedChanges(
            RegisterDisplay.Differences(before, await RegisterDisplay.AfterAsync(old, ledgerId)), EditedChanges);
    }

    private static IEnumerable<string> Many(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => prefix + i.ToString("D2", CultureInfo.InvariantCulture));

    /// <summary>
    /// What users did to imported rows before the upgrade, each as the release before it
    /// wrote it.
    /// </summary>
    private static async Task EditAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var p = new { L = ledgerId };

        // Converted to an event: the bank-home transfer into the brokerage given an
        // action in the brokerage register (the action set, the legs written again).
        await conn.ExecuteAsync("""
            WITH h AS (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-fund'),
                 gone AS (DELETE FROM txn_legs l USING h WHERE l.header_id = h.id RETURNING l.*)
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, posting_role,
                                  account_postings_on_header, header_total_postings)
            SELECT gen_random_uuid(), g.header_id, g.ledger_id, g.account_id, g.posting_index, g.amount, 'transfer',
                   g.account_postings_on_header, g.header_total_postings
              FROM gone g;
            UPDATE txn_headers SET action = 'transfer' WHERE ledger_id = @L AND external_id = 't-fund';
            """, p);

        // Split from its other side: the transfer into savings split into two lines
        // from savings.
        await SplitFromAsync(conn, ledgerId, "t-move", "a-sav", "a-gro");

        // Two occurrences fired from templates before the upgrade, clones of them: one
        // left as fired, one split from its other side afterwards.
        await FireAsync(conn, ledgerId, "r-1", "occ-r1");
        await FireAsync(conn, ledgerId, "r-3", "occ-r3");
        await SplitFromAsync(conn, ledgerId, "occ-r3", "a-sav", "a-hh");

        // The home category merged into another: its legs moved, the category gone.
        await conn.ExecuteAsync("""
            UPDATE txn_legs SET account_id = (SELECT id FROM accounts WHERE ledger_id = @L AND external_id = 'a-exp2')
             WHERE account_id = (SELECT id FROM accounts WHERE ledger_id = @L AND external_id = 'a-gro2');
            DELETE FROM txn_header_account_balances
             WHERE account_id = (SELECT id FROM accounts WHERE ledger_id = @L AND external_id = 'a-gro2');
            DELETE FROM accounts WHERE ledger_id = @L AND external_id = 'a-gro2';
            """, p);

        // A split line's memo changed on its split side.
        await conn.ExecuteAsync("""
            UPDATE txn_legs l SET leg_memo = 'food edited'
              FROM txn_headers h, accounts a
             WHERE h.id = l.header_id AND a.id = l.account_id
               AND h.ledger_id = @L AND h.external_id = 't-multi' AND l.posting_index = 0 AND a.external_id = 'a-chk';
            """, p);

        // A tag renamed, and a tag merged into another.
        await conn.ExecuteAsync("""
            UPDATE tags SET name = 'g renamed' WHERE ledger_id = @L AND name = 'g';
            UPDATE txn_leg_tags SET tag_id = (SELECT id FROM tags WHERE ledger_id = @L AND name = 't1')
             WHERE tag_id = (SELECT id FROM tags WHERE ledger_id = @L AND name = 't4');
            DELETE FROM tags WHERE ledger_id = @L AND name = 't4';
            """, p);

        // Reshaped: a one-split split into two lines from its home, and a split collapsed
        // to one line (the line kept with its memos; the header keeps its own).
        await SplitFromAsync(conn, ledgerId, "t-eqmemo", "a-chk", "a-hh");
        await conn.ExecuteAsync("""
            DELETE FROM txn_legs l USING txn_headers h
             WHERE h.id = l.header_id AND h.ledger_id = @L AND h.external_id = 't-sym' AND l.posting_index = 1;
            SELECT fn_recompute_posting_counts_for_header(id) FROM txn_headers WHERE ledger_id = @L AND external_id = 't-sym';
            """, p);

        // Payloads 264 cannot read: an array and a bare string. They are ignored (the
        // rows count as edited), not a failure.
        await conn.ExecuteAsync("""
            UPDATE txn_headers SET provider_raw_payload = '["not", "an", "object"]'::jsonb
             WHERE ledger_id = @L AND external_id = 't-cafe2';
            UPDATE txn_headers SET provider_raw_payload = '"x"'::jsonb
             WHERE ledger_id = @L AND external_id = 't-misc';
            """, p);
    }

    /// <summary>A second posting added from <paramref name="side"/>, which is then on every posting.</summary>
    private static async Task SplitFromAsync(NpgsqlConnection conn, Guid ledgerId, string externalId, string side, string other)
    {
        await conn.ExecuteAsync("""
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT gen_random_uuid(), h.id, h.ledger_id, a.id, 1, v.amount
              FROM txn_headers h
             CROSS JOIN (VALUES (@Side, -10.00), (@Other, 10.00)) v(account, amount)
              JOIN accounts a ON a.ledger_id = h.ledger_id AND a.external_id = v.account
             WHERE h.ledger_id = @L AND h.external_id = @X;
            SELECT fn_recompute_posting_counts_for_header(id) FROM txn_headers WHERE ledger_id = @L AND external_id = @X;
            """, new { L = ledgerId, X = externalId, Side = side, Other = other });
    }

    /// <summary>The fire of the release before 264: a live clone of the template, memos included.</summary>
    private static async Task FireAsync(NpgsqlConnection conn, Guid ledgerId, string reminder, string externalId)
    {
        await conn.ExecuteAsync("""
            WITH t AS (
                SELECT h.*, rt.id AS series
                  FROM txn_headers h
                  JOIN recurring_transactions rt ON rt.template_header_id = h.id
                 WHERE h.ledger_id = @L AND rt.external_id = @R),
            hdr AS (
                INSERT INTO txn_headers (id, ledger_id, origin, external_id, payee, memo, posted_at, transacted_at,
                                         created_at, recurring_transaction_id, occurrence_date)
                SELECT gen_random_uuid(), t.ledger_id, 'manual', @X, t.payee, t.memo,
                       t.posted_at + interval '31 days', t.posted_at + interval '31 days',
                       t.created_at + interval '1 day', t.series, (t.posted_at + interval '31 days')::date
                  FROM t
                RETURNING id, ledger_id)
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, leg_memo)
            SELECT gen_random_uuid(), hdr.id, hdr.ledger_id, l.account_id, l.posting_index, l.amount, l.leg_memo
              FROM hdr, t JOIN txn_legs l ON l.header_id = t.id;
            SELECT fn_recompute_posting_counts_for_header(id) FROM txn_headers WHERE ledger_id = @L AND external_id = @X;
            """, new { L = ledgerId, R = reminder, X = externalId });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_name_cut_at_64_units_inside_a_surrogate_pair_upgrades_to_what_the_importer_writes(int vintage)
    {
        // 63 units, then a character of two units, then one more: the cut at 64 falls
        // between the pair's halves. The importer stores the 63 units before it; V1 and
        // V2 stored the whole name uncut, V3 the cut one.
        var astral = new string('a', 63) + "\U0001F600" + "z";
        var cut = new string('a', 63);
        var export = Export.Replace("\"0.tags\":\"food\"", "\"0.tags\":\"" + astral + "\"", StringComparison.Ordinal);
        Assert.NotEqual(Export, export);
        var (ledgerId, expected) = await ImportAsync(export);
        Assert.Contains(expected, l => l.StartsWith("t-one L a-chk 0 ", StringComparison.Ordinal) && l.Contains("[" + cut + "]", StringComparison.Ordinal));

        var rows = Vintage(vintage).Select(r => r.ExternalId != "t-one" ? r : r with
        {
            HeaderTags = [.. r.HeaderTags.Select(n => n == "food" ? (vintage == 3 ? cut : astral) : n)],
            HomeTags = r.HomeTags.ToDictionary(k => k.Key, k => k.Value.Select(n => n == "food" ? (vintage == 3 ? cut : astral) : n).ToArray()),
        }).ToList();
        var old = await OldInstallAsync($"astral_v{vintage}", ledgerId);
        await RewindAsync(old, ledgerId, rows);
        UpgradeFrom259(old);

        var actual = await SnapshotAsync(old, ledgerId);
        Assert.True(expected.SequenceEqual(actual), Diff(expected, actual));
        await AssertStoredIsComputedAsync(old, ledgerId);
    }

    // ----- the two databases -----

    private Task<(Guid LedgerId, List<string> Snapshot)> ImportAsync() => ImportAsync(Export);

    private async Task<(Guid LedgerId, List<string> Snapshot)> ImportAsync(string export)
    {
        await using var conn = Db.OpenConnection();
        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(export), ledgerName: $"Vintage {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
        Assert.All(result.Validation.Checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Message}"));
        return (result.LedgerId, await SnapshotAsync(Db.ConnectionString, result.LedgerId));
    }

    /// <summary>A database at 259 holding this ledger's rows as the import wrote them
    /// (<see cref="InstallAt259.CreateAsync"/>).</summary>
    private async Task<string> OldInstallAsync(string suffix, Guid ledgerId)
    {
        var (old, work) = await InstallAt259.CreateAsync(Db, $"mig264_{suffix}", ledgerId);
        _work[old] = work;
        return old;
    }

    private readonly Dictionary<string, string> _work = new(StringComparer.Ordinal);

    /// <summary>The memo and tag fields of every row set to what one vintage wrote.</summary>
    private static async Task RewindAsync(string cs, Guid ledgerId, IReadOnlyList<OldRow> rows)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        // The old importers left the brokerage cash move action-less; 260 sets it.
        await conn.ExecuteAsync("""
            UPDATE txn_legs SET posting_role = NULL
             WHERE header_id IN (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-xfr');
            UPDATE txn_headers SET action = NULL WHERE ledger_id = @L AND external_id = 't-xfr';
            """, new { L = ledgerId });

        var created = 0;
        foreach (var row in rows)
        {
            var header = await conn.QuerySingleAsync<Guid>(
                "SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = @X", new { L = ledgerId, X = row.ExternalId });
            await conn.ExecuteAsync("""
                UPDATE txn_headers SET memo = @M WHERE id = @H;
                UPDATE txn_legs SET leg_memo = NULL WHERE header_id = @H;
                DELETE FROM txn_header_tags WHERE header_id = @H;
                DELETE FROM txn_leg_tags WHERE leg_id IN (SELECT id FROM txn_legs WHERE header_id = @H);
                """, new { M = row.HeaderMemo, H = header });
            foreach (var (posting, memo) in row.LineMemos)
                await conn.ExecuteAsync(
                    "UPDATE txn_legs SET leg_memo = @M WHERE header_id = @H AND posting_index = @P",
                    new { M = memo, H = header, P = posting });
            foreach (var name in row.HeaderTags)
            {
                var tag = await TagAsync(conn, ledgerId, name, created++);
                await conn.ExecuteAsync(
                    "INSERT INTO txn_header_tags (header_id, tag_id, ledger_id) VALUES (@H, @T, @L)",
                    new { H = header, T = tag, L = ledgerId });
            }
            foreach (var (posting, names) in row.HomeTags)
                foreach (var name in names)
                {
                    var tag = await TagAsync(conn, ledgerId, name, created++);
                    // The home leg: the posting's leg on the row's own account.
                    await conn.ExecuteAsync("""
                        INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id)
                        SELECT l.id, @T, @L
                          FROM txn_legs l
                          JOIN txn_headers h ON h.id = l.header_id
                          JOIN accounts a ON a.id = l.account_id
                         WHERE l.header_id = @H AND l.posting_index = @P
                           AND a.external_id = h.provider_raw_payload ->> 'acctid';
                        """, new { T = tag, L = ledgerId, H = header, P = posting });
                }
        }
    }

    /// <summary>The ledger's tag spelled exactly <paramref name="name"/>, as the old
    /// importers created one per spelling, in the order the import met them.</summary>
    private static Task<Guid> TagAsync(NpgsqlConnection conn, Guid ledgerId, string name, int order) =>
        conn.QuerySingleAsync<Guid>("""
            WITH ins AS (
                INSERT INTO tags (id, ledger_id, name, created_at)
                VALUES (gen_random_uuid(), @L, @N, TIMESTAMPTZ '2026-01-01 00:00:00+00' + make_interval(secs => @O))
                ON CONFLICT (ledger_id, name) DO NOTHING
                RETURNING id)
            SELECT id FROM ins UNION ALL SELECT id FROM tags WHERE ledger_id = @L AND name = @N LIMIT 1;
            """, new { L = ledgerId, N = name, O = order });

    private static async Task TagHeaderAsync(NpgsqlConnection conn, Guid ledgerId, string externalId, string name)
    {
        var tag = await TagAsync(conn, ledgerId, name, 1000);
        await conn.ExecuteAsync("""
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id)
            SELECT id, @T, @L FROM txn_headers WHERE ledger_id = @L AND external_id = @X;
            """, new { T = tag, L = ledgerId, X = externalId });
    }

    /// <summary>Every migration from 260 on (before <paramref name="before"/>, when given),
    /// as an install's next start runs them.</summary>
    private string UpgradeFrom259(string cs, string? before = null)
    {
        InstallAt259.UpgradeFrom259(cs, _work[cs], before);
        return _work[cs];
    }

    // ----- reads -----

    /// <summary>
    /// Every header and leg of the ledger, one line each: the header's memo and tags;
    /// each leg's memo, tags and stored presentation (kind, memo, inherited, header
    /// memo, and the tag rows with their roles), by account and posting.
    /// </summary>
    private static async Task<List<string>> SnapshotAsync(string cs, Guid ledgerId)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var lines = (await conn.QueryAsync<string>("""
            SELECT h.external_id || ' H ' || COALESCE('''' || h.memo || '''', 'NULL') || ' ['
                   || COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                                  FROM txn_header_tags x JOIN tags t ON t.id = x.tag_id WHERE x.header_id = h.id), '') || ']'
              FROM txn_headers h WHERE h.ledger_id = @L
            UNION ALL
            SELECT h.external_id || ' L ' || COALESCE(a.external_id, 'named ' || a.name) || ' ' || l.posting_index || ' '
                   || COALESCE('''' || l.leg_memo || '''', 'NULL') || ' ['
                   || COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                                  FROM txn_leg_tags x JOIN tags t ON t.id = x.tag_id WHERE x.leg_id = l.id), '') || '] '
                   || COALESCE(l.notes_kind, 'unfilled') || ' ' || COALESCE('''' || l.notes_memo || '''', 'NULL') || ' '
                   || COALESCE(l.notes_memo_inherited::text, '-') || ' ' || COALESCE('''' || l.notes_header_memo || '''', 'NULL')
                   || ' {' || COALESCE((SELECT string_agg(t.name || ':' || n.shown || n.in_play || n.inherited || n.transaction_level,
                                                          ',' ORDER BY t.name COLLATE "C")
                                         FROM txn_leg_note_tags n JOIN tags t ON t.id = n.tag_id WHERE n.leg_id = l.id), '') || '}'
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE l.ledger_id = @L
            """, new { L = ledgerId })).ToList();
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async Task<List<string>> LegsOfAsync(string cs, Guid ledgerId, string externalId)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var rows = await conn.QueryAsync<string>("""
            SELECT replace(a.external_id, 'a-', '') || ' ' || l.posting_index || ' '
                   || COALESCE('''' || l.leg_memo || '''', 'NULL') || ' ['
                   || COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                                  FROM txn_leg_tags x JOIN tags t ON t.id = x.tag_id WHERE x.leg_id = l.id), '') || '] '
                   || CASE WHEN l.notes_memo_inherited THEN 'inherited ''' || l.notes_memo || '''' ELSE 'own' END
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE h.ledger_id = @L AND h.external_id = @X
             ORDER BY a.external_id, l.posting_index
            """, new { L = ledgerId, X = externalId });
        return [.. rows];
    }

    private static async Task<(string? Memo, string Tags)> HeaderOfAsync(string cs, Guid ledgerId, string externalId)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(string?, string)>("""
            SELECT h.memo, COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                                       FROM txn_header_tags x JOIN tags t ON t.id = x.tag_id WHERE x.header_id = h.id), '')
              FROM txn_headers h WHERE h.ledger_id = @L AND h.external_id = @X
            """, new { L = ledgerId, X = externalId });
    }

    private static async Task<string?> OwnerOfAsync(string cs, Guid ledgerId, string externalId)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<string?>("""
            SELECT a.external_id FROM txn_headers h JOIN accounts a ON a.id = h.originating_account_id
             WHERE h.ledger_id = @L AND h.external_id = @X
            """, new { L = ledgerId, X = externalId });
    }

    private static async Task AssertStoredIsComputedAsync(string cs, Guid ledgerId)
    {
        await using var db = new Coffer.Api.Db.AppDbContext(
            new DbContextOptionsBuilder<Coffer.Api.Db.AppDbContext>().UseNpgsql(cs).Options);
        Assert.Empty(await Coffer.Api.Db.LegNotes.DifferencesAsync(db, ledgerId, CancellationToken.None));
    }

    private static string Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var sb = new StringBuilder("The upgraded ledger differs from a fresh import:\n");
        foreach (var line in expected.Except(actual, StringComparer.Ordinal)) sb.Append("  expected ").AppendLine(line);
        foreach (var line in actual.Except(expected, StringComparer.Ordinal)) sb.Append("  but got  ").AppendLine(line);
        return sb.ToString();
    }
}
