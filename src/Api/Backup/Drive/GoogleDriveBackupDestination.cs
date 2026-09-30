using System.Globalization;
using System.Text.RegularExpressions;

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
public sealed partial class GoogleDriveBackupDestination : IBackupDestination
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

    /// <summary>
    /// The bare artifact id a Drive file belongs to — the key dedup and
    /// retention work in. Handles both shapes a backup is stored in:
    /// <c>{id}.cofferbak</c> and one part of a set,
    /// <c>{id}.cofferbak.002-of-003</c> (ADR-0101).
    /// </summary>
    private static string StripExtension(string remoteName)
    {
        var name = remoteName;
        var ext = name.LastIndexOf(RemoteExtension, StringComparison.Ordinal);
        // A part suffix follows the extension rather than replacing it, so the
        // id is everything before the LAST ".cofferbak" — and a name that has
        // none (a stray, a legacy *.ledgrbak) is returned whole, which is what
        // makes the mirror sweep it.
        return ext >= 0 ? name[..ext] : name;
    }

    /// <summary>
    /// The part number and the set size a Drive file declares, or
    /// <c>(null, 0)</c> when it is a whole artifact. Parses the
    /// <c>.002-of-003</c> tail written by <see cref="BackupStore.PartName"/>.
    /// </summary>
    /// <remarks>
    /// The COUNT is read here rather than recomputed because it is the only
    /// record of how the set was cut. The part size is configurable, so a set
    /// written under one value must still read as complete under another.
    /// </remarks>
    private static (int? Part, int Declared) PartOf(string remoteName)
    {
        var ext = remoteName.LastIndexOf(RemoteExtension, StringComparison.Ordinal);
        if (ext < 0) return (null, 0);
        var tail = remoteName[(ext + RemoteExtension.Length)..];
        if (tail.Length == 0) return (null, 0);
        // ".002-of-003"
        var m = PartSuffix().Match(tail);
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
               int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (null, 0);
    }

    [GeneratedRegex(@"^\.(\d{3})-of-(\d{3})$")]
    private static partial Regex PartSuffix();

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

        DriveCredentials creds;
        try
        {
            creds = DriveCredentialCodec.Deserialize(_keys.OpenWithMasterKey(conn.OauthCiphertext));
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The token doesn't open under the current master KEK — a cross-KEK
            // restore, or a rotation whose new key never reached this process.
            // Previously this escaped as a raw CryptographicException, unlike the
            // ingest (IngestOrchestrator) and backup (BackupManager) paths which
            // both translate it; ADR-0092 D5 closes that gap. Reconciliation
            // normally clears the blob on restore, so reaching here means the KEK
            // changed some other way.
            throw new DriveOAuthException(
                "The stored Google Drive token could not be opened — has the master KEK changed? "
                + "Reconnect Google Drive to resume syncing.", ex);
        }
        return (creds, conn.FolderId);
    }

    /// <summary>
    /// Upload the parts of one artifact that Drive does not already have.
    /// </summary>
    /// <remarks>
    /// A backup over <see cref="BackupStore.PartSizeBytes"/> goes up as several
    /// files (ADR-0101) rather than one, for the reason the user asked for:
    /// one interrupted 141 MB upload restarts from zero, where one interrupted
    /// part of three costs a third. It also means the copy sitting in Drive is
    /// already in the shape a restore through a body-capped proxy can accept.
    /// <para>Only MISSING parts are sent, so a run that died halfway resumes
    /// instead of re-uploading what landed.</para>
    /// </remarks>
    private async Task<bool> PushOneAsync(
        DriveCredentials creds, string folderId, BackupFileInfo info,
        IReadOnlySet<int> alreadyThere, CancellationToken ct)
    {
        var count = _store.PartCountFor(info.SizeBytes);
        var sent = false;
        for (var part = 1; part <= count; part++)
        {
            if (alreadyThere.Contains(part)) continue;

            await using var content = count == 1
                ? _store.OpenRead(info.Id)
                : _store.OpenReadPart(info.Id, part);
            if (content is null)
                throw new DriveOAuthException($"Backup {info.Id} vanished before upload.");

            // Named {id}.cofferbak for a whole artifact, {id}.cofferbak.002-of-003
            // for a part; dedup + retention strip either back to the id.
            await _drive.UploadAsync(
                creds, folderId, BackupStore.PartName(info.Id, part, count), content, ct)
                .ConfigureAwait(false);
            sent = true;
        }
        return sent;
    }

    /// <summary>Make the Drive folder MIRROR the local backup set (ADR-0074): upload
    /// every local backup Drive is missing, then delete every remote file whose
    /// bare id isn't a current local backup. Matching strips a trailing
    /// <c>.cofferbak</c> and any <c>.002-of-003</c> part tail but compares the
    /// whole remaining name, so a legacy <c>*.ledgrbak</c> (or any stray upload)
    /// never matches a local id and is swept. Local retention
    /// (<see cref="BackupStore"/>) is the single source of truth for what to
    /// keep; Drive just reflects it — there is no separate Drive retention, and
    /// a pin is preserved simply by being a local backup.
    /// <para>A backup over <see cref="BackupStore.PartSizeBytes"/> is several
    /// Drive files (ADR-0101) that count as ONE backup: present means every part
    /// is present, and a set missing parts is completed rather than restarted.
    /// An artifact uploaded whole before parts existed stays whole — it is
    /// present, so nothing re-uploads gigabytes to change its shape.</para>
    /// <para>SAFETY: the delete side is skipped when there are zero local backups,
    /// so a wiped or unmounted backups directory can never nuke the cloud copies.</para>
    /// Returns the number of artifacts uploaded (an artifact completed from
    /// partial parts counts once, not once per part).</summary>
    private async Task<int> MirrorAsync(DriveCredentials creds, string folderId, CancellationToken ct)
    {
        var local = _store.List();
        var localIds = local.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var remote = await _drive.ListAsync(creds, folderId, ct).ConfigureAwait(false);

        // Group the folder by artifact id. The value is the set of part numbers
        // present; a whole {id}.cofferbak reads as part 1, which is exactly what
        // it is when the count is 1 — and when it ISN'T (a pre-ADR-0101 upload
        // of a large artifact), the "already complete" test below still treats
        // it as the whole backup, so it is left alone.
        var remoteParts = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var remoteDeclaredCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var wholeUploads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in remote)
        {
            var id = StripExtension(a.Name);
            var (part, declared) = PartOf(a.Name);
            if (part is null) wholeUploads.Add(id);
            else remoteDeclaredCount[id] = declared;
            if (!remoteParts.TryGetValue(id, out var set))
                remoteParts[id] = set = [];
            set.Add(part ?? 1);
        }

        // Upload what the folder is missing (match by bare id, then by part).
        var uploaded = 0;
        foreach (var b in local)
        {
            // Already there whole — including a large artifact uploaded before
            // parts existed. Re-cutting it would spend its whole size in
            // bandwidth to arrive at the same bytes.
            if (wholeUploads.Contains(b.Id)) continue;

            // Complete means "every part the SET SAYS it has" — the count read
            // off the remote names, not recomputed from the current part size.
            // Api:Backup:PartSizeMb is configurable, and recomputing would make
            // every set cut under the old value look short the moment someone
            // changed it, re-uploading the whole artifact alongside the parts
            // already there.
            var have = remoteParts.TryGetValue(b.Id, out var set) ? set : [];
            var declared = remoteDeclaredCount.TryGetValue(b.Id, out var n)
                ? n
                : _store.PartCountFor(b.SizeBytes);
            if (have.Count >= declared) continue;

            if (await PushOneAsync(creds, folderId, b, have, ct).ConfigureAwait(false))
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
