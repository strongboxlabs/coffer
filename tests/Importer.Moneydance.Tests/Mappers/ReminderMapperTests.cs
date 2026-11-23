using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// Reminder → recurring-reminder SERIES translation (ADR-0047 / mig 124).
/// Asserts the template <c>txn_header</c> + legs are built like a live txn
/// (flagged a template), the slim recurring row carries the RRULE (Java DOW,
/// verified) + the acdays auto-commit rule + the raw source_payload, and the
/// skip paths.
/// </summary>
public sealed class ReminderMapperTests
{
    private const string ImportSource = "moneydance_export";

    private static MdReminder ReminderFromJson(string fragment)
    {
        var wrapped = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [ {{fragment}} ]
            }
            """;
        return MdReminder.From(MdItemReader.ReadString(wrapped).AllItems.Single());
    }

    /// <summary>
    /// Accounts by MD id. A brokerage gets a Holdings sibling, as AccountImportStep
    /// wires every investment account: an event is owned by a brokerage, which is
    /// an account that names its holdings account.
    /// </summary>
    private static IReadOnlyDictionary<string, AccountRef> AccountMap(params (string MdId, string Type)[] accounts) =>
        accounts.ToDictionary(
            a => a.MdId,
            a => new AccountRef(Guid.NewGuid(), a.Type, HoldingsAccountId: a.Type == "investment" ? Guid.NewGuid() : null,
                AmountDecimals: 2),
            StringComparer.Ordinal);

    private static ReminderMapper.MapResult Map(
        MdReminder reminder, IReadOnlyDictionary<string, AccountRef> accounts, string rawJson = "{\"k\":1}") =>
        ReminderMapper.Map(reminder, accounts, Guid.NewGuid(), ImportSource, rawJson);

    [Fact]
    public void Map_stamps_the_reminder_account_as_the_template_owner()
    {
        // Migration 263: the reminder's account (txn.acctid), which is also the
        // series source, on every posting of a two-line template.
        var reminder = ReminderFromJson("""
            {
              "obj_type":"reminder","id":"rem-owner",
              "desc":"Recurring A","type":"monthly","sdt":"20230101",
              "monthlydays":"1","monthlymod":"1",
              "daily":"0","weeklydays":"","yearly":"0",
              "is_loan_reminder":"0",
              "txn.acctid":"a-checking","txn.desc":"Recurring A",
              "txn.0.acctid":"a-cat","txn.0.samt":"1000","txn.0.pamt":"-1000",
              "txn.1.acctid":"a-cat2","txn.1.samt":"500","txn.1.pamt":"-500"
            }
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-cat", "category"), ("a-cat2", "category"));

        var result = Map(reminder, accounts);

