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
    private readonly ILogger<DailyBackupJobHandler> _logger;

    public DailyBackupJobHandler(BackupManager manager, ILogger<DailyBackupJobHandler> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public string JobType => GlobalJobTypes.Backup;

    public async Task RunAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var info = await _manager.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Scheduled backup created: {Id} ({Size} bytes).", info.Id, info.SizeBytes);
    }
}
