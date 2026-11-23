namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The Demo's window, its life months and its business days.
/// </summary>
/// <remarks>
/// <para><b>Window.</b> Day 1 of the month <see cref="WindowMonths"/> months before
/// AsOf's month, through AsOf. Calendar months are numbered <c>c = 0..60</c>, and
/// <c>c = 60</c> is AsOf's partial month. Life months ARE calendar months: there is
/// no compression, and "year k" is life months <c>12(k-1)..12k-1</c>. A comment in
/// the generator writes life month n as <c>Ln</c>: <c>L0</c> is the window's first
/// month, <c>L27</c> the twenty-eighth.</para>
///
/// <para><b>Business days.</b> Monday to Friday, excluding the US Federal Reserve
/// holidays, computed here rather than listed: New Year's Day, Juneteenth,
/// Independence Day, Veterans Day and Christmas on their fixed dates; the third
/// Monday of January and of February, the last Monday of May, the first Monday of
/// September, the second Monday of October and the fourth Thursday of November. A
/// fixed holiday on a Sunday is observed on the Monday. One on a Saturday is not
/// moved, because the Reserve Banks open the Friday before.</para>
///
/// <para>Every member is a pure function of AsOf; nothing reads a clock.</para>
/// </remarks>
public sealed class DemoCalendar
{
    /// <summary>Whole months between the window's first month and AsOf's month.</summary>
    public const int WindowMonths = 60;

    /// <summary>The life month of AsOf's own, partial, month.</summary>
    public const int CurrentLifeMonth = WindowMonths;

    private const long MillisPerDay = 86_400_000L;
    private const long NoonMillis = MillisPerDay / 2;
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    /// <summary>A calendar whose window ends on <paramref name="asOf"/>.</summary>
    public DemoCalendar(DateOnly asOf)
    {
        AsOf = asOf;
        WindowStart = new DateOnly(asOf.Year, asOf.Month, 1).AddMonths(-WindowMonths);
    }

    /// <summary>The last day of the window: the generator's only "now".</summary>
    public DateOnly AsOf { get; }

    /// <summary>The first day of the window: day 1 of life month 0.</summary>
    public DateOnly WindowStart { get; }

    /// <summary>Days in the window, both ends included.</summary>
    public int DayCount => AsOf.DayNumber - WindowStart.DayNumber + 1;

    /// <summary>True when <paramref name="date"/> lies in the window.</summary>
    public bool Contains(DateOnly date) => date >= WindowStart && date <= AsOf;

    /// <summary>The zero-based day index of a date in the window.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The date is outside the window.</exception>
    public int DayIndex(DateOnly date)
    {
        if (!Contains(date))
        {
            throw new ArgumentOutOfRangeException(
                nameof(date), date, $"outside the Demo window {WindowStart:yyyy-MM-dd}..{AsOf:yyyy-MM-dd}");
        }
        return date.DayNumber - WindowStart.DayNumber;
    }

    /// <summary>Day 1 of life month <paramref name="lifeMonth"/>. A negative month
    /// lies before the window, which a pre-window loan start needs.</summary>
    public DateOnly MonthStart(int lifeMonth) => WindowStart.AddMonths(lifeMonth);

    /// <summary>The last calendar day of life month <paramref name="lifeMonth"/>,
    /// which for the current month lies after AsOf.</summary>
    public DateOnly MonthEnd(int lifeMonth) => MonthStart(lifeMonth + 1).AddDays(-1);

    /// <summary>The last day of life month <paramref name="lifeMonth"/> that is in
    /// the window: the month end, or AsOf for the current month.</summary>
    public DateOnly MonthEndInWindow(int lifeMonth)
    {
        var end = MonthEnd(lifeMonth);
        return end > AsOf ? AsOf : end;
    }

    /// <summary>Day <paramref name="day"/> of life month <paramref name="lifeMonth"/>,
    /// clamped to the month's length (a 31st in a 30-day month is the 30th).</summary>
    public DateOnly DayInMonth(int lifeMonth, int day)
    {
        if (day < 1) throw new ArgumentOutOfRangeException(nameof(day), day, "a day of the month is 1 or more");
        var start = MonthStart(lifeMonth);
        var length = DateTime.DaysInMonth(start.Year, start.Month);
        return start.AddDays((day > length ? length : day) - 1);
    }

    /// <summary>The life month a date falls in. Negative before the window.</summary>
    public int LifeMonthOf(DateOnly date) =>
        (date.Year - WindowStart.Year) * 12 + (date.Month - WindowStart.Month);