        Assert.Null(result.Skip);
        Assert.Equal(accounts["a-checking"].Id, result.Header!.OriginatingAccountId);
        Assert.Equal(result.Row!.SourceAccountId, result.Header.OriginatingAccountId);
    }

    [Fact]
    public void Map_builds_a_template_header_and_legs_for_a_monthly_reminder()
    {
        var reminder = ReminderFromJson("""
            {
              "obj_type":"reminder","id":"rem-rent",
              "desc":"Recurring A","memo":"note",
              "type":"monthly","sdt":"20230101",
              "monthlydays":"1","monthlymod":"1",
              "daily":"0","weeklydays":"","yearly":"0",
              "is_loan_reminder":"0",
              "txn.acctid":"a-checking","txn.desc":"Recurring A",
              "txn.0.acctid":"a-cat","txn.0.samt":"150000","txn.0.pamt":"-150000"
            }
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-cat", "category"));

        var result = Map(reminder, accounts);

        Assert.Null(result.Skip);

        // Template header — a non-live event.
        var h = result.Header!;
        Assert.True(h.IsRecurringTemplate);
        Assert.Equal("manual", h.Origin);
        Assert.Null(h.ProviderKey);
        Assert.Equal("mdreminder:rem-rent", h.ExternalId);
        Assert.Equal("Recurring A", h.Payee);
        Assert.Null(h.Memo);   // a one-split template's memo is its account's own (below)
        Assert.Equal("uncleared", h.Status);
        Assert.Equal(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), h.PostedAt);

        // Legs: origin on the source account (parent_amount), counterpart on
        // the target (split_amount). Minor units / 100.
        Assert.Equal(2, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Equal(h.Id, l.HeaderId));
        Assert.All(result.Legs, l => Assert.Equal(0, l.PostingIndex));
        var origin = result.Legs.Single(l => l.AccountId == accounts["a-checking"].Id);
        var counter = result.Legs.Single(l => l.AccountId == accounts["a-cat"].Id);
        Assert.Equal(-1500.00m, origin.Amount);
        Assert.Equal(1500.00m, counter.Amount);
        Assert.Equal("note", origin.LegMemo);
        Assert.Null(counter.LegMemo);

        // Slim recurring row.
        var row = result.Row!;
        Assert.Equal("rem-rent", row.ExternalId);
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=1", row.Rrule);
        Assert.Equal(h.Id, row.TemplateHeaderId);   // proposed id; the step remaps to persisted
        Assert.Equal(accounts["a-checking"].Id, row.SourceAccountId);   // mig 125 source pointer
        Assert.Equal(new DateOnly(2023, 1, 1), row.StartDate);
        Assert.False(row.IsLoanReminder);
        Assert.True(row.IsActive);
        Assert.Equal("moneydance_import", row.Origin);
        Assert.Equal("{\"k\":1}", row.SourcePayload);
        // No acknowledged date → cursor seeds at the first occurrence.
        Assert.Null(row.LastAcknowledgedDate);
        Assert.Equal(new DateOnly(2023, 1, 1), row.NextDueDate);
    }

    [Fact]
    public void Map_seeds_next_due_after_the_acknowledged_date()
    {
        // An invented monthly-on-the-10th reminder started years ago (2012-03-04),
        // acknowledged through 2025-02-10 in Moneydance: the cursor must seed at
        // 2025-03-10, NOT the 2012 first occurrence (ADR-0051 — the acknowledged floor).
        var reminder = ReminderFromJson("""
            {
              "obj_type":"reminder","id":"rem-loan","desc":"Loan payment","sdt":"20120304",
              "type":"monthly","monthlydays":"10","monthlymod":"1",
              "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"1",
              "ackdt":"20250210",
              "txn.acctid":"a-checking",
              "txn.0.acctid":"a-loan","txn.0.samt":"100000","txn.0.pamt":"-100000"
            }
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-loan", "loan"));

        var row = Map(reminder, accounts).Row!;
        Assert.Equal(new DateOnly(2025, 2, 10), row.LastAcknowledgedDate);
        Assert.Equal(new DateOnly(2025, 3, 10), row.NextDueDate);
    }

    [Fact]
    public void Map_weekly_uses_java_calendar_day_of_week()
    {
        var reminder = ReminderFromJson("""
            {
              "obj_type":"reminder","id":"rem-weekly","desc":"W","sdt":"20240115",
              "type":"weekly","weeklydays":"1","weeklymod":"2",
              "daily":"0","monthlydays":"","yearly":"0","is_loan_reminder":"0",
              "txn.acctid":"a-checking",
              "txn.0.acctid":"a-cash","txn.0.samt":"2000","txn.0.pamt":"-2000"
            }
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-cash", "bank"));

        // weeklydays=1 -> Sunday (Java Calendar DOW), interval 2.
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=SU", Map(reminder, accounts).Row!.Rrule);
    }

    [Fact]
    public void Map_daily_and_custom()
    {
        var daily = ReminderFromJson("""
            {"obj_type":"reminder","id":"rd","desc":"D","sdt":"20240101","type":"daily",
             "daily":"1","monthlydays":"","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-c","txn.0.acctid":"a-x","txn.0.samt":"500","txn.0.pamt":"-500"}
            """);
        var custom = ReminderFromJson("""
            {"obj_type":"reminder","id":"rc","desc":"C","sdt":"20240101","type":"custom",
             "daily":"0","monthlydays":"","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-c","txn.0.acctid":"a-x","txn.0.samt":"500","txn.0.pamt":"-500"}
            """);
        var accounts = AccountMap(("a-c", "bank"), ("a-x", "category"));

        Assert.Equal("FREQ=DAILY", Map(daily, accounts).Row!.Rrule);
        Assert.Null(Map(custom, accounts).Row!.Rrule);   // irregular -> manual-fire
    }

    [Fact]
    public void Map_multi_split_emits_a_leg_pair_per_split()
    {
        var reminder = ReminderFromJson("""
            {
              "obj_type":"reminder","id":"rem-paycheck","desc":"Paycheck","sdt":"20240101",
              "type":"monthly","monthlydays":"15","monthlymod":"1",
              "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
              "txn.acctid":"a-checking",
              "txn.0.acctid":"a-salary","txn.0.samt":"-300000","txn.0.pamt":"300000",
              "txn.1.acctid":"a-401k",  "txn.1.samt":"-50000", "txn.1.pamt":"50000"
            }
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-salary", "category"), ("a-401k", "category"));

        var result = Map(reminder, accounts);

        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=15", result.Row!.Rrule);
        Assert.Equal(4, result.Legs.Count);   // two splits -> two leg pairs
        var checkingTotal = result.Legs
            .Where(l => l.AccountId == accounts["a-checking"].Id)
            .Sum(l => l.Amount);
        Assert.Equal(3500.00m, checkingTotal);   // both splits' cash impact on the source, summed
    }

    [Theory]
    [InlineData(2, 2)]      // acdays 2 -> auto-commit 2 days before
    [InlineData(0, 0)]      // acdays 0 -> auto-commit on the due date
    [InlineData(-1, null)]  // acdays -1 -> off (manual)
    public void Map_acdays_maps_to_auto_commit(int acdays, int? expected)
    {
        var reminder = ReminderFromJson($$"""
            {"obj_type":"reminder","id":"ra","desc":"A","sdt":"20240101","type":"daily",
             "daily":"1","monthlydays":"","weeklydays":"","yearly":"0","acdays":"{{acdays}}",
             "is_loan_reminder":"0",
             "txn.acctid":"a-c","txn.0.acctid":"a-x","txn.0.samt":"500","txn.0.pamt":"-500"}
            """);
        var accounts = AccountMap(("a-c", "bank"), ("a-x", "category"));

        Assert.Equal(expected, Map(reminder, accounts).Row!.AutoCommitDaysBefore);
    }

    [Fact]
    public void Map_skips_reminder_with_no_template()
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-empty","desc":"orphan","sdt":"20240101",
             "type":"monthly","is_loan_reminder":"0"}
            """);
        var result = Map(reminder, AccountMap());
        Assert.Null(result.Row);
        Assert.Equal(ReminderMapper.SkipReason.NoTemplate, result.Skip);
    }

    [Fact]
    public void Map_skips_when_source_account_unknown()
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-orphan","desc":"x","sdt":"20240101",
             "type":"monthly","is_loan_reminder":"0",
             "txn.acctid":"unknown",
             "txn.0.acctid":"a-cat","txn.0.samt":"100","txn.0.pamt":"-100"}
            """);
        var result = Map(reminder, AccountMap(("a-cat", "category")));
        Assert.Null(result.Row);
        Assert.Equal(ReminderMapper.SkipReason.UnknownSourceAccount, result.Skip);
    }

    [Fact]
    public void Map_carries_the_loan_reminder_flag_passively()
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-loan","desc":"Mortgage","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"1",
             "txn.acctid":"a-checking",
             "txn.0.acctid":"a-mortgage","txn.0.samt":"100000","txn.0.pamt":"-100000"}
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-mortgage", "loan"));
        Assert.True(Map(reminder, accounts).Row!.IsLoanReminder);
    }

    // -- brokerage cash moves -------------------------------------------------
    //
    // A template is built like a live bank-route txn, so it must classify like
    // one: a one-posting cash move out of a brokerage is a 'transfer'
    // (BrokerageCashTransfer), or a fired occurrence would carry an action its
    // template never had.

    [Theory]
    [InlineData("category")]
    [InlineData("bank")]
    public void Map_a_brokerage_template_with_one_split_is_a_transfer(string targetType)
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-broker","desc":"Sweep","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-brokerage",
             "txn.0.acctid":"a-target","txn.0.samt":"100","txn.0.pamt":"-100"}
            """);
        var accounts = AccountMap(("a-brokerage", "investment"), ("a-target", targetType));

        var result = Map(reminder, accounts);

        Assert.Equal("transfer", result.Header!.Action);
        Assert.Equal(2, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Equal("transfer", l.PostingRole));
    }

    [Fact]
    public void Map_a_brokerage_template_with_more_than_one_split_stays_actionless()
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-broker-multi","desc":"Two ways","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-brokerage",
             "txn.0.acctid":"a-cat","txn.0.samt":"100","txn.0.pamt":"-100",
             "txn.1.acctid":"a-bank","txn.1.samt":"50","txn.1.pamt":"-50"}
            """);
        var accounts = AccountMap(("a-brokerage", "investment"), ("a-cat", "category"), ("a-bank", "bank"));

        var result = Map(reminder, accounts);

        Assert.Null(result.Header!.Action);
        Assert.Equal(4, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }

    [Fact]
    public void Map_a_bank_template_stays_actionless()
    {
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-bank","desc":"Bill","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-checking",
             "txn.0.acctid":"a-brokerage","txn.0.samt":"100","txn.0.pamt":"-100"}
            """);
        var accounts = AccountMap(("a-checking", "bank"), ("a-brokerage", "investment"));

        var result = Map(reminder, accounts);

        Assert.Null(result.Header!.Action);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }

    // The bank route only ever sees txns that are NOT investment-shaped
    // (MdTxn.IsInvestmentShape), so the rule applies to a template only when
    // its embedded txn is not investment-shaped either: a txntype, or one of
    // the investment xfer_types, keeps it out.
    [Theory]
    [InlineData(""" "txn.invest.txntype":"bank", """)]
    [InlineData(""" "txn.xfer_type":"xfrtp_dividend", """)]
    [InlineData(""" "txn.xfer_type":"xfrtp_miscincexp", """)]
    public void Map_an_investment_shaped_brokerage_template_stays_actionless(string shape)
    {
        var reminder = ReminderFromJson($$"""
            {"obj_type":"reminder","id":"rem-broker-inv","desc":"Sweep","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-brokerage", {{shape}}
             "txn.0.acctid":"a-target","txn.0.samt":"100","txn.0.pamt":"-100"}
            """);
        var accounts = AccountMap(("a-brokerage", "investment"), ("a-target", "category"));

        var result = Map(reminder, accounts);

        Assert.Null(result.Header!.Action);
        Assert.Equal(2, result.Legs.Count);
        Assert.All(result.Legs, l => Assert.Null(l.PostingRole));
    }

    [Fact]
    public void Map_a_brokerage_template_with_a_bank_xfer_type_is_still_a_transfer()
    {
        // xfrtp_bank is not an investment xfer_type: the QIF cash rows carry it.
        var reminder = ReminderFromJson("""
            {"obj_type":"reminder","id":"rem-broker-xfr","desc":"Sweep","sdt":"20240101",
             "type":"monthly","monthlydays":"1","monthlymod":"1",
             "daily":"0","weeklydays":"","yearly":"0","is_loan_reminder":"0",
             "txn.acctid":"a-brokerage","txn.xfer_type":"xfrtp_bank",
             "txn.0.acctid":"a-target","txn.0.samt":"100","txn.0.pamt":"-100"}
            """);
        var accounts = AccountMap(("a-brokerage", "investment"), ("a-target", "category"));

        var result = Map(reminder, accounts);

        Assert.Equal("transfer", result.Header!.Action);
    }
}
