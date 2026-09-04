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

        // A local time inside the spring-forward gap DOES NOT EXIST, and
        // ConvertTimeToUtc throws ArgumentException rather than picking a side.
        // Left unguarded that throw is not contained: SchedulerRunner calls this
        // in its claim-before-work block, ABOVE the try that wraps the handler, so
        // it escapes RunDueAsync entirely — the offending row never advances, every
        // job after it in the tick is skipped, and SchedulerService logs "will
        // retry next interval" and does it all again in fifteen minutes. One user
        // picking 02:30 costs a whole day of every scheduled job on that ledger.
        // SchedulesRepository.UpsertAsync shares this helper, so the same input
        // also 500s the schedule PUT year-round.
        //
        // Skipping FORWARD past the gap is the correct resolution for a recurring
        // daily slot: 02:30 does not happen that day, and the job should run as
        // soon as the clock reaches a time that does, not be silently deferred to
        // tomorrow (which would skip the day entirely) and not run an hour early.
        // The gap is walked rather than assumed to be an hour wide: Lord Howe
        // Island shifts by thirty minutes, and a hard-coded hour would land back
        // inside the gap there.
        if (tz.IsInvalidTime(nextLocal))
            nextLocal = SkipGap(nextLocal, tz);

        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);
    }

    /// <summary>
    /// Advance a local time out of a DST spring-forward gap, to the first instant
    /// that actually exists.
    /// </summary>
    /// <remarks>
    /// Steps by one minute rather than by the rule's delta because a gap is at most
    /// an hour or two and correctness matters more than the loop count here; the
    /// bound stops a malformed rule turning this into a hang.
    /// </remarks>
    private static DateTime SkipGap(DateTime invalidLocal, TimeZoneInfo tz)
    {
        var candidate = invalidLocal;
        for (var i = 0; i < 24 * 60 && tz.IsInvalidTime(candidate); i++)
            candidate = candidate.AddMinutes(1);

        // Unreachable for any real rule. If a zone ever did define a gap longer
        // than a day, failing loudly beats returning a time that does not exist.
        if (tz.IsInvalidTime(candidate))
        {
            throw new InvalidOperationException(
                $"Could not find a valid local time within 24h of {invalidLocal:O} in {tz.Id}.");
        }

        return candidate;
    }

    internal static TimeZoneInfo Resolve(string? timezoneId)
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
