using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// Storing a schedule whose next run falls in a DST spring-forward gap.
/// </summary>
/// <remarks>
/// <para>
/// <c>UpsertAsync</c> computes <c>next_run_at</c> through the same
/// <see cref="DailyScheduleTiming"/> helper the scheduler tick uses, and a local time
/// inside the gap made <c>ConvertTimeToUtc</c> throw — a 500 on the PUT, and on the
/// tick an exception that escaped <c>RunDueAsync</c> and stalled every job on the
/// ledger for a day.
/// </para>
/// <para>
/// <b>This is a repository test rather than an endpoint test on purpose.</b> The
/// endpoint version was written first and was VACUOUS: the endpoint passes
/// <c>DateTime.UtcNow</c>, so the next 02:30 is almost never the spring-forward date,
/// and it passed with the guard deleted. Verified by mutation, not assumed.
/// <c>UpsertAsync</c> takes <c>nowUtc</c> as a parameter, so this level can pin the one
/// instant that actually exercises the gap — and the endpoint is covered by sharing the
/// helper, which the assertion on the stored <c>next_run_at</c> pins.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SchedulesRepositoryDstTests
{
    private readonly PostgresFixture _fixture;

    public SchedulesRepositoryDstTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// 2026-03-08 06:00Z is 01:00 EST — before the jump, so the next 02:30 slot is
    /// today's, and today's 02:30 does not exist in America/New_York.
    /// </summary>
    [Fact]
    public async Task Storing_a_schedule_whose_next_run_falls_in_the_dst_gap_succeeds()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewServiceDbContext();
        var repo = new SchedulesRepository(db);

        var nowUtc = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);

        var dto = await repo.UpsertAsync(
            ledger.LedgerId,
            JobTypes.QuoteRefresh,
            enabled: true,
            hourLocal: 2,
            minuteLocal: 30,
            timezone: "America/New_York",
            configuredByUserId: ledger.UserId,
            nowUtc: nowUtc);

        Assert.True(dto.Enabled);

        var storedNextRun = await db.ScheduledJobs
            .AsNoTracking()
            .Where(j => j.LedgerId == ledger.LedgerId && j.JobType == JobTypes.QuoteRefresh)
            .Select(j => j.NextRunAt)
            .SingleAsync();

        Assert.True(storedNextRun.HasValue, "an enabled schedule stored no next_run_at");

        // Forward, not backward, and not deferred a day: 02:30 EST would have been
        // 07:30Z; the gap pushes it to 03:00 EDT = 07:00Z. A fix that jumped to
        // tomorrow would land ~24h out and silently skip a day of runs.
        Assert.True(
            storedNextRun!.Value > nowUtc,
            $"next_run_at {storedNextRun:O} is not after {nowUtc:O}");
        Assert.True(
            storedNextRun.Value < nowUtc.AddHours(12),
            $"next_run_at {storedNextRun:O} is more than twelve hours out — the gap was "
            + "deferred to another day rather than skipped forward");

        // And the instant it settled on must genuinely exist locally.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var asLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(storedNextRun.Value, DateTimeKind.Utc), tz);
        Assert.False(
            tz.IsInvalidTime(DateTime.SpecifyKind(asLocal, DateTimeKind.Unspecified)),
            $"stored a next_run_at that maps to {asLocal:O}, still inside the DST gap");
    }
}
