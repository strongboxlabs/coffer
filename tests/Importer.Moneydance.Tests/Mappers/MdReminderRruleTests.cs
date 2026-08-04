using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// MD periodicity → RFC-5545 RRULE (migration 124 / ADR-0047). Pins the
/// frequency mapping + the day-of-week assumption (see
/// <see cref="MdReminderRrule"/> remarks — verify against a real-world export).
/// </summary>
public sealed class MdReminderRruleTests
{
    private static MdReminder R(
        int? daily = null, int? monthlyDay = null, int? monthlyMod = null,
        int? weeklyDay = null, int? weeklyMod = null, int? yearly = null) =>
        new(
            Id: "r1", Description: "desc", Memo: null, Type: "t",
            StartDate: 20240101, AcknowledgedDate: null, LastDate: null, AckDays: null,
            MonthlyDay: monthlyDay, MonthlyMod: monthlyMod,
            WeeklyDay: weeklyDay, WeeklyMod: weeklyMod,
            Daily: daily, Yearly: yearly,
            IsLoanReminder: false, Tags: null, Txn: null);

    [Fact]
    public void Daily_with_and_without_interval()
    {
        Assert.Equal("FREQ=DAILY", MdReminderRrule.Build(R(daily: 1)));
        Assert.Equal("FREQ=DAILY;INTERVAL=3", MdReminderRrule.Build(R(daily: 3)));
    }

    [Fact]
    public void Monthly_by_month_day_with_and_without_interval()
    {
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=5", MdReminderRrule.Build(R(monthlyDay: 5)));
        Assert.Equal("FREQ=MONTHLY;INTERVAL=2;BYMONTHDAY=5",
            MdReminderRrule.Build(R(monthlyDay: 5, monthlyMod: 2)));
    }

    [Fact]
    public void Weekly_by_day_with_and_without_interval()
    {
        // weeklydays 2 = Monday (Java Calendar DOW; verified against a sample export).
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", MdReminderRrule.Build(R(weeklyDay: 2)));
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO",
            MdReminderRrule.Build(R(weeklyDay: 2, weeklyMod: 2)));
    }

    [Theory]
    [InlineData(1, "SU")]
    [InlineData(2, "MO")]
    [InlineData(3, "TU")]
    [InlineData(4, "WE")]
    [InlineData(5, "TH")]
    [InlineData(6, "FR")]
    [InlineData(7, "SA")]
    public void Weekly_day_of_week_uses_java_calendar_numbering(int weeklyDay, string byday)
    {
        // MD weeklydays = Java Calendar DOW (1=SU .. 7=SA), verified against a
        // sample export ("every Friday" -> 6, "Monday" -> 2). See MdReminderRrule.
        Assert.Equal($"FREQ=WEEKLY;BYDAY={byday}", MdReminderRrule.Build(R(weeklyDay: weeklyDay)));
    }

    [Fact]
    public void Yearly_with_and_without_interval()
    {
        Assert.Equal("FREQ=YEARLY", MdReminderRrule.Build(R(yearly: 1)));
        Assert.Equal("FREQ=YEARLY;INTERVAL=2", MdReminderRrule.Build(R(yearly: 2)));
    }

    [Fact]
    public void Irregular_reminder_has_no_rrule()
    {
        Assert.Null(MdReminderRrule.Build(R()));
    }

    [Fact]
    public void Precedence_daily_wins_over_other_fields()
    {
        // Mirrors ReminderMapper.DeriveSchedule precedence.
        Assert.Equal("FREQ=DAILY", MdReminderRrule.Build(R(daily: 1, monthlyDay: 5, weeklyDay: 2)));
    }
}
