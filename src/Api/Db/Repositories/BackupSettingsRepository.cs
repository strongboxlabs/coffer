using Microsoft.EntityFrameworkCore;

using Coffer.Api.Backup;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>backup_settings</c> (mig 161, ADR-0074) — the deployment-wide
/// singleton backup retention policy. Connects via the <b>service-role</b> factory
/// (the table is global config with RLS deny-all for <c>coffer_app</c>, same
/// posture as <see cref="DriveSyncRepository"/>); the admin HTTP surface gates
/// access with RequireAdmin. This policy is the single source of truth for what
/// backups are kept — local pruning and the Drive mirror both follow it.
/// </summary>
public sealed class BackupSettingsRepository
{
    private const short SingletonId = 1;
    private readonly ServiceDbContextFactory _serviceFactory;

    public BackupSettingsRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>The current retention policy. A missing row (pre-seed) reads as
    /// the historical defaults (7/8/12), so callers always get a usable policy.</summary>
    public async Task<RetentionPolicy> GetRetentionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.BackupSettings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? new RetentionPolicy(7, 8, 12)
            : new RetentionPolicy(row.RetentionDaily, row.RetentionWeekly, row.RetentionMonthly);
    }

    /// <summary>Set the GFS retention tiers. Creates the singleton row if absent.</summary>
    public async Task SetRetentionAsync(
        short daily, short weekly, short monthly,
        Guid actorUserId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.BackupSettings
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new BackupSettingsRow { Id = SingletonId };
            db.BackupSettings.Add(row);
        }
        row.RetentionDaily = daily;
        row.RetentionWeekly = weekly;
        row.RetentionMonthly = monthly;
        row.ConfiguredByUserId = actorUserId;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
