using Coffer.Api.Backup;

namespace Coffer.Api.Notifications;

/// <summary>
/// The first alert: has a backup actually succeeded recently?
/// </summary>
/// <remarks>
/// First on purpose, ahead of anything about data consistency. Projection drift is
/// slow damage that a repair fixes; a missing backup is data that does not come
/// back. This is the alert whose absence has already cost something — backups died
/// for ~47 hours and snapshots for ~68 with no signal at all.
/// <para>
/// <b>Two mechanisms, and the difference matters.</b> A heartbeat published on every
/// successful backup is the load-bearing one: an external monitor counts those pings
/// and alerts when they STOP, which is the only way to catch a deployment that is
/// not running at all. This age check is the secondary, in-app signal — it can only
/// fire while the process is alive, so it covers "the job ran and kept failing", not
/// "the container is dead". Both are needed; neither substitutes for the other
/// (ADR-0096 D5).
/// </para>
/// </remarks>
public sealed class BackupAgeMonitor
{
    /// <summary>
    /// Hours before a missing backup becomes critical.
    /// </summary>
    /// <remarks>
    /// 48 rather than 24: a daily schedule that slips once is not an emergency, and
    /// an alert that fires on a single missed night gets muted, at which point it
    /// protects nothing. The real outage ran ~47 hours, so this catches it on the
    /// second miss.
    /// </remarks>
    public const int CriticalAfterHours = 48;

    /// <summary>
    /// How long a given alert stays quiet after being raised.
    /// </summary>
    /// <remarks>
    /// The condition is standing, not momentary — a stale backup is still stale on
    /// the next tick — so without this the scheduler would announce it every 15
    /// minutes and the channel would be muted within a day. Twelve hours re-raises
    /// often enough to be noticed and rarely enough to be tolerated.
    /// </remarks>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(12);

    private readonly BackupStore _store;
    private readonly NotificationPublisher _publisher;

    public BackupAgeMonitor(BackupStore store, NotificationPublisher publisher)
    {
        _store = store;
        _publisher = publisher;
    }

    /// <summary>
    /// Publish a critical event when the newest backup is older than
    /// <see cref="CriticalAfterHours"/>, or none exists at all.
    /// </summary>
    /// <returns>The age of the newest backup, or null when there are none.</returns>
    public async Task<TimeSpan?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var newest = _store.List()
            .OrderByDescending(b => b.CreatedAtUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            await _publisher.PublishThrottledAsync(new NotificationEvent(
                Severity: NotificationSeverity.Critical,
                Topic: NotificationTopics.Backup,
                EventKey: "backup.none",
                Summary: "No backup exists. Nothing is protecting this install's data.",
                Detail: null,
                Monitor: NotificationMonitors.Backup,
                Signal: MonitorSignal.Failure),
                RepeatAfter, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var age = DateTime.UtcNow - newest.CreatedAtUtc;
        if (age.TotalHours >= CriticalAfterHours)
        {
            await _publisher.PublishThrottledAsync(new NotificationEvent(
                Severity: NotificationSeverity.Critical,
                Topic: NotificationTopics.Backup,
                EventKey: "backup.stale",
                Summary: $"No successful backup in {age.TotalHours:F0} hours "
                         + $"(newest: {newest.CreatedAtUtc:u}).",
                Detail: new Dictionary<string, string>
                {
                    ["newest_at"] = newest.CreatedAtUtc.ToString("u"),
                    ["age_hours"] = age.TotalHours.ToString("F1"),
                    ["threshold_hours"] = CriticalAfterHours.ToString(),
                },
                Monitor: NotificationMonitors.Backup,
                Signal: MonitorSignal.Failure),
                RepeatAfter, cancellationToken).ConfigureAwait(false);
        }

        return age;
    }
}
