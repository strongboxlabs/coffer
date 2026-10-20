using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Builds an RFC 5545 <c>RRULE</c> string from a Moneydance reminder's
/// periodicity fields (ADR-0047 — migration 124 stores recurrence as RRULE,
/// not the old discrete columns). Mirrors the precedence
/// <see cref="ReminderMapper"/> used: daily → monthly → weekly → yearly, with
/// everything-zero falling to <c>custom</c> (returns null → no auto-expansion;
/// the user fires it manually).
/// </summary>
/// <remarks>
/// <para><b>Day-of-week mapping.</b> MD's <c>weeklydays</c> uses the Java
/// Calendar day-of-week numbering — <c>1=SU, 2=MO, 3=TU, 4=WE, 5=TH, 6=FR,
/// 7=SA</c>; an empty value means "not weekly". Verified against a sample MD
/// export (a "every Friday" reminder carried <c>6</c>, a "Monday" reminder
/// carried <c>2</c>), which is why this is 1-based 1=SU rather than the
/// 0-based / 1=MO scheme one might assume.</para>
/// </remarks>
public static class MdReminderRrule
{
    /// <summary>The RRULE for <paramref name="r"/>, or null for an irregular
    /// (custom) reminder that has no expressible recurrence.</summary>
    public static string? Build(MdReminder r)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (r.Daily is { } daily && daily > 0)
            return "FREQ=DAILY" + Interval(daily);

        if (r.MonthlyDay is { } md && md > 0 && md <= 31)
            return "FREQ=MONTHLY" + Interval(r.MonthlyMod is { } mm && mm > 0 ? mm : 1)
                   + ";BYMONTHDAY=" + md;

        if (r.WeeklyDay is { } wd && wd >= 1 && wd <= 7)
            return "FREQ=WEEKLY" + Interval(r.WeeklyMod is { } wm && wm > 0 ? wm : 1)
                   + ";BYDAY=" + ByDay(wd);

        if (r.Yearly is { } y && y > 0)
            return "FREQ=YEARLY" + Interval(y);

        return null;
    }

    private static string Interval(int n) => n > 1 ? $";INTERVAL={n}" : string.Empty;

    // Java Calendar day-of-week (see class remarks) -> RRULE BYDAY.
    private static string ByDay(int dow) => dow switch
    {
        1 => "SU",
        2 => "MO",
        3 => "TU",
        4 => "WE",
        5 => "TH",
        6 => "FR",
        7 => "SA",
        _ => "SU",
    };
}
