using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Backup;

/// <summary>
/// Scheduled whole-DB backup (ADR-0060). The global job_type <c>backup</c>:
/// when the deployment's backup schedule is due, run a backup using the stored
/// (master-KEK-sealed) passphrase via <see cref="BackupManager"/> — the same
/// path a manual create takes, so scheduled and manual artifacts share one
/// restore secret. Ignores the worker's <c>db</c>; it drives pg_dump itself.
/// </summary>
public sealed class DailyBackupJobHandler : IGlobalScheduledJobHandler
{
    private readonly BackupManager _manager;
    private readonly Coffer.Api.Notifications.NotificationPublisher _notifications;
    private readonly ILogger<DailyBackupJobHandler> _logger;

    public DailyBackupJobHandler(
        BackupManager manager,
        Coffer.Api.Notifications.NotificationPublisher notifications,
        ILogger<DailyBackupJobHandler> logger)
    {
        _manager = manager;
        _notifications = notifications;
        _logger = logger;
    }

    public string JobType => GlobalJobTypes.Backup;

    public async Task RunAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _manager.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Scheduled backup created: {Id} ({Size} bytes).", info.Id, info.SizeBytes);

            // The HEARTBEAT, and the load-bearing half of ADR-0096 D5. An external
            // monitor counts these and alerts when they stop — the only way to catch
            // a deployment that is not running at all. Backups once died for ~47
            // hours and the only record was LogError lines inside a container.
            await _notifications.PublishAsync(new Coffer.Api.Notifications.NotificationEvent(
                Severity: Coffer.Api.Notifications.NotificationSeverity.Info,
                Topic: Coffer.Api.Notifications.NotificationTopics.Backup,
                EventKey: "backup.succeeded",
                Summary: $"Scheduled backup created ({info.SizeBytes} bytes).",
                Detail: new Dictionary<string, string>
                {
                    ["backup_id"] = info.Id,
                    ["size_bytes"] = info.SizeBytes.ToString(),
                },
                Monitor: Coffer.Api.Notifications.NotificationMonitors.Backup,
                Signal: Coffer.Api.Notifications.MonitorSignal.Success),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Announce, then rethrow: the scheduler still needs to count the failure
            // and disable the job after five, and a notification must not swallow
            // that. Publishing first means the signal goes out even if the rethrow
            // is what ends this tick.
            await _notifications.PublishAsync(new Coffer.Api.Notifications.NotificationEvent(
                Severity: Coffer.Api.Notifications.NotificationSeverity.Critical,
                Topic: Coffer.Api.Notifications.NotificationTopics.Backup,
                EventKey: "backup.failed",
                Summary: "Scheduled backup FAILED: " + ex.Message,
                Detail: null,
                Monitor: Coffer.Api.Notifications.NotificationMonitors.Backup,
                Signal: Coffer.Api.Notifications.MonitorSignal.Failure),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
