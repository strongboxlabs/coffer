using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdAcctTests
{
    private static MdItem ReadOnlyAcct(string json)
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
    public void Parses_a_typical_bank_account()
    {
        var item = ReadOnlyAcct("""
            {
              "obj_type": "acct", "id": "bank-1", "name": "Northwind Checking",
              "type": "b", "currid": "USD",
              "is_inactive": "n", "hide": "no",
              "sbal": "850317",
              "bank_account_number": "xxx0000", "bank_name": "Northwind",
              "ofx_bank_id": "031176110",
              "comment": "primary checking"
            }
            """);

        var acct = MdAcct.From(item);

        Assert.Equal("bank-1", acct.Id);
        Assert.Equal("Northwind Checking", acct.Name);
        Assert.Equal("b", acct.TypeCode);
        Assert.Equal("USD", acct.CurrId);
        Assert.False(acct.IsInactive);
        Assert.False(acct.IsHidden);
        Assert.Equal(850317, acct.StartingBalance);
        Assert.Equal("xxx0000", acct.BankAccountNumber);
        Assert.Equal("Northwind", acct.BankName);
        Assert.Equal("031176110", acct.OfxBankId);
        Assert.Equal("primary checking", acct.Comment);
        Assert.False(acct.IsRoot);
        Assert.False(acct.IsSecuritySubAccount);
    }

    [Fact]
    public void Recognizes_security_sub_account_and_root()
    {
        var sec = MdAcct.From(ReadOnlyAcct("""
            {"obj_type":"acct","id":"sec-1","name":"IDXB","type":"s","currid":"sec-cur-1"}
            """));
        Assert.True(sec.IsSecuritySubAccount);
        Assert.False(sec.IsRoot);

        var root = MdAcct.From(ReadOnlyAcct("""
            {"obj_type":"acct","id":"root-1","name":"","type":"r","currid":"USD"}
            """));
        Assert.True(root.IsRoot);
        Assert.False(root.IsSecuritySubAccount);
    }

    [Fact]
    public void Parent_id_and_inactive_flag_round_trip()
    {
        var inactiveChild = MdAcct.From(ReadOnlyAcct("""
            {
              "obj_type":"acct","id":"a-2","name":"Old Loan","type":"l","currid":"USD",
              "parentid":"a-1","is_inactive":"y"
            }
            """));

        Assert.Equal("a-1", inactiveChild.ParentId);
        Assert.True(inactiveChild.IsInactive);
    }

    [Fact]
    public void Throws_when_obj_type_is_wrong()
    {
        var item = ReadOnlyAcct("""
            {"obj_type":"curr","id":"x","name":"y","type":"s","currid":"z"}
            """);
        Assert.Throws<ArgumentException>(() => MdAcct.From(item));
    }

    [Fact]
    public void Throws_when_required_type_field_is_missing()
    {
        var item = ReadOnlyAcct("""
            {"obj_type":"acct","id":"a-1","name":"no type","currid":"USD"}
            """);
        Assert.Throws<InvalidDataException>(() => MdAcct.From(item));
    }
}
