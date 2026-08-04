using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Endpoint-facing gateway for <c>scheduled_jobs</c> (mig 136) — the per-(ledger,
/// job_type) daily schedule. The background worker queries the DbSet directly
/// for its due-set; this is the read/write surface for the settings UI.
/// </summary>
public sealed class SchedulesRepository
{
    private readonly AppDbContext _db;

    public SchedulesRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>The (ledger, job_type) schedule, or null when never configured.</summary>
    public async Task<ScheduleDto?> GetAsync(
        Guid ledgerId, string jobType, CancellationToken cancellationToken = default)
    {
        var row = await _db.ScheduledJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.LedgerId == ledgerId && j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDto(row);
    }

    /// <summary>Upsert the (ledger, job_type) schedule; <c>next_run_at</c>
    /// recomputed when enabled, cleared when disabled.</summary>
    public async Task<ScheduleDto> UpsertAsync(
        Guid ledgerId,
        string jobType,
        bool enabled,
        short hourLocal,
        short minuteLocal,
        string? timezone,
        Guid configuredByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var nextRunAt = enabled
            ? DailyScheduleTiming.NextRunUtc(hourLocal, minuteLocal, timezone, nowUtc)
            : (DateTime?)null;
        var row = await _db.ScheduledJobs
            .FirstOrDefaultAsync(j => j.LedgerId == ledgerId && j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new ScheduledJobRow { LedgerId = ledgerId, JobType = jobType };
            _db.ScheduledJobs.Add(row);
        }
        row.Enabled = enabled;
        row.HourLocal = hourLocal;
        row.MinuteLocal = minuteLocal;
        row.Timezone = timezone;
        row.ConfiguredByUserId = configuredByUserId;
        row.NextRunAt = nextRunAt;
        row.UpdatedAt = nowUtc;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    private static ScheduleDto ToDto(ScheduledJobRow row) =>
        new(row.Enabled, row.HourLocal, row.MinuteLocal, row.Timezone, row.LastRunAt, row.NextRunAt);
}
