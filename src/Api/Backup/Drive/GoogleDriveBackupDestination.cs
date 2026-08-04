using Microsoft.Extensions.Logging;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Backup.Drive;

/// <summary>
/// <see cref="IBackupDestination"/> over Google Drive (ADR-0062 §④b+c). Owns the
/// artifact push, the per-install folder's remote GFS retention, and recording
/// the sync outcome on <c>drive_sync</c>. Reads the sealed OAuth blob via
/// <see cref="DriveSyncRepository"/> + opens it with the master KEK; the
/// connection lifecycle (connect / disconnect) stays in <see cref="DriveSyncService"/>.
/// </summary>
public sealed class GoogleDriveBackupDestination : IBackupDestination
{
    private readonly DriveSyncRepository _repo;
    private readonly LedgerKeyService _keys;
    private readonly IDriveClient _drive;
    private readonly BackupStore _store;
    private readonly ILogger<GoogleDriveBackupDestination> _logger;

    public GoogleDriveBackupDestination(
        DriveSyncRepository repo,
        LedgerKeyService keys,
        IDriveClient drive,
        BackupStore store,
        ILogger<GoogleDriveBackupDestination> logger)
    {
        _repo = repo;
        _keys = keys;
        _drive = drive;
        _store = store;
        _logger = logger;
    }

    /// <summary>The extension Drive files carry, matching the local artifact.
    /// The Drive file name is <c>{artifactId}.cofferbak</c>; we strip it back to
    /// the bare id for dedup + retention (which key off the local stem).</summary>
    private const string RemoteExtension = ".cofferbak";

    public string Name => "google-drive";

    private static string StripExtension(string remoteName) =>
        remoteName.EndsWith(RemoteExtension, StringComparison.Ordinal)
            ? remoteName[..^RemoteExtension.Length]
            : remoteName;

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var conn = await _repo.GetConnectionAsync(ct).ConfigureAwait(false);
        return conn is { Enabled: true };
    }

    // pinnedIds is unused under the mirror model (ADR-0074): Drive reflects the
    // LOCAL backup set, and a pin is just a backup local retention keeps — so it's
    // already in the local set the mirror preserves. The parameter stays on the
    // interface for a future destination that runs its own retention.

    public async Task PushLatestAsync(
        IReadOnlySet<string> pinnedIds, DateTime nowUtc, CancellationToken ct = default)
    {
        var (creds, folderId) = await ResolveAsync(ct).ConfigureAwait(false);
        try
        {
            var uploaded = await MirrorAsync(creds, folderId, ct).ConfigureAwait(false);
            await _repo.RecordSyncOutcomeAsync("ok", null, nowUtc, ct).ConfigureAwait(false);
            _logger.LogInformation("Mirrored local backups to Google Drive ({Count} uploaded).", uploaded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _repo.RecordSyncOutcomeAsync("error", ex.Message, nowUtc, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> UploadMissingAsync(
        IReadOnlySet<string> pinnedIds, DateTime nowUtc, CancellationToken ct = default)
    {
        var (creds, folderId) = await ResolveAsync(ct).ConfigureAwait(false);
        try
        {
            var pushed = await MirrorAsync(creds, folderId, ct).ConfigureAwait(false);
            await _repo.RecordSyncOutcomeAsync("ok", null, nowUtc, ct).ConfigureAwait(false);
            _logger.LogInformation("Mirrored local backups to Google Drive ({Count} uploaded).", pushed);
            return pushed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _repo.RecordSyncOutcomeAsync("error", ex.Message, nowUtc, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(DriveCredentials Creds, string FolderId)> ResolveAsync(CancellationToken ct)
    {
        var conn = await _repo.GetConnectionAsync(ct).ConfigureAwait(false)
            ?? throw new DriveOAuthException("Google Drive isn't connected.");
        var creds = DriveCredentialCodec.Deserialize(_keys.OpenWithMasterKey(conn.OauthCiphertext));
        return (creds, conn.FolderId);
    }

    private async Task PushOneAsync(
        DriveCredentials creds, string folderId, BackupFileInfo info, CancellationToken ct)
    {
        await using var content = _store.OpenRead(info.Id)
            ?? throw new DriveOAuthException($"Backup {info.Id} vanished before upload.");
        // Upload as {id}.cofferbak so the Drive file is recognizable + downloads
        // with the right extension; dedup + retention strip it back to the id.
        await _drive.UploadAsync(creds, folderId, info.Id + RemoteExtension, content, ct).ConfigureAwait(false);
    }

    /// <summary>Make the Drive folder MIRROR the local backup set (ADR-0074): upload
    /// every local backup missing from Drive, then delete every remote file whose
    /// bare id isn't a current local backup. Matching strips a trailing
    /// <c>.cofferbak</c> but compares the whole remaining name, so a legacy
    /// <c>*.ledgrbak</c> (or any stray upload) never matches a local id and is
    /// swept. Local retention (<see cref="BackupStore"/>) is the single source of
    /// truth for what to keep; Drive just reflects it — there is no separate Drive
    /// retention, and a pin is preserved simply by being a local backup.
    /// <para>SAFETY: the delete side is skipped when there are zero local backups,
    /// so a wiped or unmounted backups directory can never nuke the cloud copies.</para>
    /// Returns the number of artifacts uploaded.</summary>
    private async Task<int> MirrorAsync(DriveCredentials creds, string folderId, CancellationToken ct)
    {
        var local = _store.List();
        var localIds = local.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var remote = await _drive.ListAsync(creds, folderId, ct).ConfigureAwait(false);
        var remoteIds = remote.Select(a => StripExtension(a.Name)).ToHashSet(StringComparer.Ordinal);

        // Upload local backups the folder is missing (match by bare id).
        var uploaded = 0;
        foreach (var b in local.Where(b => !remoteIds.Contains(b.Id)))
        {
            await PushOneAsync(creds, folderId, b, ct).ConfigureAwait(false);
            uploaded++;
        }

        // Delete everything on Drive that isn't a current local backup — sweeps
        // legacy-extension artifacts and any strays. Skipped entirely when local is
        // empty (safety net against an empty/unmounted backups dir emptying Drive).
        if (localIds.Count > 0)
        {
            foreach (var artifact in remote.Where(a => !localIds.Contains(StripExtension(a.Name))))
            {
                await _drive.DeleteAsync(creds, artifact.FileId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Removed Drive backup {Name} — not in the local set (mirror).", artifact.Name);
            }
        }
        return uploaded;
    }
}
