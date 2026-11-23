using Coffer.Importer.Moneydance.Json;

namespace Coffer.Importer.Moneydance.Tests.Cli;

/// <summary>
/// The audit's currency-conversion count (<c>AuditCommand.CountTransfersBetweenCurrencies</c>):
/// a transaction counts when one of its splits joins two real accounts whose currencies
/// differ, the two-amount pairs the import keeps (ADR-0114 D13). A split's <c>samt</c>
/// and <c>pamt</c> differ in sign on every split, so comparing them counts nothing
/// useful. Every fixture is synthetic.
/// </summary>
public sealed class AuditCurrencyConversionTests
{
    private const string Header = """
        {"obj_type":"curr","id":"c-eur","currid":"EUR","name":"Euro","dec":"2","isbase":"1"},
        {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2"},
        {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-eur"},
        {"obj_type":"acct","id":"a-eur","type":"b","name":"Account E","parentid":"a-root","currid":"c-eur"},
        {"obj_type":"acct","id":"a-eur2","type":"b","name":"Account F","parentid":"a-root","currid":"c-eur"},
        {"obj_type":"acct","id":"a-usd","type":"b","name":"Account D","parentid":"a-root","currid":"c-usd"},
        {"obj_type":"acct","id":"c-usd-cat","type":"e","name":"Category G","parentid":"a-root","currid":"c-usd"}
        """;

    private static int Count(string transactions)
    {
        using var export = MdItemReader.ReadString($$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{Header}}{{transactions}} ]
            }
            """);
        return AuditCommand.CountTransfersBetweenCurrencies(export);
    }

    [Fact]
    public void A_transfer_within_one_currency_is_not_counted()
    {
        Assert.Equal(0, Count("""
            ,{"obj_type":"txn","id":"t-1","acctid":"a-eur","dt":"20260101",
              "0.id":"s-1","0.acctid":"a-eur2","0.pamt":"-1000","0.samt":"1000"}
            """));
    }

    [Fact]
    public void A_transfer_between_currencies_is_counted()
    {
        Assert.Equal(1, Count("""
            ,{"obj_type":"txn","id":"t-1","acctid":"a-eur","dt":"20260101",
              "0.id":"s-1","0.acctid":"a-usd","0.pamt":"-1000","0.samt":"1100"}
            """));
    }

    [Fact]
    public void A_category_in_another_currency_is_not_counted()
    {
        // A category leg mirrors the real side, so it is never a two-amount pair.
        Assert.Equal(0, Count("""
            ,{"obj_type":"txn","id":"t-1","acctid":"a-eur","dt":"20260101",
              "0.id":"s-1","0.acctid":"c-usd-cat","0.pamt":"-1000","0.samt":"1100"}
            """));
    }

    [Fact]
    public void Each_transaction_with_a_split_between_currencies_counts_once()
    {
        // t-1 has two such splits and counts once; t-2 has one beside a category line;
        // t-3 has none.
        Assert.Equal(2, Count("""
            ,{"obj_type":"txn","id":"t-1","acctid":"a-eur","dt":"20260101",
              "0.id":"s-1","0.acctid":"a-usd","0.pamt":"-1000","0.samt":"1100",
              "1.id":"s-2","1.acctid":"a-usd","1.pamt":"-500","1.samt":"550",
              "2.id":"s-3","2.acctid":"a-eur2","2.pamt":"-200","2.samt":"200"},
            {"obj_type":"txn","id":"t-2","acctid":"a-usd","dt":"20260102",
              "0.id":"s-4","0.acctid":"c-usd-cat","0.pamt":"-300","0.samt":"270",
              "1.id":"s-5","1.acctid":"a-eur","1.pamt":"-400","1.samt":"360"},
            {"obj_type":"txn","id":"t-3","acctid":"a-eur","dt":"20260103",
              "0.id":"s-6","0.acctid":"a-eur2","0.pamt":"-200","0.samt":"200"}
            """));
    }
}
