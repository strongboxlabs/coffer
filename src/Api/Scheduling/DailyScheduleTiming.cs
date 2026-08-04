namespace Coffer.Api.Scheduling;

/// <summary>
/// Shared timing for the per-ledger daily scheduler (mig 136/137). One
/// definition of "next daily run at a given time-of-day in a given timezone" so
/// every job type computes it the same way.
/// </summary>
public static class DailyScheduleTiming
{
    /// <summary>
    /// The next UTC instant for a daily run at <paramref name="hourLocal"/>:
    /// <paramref name="minuteLocal"/> in <paramref name="timezoneId"/> (an IANA
    /// id, e.g. <c>America/New_York</c>) — today's occurrence if still ahead,
    /// else tomorrow's. A null/blank/unknown id falls back to the server's local
    /// timezone. IANA (not a fixed offset) so DST stays correct year-round.
    /// </summary>
    public static DateTime NextRunUtc(int hourLocal, int minuteLocal, string? timezoneId, DateTime nowUtc)
    {
        var tz = Resolve(timezoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var todayRun = new DateTime(
            localNow.Year, localNow.Month, localNow.Day,
            hourLocal, minuteLocal, 0, DateTimeKind.Unspecified);
        var nextLocal = localNow < todayRun ? todayRun : todayRun.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);
    }

    private static TimeZoneInfo Resolve(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
            return TimeZoneInfo.Local;
        try
        {
            // .NET resolves both IANA and Windows ids cross-platform (ICU).
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
