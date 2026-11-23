namespace Coffer.Api.Scheduling;

/// <summary>
/// Default times of day that more than one writer of <c>scheduled_jobs</c> must agree on.
/// </summary>
public static class ScheduleDefaults
{
    /// <summary>
    /// 05:00 local, for <see cref="JobTypes.ReminderAutoPost"/>. After the 03:00 snapshot,
    /// so that is a stable pre-dawn picture taken before the day's reminders land, and
    /// before the 06:00 feed sync, so the day's reminders exist before bank data arrives
    /// or anyone looks at a balance.
    /// </summary>
    /// <remarks>
    /// Shared by every path that turns the job on without asking for a time (a reminder
    /// saved with auto-post, a Moneydance import that brings one) and by the schedules
    /// page's reading of a ledger that has no row yet, so the time a user is shown is the
    /// time the job is created with.
    /// </remarks>
    public const int ReminderAutoPostHour = 5;
}
