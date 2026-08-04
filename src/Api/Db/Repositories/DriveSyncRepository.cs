using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway for <c>drive_sync</c> (mig 142, ADR-0062) — the deployment-wide
/// singleton Google Drive backup-destination config. Connects via the
/// <b>service-role</b> factory (the table is global config with RLS deny-all
/// for <c>coffer_app</c>, same posture as <see cref="GlobalSchedulesRepository"/>);
/// the admin HTTP surface gates access with RequireAdmin. The sealed OAuth blob
/// never leaves the data layer except to the destination + KEK-rotation paths
/// that must open/re-wrap it.
/// </summary>
public sealed class DriveSyncRepository
{
    private const short SingletonId = 1;
    private readonly ServiceDbContextFactory _serviceFactory;

    public DriveSyncRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>Current status (never the ciphertext). A missing row reads as
    /// "not configured": disabled, disconnected.</summary>
    public async Task<DriveSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.DriveSync.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return new DriveSyncStatus(false, false, null, null, null, null, null, null);
        return ToStatus(row);
    }

    /// <summary>Return the stable per-install id, generating + persisting
    /// <paramref name="candidateIfAbsent"/> the first time (mig 143). Idempotent:
    /// once set it's never changed, and it survives disconnect, so a reconnect
    /// reuses the same Drive folder name.</summary>
    public async Task<string> EnsureInstallIdAsync(
        string candidateIfAbsent, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await LoadOrCreateAsync(db, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(row.InstallId))
        {
            row.InstallId = candidateIfAbsent;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return row.InstallId!;
    }

    /// <summary>The sealed OAuth blob, or null when not connected. Opened by the
    /// destination + KEK-rotation paths with <c>LedgerKeyService.OpenWithMasterKey</c>.</summary>
    public async Task<byte[]?> GetOauthCiphertextAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        return await db.DriveSync.AsNoTracking()
            .Where(r => r.Id == SingletonId)
            .Select(r => r.OauthCiphertext)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The sealed OAuth blob + target folder id, or null when not
    /// connected. Used by the push/upload path.</summary>
    public async Task<DriveConnection?> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.DriveSync.AsNoTracking()
            .Where(r => r.Id == SingletonId)
            .Select(r => new { r.OauthCiphertext, r.FolderId, r.Enabled })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row?.OauthCiphertext is not { Length: > 0 } || row.FolderId is null)
            return null;
        return new DriveConnection(row.OauthCiphertext, row.FolderId, row.Enabled);
    }

    /// <summary>Store the sealed OAuth blob + connected folder/account, enabling
    /// sync. Creates the singleton row if absent.</summary>
    public async Task<DriveSyncStatus> ConnectAsync(
        byte[] oauthCiphertext,
        string folderId,
        string folderName,
        string? connectedEmail,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oauthCiphertext);
        await using var db = _serviceFactory.Create();
        var row = await LoadOrCreateAsync(db, cancellationToken).ConfigureAwait(false);
        row.OauthCiphertext = oauthCiphertext;
        row.FolderId = folderId;
        row.FolderName = folderName;
        row.ConnectedEmail = connectedEmail;
        row.Enabled = true;
        row.LastSyncStatus = null;
        row.LastSyncError = null;
        row.ConfiguredByUserId = actorUserId;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(row);
    }

    /// <summary>Disconnect: clear the token + folder + account and disable. Idempotent.</summary>
    public async Task DisconnectAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.DriveSync
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return;
        row.OauthCiphertext = null;
        row.FolderId = null;
        row.FolderName = null;
        row.ConnectedEmail = null;
        row.Enabled = false;
        row.LastSyncStatus = null;
        row.LastSyncError = null;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enable/disable auto-push (the endpoint checks a connection exists
    /// before enabling). Creates the singleton row if absent.</summary>
    public async Task<DriveSyncStatus> SetEnabledAsync(
        bool enabled, Guid actorUserId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await LoadOrCreateAsync(db, cancellationToken).ConfigureAwait(false);
        row.Enabled = enabled;
        row.ConfiguredByUserId = actorUserId;
        row.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(row);
    }

    /// <summary>Record the outcome of a sync run (timestamp + ok/error).</summary>
    public async Task RecordSyncOutcomeAsync(
        string status, string? error, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        var row = await db.DriveSync
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return;
        row.LastSyncAt = atUtc;
        row.LastSyncStatus = status;
        row.LastSyncError = error;
        row.UpdatedAt = atUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Swap the sealed OAuth blob in place — used by `rotate-kek` to
    /// re-wrap it under the new master KEK (ADR-0062 D3). No-op when not connected.</summary>
    public async Task ReplaceOauthCiphertextAsync(
        byte[] oauthCiphertext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oauthCiphertext);
        await using var db = _serviceFactory.Create();
        await db.DriveSync
            .Where(r => r.Id == SingletonId && r.OauthCiphertext != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OauthCiphertext, oauthCiphertext),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DriveSyncRow> LoadOrCreateAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.DriveSync
            .FirstOrDefaultAsync(r => r.Id == SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new DriveSyncRow { Id = SingletonId };
            db.DriveSync.Add(row);
        }
        return row;
    }

    /// <summary>Sealed OAuth blob + target folder id + enabled flag for the
    /// upload path. The ciphertext is opened by the caller with the master KEK.</summary>
    public sealed record DriveConnection(byte[] OauthCiphertext, string FolderId, bool Enabled);

    private static DriveSyncStatus ToStatus(DriveSyncRow row) =>
        new(
            row.Enabled,
            Connected: row.OauthCiphertext is { Length: > 0 },
            row.ConnectedEmail,
            row.FolderName,
            row.InstallId,
            row.LastSyncAt,
            row.LastSyncStatus,
            row.LastSyncError);
}
