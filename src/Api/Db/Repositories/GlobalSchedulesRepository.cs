using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>global_scheduled_jobs</c> (mig 139) — the deployment-wide
/// (non-ledger) schedule + the master-KEK-sealed backup passphrase. Connects
/// via the <b>service-role</b> factory: the table is global config with no
/// per-ledger RLS predicate, reserved to <c>coffer_service</c> (mirrors
/// <see cref="SessionsRepository"/> / bootstrap tokens). The admin HTTP surface
/// gates access with the RequireAdmin policy; this repository is the data seam.
/// </summary>
public sealed class GlobalSchedulesRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public GlobalSchedulesRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>The global schedule for <paramref name="jobType"/> plus whether
    /// a passphrase has been set, or null when never configured.</summary>
    public async Task<GlobalScheduleState?> GetAsync(
        string jobType, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.GlobalScheduledJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToState(row);
    }

    /// <summary>Upsert the schedule fields; <c>next_run_at</c> recomputed when
    /// enabled, cleared when disabled. The sealed passphrase is left
    /// untouched.</summary>
    public async Task<GlobalScheduleState> UpsertScheduleAsync(
        string jobType,
        bool enabled,
        short hourLocal,
        short minuteLocal,
        string? timezone,
        Guid configuredByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.GlobalScheduledJobs
            .FirstOrDefaultAsync(j => j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new GlobalScheduledJobRow { JobType = jobType };
            db.GlobalScheduledJobs.Add(row);
        }
        row.Enabled = enabled;
        row.HourLocal = hourLocal;
        row.MinuteLocal = minuteLocal;
        row.Timezone = timezone;
        row.ConfiguredByUserId = configuredByUserId;
        row.NextRunAt = enabled
            ? DailyScheduleTiming.NextRunUtc(hourLocal, minuteLocal, timezone, nowUtc)
            : null;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToState(row);
    }

    /// <summary>Store the sealed passphrase (creating the row, disabled, if it
    /// doesn't exist yet); schedule fields are left untouched.</summary>
    public async Task SetPassphraseCiphertextAsync(
        string jobType,
        byte[] passphraseCiphertext,
        Guid configuredByUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passphraseCiphertext);
        await using var db = _serviceFactory.Create();
        var row = await db.GlobalScheduledJobs
            .FirstOrDefaultAsync(j => j.JobType == jobType, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new GlobalScheduledJobRow { JobType = jobType };
            db.GlobalScheduledJobs.Add(row);
        }
        row.PassphraseCiphertext = passphraseCiphertext;
        row.ConfiguredByUserId = configuredByUserId;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The sealed passphrase bytes, or null when none is set. Callers
    /// open it with <c>LedgerKeyService.OpenWithMasterKey</c>.</summary>
    public async Task<byte[]?> GetPassphraseCiphertextAsync(
        string jobType, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.GlobalScheduledJobs.AsNoTracking()
            .Where(j => j.JobType == jobType)
            .Select(j => j.PassphraseCiphertext)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static GlobalScheduleState ToState(GlobalScheduledJobRow row) =>
        new(
            new ScheduleDto(
                row.Enabled, row.HourLocal, row.MinuteLocal,
                row.Timezone, row.LastRunAt, row.NextRunAt),
            PassphraseConfigured: row.PassphraseCiphertext is { Length: > 0 });
}

/// <summary>The global schedule plus whether a passphrase has been set. The
/// ciphertext itself never leaves the data layer — only this boolean.</summary>
public sealed record GlobalScheduleState(ScheduleDto Schedule, bool PassphraseConfigured);
