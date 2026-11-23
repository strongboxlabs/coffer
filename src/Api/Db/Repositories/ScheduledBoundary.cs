namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The one boundary between what the register calls Scheduled and what it does not,
/// shared by the register and by every current value (ADR-0056, 2026-10-21
/// amendment).
/// </summary>
/// <remarks>
/// The register stores a date as midnight UTC of that calendar day and marks a row
/// Scheduled when its <c>posted_at</c> falls on a day after the caller's LOCAL today
/// (<see cref="RegisterFilter.Today"/>): at or after <see cref="ScheduledFrom"/>. A
/// current balance, holding or price counts exactly the rows that are not Scheduled,
/// so the two cannot disagree about a row on the boundary day for any part of any
/// day, whatever the caller's time zone.
/// <para>
/// A caller that sends no date (MCP, a background job, any browserless client) gets
/// the register's own fallback, the server clock's UTC date. That is the only place
/// the fallback is written, so a balance never invents a rule the register does not
/// apply.
/// </para>
/// </remarks>
public static class ScheduledBoundary
{
    /// <summary>The caller's local today, or the register's fallback when none was
    /// sent: the server clock's UTC date.</summary>
    public static DateOnly Today(DateOnly? callerToday, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return callerToday ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The first instant the register calls Scheduled: the caller's local tomorrow at
    /// midnight UTC, the way the register stores that date. A row with
    /// <c>posted_at</c> at or after it is Scheduled; every current value counts only
    /// the rows before it.
    /// </summary>
    public static DateTime ScheduledFrom(DateOnly? callerToday, TimeProvider clock) =>
        ScheduledFrom(Today(callerToday, clock));

    /// <summary><see cref="ScheduledFrom(DateOnly?, TimeProvider)"/> for a date already
    /// known, which needs no clock.</summary>
    public static DateTime ScheduledFrom(DateOnly today) =>
        today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// The last instant the register does not call Scheduled: <see cref="ScheduledFrom"/>
    /// less one microsecond, timestamptz's resolution, so an as-of read at it
    /// (<c>posted_at &lt;= as_of</c>) admits exactly the rows a current value counts
    /// (<c>posted_at &lt; scheduled_from</c>). For a read that takes an instant rather
    /// than a boundary, when its caller asks for "now".
    /// </summary>
    public static DateTime LastCountedInstant(DateOnly? callerToday, TimeProvider clock) =>
        LastCountedInstant(Today(callerToday, clock));

    /// <summary><see cref="LastCountedInstant(DateOnly?, TimeProvider)"/> for a date
    /// already known, which needs no clock.</summary>
    public static DateTime LastCountedInstant(DateOnly today) =>
        ScheduledFrom(today).AddTicks(-TimeSpan.TicksPerMicrosecond);

    /// <summary>The register's Scheduled test for one row.</summary>
    public static bool IsScheduled(DateTime postedAt, DateTime scheduledFrom) =>
        postedAt >= scheduledFrom;
}
