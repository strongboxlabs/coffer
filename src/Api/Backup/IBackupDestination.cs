namespace Coffer.Api.Backup;

/// <summary>
/// An off-host backup destination (ADR-0062 D4) — the generalization point so
/// the backup flow can push to one or more places without knowing the transport.
/// Google Drive (<c>GoogleDriveBackupDestination</c>) is the first impl; a future
/// S3/etc. adds another. The backup engine (<see cref="BackupManager"/>) pushes
/// after every successful local backup; the admin surface drives manual
/// sync / upload-existing.
/// </summary>
public interface IBackupDestination
{
    /// <summary>Stable name for logging / disambiguation (e.g. "google-drive").</summary>
    string Name { get; }

    /// <summary>True when this destination is connected AND its auto-sync toggle
    /// is on — the gate the automatic push-on-backup path uses.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Push the newest local artifact, then reconcile this destination's
    /// retention (excluding <paramref name="pinnedIds"/>), and record the outcome.
    /// Requires a connected destination (independent of the auto-sync toggle, so
    /// manual "Sync now" works while paused). Throws on failure after recording
    /// it — the caller decides whether to surface or swallow.</summary>
    Task PushLatestAsync(
        IReadOnlySet<string> pinnedIds, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Push every local artifact not already on the destination
    /// (backfill), then reconcile retention; record the outcome. Returns the count
    /// uploaded. Requires a connected destination.</summary>
    Task<int> UploadMissingAsync(
        IReadOnlySet<string> pinnedIds, DateTime nowUtc, CancellationToken cancellationToken = default);
}
