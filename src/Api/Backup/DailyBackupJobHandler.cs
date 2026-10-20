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
    private readonly IBackupRunner _manager;
    private readonly Coffer.Api.Notifications.NotificationPublisher _notifications;
    private readonly ILogger<DailyBackupJobHandler> _logger;
    private readonly TimeProvider _clock;

    public DailyBackupJobHandler(
        IBackupRunner manager,
        Coffer.Api.Notifications.NotificationPublisher notifications,
        ILogger<DailyBackupJobHandler> logger,
        TimeProvider clock)
    {
        _manager = manager;
        _notifications = notifications;
        _logger = logger;
        _clock = clock;
    }

    public string JobType => GlobalJobTypes.Backup;

    public async Task RunAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        Coffer.Api.Backup.BackupFileInfo info;
        try
        {
            info = await _manager.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping while the backup runs (a deploy, a restart). The
            // scheduler has already recorded this run as started, and without a word here
            // nothing says it did not finish: only the 48-hour age alert eventually
            // notices (FU-065). Say it now, on a token of its own — the one that fired
            // is the reason a publish on it would never happen.
            await AnnounceAsync(
                "Scheduled backup FAILED: it was interrupted because the server stopped while it was running.",
                detail: null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Announce, then rethrow: the scheduler still needs to count the failure
            // and disable the job after five, and a notification must not swallow
            // that. Publishing first means the signal goes out even if the rethrow
            // is what ends this tick.
            await AnnounceAsync(
                // NOT ex.Message. This summary is delivered to whatever webhook or
                // Healthchecks URL an operator configured, and a failed pg_dump or a
                // failed remote push produces an Npgsql or HTTP message naming
                // internal hostnames, ports, database names and file paths — the
                // exact payload most worth reading for anyone holding that URL.
                "Scheduled backup FAILED: " + Coffer.Api.Notifications.PublishedFailure.Describe(ex),
                Coffer.Api.Notifications.PublishedFailure.DetailFor(ex)).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "Scheduled backup created: {Id} ({Size} bytes).", info.Id, info.SizeBytes);

        // The HEARTBEAT, and the load-bearing half of ADR-0096 D5. An external
        // monitor counts these and alerts when they stop — the only way to catch
        // a deployment that is not running at all. Backups once stopped for days
        // and the only record was LogError lines inside a container.
        //
        // OUTSIDE the try above, on purpose (FU-067). It sat inside it, so a failed event
        // write announced a backup that was written, verified and listed as FAILED, and
        // counted toward the five-failure auto-disable. The backup is the fact; this is
        // the report of it, and a report that cannot be written is logged, not turned
        // into a second, false, failure.
        try
        {
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
            _logger.LogError(ex,
                "Backup {Id} was written, but its success event could not be recorded; the backup stands.",
                info.Id);
        }
    }

    /// <summary>Publish a Critical <c>backup.failed</c>. Best-effort and on its own short
    /// token: it runs on the way out of a failure, and must neither be killed by the
    /// cancellation that caused it nor replace the exception being rethrown.</summary>
    private async Task AnnounceAsync(string summary, IReadOnlyDictionary<string, string>? detail)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), _clock);
            await _notifications.PublishAsync(new Coffer.Api.Notifications.NotificationEvent(
                Severity: Coffer.Api.Notifications.NotificationSeverity.Critical,
                Topic: Coffer.Api.Notifications.NotificationTopics.Backup,
                EventKey: "backup.failed",
                Summary: summary,
                Detail: detail,
                Monitor: Coffer.Api.Notifications.NotificationMonitors.Backup,
                Signal: Coffer.Api.Notifications.MonitorSignal.Failure),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not announce a failed scheduled backup.");
        }
    }
}
