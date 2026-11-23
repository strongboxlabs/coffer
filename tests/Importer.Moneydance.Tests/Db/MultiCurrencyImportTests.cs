using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// A Moneydance file with a euro base and accounts in euros, dollars, yen and a 3-place
/// currency, built from the shapes of a real export (synthetic values, synthetic names):
/// <list type="bullet">
///   <item><description>every amount is an integer at its own currency's <c>dec</c>
///   places (the yen 0, the euro and the dollar 2, the dinar 3, the security 5);</description></item>
///   <item><description>a split's <c>pamt</c> is in the parent account's currency and its
///   <c>samt</c> in the split account's, with <c>rate</c> = samt / |pamt|;</description></item>
///   <item><description>a security quoted in dollars carries <c>rel_curr_id</c> and
///   <c>relative_to_currid</c>, and its snapshots' rates are relative to that currency;</description></item>
///   <item><description>a category can hold a currency of its own.</description></item>
/// </list>
/// Coffer reads a category's line in the currency of the real account on the other side
/// (ADR-0114 D7), so a category leg carries the real side's amount and an imported
/// category takes the ledger's reporting currency. A transfer between two real accounts
/// in different currencies keeps each side's own amount, a two-amount pair the
/// header-balance check passes (ADR-0114 D13), on the bank route, the investment route
/// and a reminder template alike.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class MultiCurrencyImportTests : DatabaseTest
{
    public MultiCurrencyImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Currencies = """
        {"obj_type":"curr","id":"c-eur","currid":"EUR","name":"Euro","dec":"2","isbase":"1","rate":"1.0","rrate":"1.0"},
        {"obj_type":"curr","id":"c-jpy","currid":"JPY","name":"Yen","dec":"0","rate":"160.0","rrate":"160.0"},
        {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"0","rate":"1.1","rrate":"1.1"},
        {"obj_type":"curr","id":"c-kwd","currid":"KWD","name":"Dinar","dec":"3","rate":"0.33","rrate":"0.33"}
        """;

    private const string Security = """
        {"obj_type":"curr","id":"c-fund","type":"s","currid":"X100","name":"Fund Q","ticker":"FQ","dec":"5",
         "rel_curr_id":"c-usd","relative_to_currid":"USD","rate":"0.045","rrate":"0.04"}
        """;

    private const string Accounts = """
        {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-eur"},
        {"obj_type":"acct","id":"a-eur","type":"b","name":"Account E","parentid":"a-root","currid":"c-eur","sbal":"0"},
        {"obj_type":"acct","id":"a-jpy","type":"b","name":"Account Y","parentid":"a-root","currid":"c-jpy","sbal":"125000"},
        {"obj_type":"acct","id":"a-usd","type":"b","name":"Account D","parentid":"a-root","currid":"c-usd","sbal":"0"},
        {"obj_type":"acct","id":"a-kwd","type":"b","name":"Account K","parentid":"a-root","currid":"c-kwd","sbal":"1234567"},
        {"obj_type":"acct","id":"a-brk","type":"v","name":"Broker B","parentid":"a-root","currid":"c-eur","sbal":"0"},
        {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund Q","parentid":"a-brk","currid":"c-fund","sbal":"0"},
        {"obj_type":"acct","id":"c-food","type":"e","name":"Category F","parentid":"a-root","currid":"c-eur"},
        {"obj_type":"acct","id":"c-fun","type":"e","name":"Category G","parentid":"a-root","currid":"c-usd"}
        """;

    /// <summary>Every transaction balances: each posting with a category side mirrors its
    /// real side, and each transfer between currencies (t-xfer, t-mixed's first line,
    /// t-inv-xfr and the rem-xfer template) carries two nonzero amounts of opposite
    /// sign.</summary>
    private const string Transactions = """
        {"obj_type":"txn","id":"t-kwd-food","acctid":"a-kwd","desc":"Dinar to euro category","dt":"20260301",
         "0.id":"s-10","0.acctid":"c-food","0.pamt":"-12345","0.samt":"3500","0.rate":"0.28"},
        {"obj_type":"txn","id":"t-xfer","acctid":"a-usd","desc":"Between currencies","dt":"20260307",
         "0.id":"s-7","0.acctid":"a-eur","0.pamt":"-5000","0.samt":"4500","0.rate":"0.9"},
        {"obj_type":"txn","id":"t-mixed","acctid":"a-usd","desc":"Transfer and a category line","dt":"20260309",
         "0.id":"s-11","0.acctid":"a-eur","0.pamt":"-2000","0.samt":"1800","0.rate":"0.9",
         "1.id":"s-12","1.acctid":"c-food","1.pamt":"-1000","1.samt":"900","1.rate":"0.9"},
        {"obj_type":"txn","id":"t-inv-xfr","acctid":"a-brk","desc":"Brokerage to dollars","dt":"20260310",
         "invest.txntype":"bank","xfer_type":"xfrtp_bank",
         "0.id":"s-13","0.acctid":"a-usd","0.invest.splittype":"xfr","0.pamt":"-3000","0.samt":"3300","0.rate":"1.1"},
        {"obj_type":"reminder","id":"rem-xfer","desc":"Monthly move","type":"monthly","sdt":"20260101",
         "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
         "is_loan_reminder":"0","txn.acctid":"a-usd","txn.desc":"Monthly move",
         "txn.0.acctid":"a-eur","txn.0.pamt":"-4000","txn.0.samt":"3600"},
        {"obj_type":"txn","id":"t-yen-food","acctid":"a-jpy","desc":"Yen to euro category","dt":"20260301",
         "0.id":"s-1","0.acctid":"c-food","0.pamt":"-2400","0.samt":"1500","0.rate":"0.625"},
        {"obj_type":"txn","id":"t-usd-food","acctid":"a-usd","desc":"Dollar to euro category","dt":"20260302",
         "0.id":"s-2","0.acctid":"c-food","0.pamt":"-2000","0.samt":"1800","0.rate":"0.9"},
        {"obj_type":"txn","id":"t-eur-fun","acctid":"a-eur","desc":"Euro to dollar category","dt":"20260303",
         "0.id":"s-3","0.acctid":"c-fun","0.pamt":"-500","0.samt":"550","0.rate":"1.1"},
        {"obj_type":"txn","id":"t-usd-split","acctid":"a-usd","desc":"Two lines","dt":"20260304",
         "0.id":"s-4","0.acctid":"c-food","0.pamt":"-1000","0.samt":"900","0.rate":"0.9",
         "1.id":"s-5","1.acctid":"c-fun","1.pamt":"-300","1.samt":"300"},
        {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","dt":"20260305",
         "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
         "0.id":"s-6","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"300000","0.pamt":"-6000","0.rate":"50.0"},
        {"obj_type":"txn","id":"t-div","acctid":"a-brk","desc":"Dividend","dt":"20260308",
         "invest.txntype":"div","xfer_type":"xfrtp_dividend",
         "0.id":"s-8","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"0","0.pamt":"0",
         "1.id":"s-9","1.acctid":"c-fun","1.invest.splittype":"inc","1.pamt":"700","1.samt":"-770","1.rate":"1.1"},
        {"obj_type":"csnap","id":"snap-fund","curr":"c-fund","dt":"20260306","urt":"0.04","relrt":"0.04"},
        {"obj_type":"csnap","id":"snap-usd","curr":"c-usd","dt":"20260306","urt":"1.1","relrt":"1.1"}
        """;

    private static string Export(string items) => $$"""
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [ {{items}} ]
        }
        """;

    private static string FullExport(string extra = "") =>
        Export(Currencies + "," + Security + "," + Accounts + "," + Transactions + extra);

    [Fact]
    public async Task Each_amount_is_scaled_by_its_own_currency_and_every_category_leg_follows_its_real_side()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, FullExport(), "EUR");
        var ledger = result.LedgerId;

        // Every posting balances once the category legs carry the real side's amount and
        // each transfer between currencies is a pair of two nonzero opposite amounts, and
        // each account's balance agrees with what Moneydance computes.
        Assert.True(result.Validation.AllPassed,
            string.Join("; ", result.Validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}")));

        // The yen account's opening balance is whole yen (dec 0).
        Assert.Equal(125_000m, await OpeningBalanceAsync(conn, ledger, "a-jpy"));

        // A 3-place currency (ADR-0114 D15): thousandths, kept exactly, on the account's
        // opening balance and on both legs, the category's included.
        Assert.Equal(1_234.567m, await OpeningBalanceAsync(conn, ledger, "a-kwd"));
        Assert.Equal((-12.345m, 12.345m), await LegsAsync(conn, ledger, "t-kwd-food", 0));
        Assert.Equal("KWD", await CurrencyOfAsync(conn, ledger, "a-kwd"));

        // A transfer between currencies keeps what each account saw: the dollar side's
        // figure out and the euro side's in (ADR-0114 D13).
        Assert.Equal((-50.00m, 45.00m), await LegsAsync(conn, ledger, "t-xfer", 0));
        // A header with a transfer between currencies and a category line: the pair keeps
        // both amounts, and the category line mirrors its real side.
        Assert.Equal((-20.00m, 18.00m), await LegsAsync(conn, ledger, "t-mixed", 0));
        Assert.Equal((-10.00m, 10.00m), await LegsAsync(conn, ledger, "t-mixed", 1));
        // The investment route: the euro brokerage's figure out, the dollar account's in.
        Assert.Equal((-30.00m, 33.00m), await LegsAsync(conn, ledger, "t-inv-xfr", 0));
        // A reminder template between currencies keeps both amounts too.
        Assert.Equal(
            new Dictionary<string, decimal> { ["a-eur"] = 36.00m, ["a-usd"] = -40.00m },
            await TemplateLegsAsync(conn, ledger, "rem-xfer"));

        // Whole yen out and on the category: not divided by a hundred, and not the euro samt.
        Assert.Equal((-2400m, 2400m), await LegsAsync(conn, ledger, "t-yen-food", 0));
        // A dollar account to a euro category: the dollar side's figure on both legs, not
        // the euro samt.
        Assert.Equal((-20.00m, 20.00m), await LegsAsync(conn, ledger, "t-usd-food", 0));
        // A euro account to a category Moneydance kept in dollars: the euro side's figure,
        // not the dollar samt.
        Assert.Equal((-5.00m, 5.00m), await LegsAsync(conn, ledger, "t-eur-fun", 0));
        // Each line of a split on its own.
        Assert.Equal((-10.00m, 10.00m), await LegsAsync(conn, ledger, "t-usd-split", 0));
        Assert.Equal((-3.00m, 3.00m), await LegsAsync(conn, ledger, "t-usd-split", 1));

        // Categories hold no money of their own, so they take the ledger's currency;
        // real accounts keep their own.
        Assert.Equal("EUR", await CurrencyOfAsync(conn, ledger, "c-food"));
        Assert.Equal("EUR", await CurrencyOfAsync(conn, ledger, "c-fun"));
        Assert.Equal("JPY", await CurrencyOfAsync(conn, ledger, "a-jpy"));
        Assert.Equal("USD", await CurrencyOfAsync(conn, ledger, "a-usd"));

        // The buy: its shares (the samt, at the security's 5 places) for a euro amount.
        var buy = await conn.QuerySingleAsync<(decimal Amount, decimal Quantity, decimal UnitPrice)>(
            """
            SELECT l.amount, l.quantity, l.unit_price FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @ledger AND h.external_id = 't-buy' AND l.quantity IS NOT NULL;
            """, new { ledger });
        Assert.Equal((60.00m, 3m, 20m), (buy.Amount, buy.Quantity, buy.UnitPrice));

        // A dividend into a category Moneydance kept in dollars: the euro figure into the
        // euro brokerage and on the category, not the dollar samt. (An investment header numbers
        // its postings by the pairs it emits, and the empty sec split emits none.)
        Assert.Equal((7.00m, -7.00m), await LegsAsync(conn, ledger, "t-div", 0));

        // The fund's snapshot is relative to its relative currency, the dollar:
        // the price is the inverse of the snapshot's rate, in dollars, not euros. Its trade
        // price is in the brokerage's euros.
        Assert.Equal((25.0000m, "USD"), await PriceAsync(conn, ledger, "import"));
        Assert.Equal((20.0000m, "EUR"), await PriceAsync(conn, ledger, "trade"));
    }

    [Fact]
    public async Task A_transfer_between_currencies_keeps_each_sides_amount_and_passes_the_balance_check()
    {
        // ADR-0114 D13: a transfer between currencies records both amounts. The import
        // keeps what each account saw, and the header-balance check leaves the pair out
        // of its header's sum and passes it, since its amounts are nonzero and opposite.
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, FullExport(), "EUR");

        Assert.Equal((-50.00m, 45.00m), await LegsAsync(conn, result.LedgerId, "t-xfer", 0));
        var balance = Assert.Single(result.Validation.Checks, c => c.Name == "header-balance");
        Assert.True(balance.Passed, balance.Message);
    }

    [Theory]
    // A transfer between currencies with one side zero is not a two-amount pair that
    // passes (ADR-0053: no zero-leg exemption).
    [InlineData("""
        ,{"obj_type":"txn","id":"t-zero","acctid":"a-usd","desc":"One side zero","dt":"20260311",
          "0.id":"s-20","0.acctid":"a-eur","0.pamt":"-5000","0.samt":"0"}
        """)]
    // The same two sides in one sign.
    [InlineData("""
        ,{"obj_type":"txn","id":"t-same-sign","acctid":"a-usd","desc":"Same sign","dt":"20260311",
          "0.id":"s-21","0.acctid":"a-eur","0.pamt":"-5000","0.samt":"-4500"}
        """)]
    // A reminder template between currencies with one side zero: templates are checked too.
    [InlineData("""
        ,{"obj_type":"reminder","id":"rem-zero","desc":"Zero side","type":"monthly","sdt":"20260101",
          "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
          "is_loan_reminder":"0","txn.acctid":"a-usd","txn.desc":"Zero side",
          "txn.0.acctid":"a-eur","txn.0.pamt":"-4000","txn.0.samt":"0"}
        """)]
    public async Task A_transfer_between_currencies_without_two_nonzero_opposite_amounts_fails_the_balance_check(
        string extra)
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, FullExport(extra), "EUR");

        var balance = Assert.Single(result.Validation.Checks, c => c.Name == "header-balance");
        Assert.False(balance.Passed);
        // Only the added transaction: the fixture's own pairs still pass.
        Assert.Equal(Coffer.Importer.Moneydance.Pipeline.CheckSummaries.Unbalanced(1), balance.Summary);
    }

    [Theory]
    // The security's relative currency names no currency of the file.
    [InlineData("\"rel_curr_id\":\"c-usd\"", "\"rel_curr_id\":\"c-gone\"", "is quoted in a currency ('c-gone')")]
    // Its id and its code disagree.
    [InlineData("\"relative_to_currid\":\"USD\"", "\"relative_to_currid\":\"JPY\"", "is quoted in 'USD' by id and 'JPY' by code")]
    // An account's currency keeps more places than Coffer keeps for its code
    // (ADR-0114 D15).
    [InlineData("\"currid\":\"JPY\",\"name\":\"Yen\",\"dec\":\"0\"", "\"currid\":\"JPY\",\"name\":\"Yen\",\"dec\":\"2\"",
        "'Account Y' is in 'Yen', whose amounts the file keeps at 2 decimal places, and Coffer keeps JPY at 0")]
    // An account's currency is not one Coffer accepts for an account.
    [InlineData("\"currid\":\"KWD\",\"name\":\"Dinar\"", "\"currid\":\"CLF\",\"name\":\"Dinar\"",
        "'Account K' is in 'Dinar', whose code 'CLF' is not a currency Coffer accepts for an account")]
    public async Task A_currency_the_import_cannot_read_refuses_the_file_and_writes_nothing(
        string from, string to, string expected)
    {
        await using var conn = Db.OpenConnection();
        var json = FullExport().Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(FullExport(), json);
        var name = $"Refused {Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(conn, json, "EUR", name));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ledgers WHERE name = @name;", new { name }));
    }

    private static Task<MoneydanceImportResult> ImportAsync(
        NpgsqlConnection conn, string json, string reportingCurrency, string? name = null) =>
        new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(json), ledgerName: name ?? $"Multi-currency {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync, currencyCode: reportingCurrency);

    /// <summary>The origin and counterpart legs of one posting, origin first (the leg on
    /// the transaction's own account).</summary>
    private static async Task<(decimal Origin, decimal Counterpart)> LegsAsync(
        NpgsqlConnection conn, Guid ledgerId, string txnId, int postingIndex)
    {
        var legs = (await conn.QueryAsync<(decimal Amount, bool OnHome)>(
            """
            SELECT l.amount, a.external_id IS NOT DISTINCT FROM (h.provider_raw_payload->>'acctid')
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE h.ledger_id = @ledgerId AND h.external_id = @txnId AND l.posting_index = @postingIndex;
            """, new { ledgerId, txnId, postingIndex })).ToList();
        Assert.Equal(2, legs.Count);
        return (legs.Single(l => l.OnHome).Amount, legs.Single(l => !l.OnHome).Amount);
    }

    /// <summary>The amounts of the template a Moneydance reminder became, keyed by the
    /// Moneydance id of each leg's account.</summary>
    private static async Task<Dictionary<string, decimal>> TemplateLegsAsync(
        NpgsqlConnection conn, Guid ledgerId, string reminderMdId) =>
        (await conn.QueryAsync<(string Account, decimal Amount)>(
            """
            SELECT a.external_id, l.amount
              FROM recurring_transactions rt
              JOIN txn_legs l ON l.header_id = rt.template_header_id
              JOIN accounts a ON a.id = l.account_id
             WHERE rt.ledger_id = @ledgerId AND rt.external_id = @reminderMdId;
            """, new { ledgerId, reminderMdId })).ToDictionary(r => r.Account, r => r.Amount);

    private static Task<decimal> OpeningBalanceAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.ExecuteScalarAsync<decimal>(
            "SELECT opening_balance FROM accounts WHERE ledger_id = @ledgerId AND external_id = @mdId;",
            new { ledgerId, mdId });

    private static Task<string?> CurrencyOfAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.ExecuteScalarAsync<string?>(
            "SELECT currency_code FROM accounts WHERE ledger_id = @ledgerId AND external_id = @mdId;",
            new { ledgerId, mdId });

    private static Task<(decimal Price, string CurrencyCode)> PriceAsync(NpgsqlConnection conn, Guid ledgerId, string source) =>
        conn.QuerySingleAsync<(decimal, string)>(
            "SELECT price, currency_code FROM security_prices WHERE ledger_id = @ledgerId AND source = @source;",
            new { ledgerId, source });
}
