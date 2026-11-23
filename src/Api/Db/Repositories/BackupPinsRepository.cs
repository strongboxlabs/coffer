using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>backup_pins</c> (mig 144, ADR-0062; kind and label from mig 268, ADR-0110)
/// — pins keyed by backup artifact id. Service-role only (deployment-global, RLS
/// deny-all for <c>coffer_app</c>); the admin HTTP surface gates with RequireAdmin.
/// A pinned artifact, of either kind, is excluded from both local and Drive retention sweeps.
/// Two kinds: an admin "never delete" pin, and an upgrade pin that an upgrade started from
/// the app takes with its backup, of which only the newest <see cref="UpgradePinsKept"/> are kept.
/// </summary>
public sealed class BackupPinsRepository
{
    /// <summary>How many upgrade backups are held after an upgrade succeeds.</summary>
    public const int UpgradePinsKept = 2;

    private readonly ServiceDbContextFactory _serviceFactory;

    public BackupPinsRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>The set of pinned artifact ids of either kind (retention excludes these).</summary>
    public async Task<IReadOnlySet<string>> GetPinnedIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var ids = await db.BackupPins.AsNoTracking()
            .Select(p => p.ArtifactId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The ids of admin pins only: the backup list's "pinned" flag, which an upgrade pin does not set.</summary>
    public async Task<IReadOnlySet<string>> GetAdminPinnedIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var ids = await db.BackupPins.AsNoTracking()
            .Where(p => p.Kind == BackupPinKinds.Admin)
            .Select(p => p.ArtifactId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The label of each upgrade pin, by artifact id, for the backup list.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetUpgradeLabelsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var rows = await db.BackupPins.AsNoTracking()
            .Where(p => p.Kind == BackupPinKinds.Upgrade)
            .Select(p => new { p.ArtifactId, p.Label })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.ArtifactId, r => r.Label ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>
    /// Pin an artifact as an admin pin. Idempotent. A backup held for an upgrade becomes an admin
    /// pin, so the trim after a successful upgrade no longer touches it (the table holds one row
    /// per backup).
    /// </summary>
    public async Task PinAsync(string artifactId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactId);
        await using var db = _serviceFactory.Create();
        var row = await db.BackupPins
            .FirstOrDefaultAsync(p => p.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            db.BackupPins.Add(new BackupPinRow { ArtifactId = artifactId, PinnedByUserId = actorUserId });
        }
        else if (row.Kind != BackupPinKinds.Admin)
        {
            row.Kind = BackupPinKinds.Admin;
            row.Label = null;
            row.PinnedByUserId = actorUserId;
        }
        else
        {
            return;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hold a backup for an upgrade, with the label the backup list shows. A backup an admin has
    /// already pinned stays an admin pin: an upgrade never takes one over.
    /// </summary>
    public async Task PinForUpgradeAsync(string artifactId, string label, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactId);
        ArgumentException.ThrowIfNullOrEmpty(label);
        await using var db = _serviceFactory.Create();
        var exists = await db.BackupPins
            .AnyAsync(p => p.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (exists) return;
        db.BackupPins.Add(new BackupPinRow
        {
            ArtifactId = artifactId,
            PinnedByUserId = actorUserId,
            Kind = BackupPinKinds.Upgrade,
            Label = label.Length > 200 ? label[..200] : label,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Release an upgrade pin at once, for an attempt that failed before it changed anything: the
    /// backup returns to normal retention. An admin pin on the same id is left alone.
    /// </summary>
    public async Task ReleaseUpgradePinAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await db.BackupPins
            .Where(p => p.ArtifactId == artifactId && p.Kind == BackupPinKinds.Upgrade)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// After an upgrade succeeds, keep only the newest <see cref="UpgradePinsKept"/> upgrade pins.
    /// The older backups lose the pin and fall back to normal retention; they are not deleted.
    /// Admin pins are never touched. Returns the ids that were released.
    /// </summary>
    public async Task<IReadOnlyList<string>> TrimUpgradePinsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var stale = await db.BackupPins
            .Where(p => p.Kind == BackupPinKinds.Upgrade)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.ArtifactId)
            .Skip(UpgradePinsKept)
            .Select(p => p.ArtifactId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stale.Count == 0) return stale;
        await db.BackupPins
            .Where(p => stale.Contains(p.ArtifactId) && p.Kind == BackupPinKinds.Upgrade)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return stale;
    }

    /// <summary>Unpin an artifact (idempotent), whatever its kind.</summary>
    public async Task UnpinAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await db.BackupPins
            .Where(p => p.ArtifactId == artifactId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
