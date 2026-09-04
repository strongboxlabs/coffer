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

    /// <summary>
    /// Create this (ledger, job_type) schedule ENABLED if it does not exist yet, and
    /// leave it exactly as it is if it does. Returns whether a row was created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For reminder auto-post, where the opt-in is the user's own action elsewhere.</b>
    /// Ticking "Auto-post / N days before" on a reminder IS the request to have it
    /// posted; making that tick depend on a second switch somewhere else reproduced the
    /// exact defect ADR-0097 set out to remove — a user told their rent posts itself while
    /// nothing acted on it. So the first reminder that asks for auto-posting turns the job
    /// on.
    /// </para>
    /// <para>
    /// <b>It never flips an EXISTING row, and that is the important half.</b> A disabled
    /// row is disabled for a reason: an operator paused it, or the scheduler switched it
    /// off after five consecutive failures and stamped <c>disabled_reason</c> (mig 216).
    /// Re-enabling on any reminder edit would silently launder that streak — editing an
    /// unrelated reminder would restart a job the scheduler had deliberately given up on,
    /// and the five-strike protection would never hold. Re-enabling stays an explicit act.
    /// </para>
    /// <para>
    /// No timezone is set on creation: null means the server's local zone, the same
    /// fallback <c>DailyScheduleTiming.Resolve</c> already applies, and the UI writes a
    /// real zone the first time someone sets the time. Guessing a zone here would be
    /// guessing where the user lives from a reminder edit.
    /// </para>
    /// </remarks>
    public async Task<bool> EnsureCreatedEnabledAsync(
        Guid ledgerId,
        string jobType,
        int hourLocal,
        Guid configuredByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.ScheduledJobs
            .AnyAsync(j => j.LedgerId == ledgerId && j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        if (exists) return false;

        _db.ScheduledJobs.Add(new ScheduledJobRow
        {
            LedgerId = ledgerId,
            JobType = jobType,
            Enabled = true,
            HourLocal = (short)hourLocal,
            MinuteLocal = 0,
            Timezone = null,
            NextRunAt = DailyScheduleTiming.NextRunUtc(hourLocal, 0, null, nowUtc),
            ConfiguredByUserId = configuredByUserId,
            UpdatedAt = nowUtc,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
        // Clearing the failure streak belongs to the disabled -> enabled TRANSITION and
        // nowhere else.
        //
        // It is not cosmetic. consecutive_failures is a streak the scheduler compares
        // against DisableAfterConsecutiveFailures, and it survived a re-enable — so a
        // job auto-disabled at five came back still holding five, and the next single
        // failure computed six and switched it straight off again. Re-enabling granted
        // one attempt, not five, and the log line that says "re-enable it once the cause
        // is fixed" promised otherwise.
        //
        // Narrow on purpose: not on every upsert, because changing the time on an
        // already-enabled failing job must not launder its state, and not on disable,
        // because the count is exactly what a reader wants to see on a job that just
        // stopped.
        if (enabled && !row.Enabled)
        {
            row.ConsecutiveFailures = 0;
            row.LastError = null;
            row.LastFailureAt = null;
            row.DisabledReason = null;
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

    // NAMED arguments, not positional. ScheduleDto has optional trailing parameters, so
    // a positional projection compiles against a stale field list and silently maps the
    // wrong values the next time one is inserted rather than appended.
    private static ScheduleDto ToDto(ScheduledJobRow row) =>
        new(
            Enabled: row.Enabled,
            HourLocal: row.HourLocal,
            MinuteLocal: row.MinuteLocal,
            Timezone: row.Timezone,
            LastRunAt: row.LastRunAt,
            NextRunAt: row.NextRunAt,
            ConsecutiveFailures: row.ConsecutiveFailures,
            LastError: row.LastError,
            LastFailureAt: row.LastFailureAt,
            DisabledReason: row.DisabledReason);
}