    /// <summary>The life month <paramref name="monthsBeforeAsOf"/> calendar months
    /// before AsOf's month: <c>MonthsBeforeAsOf(k)</c> is <c>CurrentLifeMonth - k</c>.</summary>
    public static int MonthsBeforeAsOf(int monthsBeforeAsOf) => CurrentLifeMonth - monthsBeforeAsOf;

    /// <summary>The one-based life year a life month belongs to.</summary>
    public static int LifeYearOf(int lifeMonth)
    {
        if (lifeMonth < 0) throw new ArgumentOutOfRangeException(nameof(lifeMonth), lifeMonth, "before the window");
        return lifeMonth / 12 + 1;
    }

    /// <summary>True when the date is a Monday-to-Friday that is not a Federal
    /// Reserve holiday.</summary>
    public static bool IsBusinessDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date);

    /// <summary>The date itself when it is a business day, else the next one.</summary>
    public static DateOnly OnOrNextBusinessDay(DateOnly date)
    {
        while (!IsBusinessDay(date)) date = date.AddDays(1);
        return date;
    }

    /// <summary>The date itself when it is a business day, else the one before.</summary>
    public static DateOnly OnOrPreviousBusinessDay(DateOnly date)
    {
        while (!IsBusinessDay(date)) date = date.AddDays(-1);
        return date;
    }

    /// <summary>The first business day strictly after the date.</summary>
    public static DateOnly NextBusinessDayAfter(DateOnly date) => OnOrNextBusinessDay(date.AddDays(1));

    /// <summary>The last business day strictly before the date.</summary>
    public static DateOnly PreviousBusinessDayBefore(DateOnly date) => OnOrPreviousBusinessDay(date.AddDays(-1));

    /// <summary>Every business day from <paramref name="from"/> through
    /// <paramref name="through"/>, in order.</summary>
    public static IEnumerable<DateOnly> BusinessDays(DateOnly from, DateOnly through)
    {
        for (var d = from; d <= through; d = d.AddDays(1))
        {
            if (IsBusinessDay(d)) yield return d;
        }
    }

    /// <summary>Every business day in the window, in order.</summary>
    public IEnumerable<DateOnly> BusinessDaysInWindow() => BusinessDays(WindowStart, AsOf);

    /// <summary>
    /// True when the date is the day a Federal Reserve holiday is observed. A
    /// Saturday holiday is its own (non-business) Saturday; a Sunday one is the
    /// Monday after.
    /// </summary>
    public static bool IsHoliday(DateOnly date)
    {
        foreach (var holiday in HolidaysIn(date.Year))
        {
            if (holiday == date) return true;
        }
        return false;
    }

    /// <summary>The observed Federal Reserve holidays of one year, in date order.
    /// A Saturday holiday is listed on its Saturday.</summary>
    public static IReadOnlyList<DateOnly> HolidaysIn(int year)
    {
        var days = new List<DateOnly>(11)
        {
            Observed(new DateOnly(year, 1, 1)),
            NthWeekday(year, 1, DayOfWeek.Monday, 3),
            NthWeekday(year, 2, DayOfWeek.Monday, 3),
            LastWeekday(year, 5, DayOfWeek.Monday),
            Observed(new DateOnly(year, 6, 19)),
            Observed(new DateOnly(year, 7, 4)),
            NthWeekday(year, 9, DayOfWeek.Monday, 1),
            NthWeekday(year, 10, DayOfWeek.Monday, 2),
            Observed(new DateOnly(year, 11, 11)),
            NthWeekday(year, 11, DayOfWeek.Thursday, 4),
            Observed(new DateOnly(year, 12, 25)),
        };
        days.Sort();
        return days;
    }

    /// <summary>Epoch milliseconds of 12:00 UTC on the date: the base of every
    /// generated timestamp.</summary>
    public static long NoonEpochMillis(DateOnly date) =>
        (date.DayNumber - EpochDayNumber) * MillisPerDay + NoonMillis;

    /// <summary>Epoch milliseconds of the last millisecond of AsOf (UTC): the
    /// ceiling every generated timestamp is clamped to.</summary>
    public long LatestEpochMillis => (AsOf.DayNumber - EpochDayNumber + 1) * MillisPerDay - 1;

    /// <summary>The date as Moneydance's <c>yyyymmdd</c> integer.</summary>
    public static int MdDate(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;

    private static DateOnly Observed(DateOnly actual) =>
        actual.DayOfWeek == DayOfWeek.Sunday ? actual.AddDays(1) : actual;

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (n - 1) * 7);
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var back = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
        return last.AddDays(-back);
    }
}
