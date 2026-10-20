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
              "price_date":"1515760245123",
              "rate":"0.08103727714748785","rrate":"0.08103727714748785",
              "hi":"0.08130081300813008","lo":"0.08130081300813008",
              "rhi":"0.08130081300813008","rlo":"0.08130081300813008",
              "vol":"0"
            }
            """);

        var snap = MdCsnap.From(item);

        Assert.Equal("snap-1", snap.Id);
        Assert.Equal("sec-etfa", snap.CurrId);
        Assert.Equal(20180112, snap.Date);
        Assert.Equal(1515760245123, snap.PriceDateMillis);
        Assert.Equal(0.08103727714748785m, snap.Rate);
        Assert.Equal(0.08130081300813008m, snap.High);
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
              "desc":"Card A Auto Payment",
              "memo":"",
              "type":"1",
              "sdt":"20240110","ackdt":"20260110","ldt":"0",
              "acdays":"-1",
              "monthlydays":"10","monthlymod":"0",
              "weeklydays":"0","weeklymod":"0",
              "daily":"0","yearly":"0",
              "is_loan_reminder":"0",
              "txn.acctid":"acct-checking","txn.desc":"Card A",
              "txn.memo":"00-000-000-000-0 Payee A",
              "txn.dt":"0","txn.td":"20000110",
              "txn.dtentered":"1419951612345",
              "txn.chk":"",
              "txn.0.id":"split-rem-0","txn.0.acctid":"acct-store",
              "txn.0.samt":"24680","txn.0.pamt":"-24680",
              "txn.0.desc":"Card A","txn.0.stat":" ",
              "txn.tags":""
            }
            """);

        var reminder = MdReminder.From(item);

        Assert.Equal("rem-1", reminder.Id);
        Assert.Equal("Card A Auto Payment", reminder.Description);
        Assert.Equal("1", reminder.Type);
        Assert.Equal(20240110, reminder.StartDate);
        Assert.Equal(20260110, reminder.AcknowledgedDate);
        Assert.Equal(10, reminder.MonthlyDay);
        Assert.False(reminder.IsLoanReminder);

        Assert.NotNull(reminder.Txn);
        Assert.Equal("acct-checking", reminder.Txn!.AcctId);
        Assert.Equal("Card A", reminder.Txn.Description);
        Assert.Equal(20000110, reminder.Txn.TransactedDate);
        Assert.Single(reminder.Txn.Splits);
        Assert.Equal("acct-store", reminder.Txn.Splits[0].AcctId);
        Assert.Equal(24680, reminder.Txn.Splits[0].SplitAmount);
        Assert.Equal(-24680, reminder.Txn.Splits[0].ParentAmount);
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
