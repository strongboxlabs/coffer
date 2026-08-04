using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Unit tests for the BalanceValidator's export-walking logic. The validator
/// reconstructs each MD account's closing balance from <c>sbal</c> + summed
/// flows; these tests pin the per-shape arithmetic against synthetic
/// fixtures small enough to compute by hand.
/// </summary>
public sealed class BalanceValidatorTests
{
    private static MdExport ExportFromJson(string allItems) =>
        MdItemReader.ReadString($$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{allItems}} ]
            }
            """);

    [Fact]
    public void Account_with_no_transactions_keeps_starting_balance()
    {
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-bank","name":"Checking","type":"b",
             "currid":"USD","sbal":"100000"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.Equal(1000.00m, balances["a-bank"]);
    }

    [Fact]
    public void Single_split_txn_applies_parent_amount_to_primary()
    {
        // Bank starts at $100; single txn for -$45.35 to a category.
        // Closing should be $54.65.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-bank","name":"Checking","type":"b",
             "currid":"USD","sbal":"10000"},
            {"obj_type":"acct","id":"a-gas","name":"Gas","type":"e",
             "currid":"USD","sbal":"0"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Fuel Stop","dt":"20240115",
             "0.id":"s","0.acctid":"a-gas","0.samt":"4535","0.pamt":"-4535"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.Equal(54.65m, balances["a-bank"]);
        // Categories are deliberately not in the result map.
        Assert.False(balances.ContainsKey("a-gas"));
    }

    [Fact]
    public void Multi_split_sums_parent_amounts_on_primary_only_once()
    {
        // Three-way split from Cash: $10 to Rent + $1.50 to Utilities + $0.60 to Net.
        // Cash closing = sbal − (10 + 1.50 + 0.60) = -12.10.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-cash","name":"Cash","type":"b",
             "currid":"USD","sbal":"0"},
            {"obj_type":"txn","id":"t-2","acctid":"a-cash","desc":"Multi","dt":"20240101",
             "0.id":"s0","0.acctid":"a-rent","0.samt":"1000","0.pamt":"-1000",
             "1.id":"s1","1.acctid":"a-utility","1.samt":"150","1.pamt":"-150",
             "2.id":"s2","2.acctid":"a-net","2.samt":"60","2.pamt":"-60"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.Equal(-12.10m, balances["a-cash"]);
    }

    [Fact]
    public void Cross_account_transfer_applies_to_both_sides()
    {
        // $500 from Checking to Savings: Checking should drop $500, Savings rise $500.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-checking","name":"Checking","type":"b",
             "currid":"USD","sbal":"100000"},
            {"obj_type":"acct","id":"a-savings","name":"Savings","type":"b",
             "currid":"USD","sbal":"0"},
            {"obj_type":"txn","id":"xfer","acctid":"a-checking","desc":"Move","dt":"20240315",
             "0.id":"s","0.acctid":"a-savings","0.samt":"50000","0.pamt":"-50000"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.Equal(500.00m,  balances["a-checking"]);
        Assert.Equal(500.00m,  balances["a-savings"]);
    }

    [Fact]
    public void Investment_buy_only_changes_brokerage_cash()
    {
        // 100 shares × $1.00 buy on a brokerage that started at $5,000.
        // Brokerage cash = $5,000 − $100 = $4,900.
        // Security sub-account is filtered (type='s'); doesn't appear.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-broker","name":"Brokerage A","type":"v",
             "currid":"USD","sbal":"500000"},
            {"obj_type":"acct","id":"a-secsub","name":"IDXB","type":"s",
             "currid":"sec-1","parentid":"a-broker"},
            {"obj_type":"txn","id":"buy-1","acctid":"a-broker","desc":"BUY","dt":"20240115",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s","0.acctid":"a-secsub","0.invest.splittype":"sec",
             "0.samt":"1000000","0.pamt":"-10000"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.Equal(4900.00m, balances["a-broker"]);
        Assert.False(balances.ContainsKey("a-secsub"));   // type='s' filtered out
    }

    [Fact]
    public void Investment_buyx_moves_cash_only_on_external_account()
    {
        // buyx: brokerage's pamt sums to 0 (cash neutral on brokerage), but the
        // external bank account loses $100 via the xfr split's split_amount.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-broker","name":"Brokerage A","type":"v",
             "currid":"USD","sbal":"0"},
            {"obj_type":"acct","id":"a-secsub","name":"IDXB","type":"s",
             "currid":"sec-1","parentid":"a-broker"},
            {"obj_type":"acct","id":"a-bank","name":"Checking","type":"b",
             "currid":"USD","sbal":"100000"},
            {"obj_type":"txn","id":"buyx-1","acctid":"a-broker","dt":"20240301",
             "invest.txntype":"buyx","xfer_type":"xfrtp_buysellxfr",
             "0.id":"sec","0.acctid":"a-secsub","0.invest.splittype":"sec",
             "0.samt":"1000000","0.pamt":"-10000",
             "1.id":"xfr","1.acctid":"a-bank","1.invest.splittype":"xfr",
             "1.samt":"-10000","1.pamt":"10000"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        // Brokerage: sbal 0 + sum(pamt) = 0 + (-100 + 100) = 0. ✓
        Assert.Equal(0m, balances["a-broker"]);
        // Bank: sbal 1000 + samt of the xfr split = 1000 - 100 = 900. ✓
        Assert.Equal(900.00m, balances["a-bank"]);
    }

    [Fact]
    public void Self_referential_split_does_not_double_count()
    {
        // Edge case: a split whose target is the primary itself. The primary's
        // pamt sum already captures the cash impact; adding samt on the
        // target side would double-count. The validator skips self-ref splits.
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-bank","name":"Bank","type":"b",
             "currid":"USD","sbal":"10000"},
            {"obj_type":"txn","id":"t-self","acctid":"a-bank","desc":"weird","dt":"20240101",
             "0.id":"s","0.acctid":"a-bank","0.samt":"500","0.pamt":"-500"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        // sbal $100 + pamt -$5 = $95.  No double counting via the same-target samt.
        Assert.Equal(95.00m, balances["a-bank"]);
    }

    [Fact]
    public void Skips_root_security_subaccount_and_categories()
    {
        var export = ExportFromJson("""
            {"obj_type":"acct","id":"a-root","name":"","type":"r","currid":"USD"},
            {"obj_type":"acct","id":"a-secsub","name":"IDXB","type":"s","currid":"sec-1"},
            {"obj_type":"acct","id":"a-income","name":"Salary","type":"i","currid":"USD"},
            {"obj_type":"acct","id":"a-expense","name":"Groceries","type":"e","currid":"USD"},
            {"obj_type":"acct","id":"a-bank","name":"Checking","type":"b","currid":"USD","sbal":"10000"}
            """);

        var balances = BalanceValidator.ComputeExpectedByMdAccountId(export);

        Assert.True (balances.ContainsKey("a-bank"));
        Assert.False(balances.ContainsKey("a-root"));
        Assert.False(balances.ContainsKey("a-secsub"));
        Assert.False(balances.ContainsKey("a-income"));
        Assert.False(balances.ContainsKey("a-expense"));
    }
}
