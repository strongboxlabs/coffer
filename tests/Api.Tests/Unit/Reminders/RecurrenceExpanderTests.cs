using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Unit.Reminders;

/// <summary>
/// Date-math correctness for <see cref="RecurrenceExpander"/> — the reason
/// ADR-0047 uses a library instead of a hand-rolled walk. Covers the patterns
/// the editor + MD importer produce plus the edge cases that bite naive
/// expanders: month-end skip, last-day, interval stepping, COUNT termination,
/// and window clipping. Pure unit test (no DB, no clock).
/// </summary>
public sealed class RecurrenceExpanderTests
{
    private static readonly RecurrenceExpander Expander = new();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static IReadOnlyList<DateOnly> Expand(
        string rrule, DateOnly start, DateOnly from, DateOnly to) =>
        Expander.Expand(rrule, start, from, to);

    [Fact]
    public void Daily_yields_consecutive_days_in_window()
    {
        var result = Expand("FREQ=DAILY", D(2024, 1, 1), D(2024, 1, 1), D(2024, 1, 4));
        Assert.Equal(new[] { D(2024, 1, 1), D(2024, 1, 2), D(2024, 1, 3), D(2024, 1, 4) }, result);
    }

    [Fact]
    public void Monthly_by_month_day_5_yields_the_fifth_each_month()
    {
        // The screenshot's reminder: Monthly · every · 5th.
        var result = Expand("FREQ=MONTHLY;BYMONTHDAY=5", D(2024, 1, 5), D(2024, 1, 1), D(2024, 4, 30));
        Assert.Equal(new[] { D(2024, 1, 5), D(2024, 2, 5), D(2024, 3, 5), D(2024, 4, 5) }, result);
    }

    [Fact]
    public void Monthly_last_day_clamps_to_each_month_length_including_leap_february()
    {
        var result = Expand("FREQ=MONTHLY;BYMONTHDAY=-1", D(2024, 1, 31), D(2024, 1, 1), D(2024, 4, 30));
        // 2024 is a leap year -> Feb 29; April has 30 days.
        Assert.Equal(new[] { D(2024, 1, 31), D(2024, 2, 29), D(2024, 3, 31), D(2024, 4, 30) }, result);
    }

    [Fact]
    public void Monthly_by_month_day_31_skips_months_without_a_31st()
    {
        // RFC 5545: BYMONTHDAY=31 yields ONLY in months that have a 31st
        // (not a clamp) — Feb + April are skipped.
        var result = Expand("FREQ=MONTHLY;BYMONTHDAY=31", D(2024, 1, 31), D(2024, 1, 1), D(2024, 4, 30));
        Assert.Equal(new[] { D(2024, 1, 31), D(2024, 3, 31) }, result);
    }

    [Fact]
    public void Weekly_by_day_yields_that_weekday()
    {
        // 2024-01-02 is a Tuesday.
        var result = Expand("FREQ=WEEKLY;BYDAY=TU", D(2024, 1, 2), D(2024, 1, 1), D(2024, 1, 31));
        Assert.Equal(
            new[] { D(2024, 1, 2), D(2024, 1, 9), D(2024, 1, 16), D(2024, 1, 23), D(2024, 1, 30) },
            result);
    }

    [Fact]
    public void Weekly_interval_2_yields_every_other_week()
    {
        var result = Expand("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU", D(2024, 1, 2), D(2024, 1, 1), D(2024, 1, 31));
        Assert.Equal(new[] { D(2024, 1, 2), D(2024, 1, 16), D(2024, 1, 30) }, result);
    }

    [Fact]
    public void Yearly_yields_the_same_date_each_year()
    {
        var result = Expand("FREQ=YEARLY", D(2024, 7, 4), D(2024, 1, 1), D(2027, 12, 31));
        Assert.Equal(new[] { D(2024, 7, 4), D(2025, 7, 4), D(2026, 7, 4), D(2027, 7, 4) }, result);
    }

    [Fact]
    public void Count_terminates_the_series()
    {
        var result = Expand("FREQ=DAILY;COUNT=3", D(2024, 1, 10), D(2024, 1, 1), D(2024, 1, 31));
        Assert.Equal(new[] { D(2024, 1, 10), D(2024, 1, 11), D(2024, 1, 12) }, result);
    }

    [Fact]
    public void Window_clips_both_ends_inclusively()
    {
        var result = Expand("FREQ=DAILY", D(2024, 1, 1), D(2024, 1, 10), D(2024, 1, 12));
        Assert.Equal(new[] { D(2024, 1, 10), D(2024, 1, 11), D(2024, 1, 12) }, result);
    }

    [Fact]
    public void Blank_rule_or_inverted_window_yields_nothing()
    {
        Assert.Empty(Expand("", D(2024, 1, 1), D(2024, 1, 1), D(2024, 1, 31)));
        Assert.Empty(Expand("FREQ=DAILY", D(2024, 1, 1), D(2024, 1, 31), D(2024, 1, 1)));
    }

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=5")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO")]
    [InlineData("FREQ=YEARLY;COUNT=3")]
    public void IsValidRrule_accepts_well_formed_rules(string rrule) =>
        Assert.True(Expander.IsValidRrule(rrule));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FREQ=NONSENSE")]
    [InlineData("not an rrule at all")]
    public void IsValidRrule_rejects_blank_or_malformed_rules(string? rrule) =>
        Assert.False(Expander.IsValidRrule(rrule));
}
