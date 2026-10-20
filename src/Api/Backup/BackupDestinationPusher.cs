using Microsoft.Extensions.Logging;

using Coffer.Api.Notifications;

namespace Coffer.Api.Backup;

/// <summary>
/// Pushes a freshly stored backup to every enabled off-host destination, and says so when
/// one fails (FU-063).
/// </summary>
/// <remarks>
/// A push failure NEVER fails the backup: the local artifact is already safe, and the
/// destination records its own last-sync error. But "the backup succeeded" is then the whole
/// of what is reported, so a revoked Google token stops the off-site copies indefinitely and
/// the only trace is a status line on a settings page. Each failure is now announced as
/// <c>drive.push-failed</c>; <see cref="DriveSyncMonitor"/> escalates when it keeps being true.
/// Its own class so that the behaviour can be tested without a pg_dump.
/// </remarks>
public sealed class BackupDestinationPusher
{
    private readonly IEnumerable<IBackupDestination> _destinations;
    private readonly NotificationPublisher _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackupDestinationPusher> _logger;

    public BackupDestinationPusher(
        IEnumerable<IBackupDestination> destinations,
        NotificationPublisher notifications,
        TimeProvider clock,
        ILogger<BackupDestinationPusher> logger)
    {
        _destinations = destinations;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Push to each enabled destination and reconcile its retention
    /// (ADR-0062 4b+c). Never throws for a destination's failure.</summary>
    public async Task PushAsync(IReadOnlySet<string> pinned, CancellationToken ct)
    {
        foreach (var dest in _destinations)
        {
            try
            {
                if (await dest.IsEnabledAsync(ct).ConfigureAwait(false))
                    await dest.PushLatestAsync(pinned, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Backup push to {Destination} failed (the local backup itself succeeded).", dest.Name);
                await AnnouncePushFailureAsync(dest.Name, ex, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A Warning, not a Critical: the backup is safe, and a single failed push is often
    /// transient. <see cref="DriveSyncMonitor"/> is what raises it when it is not.
    /// </summary>
    /// <remarks>Not <c>ex.Message</c>: the summary goes to whatever webhook an operator
    /// configured, and a Drive or HTTP failure can name hosts and paths.</remarks>
    private async Task AnnouncePushFailureAsync(string destination, Exception ex, CancellationToken ct)
    {
        try
        {
            await _notifications.PublishAsync(new NotificationEvent(
                Severity: NotificationSeverity.Warning,
                Topic: NotificationTopics.Backup,
                EventKey: "drive.push-failed",
                Summary: $"The backup was written, but copying it to {destination} failed: "
                         + PublishedFailure.Describe(ex) + ".",
                Detail: PublishedFailure.DetailFor(ex)), ct).ConfigureAwait(false);
        }
        catch (Exception announce) when (announce is not OperationCanceledException)
        {
            // The backup is safe and the failure is already logged; a notification that
            // cannot be written must not turn a good backup into a failed one.
            _logger.LogError(announce, "Could not announce the failed push to {Destination}.", destination);
        }
    }
}
