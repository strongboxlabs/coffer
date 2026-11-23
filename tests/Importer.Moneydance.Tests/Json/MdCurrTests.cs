using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdCurrTests
{
    private static MdItem ReadOnlyCurr(string json)
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
    public void Parses_a_security_with_ticker_and_cusip()
    {
        var item = ReadOnlyCurr("""
            {
              "obj_type":"curr","id":"sec-1","name":"Fund A Emerging Markets Core Equity",
              "currid":"^OEFQ","type":"s",
              "ticker":"IDXE",
              "curr_id.CUSIP":"999888666",
              "dec":"5",
              "rate":"0.04051863857374392","rrate":"0.04051863857374392"
            }
            """);

        var curr = MdCurr.From(item);

        Assert.True(curr.IsSecurity);
        Assert.Equal("IDXE", curr.Ticker);
        Assert.Equal("999888666", curr.Cusip);
        Assert.Equal(5, curr.Decimals);
        Assert.Equal(0.04051863857374392m, curr.Rate);
    }

    [Fact]
    public void Security_type_fields_are_read_from_the_security_account_not_the_curr()
    {
        // Moneydance writes sec_type, sec_subtype and sec_exchange on the type-s
        // security ACCOUNT (ADR-0016, amendment of 2026-11-18). The curr view has no
        // such fields; the account view carries them, and only for a type-s account.
        Assert.DoesNotContain(typeof(MdCurr).GetProperties(), p => p.Name.StartsWith("Sec", StringComparison.Ordinal));

        var acct = MdAcct.From(ReadOnlyCurr("""
            {
              "obj_type":"acct","id":"s-1","name":"Fund A","type":"s","currid":"sec-1",
              "sec_type":"2","sec_subtype":"International","sec_exchange":"NYSE"
            }
            """));
        Assert.Equal(new MdSecurityFields("2", "International", "NYSE"), acct.Security);

        var bank = MdAcct.From(ReadOnlyCurr("""
            {"obj_type":"acct","id":"b-1","name":"Checking","type":"b","currid":"USD","sec_type":"2"}
            """));
        Assert.Null(bank.Security);

        var blank = MdAcct.From(ReadOnlyCurr("""
            {"obj_type":"acct","id":"s-2","name":"Fund B","type":"s","currid":"sec-2","sec_type":"","sec_exchange":""}
            """));
        Assert.Equal(MdSecurityFields.None, blank.Security);
    }

    [Fact]
    public void Falls_back_to_broken_cusip_field()
    {
        var item = ReadOnlyCurr("""
            {
              "obj_type":"curr","id":"sec-2","name":"Old Fund","currid":"^Old",
              "type":"s","ticker":"OLD",
              "curr_id.CUSIP-broken":"OEFQ",
              "dec":"4"
            }
            """);

        var curr = MdCurr.From(item);
        Assert.Equal("OEFQ", curr.Cusip);
    }

    [Fact]
    public void Recognizes_a_plain_currency_without_type()
    {
        var item = ReadOnlyCurr("""
            {
              "obj_type":"curr","id":"c-1","name":"South Korean Won",
              "currid":"KRW","isbase":"n","hide_in_ui":"y",
              "rate":"11.49645","rrate":"1149.645","dec":"0"
            }
            """);

        var curr = MdCurr.From(item);

        Assert.False(curr.IsSecurity);
        Assert.Null(curr.Ticker);
        Assert.Null(curr.Cusip);
        Assert.True(curr.IsHidden);
        Assert.False(curr.IsBase);
        Assert.Equal(0, curr.Decimals);
    }

    [Fact]
    public void Throws_when_obj_type_is_wrong()
    {
        var item = ReadOnlyCurr("""
            {"obj_type":"acct","id":"x","name":"y","type":"b","currid":"USD"}
            """);
        Assert.Throws<ArgumentException>(() => MdCurr.From(item));
    }
}
