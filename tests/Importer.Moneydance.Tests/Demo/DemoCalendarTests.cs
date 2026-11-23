using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The Demo's window, life months and Federal Reserve business days. The holiday
/// spot checks each name a rule: a Sunday holiday observed on the Monday, a
/// Saturday one left where it is, and each floating holiday on its weekday.
/// </summary>
public sealed class DemoCalendarTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    [Fact]
    public void The_window_starts_on_day_one_sixty_months_before_AsOf_month()
    {
        var calendar = new DemoCalendar(AsOf);

        Assert.Equal(new DateOnly(2021, 11, 1), calendar.WindowStart);
        Assert.Equal(AsOf, calendar.AsOf);
        Assert.Equal(new DateOnly(2021, 11, 1), calendar.MonthStart(0));
        Assert.Equal(new DateOnly(2026, 11, 1), calendar.MonthStart(DemoCalendar.CurrentLifeMonth));
        Assert.Equal(DemoCalendar.CurrentLifeMonth, calendar.LifeMonthOf(AsOf));
        Assert.Equal(AsOf, calendar.MonthEndInWindow(DemoCalendar.CurrentLifeMonth));
        Assert.Equal(new DateOnly(2026, 11, 30), calendar.MonthEnd(DemoCalendar.CurrentLifeMonth));
        Assert.Equal(AsOf.DayNumber - new DateOnly(2021, 11, 1).DayNumber + 1, calendar.DayCount);
    }

    [Fact]
    public void Life_months_and_years_follow_calendar_months()
    {
        var calendar = new DemoCalendar(AsOf);

        Assert.Equal(28, calendar.LifeMonthOf(new DateOnly(2024, 3, 15)));
        Assert.Equal(-24, calendar.LifeMonthOf(new DateOnly(2019, 11, 8)));
        Assert.Equal(1, DemoCalendar.LifeYearOf(0));
        Assert.Equal(1, DemoCalendar.LifeYearOf(11));
        Assert.Equal(2, DemoCalendar.LifeYearOf(12));
        Assert.Equal(5, DemoCalendar.LifeYearOf(59));
        Assert.Equal(57, DemoCalendar.MonthsBeforeAsOf(3));
    }

    [Fact]
    public void A_day_past_the_month_end_is_clamped()
    {
        var calendar = new DemoCalendar(AsOf);

        // Life month 3 is February 2022.
        Assert.Equal(new DateOnly(2022, 2, 28), calendar.DayInMonth(3, 31));
        Assert.Equal(new DateOnly(2022, 2, 15), calendar.DayInMonth(3, 15));
    }

    [Fact]
    public void A_window_on_a_leap_day_starts_on_the_first_of_its_month()
    {
        var calendar = new DemoCalendar(new DateOnly(2028, 2, 29));

        Assert.Equal(new DateOnly(2023, 2, 1), calendar.WindowStart);
        Assert.True(calendar.Contains(new DateOnly(2028, 2, 29)));
        Assert.False(calendar.Contains(new DateOnly(2028, 3, 1)));
        Assert.False(calendar.Contains(new DateOnly(2023, 1, 31)));
    }

    [Fact]
    public void Juneteenth_on_a_Sunday_is_observed_on_the_Monday()
    {
        var sunday = new DateOnly(2022, 6, 19);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);

        Assert.True(DemoCalendar.IsHoliday(new DateOnly(2022, 6, 20)));
        Assert.False(DemoCalendar.IsBusinessDay(new DateOnly(2022, 6, 20)));
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2022, 6, 21)));
    }

    [Fact]
    public void July_4_on_a_Saturday_is_not_moved()
    {
        var saturday = new DateOnly(2026, 7, 4);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        Assert.True(DemoCalendar.IsHoliday(saturday));
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2026, 7, 3)));
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2026, 7, 6)));
    }

    [Fact]
    public void Christmas_and_Veterans_Day_on_a_Saturday_leave_the_Friday_open()
    {
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2027, 12, 25).DayOfWeek);
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2027, 12, 24)));

        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2023, 11, 11).DayOfWeek);
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2023, 11, 10)));
    }

    [Fact]
    public void New_Year_on_a_Sunday_is_observed_on_January_2()
    {
        Assert.Equal(DayOfWeek.Sunday, new DateOnly(2023, 1, 1).DayOfWeek);
        Assert.False(DemoCalendar.IsBusinessDay(new DateOnly(2023, 1, 2)));
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2023, 1, 3)));
    }

    [Theory]
    [InlineData(2026, 1, 19)]  // third Monday of January
    [InlineData(2026, 2, 16)]  // third Monday of February
    [InlineData(2026, 5, 25)]  // last Monday of May
    [InlineData(2026, 9, 7)]   // first Monday of September
    [InlineData(2026, 10, 12)] // second Monday of October
    [InlineData(2026, 11, 26)] // fourth Thursday of November
    [InlineData(2025, 11, 27)] // fourth Thursday, in a November starting on a Saturday
    [InlineData(2024, 5, 27)]  // last Monday, in a May with five Mondays
    public void Floating_holidays_fall_on_their_weekday(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);

        Assert.True(DemoCalendar.IsHoliday(date), $"{date:yyyy-MM-dd} should be a holiday");
        Assert.False(DemoCalendar.IsBusinessDay(date));
    }

    [Fact]
    public void Every_year_has_eleven_observed_holidays_in_date_order()
    {
        for (var year = 2020; year <= 2030; year++)
        {
            var days = DemoCalendar.HolidaysIn(year);
            Assert.Equal(11, days.Count);
            Assert.Equal(days.OrderBy(d => d), days);
            Assert.All(days, d => Assert.Equal(year, d.Year));
        }
    }

    [Fact]
    public void Ordinary_weekdays_are_business_days_and_weekends_are_not()
    {
        Assert.True(DemoCalendar.IsBusinessDay(new DateOnly(2026, 11, 18)));  // a Wednesday
        Assert.False(DemoCalendar.IsBusinessDay(new DateOnly(2026, 11, 21))); // a Saturday
        Assert.False(DemoCalendar.IsBusinessDay(new DateOnly(2026, 11, 22))); // a Sunday
        Assert.False(DemoCalendar.IsHoliday(new DateOnly(2026, 11, 21)));
    }

    [Fact]
    public void Next_and_previous_business_days_step_over_weekends_and_holidays()
    {
        // Labor Day 2026 is Monday 7 September.
        Assert.Equal(new DateOnly(2026, 9, 8), DemoCalendar.OnOrNextBusinessDay(new DateOnly(2026, 9, 5)));
        Assert.Equal(new DateOnly(2026, 9, 4), DemoCalendar.OnOrPreviousBusinessDay(new DateOnly(2026, 9, 7)));
        Assert.Equal(new DateOnly(2026, 9, 8), DemoCalendar.NextBusinessDayAfter(new DateOnly(2026, 9, 4)));
        Assert.Equal(new DateOnly(2026, 9, 4), DemoCalendar.PreviousBusinessDayBefore(new DateOnly(2026, 9, 8)));

        var wednesday = new DateOnly(2026, 11, 18);
        Assert.Equal(wednesday, DemoCalendar.OnOrNextBusinessDay(wednesday));
        Assert.Equal(wednesday, DemoCalendar.OnOrPreviousBusinessDay(wednesday));

        // Thanksgiving week: Thursday is a holiday, Friday is open.
        Assert.Equal(new DateOnly(2026, 11, 27), DemoCalendar.NextBusinessDayAfter(new DateOnly(2026, 11, 25)));
    }

    [Fact]
    public void Business_days_in_a_holiday_week_skip_the_holiday()
    {
        var days = DemoCalendar.BusinessDays(new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 29)).ToList();

        Assert.Equal(
            new[] { new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 27) },
            days);
    }

    [Fact]
    public void Timestamps_are_noon_UTC_and_clamp_to_the_end_of_AsOf()
    {
        var calendar = new DemoCalendar(AsOf);
        var expected = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        Assert.Equal(expected, DemoCalendar.NoonEpochMillis(AsOf));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() - 1,
            calendar.LatestEpochMillis);
        Assert.Equal(20261118, DemoCalendar.MdDate(AsOf));
    }

    [Fact]
    public void A_date_outside_the_window_has_no_index()
    {
        var calendar = new DemoCalendar(AsOf);

        Assert.Equal(0, calendar.DayIndex(calendar.WindowStart));
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.DayIndex(AsOf.AddDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.DayIndex(calendar.WindowStart.AddDays(-1)));
    }
}
