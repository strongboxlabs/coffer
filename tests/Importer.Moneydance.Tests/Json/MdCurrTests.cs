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
              "obj_type":"curr","id":"sec-1","name":"DFA Emerging Markets Core Equity",
              "currid":"^OEFQ","type":"s",
              "ticker":"DFCEX",
              "curr_id.CUSIP":"233203488",
              "sec_type":"Mutual Fund","sec_subtype":"International",
              "sec_exchange":"NYSE",
              "dec":"5",
              "rate":"0.03806623524933384","rrate":"0.03806623524933384"
            }
            """);

        var curr = MdCurr.From(item);

        Assert.True(curr.IsSecurity);
        Assert.Equal("DFCEX", curr.Ticker);
        Assert.Equal("233203488", curr.Cusip);
        Assert.Equal("Mutual Fund", curr.SecType);
        Assert.Equal("International", curr.SecSubtype);
        Assert.Equal("NYSE", curr.SecExchange);
        Assert.Equal(5, curr.Decimals);
        Assert.Equal(0.03806623524933384m, curr.Rate);
    }

    [Fact]
    public void Falls_back_to_broken_cusip_field()
    {
        var item = ReadOnlyCurr("""
            {
              "obj_type":"curr","id":"sec-2","name":"Old Fund","currid":"^Old",
              "type":"s","ticker":"OLD",
              "curr_id.CUSIP-broken":"OEFQ",
              "sec_type":"Mutual Fund","dec":"4"
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
        Assert.Null(curr.SecType);
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
