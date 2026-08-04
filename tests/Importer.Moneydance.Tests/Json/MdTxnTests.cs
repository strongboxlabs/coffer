using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdTxnTests
{
    private static MdItem ReadOnlyTxn(string json)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{json}} ]
            }
            """;
        return MdItemReader.ReadString(wrapped).AllItems.Single();
    }

    [Fact]
    public void Parses_a_simple_single_split_txn()
    {
        var item = ReadOnlyTxn("""
            {
              "obj_type":"txn","id":"t-1",
              "acctid":"acct-checking",
              "desc":"Fuel Stop","memo":"DOE,JOHN",
              "dt":"20191214","td":"20191214",
              "dtentered":"1576644200025",
              "stat":"X","chk":"",
              "ol.orig-payee":"FUEL STOP 0000000000     ROCKVIL",
              "ol.orig-memo":"DOE,JOHN",
              "ol_fi_id":"ofx:Citigroup:24909",
              "ol_fitid_1":"1915193500001",
              "0.id":"split-1","0.acctid":"acct-gas",
              "0.samt":"4535","0.pamt":"-4535",
              "0.desc":"Fuel Stop","0.stat":"X"
            }
            """);

        var txn = MdTxn.From(item);

        Assert.Equal("t-1", txn.Id);
        Assert.Equal("acct-checking", txn.AcctId);
        Assert.Equal("Fuel Stop", txn.Description);
        Assert.Equal("DOE,JOHN", txn.Memo);
        Assert.Equal(20191214, txn.Date);
        Assert.Equal(20191214, txn.TransactedDate);
        Assert.Equal(1576644200025, txn.DateEnteredMillis);
        Assert.Equal("X", txn.Status);
        Assert.Null(txn.CheckNumber);
        Assert.False(txn.IsInvestmentTxn);

        var split = Assert.Single(txn.Splits);
        Assert.Equal(0, split.Index);
        Assert.Equal("split-1", split.Id);
        Assert.Equal("acct-gas", split.AcctId);
        Assert.Equal(4535, split.SplitAmount);
        Assert.Equal(-4535, split.ParentAmount);
        Assert.Equal("Fuel Stop", split.Description);
        Assert.Equal("X", split.Status);
    }

    [Fact]
    public void Walks_multiple_splits_in_order_and_stops_at_gap()
    {
        var item = ReadOnlyTxn("""
            {
              "obj_type":"txn","id":"t-2",
              "acctid":"acct-checking",
              "desc":"Split bill","dt":"20240101",
              "0.id":"s0","0.acctid":"acct-rent","0.samt":"100000","0.pamt":"-100000",
              "1.id":"s1","1.acctid":"acct-utilities","1.samt":"15000","1.pamt":"-15000",
              "2.id":"s2","2.acctid":"acct-internet","2.samt":"6000","2.pamt":"-6000"
            }
            """);

        var txn = MdTxn.From(item);

        Assert.Equal(3, txn.Splits.Count);
        Assert.Equal(new[] { 0, 1, 2 }, txn.Splits.Select(s => s.Index).ToArray());
        Assert.Equal(new[] { "acct-rent", "acct-utilities", "acct-internet" },
            txn.Splits.Select(s => s.AcctId).ToArray());
        Assert.Equal(new long[] { 100000, 15000, 6000 },
            txn.Splits.Select(s => s.SplitAmount).ToArray());
    }

    [Fact]
    public void Investment_txn_carries_invest_txntype_and_split_invest_type()
    {
        var item = ReadOnlyTxn("""
            {
              "obj_type":"txn","id":"t-3",
              "acctid":"acct-brokerage",
              "desc":"DIVIDEND RECEIVED","memo":"DIVIDEND RECEIVED",
              "dt":"20210226","stat":"X","reinvest":"false",
              "invest.txntype":"div","xfer_type":"xfrtp_dividend",
              "0.id":"sp0","0.acctid":"sec-etfa",
              "0.samt":"0","0.pamt":"0",
              "0.invest.splittype":"sec","0.stat":"X",
              "1.id":"sp1","1.acctid":"acct-cash-side",
              "1.samt":"-57896","1.pamt":"57896",
              "1.invest.splittype":"inc","1.stat":"X"
            }
            """);

        var txn = MdTxn.From(item);

        Assert.True(txn.IsInvestmentTxn);
        Assert.Equal("div", txn.InvestTxnType);
        Assert.Equal("xfrtp_dividend", txn.XferType);
        Assert.False(txn.Reinvest);
        Assert.Equal(2, txn.Splits.Count);
        Assert.Equal("sec", txn.Splits[0].InvestSplitType);
        Assert.Equal("inc", txn.Splits[1].InvestSplitType);
    }

    [Fact]
    public void Throws_when_obj_type_is_wrong()
    {
        var item = ReadOnlyTxn("""
            {"obj_type":"acct","id":"x","name":"y","type":"b","currid":"USD"}
            """);
        Assert.Throws<ArgumentException>(() => MdTxn.From(item));
    }

    [Fact]
    public void Throws_when_required_fields_are_missing()
    {
        var noAcctId = ReadOnlyTxn("""
            {"obj_type":"txn","id":"t-1","desc":"x","dt":"20240101"}
            """);
        Assert.Throws<InvalidDataException>(() => MdTxn.From(noAcctId));

        var noDate = ReadOnlyTxn("""
            {"obj_type":"txn","id":"t-2","acctid":"a","desc":"x"}
            """);
        Assert.Throws<InvalidDataException>(() => MdTxn.From(noDate));
    }
}
