using Coffer.Api.Backup;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Notifications;

/// <summary>
/// Has a backup actually reached Google Drive recently? The off-host counterpart of
/// <see cref="BackupAgeMonitor"/> (FU-063).
/// </summary>
/// <remarks>
/// <para>
/// A failed push is logged and recorded on the Drive card, and the backup that triggered it
/// still reports success, because the local copy is safe. That is correct per backup and
/// wrong over time: a revoked Google token, a full Drive or a changed folder stops the
/// off-site copies indefinitely, and the only trace is a status line on a settings page
/// nobody is looking at. <c>drive.push-failed</c> says so for each failure; this says so
/// when it keeps being true.
/// </para>
/// <para>
/// <b>What "recently" is measured from.</b> Not <c>last_sync_at</c>, which every attempt
/// overwrites, failures included, so a Drive that has failed every night for a week looks
/// freshly synced. The newest <c>drive_mirrored_backups.mirrored_at</c> moves only when a
/// backup actually lands on Drive.
/// </para>
/// <para>
/// Silent when Drive is not enabled — nothing is promised — and when there is no local
/// backup at all, which <see cref="BackupAgeMonitor"/> already says.
/// </para>
/// </remarks>
public sealed class DriveSyncMonitor
{
    /// <summary>
    /// Hours without a backup reaching Drive before it becomes critical. The same 48 as
    /// <see cref="BackupAgeMonitor.CriticalAfterHours"/>, for the same reason: one missed
    /// night is not an emergency and an alert that fires on it gets muted.
    /// </summary>
    public const int CriticalAfterHours = BackupAgeMonitor.CriticalAfterHours;

    private readonly BackupStore _store;
    private readonly DriveSyncRepository _drive;
    private readonly NotificationPublisher _publisher;
    private readonly TimeProvider _clock;

    public DriveSyncMonitor(
        BackupStore store, DriveSyncRepository drive, NotificationPublisher publisher, TimeProvider clock)
    {
        _store = store;
        _drive = drive;
        _publisher = publisher;
        _clock = clock;
    }

    /// <summary>
    /// Publish a critical <c>drive.stale</c> when Drive is on and no backup has reached it
    /// for <see cref="CriticalAfterHours"/>.
    /// </summary>
    /// <returns>How long since a backup reached Drive, or null when Drive is off, nothing is
    /// there to copy, or no backup has ever reached it and none is old enough to expect one.</returns>
    public async Task<TimeSpan?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var status = await _drive.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Enabled || !status.Connected) return null;

        var newestLocal = _store.List().OrderByDescending(b => b.CreatedAtUtc).FirstOrDefault();
        if (newestLocal is null) return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        var lastReached = await _drive.GetLastMirroredAtAsync(cancellationToken).ConfigureAwait(false);

        // Nothing has ever reached Drive: measure from the newest local backup, which is
        // when one first should have. A connection made an hour ago has had no chance yet.
        var since = lastReached ?? newestLocal.CreatedAtUtc;
        var age = now - since;
        if (age.TotalHours < CriticalAfterHours) return age;

        await _publisher.PublishThrottledAsync(new NotificationEvent(
            Severity: NotificationSeverity.Critical,
            Topic: NotificationTopics.Backup,
            EventKey: "drive.stale",
            Summary: lastReached is null
                ? $"No backup has reached Google Drive: the newest local backup is {age.TotalHours:F0} hours old."
                : $"No backup has reached Google Drive in {age.TotalHours:F0} hours "
                  + $"(last: {lastReached:u}).",
            // The last push's own error text is not repeated here: this goes to whatever
            // webhook an operator configured, and a Drive failure can name hosts and paths.
            Detail: new Dictionary<string, string>
            {
                ["last_reached_at"] = lastReached?.ToString("u") ?? "never",
                ["age_hours"] = age.TotalHours.ToString("F1"),
                ["threshold_hours"] = CriticalAfterHours.ToString(),
                ["last_push_status"] = status.LastSyncStatus ?? "none",
            }),
            BackupAgeMonitor.RepeatAfter, cancellationToken).ConfigureAwait(false);
        return age;
    }
}
