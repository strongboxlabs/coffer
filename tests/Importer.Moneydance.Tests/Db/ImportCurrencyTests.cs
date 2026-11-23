using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// ADR-0114: an imported account is in its own Moneydance currency, a Holdings sibling in
/// its brokerage's, and an imported price in the file's base currency, which a snapshot's
/// rate is relative to. Nothing defaults to USD: a file whose currencies cannot be read is
/// refused before anything is written. Every fixture is synthetic.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class ImportCurrencyTests : DatabaseTest
{
    public ImportCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>
    /// A file whose base currency is <paramref name="baseCode"/>, holding a bank account
    /// and a category in the base currency, a bank account in <paramref name="otherCode"/>,
    /// a brokerage in <paramref name="otherCode"/> with one fund, one buy of it there,
    /// and one price snapshot of the fund.
    /// </summary>
    private static string TwoCurrencyExport(string baseCode, string otherCode) => $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-base","currid":"{{baseCode}}","name":"Base money","dec":"2","isbase":"1","rate":"1","rrate":"1"},
            {"obj_type":"curr","id":"c-other","currid":"{{otherCode}}","name":"Other money","dec":"2","rate":"1.1","rrate":"1.1"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"0.04","rrate":"0.04"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-base"},
            {"obj_type":"acct","id":"a-base","type":"b","name":"Account A","parentid":"a-root","currid":"c-base","sbal":"0"},
            {"obj_type":"acct","id":"a-other","type":"b","name":"Account B","parentid":"a-root","currid":"c-other","sbal":"0"},
            {"obj_type":"acct","id":"c-cat","type":"e","name":"Category A","parentid":"a-root","currid":"c-base"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage A","parentid":"a-root","currid":"c-other","sbal":"0"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-1","acctid":"a-base","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-cat","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-2","acctid":"a-other","desc":"Shop","dt":"20260101",
             "0.id":"s-2","0.acctid":"c-cat","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","dt":"20260102",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-3","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"csnap","id":"snap-1","curr":"c-fund","dt":"20260105","urt":"0.04"}
          ]
        }
        """;

    [Fact]
    public async Task A_euro_file_keeps_each_accounts_own_currency_and_prices_in_euros()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, TwoCurrencyExport("EUR", "USD"), "EUR");

        Assert.Equal("EUR", await CurrencyOfAsync(conn, result.LedgerId, "a-base"));
        Assert.Equal("USD", await CurrencyOfAsync(conn, result.LedgerId, "a-other"));
        Assert.Equal("EUR", await CurrencyOfAsync(conn, result.LedgerId, "c-cat"));
        Assert.Equal("USD", await CurrencyOfAsync(conn, result.LedgerId, "a-brk"));
        // ADR-0019 Rule 4: the Holdings sibling is in its brokerage's currency.
        Assert.Equal("USD", await HoldingsCurrencyOfAsync(conn, result.LedgerId, "a-brk"));

        // The snapshot's rate is relative to the base currency: the price is its inverse,
        // in euros.
        var snapshot = await conn.QuerySingleAsync<(decimal Price, string CurrencyCode)>(
            "SELECT price, currency_code FROM security_prices WHERE ledger_id = @L AND source = 'import';",
            new { L = result.LedgerId });
        Assert.Equal((25.0000m, "EUR"), snapshot);

        // A trade's price is in the brokerage's currency (migration 274's rule), not the base.
        var trade = await conn.QuerySingleAsync<(decimal Price, string CurrencyCode)>(
            "SELECT price, currency_code FROM security_prices WHERE ledger_id = @L AND source = 'trade';",
            new { L = result.LedgerId });
        Assert.Equal((10.0000m, "USD"), trade);

        Assert.Equal("EUR", await conn.ExecuteScalarAsync<string>(
            "SELECT currency_code FROM ledgers WHERE id = @L;", new { L = result.LedgerId }));
    }

    [Fact]
    public async Task A_dollar_file_imports_every_account_and_price_in_dollars_as_before()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, TwoCurrencyExport("USD", "USD"), "USD");

        Assert.Equal(["USD"], (await conn.QueryAsync<string>(
            "SELECT DISTINCT currency_code FROM accounts WHERE ledger_id = @L;", new { L = result.LedgerId })).ToList());
        Assert.Equal(["USD"], (await conn.QueryAsync<string>(
            "SELECT DISTINCT currency_code FROM security_prices WHERE ledger_id = @L;", new { L = result.LedgerId })).ToList());
        Assert.Equal(25.0000m, await conn.ExecuteScalarAsync<decimal>(
            "SELECT price FROM security_prices WHERE ledger_id = @L AND source = 'import';", new { L = result.LedgerId }));
    }

    [Fact]
    public async Task The_demo_export_imports_every_account_and_price_in_dollars()
    {
        // ADR-0091: the generated Demo is the sample the hub's Demo ledger imports. Its base is USD
        // and every account names it, so the import is what it was when every row was
        // stamped USD. The tiny span keeps every account, security and price.
        await using var conn = Db.OpenConnection();
        using var export = DemoTestExports.TinyExport();
        var result = await new MoneydanceImportService().ImportAsync(
            conn, export, ledgerName: $"Demo currency {Guid.NewGuid():N}", ownerUserId: LedgerRow.SystemUserId,
            progress: null, cancellationToken: default, beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);

        Assert.Equal(["USD"], (await conn.QueryAsync<string>(
            "SELECT DISTINCT currency_code FROM accounts WHERE ledger_id = @L;", new { L = result.LedgerId })).ToList());
        Assert.Equal(["USD"], (await conn.QueryAsync<string>(
            "SELECT DISTINCT currency_code FROM security_prices WHERE ledger_id = @L;", new { L = result.LedgerId })).ToList());
    }

    [Theory]
    // The account names no currency.
    [InlineData("""{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","sbal":"0"}""", "'Account C' has no currency")]
    // The account names a currency the file does not contain.
    [InlineData("""{"obj_type":"acct","id":"a-bad","type":"b","name":"Account C","parentid":"a-root","currid":"c-gone","sbal":"0"}""", "'Account C' names a currency ('c-gone')")]
    // The account names a security as its currency.
    [InlineData("""{"obj_type":"acct","id":"a-bad","type":"c","name":"Account C","parentid":"a-root","currid":"c-fund","sbal":"0"}""", "'Account C' names the security 'Fund A'")]
    public async Task An_account_whose_currency_cannot_be_read_refuses_the_import_and_writes_nothing(
        string badAccount, string expected)
    {
        await using var conn = Db.OpenConnection();
        var json = TwoCurrencyExport("EUR", "USD").Replace(
            """{"obj_type":"acct","id":"a-root",""", badAccount + """,{"obj_type":"acct","id":"a-root",""", StringComparison.Ordinal);
        var name = $"Refused {Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(conn, json, "EUR", name));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ledgers WHERE name = @name;", new { name }));
    }

    [Fact]
    public async Task A_placeholder_loan_parent_is_not_refused_over_terms_that_are_never_seeded()
    {
        // ADR-0016: a loan that only groups other accounts and has no transactions of its
        // own is a placeholder the import does not create. Its escrow, at more places than
        // its currency keeps, is never read, so it does not refuse the file (ADR-0114 D15
        // checks the figures the import writes).
        await using var conn = Db.OpenConnection();
        var json = TwoCurrencyExport("EUR", "USD").Replace(
            """{"obj_type":"acct","id":"a-base","type":"b","name":"Account A","parentid":"a-root",""",
            """
            {"obj_type":"acct","id":"a-loan","type":"o","name":"Loan L","parentid":"a-root","currid":"c-base","sbal":"0",
             "int_rate":"4.0","num_payments":"360","pmts_per_year":"12","init_principal":"10000000",
             "escrow_payment":"12.345","calc_pmt":"1"},
            {"obj_type":"acct","id":"a-base","type":"b","name":"Account A","parentid":"a-loan",
            """,
            StringComparison.Ordinal);
        using var export = MdItemReader.ReadString(json);

        Assert.Empty(new MoneydanceImportService().Preview(export, "EUR").CurrencyProblems);

        var result = await ImportAsync(conn, json, "EUR");

        Assert.Null(await CurrencyOfAsync(conn, result.LedgerId, "a-loan"));
        Assert.Equal("EUR", await CurrencyOfAsync(conn, result.LedgerId, "a-base"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM loan_terms lt JOIN accounts a ON a.id = lt.account_id WHERE a.ledger_id = @L;",
            new { L = result.LedgerId }));
    }

    [Fact]
    public async Task A_file_with_no_base_currency_refuses_the_import_and_writes_nothing()
    {
        await using var conn = Db.OpenConnection();
        var json = TwoCurrencyExport("EUR", "USD").Replace("\"isbase\":\"1\",", "", StringComparison.Ordinal);
        var name = $"No base {Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(conn, json, "EUR", name));

        Assert.Contains("no currency as its base currency", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ledgers WHERE name = @name;", new { name }));
    }

    [Fact]
    public async Task The_price_step_alone_refuses_a_file_with_no_base_currency_rather_than_stamping_one()
    {
        // The step's own guarantee, for a caller that runs it without ImportAsync's check.
        await using var conn = Db.OpenConnection();
        var json = TwoCurrencyExport("EUR", "USD").Replace("\"isbase\":\"1\",", "", StringComparison.Ordinal);
        var context = new ImportContext(MdItemReader.ReadString(json), TestLedger.Id);
        // The fund must be known for its snapshot to be a price at all.
        await SecurityImportStep.RunAsync(conn, context);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PriceSnapshotImportStep.RunAsync(conn, context));
        Assert.Contains("no currency as its base currency", ex.Message, StringComparison.Ordinal);
    }

    private static Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection conn, string json, string reportingCurrency, string? name = null) =>
        new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(json), ledgerName: name ?? $"Currency {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync, currencyCode: reportingCurrency);

    private static Task<string?> CurrencyOfAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.ExecuteScalarAsync<string?>(
            "SELECT currency_code FROM accounts WHERE ledger_id = @ledgerId AND external_id = @mdId;",
            new { ledgerId, mdId });

    private static Task<string?> HoldingsCurrencyOfAsync(NpgsqlConnection conn, Guid ledgerId, string brokerageMdId) =>
        conn.ExecuteScalarAsync<string?>(
            """
            SELECT h.currency_code FROM accounts b JOIN accounts h ON h.id = b.holdings_account_id
             WHERE b.ledger_id = @ledgerId AND b.external_id = @brokerageMdId;
            """,
            new { ledgerId, brokerageMdId });
}
