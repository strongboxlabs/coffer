using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Json;

public sealed class MdCsnapAndReminderTests
{
    private static MdItem ReadOnlyItem(string json)
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
    public void Csnap_round_trips_price_fields()
    {
        var item = ReadOnlyItem("""
            {
              "obj_type":"csnap","id":"snap-1",
              "curr":"sec-etfa",
              "dt":"20180112",
              "price_date":"1528383840395",
              "rate":"0.09445992537665895","rrate":"0.09445992537665895",
              "hi":"0.09363295880149813","lo":"0.09363295880149813",
              "rhi":"0.09363295880149813","rlo":"0.09363295880149813",
              "vol":"0"
            }
            """);

        var snap = MdCsnap.From(item);

        Assert.Equal("snap-1", snap.Id);
        Assert.Equal("sec-etfa", snap.CurrId);
        Assert.Equal(20180112, snap.Date);
        Assert.Equal(1528383840395, snap.PriceDateMillis);
        Assert.Equal(0.09445992537665895m, snap.Rate);
        Assert.Equal(0.09363295880149813m, snap.High);
        Assert.Equal(0L, snap.Volume);
    }

    [Fact]
    public void Csnap_throws_when_curr_is_missing()
    {
        var item = ReadOnlyItem("""
            {"obj_type":"csnap","id":"snap-2","dt":"20240101"}
            """);
        Assert.Throws<InvalidDataException>(() => MdCsnap.From(item));
    }

    [Fact]
    public void Reminder_extracts_recurrence_and_embedded_txn_template()
    {
        var item = ReadOnlyItem("""
            {
              "obj_type":"reminder","id":"rem-1",
              "desc":"Galaxy Card Auto Payment",
              "memo":"",
              "type":"1",
              "sdt":"20240520","ackdt":"20260520","ldt":"0",
              "acdays":"-1",
              "monthlydays":"20","monthlymod":"0",
              "weeklydays":"0","weeklymod":"0",
              "daily":"0","yearly":"0",
              "is_loan_reminder":"0",
              "txn.acctid":"acct-checking","txn.desc":"Galaxy Card",
              "txn.memo":"00-000-000-000-0 Jane Doe",
              "txn.dt":"0","txn.td":"20000327",
              "txn.dtentered":"1435662862964",
              "txn.chk":"",
              "txn.0.id":"split-rem-0","txn.0.acctid":"acct-store",
              "txn.0.samt":"30062","txn.0.pamt":"-30062",
              "txn.0.desc":"Galaxy Card","txn.0.stat":" ",
              "txn.tags":""
            }
            """);

        var reminder = MdReminder.From(item);

        Assert.Equal("rem-1", reminder.Id);
        Assert.Equal("Galaxy Card Auto Payment", reminder.Description);
        Assert.Equal("1", reminder.Type);
        Assert.Equal(20240520, reminder.StartDate);
        Assert.Equal(20260520, reminder.AcknowledgedDate);
        Assert.Equal(20, reminder.MonthlyDay);
        Assert.False(reminder.IsLoanReminder);

        Assert.NotNull(reminder.Txn);
        Assert.Equal("acct-checking", reminder.Txn!.AcctId);
        Assert.Equal("Galaxy Card", reminder.Txn.Description);
        Assert.Equal(20000327, reminder.Txn.TransactedDate);
        Assert.Single(reminder.Txn.Splits);
        Assert.Equal("acct-store", reminder.Txn.Splits[0].AcctId);
        Assert.Equal(30062, reminder.Txn.Splits[0].SplitAmount);
        Assert.Equal(-30062, reminder.Txn.Splits[0].ParentAmount);
    }

    [Fact]
    public void Reminder_without_embedded_txn_returns_null_template()
    {
        var item = ReadOnlyItem("""
            {
              "obj_type":"reminder","id":"rem-2",
              "desc":"Empty reminder","type":"1",
              "sdt":"20240101"
            }
            """);

        var reminder = MdReminder.From(item);
        Assert.Null(reminder.Txn);
    }
}
