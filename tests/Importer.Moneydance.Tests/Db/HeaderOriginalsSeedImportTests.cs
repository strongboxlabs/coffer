using Dapper;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// FU-024: an imported header whose Moneydance feed text differs from the
/// curated text gets a <c>txn_header_originals</c> row, on both import routes;
/// one whose feed text agrees gets none.
/// </summary>
/// <remarks>
/// The row must look exactly like the one the API's first edit would have
/// captured (<c>HeaderOriginals.CaptureAsync</c>: every column snapshotted), so
/// posted_at, transacted_at and check_number are the header's own written
/// values, not something the importer recomputes beside it.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class HeaderOriginalsSeedImportTests : DatabaseTest
{
    public HeaderOriginalsSeedImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Differing_feed_text_is_seeded_and_agreeing_feed_text_is_not()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-cat","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-diff","acctid":"a-bank","desc":"Shop","memo":"curated memo",
                 "ol.orig-payee":"SHOP 0000 RAW","dt":"20260102","td":"20260101","chk":"7",
                 "0.id":"s-1","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000"},
                {"obj_type":"txn","id":"t-same","acctid":"a-bank","desc":"Shop","memo":"curated memo",
                 "ol.orig-payee":"Shop","ol.orig-memo":"curated memo","dt":"20260103",
                 "0.id":"s-2","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000"},
                {"obj_type":"txn","id":"t-inv","acctid":"a-brk","desc":"Sweep","memo":"curated memo",
                 "ol.orig-memo":"raw memo","dt":"20260104","chk":"XIn",
                 "invest.txntype":"bank","xfer_type":"xfrtp_bank",
                 "0.id":"s-3","0.acctid":"a-bank","0.invest.splittype":"xfr",
                 "0.samt":"1000","0.pamt":"-1000"}
              ]
            }
            """;
        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, "test:header-originals");
        await InvestmentTransactionImportStep.RunAsync(conn, context, "test:header-originals");
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        var rows = await OriginalsAsync(conn, ledgerId);

        // t-same's feed text equals its curated text: no row, so it does not
        // read as one whose feed text differs.
        Assert.Equal(["t-diff", "t-inv"], rows.Select(r => r.ExternalId));

        var bank = rows[0];
        Assert.Equal(ledgerId,        bank.LedgerId);
        Assert.Equal("SHOP 0000 RAW", bank.Payee);
        Assert.Equal("curated memo",  bank.Memo);
        Assert.Equal(bank.HeaderPostedAt,     bank.PostedAt);
        Assert.Equal(bank.HeaderTransactedAt, bank.TransactedAt);
        Assert.Equal("7",                     bank.CheckNumber);
        Assert.NotEqual(bank.PostedAt, bank.TransactedAt);   // the two dates are not conflated
        // ADR-0114 D15: the amount as written on the originating account, and no rounding
        // mark, since a seed is a whole-row snapshot, not a rounded import.
        Assert.Equal(-10.00m, bank.Amount);
        Assert.Equal(bank.HomeLegSum, bank.Amount);
        Assert.False(bank.RoundedAtImport);

        var inv = rows[1];
        Assert.Equal(ledgerId,   inv.LedgerId);
        Assert.Equal("Sweep",    inv.Payee);
        Assert.Equal("raw memo", inv.Memo);
        Assert.Equal(inv.HeaderPostedAt,     inv.PostedAt);
        Assert.Equal(inv.HeaderTransactedAt, inv.TransactedAt);
        Assert.Equal("XIn",                  inv.CheckNumber);   // the investment route's chk
        Assert.Equal(inv.HeaderCheckNumber,  inv.CheckNumber);
        Assert.Equal(-10.00m, inv.Amount);
        Assert.Equal(inv.HomeLegSum, inv.Amount);
        Assert.False(inv.RoundedAtImport);

        // The canonical rows still carry the curated text.
        var payees = (await conn.QueryAsync<string>(
            "SELECT payee FROM txn_headers WHERE ledger_id = @L ORDER BY external_id;",
            new { L = ledgerId })).ToList();
        Assert.Equal(["Shop", "Sweep", "Shop"], payees);   // t-diff, t-inv, t-same
    }

    [Fact]
    public async Task A_fitid_duplicate_the_import_drops_leaves_no_originals_row()
    {
        // The step drops the second of two rows with one OFX (fi_id, fitid). Its
        // seed is still handed to the repository, whose insert joins the header
        // rows written and so finds none for it.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-cat","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-kept","acctid":"a-bank","desc":"Shop","ol.orig-payee":"SHOP RAW",
                 "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1","dt":"20260102",
                 "0.id":"s-1","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000"},
                {"obj_type":"txn","id":"t-dup","acctid":"a-bank","desc":"Shop","ol.orig-payee":"SHOP RAW AGAIN",
                 "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1","dt":"20260102",
                 "0.id":"s-2","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000"}
              ]
            }
            """;
        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, "test:header-originals");
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        var rows = (await conn.QueryAsync<(string ExternalId, string? Payee)>("""
            SELECT h.external_id, o.payee
              FROM txn_header_originals o JOIN txn_headers h ON h.id = o.header_id
             WHERE h.ledger_id = @L;
            """, new { L = ledgerId })).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(("t-kept", "SHOP RAW"), (row.ExternalId, row.Payee));
    }
    [Fact]
    public async Task A_seed_carries_the_amount_on_the_originating_account_and_no_other_side()
    {
        // ADR-0114 D15: the seed's amount is the transaction's amount on its originating
        // account, the sum of its legs there. A split adds up its lines on that account;
        // an investment buy counts the brokerage's cash leg and not the Holdings sibling's.
        // Summing every leg of a header would give zero for both.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
                {"obj_type":"curr","id":"c-fund","type":"s","currid":"X100","name":"Fund Q","ticker":"FQ","dec":"4",
                 "rate":"0.05","rrate":"0.05"},
                {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund Q","parentid":"a-brk","currid":"c-fund","sbal":"0"},
                {"obj_type":"acct","id":"a-cat","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-cat2","type":"e","name":"Household","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-split","acctid":"a-bank","desc":"Shop","ol.orig-payee":"SHOP RAW","dt":"20260102",
                 "0.id":"s-1","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000",
                 "1.id":"s-2","1.acctid":"a-cat2","1.samt":"250","1.pamt":"-250"},
                {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","ol.orig-payee":"BUY RAW","dt":"20260103",
                 "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
                 "0.id":"s-3","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"30000","0.pamt":"-6000","0.rate":"50.0"},
                {"obj_type":"txn","id":"t-plain","acctid":"a-bank","desc":"Shop","dt":"20260104",
                 "0.id":"s-4","0.acctid":"a-cat","0.samt":"500","0.pamt":"-500"}
              ]
            }
            """;
        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await SecurityImportStep.RunAsync(conn, context);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, "test:header-originals");
        await InvestmentTransactionImportStep.RunAsync(conn, context, "test:header-originals");
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);

        var rows = (await OriginalsAsync(conn, ledgerId)).ToDictionary(r => r.ExternalId);

        // A header with no differing feed text writes no originals row.
        Assert.Equal(["t-buy", "t-split"], rows.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(-12.50m, rows["t-split"].Amount);       // both lines on the bank
        Assert.Equal(rows["t-split"].HomeLegSum, rows["t-split"].Amount);
        Assert.Equal(-60.00m, rows["t-buy"].Amount);         // the brokerage's cash leg only
        Assert.Equal(rows["t-buy"].HomeLegSum, rows["t-buy"].Amount);
        Assert.All(rows.Values, r => Assert.False(r.RoundedAtImport));
        Assert.All(rows.Values, r => Assert.Equal(0m, r.AllLegSum));   // why the sum is per account
    }

    private static async Task<List<OriginalsRow>> OriginalsAsync(NpgsqlConnection conn, Guid ledgerId) =>
        (await conn.QueryAsync<OriginalsRow>("""
            SELECT h.external_id   AS ExternalId,
                   o.ledger_id     AS LedgerId,
                   o.payee         AS Payee,
                   o.memo          AS Memo,
                   o.posted_at     AS PostedAt,
                   o.transacted_at AS TransactedAt,
                   o.check_number  AS CheckNumber,
                   h.posted_at     AS HeaderPostedAt,
                   h.transacted_at AS HeaderTransactedAt,
                   h.check_number  AS HeaderCheckNumber,
                   o.amount        AS Amount,
                   o.rounded_at_import AS RoundedAtImport,
                   (SELECT sum(l.amount) FROM txn_legs l
                     WHERE l.header_id = h.id AND l.account_id = h.originating_account_id) AS HomeLegSum,
                   (SELECT sum(l.amount) FROM txn_legs l WHERE l.header_id = h.id) AS AllLegSum
              FROM txn_header_originals o
              JOIN txn_headers h ON h.id = o.header_id
             WHERE h.ledger_id = @L
             ORDER BY h.external_id;
            """, new { L = ledgerId })).ToList();

    private sealed record OriginalsRow(
        string ExternalId,
        Guid LedgerId,
        string? Payee,
        string? Memo,
        DateTime? PostedAt,
        DateTime? TransactedAt,
        string? CheckNumber,
        DateTime HeaderPostedAt,
        DateTime HeaderTransactedAt,
        string? HeaderCheckNumber,
        decimal? Amount,
        bool RoundedAtImport,
        decimal? HomeLegSum,
        decimal? AllLegSum);
}
