using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// A Moneydance export imported end to end, then read back from the database the
/// way every register reads it: the stored memos and tags of each transaction run
/// through the one inheritance rule, from every account the transaction touches.
/// </summary>
/// <remarks>
/// The mapper tests pin each shape's placement; this pins that nothing between the
/// mapper and the tables moves a value or drops a tag, that tag names resolve to one
/// tag per name whatever their case, and that the stored result keeps the placement
/// invariants (no notes on a one-split's header, nothing on an event's investment
/// side, no blank memo; <see cref="MemoPlacementInvariants"/>) and the
/// originating-account invariants.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class MemoTagsImportTests : DatabaseTest
{
    public MemoTagsImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Export = """
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

            {"obj_type":"reminder","id":"r-1","desc":"Rent","memo":"rent memo","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1","is_loan_reminder":"0",
             "txn.acctid":"a-chk","txn.desc":"Rent","txn.memo":"txn memo","txn.tags":"home",
             "txn.0.acctid":"a-hh","txn.0.desc":"landlord","txn.0.tags":"Fixed","txn.0.samt":"1000","txn.0.pamt":"-1000"}
          ]
        }
        """;

    /// <summary>What one register row shows; the tags compared as one ordered string.</summary>
    private sealed record Shown(string? Memo, string Tags, bool IsReadOnly);

    private static Shown S(string? memo, IEnumerable<string> tags, bool isReadOnly) =>
        new(memo, string.Join(", ", tags.Order(StringComparer.OrdinalIgnoreCase)), isReadOnly);

    [Fact]
    public async Task Every_register_shows_the_moneydance_values_and_the_stored_notes_keep_their_invariants()
    {
        await using var conn = Db.OpenConnection();

        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(Export), ledgerName: $"Memo tags {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
        var ledgerId = result.LedgerId;
        Assert.All(result.Validation.Checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Message}"));

        var chk = await AccountAsync(conn, ledgerId, "a-chk");
        var sav = await AccountAsync(conn, ledgerId, "a-sav");
        var brk = await AccountAsync(conn, ledgerId, "a-brk");
        var brk2 = await AccountAsync(conn, ledgerId, "a-brk2");
        var gro = await AccountAsync(conn, ledgerId, "a-gro");
        var hh = await AccountAsync(conn, ledgerId, "a-hh");
        var fee = await AccountAsync(conn, ledgerId, "a-fee");

        // One-split: the entered side's memo (trimmed) and split tags; the other side's
        // own 0.desc and the transaction tags.
        Assert.Equal(S("weekly shop", ["food"], false), await ShownAsync(conn, "t-one", chk, 0));
        Assert.Equal(S("receipt 12", ["Family"], false), await ShownAsync(conn, "t-one", gro, 0));

        // 0.desc is the payee copy: no value of its own, the memo inherited.
        Assert.Equal(S("top up", [], false), await ShownAsync(conn, "t-copy", gro, 0));

        // Multi-split: header memo on the split side's row, lines on their own legs; the
        // opposite sides read-only, each showing its line. "family" is the "Family"
        // tag, whatever its case here.
        Assert.Equal(S("food", ["Family", "weekly"], false), await ShownAsync(conn, "t-multi", chk, 0));
        Assert.Equal(S("food", ["Family", "weekly"], true), await ShownAsync(conn, "t-multi", gro, 0));
        Assert.Equal(S("to savings", ["Family"], true), await ShownAsync(conn, "t-multi", sav, 1));
        Assert.Equal("weekend", await HeaderMemoAsync(conn, "t-multi"));

        // The symmetric shape: owned by the bank, one read-only row per line in the category.
        Assert.Equal(S("soap", [], true), await ShownAsync(conn, "t-sym", hh, 0));
        Assert.Equal(S("towels", [], true), await ShownAsync(conn, "t-sym", hh, 1));
        Assert.Equal(chk, await OriginatingAsync(conn, "t-sym"));

        // A brokerage-home cash move: the event's one value, on the brokerage.
        Assert.Equal(S("monthly · into checking", ["cash"], false), await ShownAsync(conn, "t-xfr", brk, 0));
        Assert.Equal(S("monthly · into checking", ["cash"], true), await ShownAsync(conn, "t-xfr", chk, 0));
        Assert.Equal(brk, await OriginatingAsync(conn, "t-xfr"));

        // A bank-home transfer into the brokerage: a plain one-split owned by the bank.
        Assert.Equal(S("deposit", [], false), await ShownAsync(conn, "t-fund", brk, 0));
        Assert.Equal(chk, await OriginatingAsync(conn, "t-fund"));

        // A buy with a fee: one value on the header, shown on every row; "PLAN" is "plan".
        Assert.Equal("rebalance · commission", await HeaderMemoAsync(conn, "t-buy"));
        Assert.Equal(S("rebalance · commission", ["cost", "plan"], true), await ShownAsync(conn, "t-buy", fee, 1));

        // A buy funded from another brokerage: owned by the home brokerage; read-only there.
        Assert.Equal(brk, await OriginatingAsync(conn, "t-buyx"));
        var funding = await ShownAsync(conn, "t-buyx", brk2, 1);
        Assert.True(funding.IsReadOnly);
        Assert.Equal("from the other · funding", funding.Memo);

        // The reminder template: the reminder's memo on its account, its tags imported.
        Assert.Equal(S("rent memo", ["Fixed"], false), await ShownAsync(conn, "mdreminder:r-1", chk, 0));
        Assert.Equal(S("landlord", ["home"], false), await ShownAsync(conn, "mdreminder:r-1", hh, 0));

        // One tag per name under the key, spelled as it was first met.
        var names = (await conn.QueryAsync<string>(
            "SELECT name FROM tags WHERE ledger_id = @L;", new { L = ledgerId })).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Family", "Fixed", "cash", "cost", "food", "home", "plan", "weekly"], names);

        Assert.Empty(await MemoPlacementInvariants.ViolationsAsync(conn, ledgerId));
    }

    // -------------------------------------------------------------------------

    private static async Task<Shown> ShownAsync(NpgsqlConnection conn, string externalId, Guid account, int posting)
    {
        var header = await conn.QuerySingleAsync<(Guid Id, string? Memo, string? Action, Guid? Originating)>("""
            SELECT id, memo, action, originating_account_id FROM txn_headers WHERE external_id = @X;
            """, new { X = externalId });
        var legs = (await conn.QueryAsync<(Guid Id, Guid AccountId, int PostingIndex, string? LegMemo, bool OnInvestmentSide)>("""
            SELECT l.id, l.account_id, l.posting_index, l.leg_memo,
                   (h.action IS NOT NULL AND (l.account_id = h.originating_account_id
                        OR l.account_id = (SELECT b.holdings_account_id FROM accounts b WHERE b.id = h.originating_account_id)))
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE l.header_id = @H;
            """, new { H = header.Id })).ToList();
        var headerTags = (await conn.QueryAsync<string>("""
            SELECT t.name FROM txn_header_tags x JOIN tags t ON t.id = x.tag_id WHERE x.header_id = @H ORDER BY t.created_at, t.name;
            """, new { H = header.Id })).ToList();
        var legTags = (await conn.QueryAsync<(Guid LegId, string Name)>("""
            SELECT x.leg_id, t.name FROM txn_leg_tags x JOIN tags t ON t.id = x.tag_id
              JOIN txn_legs l ON l.id = x.leg_id WHERE l.header_id = @H;
            """, new { H = header.Id })).ToList();

        var shape = new TxnShape(header.Originating, header.Action,
            [.. legs.Select(l => new ShapeLeg(new LegKey(l.PostingIndex, l.AccountId), l.Id, OnInvestmentSide: l.OnInvestmentSide))]);
        var notes = new Annotations<string>(
            new Notes<string>(header.Memo, headerTags),
            legs.ToDictionary(
                l => new LegKey(l.PostingIndex, l.AccountId),
                l => new Notes<string>(l.LegMemo, [.. legTags.Where(t => t.LegId == l.Id).Select(t => t.Name)])));
        var shown = MemoTagRules.PresentInMemory(shape, notes, new LegKey(posting, account));
        return S(shown.Memo, shown.Tags, shown.Presentation.IsReadOnly);
    }

    private static Task<string?> HeaderMemoAsync(NpgsqlConnection conn, string externalId) =>
        conn.QuerySingleAsync<string?>("SELECT memo FROM txn_headers WHERE external_id = @X;", new { X = externalId });

    private static Task<Guid?> OriginatingAsync(NpgsqlConnection conn, string externalId) =>
        conn.QuerySingleAsync<Guid?>(
            "SELECT originating_account_id FROM txn_headers WHERE external_id = @X;", new { X = externalId });

    private static Task<Guid> AccountAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.QuerySingleAsync<Guid>(
            "SELECT id FROM accounts WHERE ledger_id = @L AND external_id = @X;", new { L = ledgerId, X = mdId });
}
