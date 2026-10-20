using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>backup_pins</c> (mig 144, ADR-0062) — admin "never delete"
/// pins keyed by backup artifact id. Service-role only (deployment-global, RLS
/// deny-all for <c>coffer_app</c>); the admin HTTP surface gates with RequireAdmin.
/// A pinned artifact is excluded from both local and Drive retention sweeps.
/// </summary>
public sealed class BackupPinsRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public BackupPinsRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>The set of pinned artifact ids (retention excludes these).</summary>
    public async Task<IReadOnlySet<string>> GetPinnedIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var ids = await db.BackupPins.AsNoTracking()
            .Select(p => p.ArtifactId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Pin an artifact (idempotent — re-pinning is a no-op).</summary>
    public async Task PinAsync(string artifactId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactId);
        await using var db = _serviceFactory.Create();
        var exists = await db.BackupPins
            .AnyAsync(p => p.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (exists) return;
        db.BackupPins.Add(new BackupPinRow { ArtifactId = artifactId, PinnedByUserId = actorUserId });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unpin an artifact (idempotent).</summary>
    public async Task UnpinAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        await db.BackupPins
            .Where(p => p.ArtifactId == artifactId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
