using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Scheduling;

/// <summary>
/// The one definition of "next daily run", including the day of the year when the
/// time a user picked does not exist.
/// </summary>
/// <remarks>
/// <para>
/// A local time inside the DST spring-forward gap is not merely unusual — it makes
/// <c>TimeZoneInfo.ConvertTimeToUtc</c> throw <see cref="ArgumentException"/>. This
/// helper is called from <c>SchedulerRunner</c>'s claim-before-work block, ABOVE the
/// try that wraps the handler, so the throw escapes <c>RunDueAsync</c>: the row never
/// advances, every job after it in the tick is skipped, and the tick repeats fifteen
/// minutes later having done nothing. <c>SchedulesRepository.UpsertAsync</c> shares the
/// helper, so the same input also 500s the schedule PUT.
/// </para>
/// <para>
/// America/New_York on 2026-03-08 skips 02:00-03:00 local, so 02:30 is the canonical
/// non-existent time. Australia/Lord_Howe is here because its shift is THIRTY minutes,
/// which catches a fix that hard-codes an hour and lands back inside the gap.
/// </para>
/// </remarks>
public sealed class DailyScheduleTimingTests
{
    private const string NewYork = "America/New_York";

    /// <summary>The gap is walked, not thrown on.</summary>
    [Fact]
    public void A_run_time_inside_the_spring_forward_gap_resolves_instead_of_throwing()
    {
        // 2026-03-08 06:00Z is 01:00 EST — before the 02:00 jump, so the next 02:30
        // slot is today's, and today's 02:30 does not exist.
        var nowUtc = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);

        var next = DailyScheduleTiming.NextRunUtc(2, 30, NewYork, nowUtc);

        Assert.True(
            next > nowUtc,
            $"next run {next:O} is not after now {nowUtc:O} — a gap fix must move forward, "
            + "never backward into an instant that has already passed");

        // The whole point: whatever instant came back must actually exist locally.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(NewYork);
        var asLocal = TimeZoneInfo.ConvertTimeFromUtc(next, tz);
        Assert.False(
            tz.IsInvalidTime(DateTime.SpecifyKind(asLocal, DateTimeKind.Unspecified)),
            $"resolved to {asLocal:O}, which is still inside the DST gap");
    }

    /// <summary>
    /// The gap is skipped by MINUTES, not by a day — deferring would silently drop a
    /// day of scheduled work, which is the same outage the throw caused.
    /// </summary>
    [Fact]
    public void The_gap_is_skipped_forward_not_deferred_to_the_next_day()
    {
        var nowUtc = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);

        var next = DailyScheduleTiming.NextRunUtc(2, 30, NewYork, nowUtc);

        // 02:30 EST would have been 07:30Z; the gap pushes it to 03:00 EDT = 07:00Z.
        // Either way it lands the SAME morning. A fix that returned tomorrow would be
        // ~24h later and passes the "exists" assertion above, so this is the one that
        // catches it.
        Assert.True(
            next < nowUtc.AddHours(12),
            $"next run {next:O} is more than twelve hours out — the gap was deferred to "
            + "another day rather than skipped forward, losing a day of runs");
    }

    /// <summary>
    /// A thirty-minute gap. A fix that assumed one hour would overshoot here; one that
    /// hard-coded a jump of exactly the wrong width would land back inside it.
    /// </summary>
    [Fact]
    public void A_half_hour_gap_zone_also_resolves()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");
        }
        catch (TimeZoneNotFoundException)
        {
            return; // No ICU data for it here; the New_York cases still cover the shape.
        }

        // Lord Howe springs forward 30 minutes at 02:00 local on 2026-10-04.
        var nowUtc = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var next = DailyScheduleTiming.NextRunUtc(2, 15, "Australia/Lord_Howe", nowUtc);

        var asLocal = TimeZoneInfo.ConvertTimeFromUtc(next, tz);
        Assert.False(
            tz.IsInvalidTime(DateTime.SpecifyKind(asLocal, DateTimeKind.Unspecified)),
            $"resolved to {asLocal:O}, still inside Lord Howe's thirty-minute gap");
    }

    /// <summary>
    /// The ordinary path is unchanged, pinned to exact instants. Without this, "skip
    /// the gap" implemented as an unconditional AddDays would pass every case above
    /// and quietly delay every scheduled job in the system by a day.
    /// </summary>
    [Theory]
    // 12:00Z is 07:00 EST; today's 19:00 slot is still ahead -> today, 19:00 EST = 00:00Z next day.
    [InlineData(12, 19, "2026-02-11T00:00:00Z")]
    // 23:00Z is 18:00 EST; today's 09:00 slot has passed -> tomorrow, 09:00 EST = 14:00Z.
    [InlineData(23, 9, "2026-02-11T14:00:00Z")]
    public void An_ordinary_day_still_picks_the_exact_next_slot(
        int nowHourUtc, int slotHourLocal, string expectedUtc)
    {
        var nowUtc = new DateTime(2026, 2, 10, nowHourUtc, 0, 0, DateTimeKind.Utc);

        var next = DailyScheduleTiming.NextRunUtc(slotHourLocal, 0, NewYork, nowUtc);

        Assert.Equal(
            DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.SpecifyKind(next, DateTimeKind.Utc));
    }

    /// <summary>
    /// An unknown zone id falls back rather than throwing — the scheduler must not be
    /// stopped by a row carrying a zone this host has no data for.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void An_unusable_timezone_id_falls_back_instead_of_throwing(string? id)
    {
        var nowUtc = new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);

        var next = DailyScheduleTiming.NextRunUtc(19, 0, id, nowUtc);

        Assert.True(next > nowUtc);
    }
}
