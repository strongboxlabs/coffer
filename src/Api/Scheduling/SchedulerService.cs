using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;

namespace Coffer.Api.Scheduling;

/// <summary>
/// The single per-ledger daily scheduler (replaces the per-feature workers).
/// Ticks every <see cref="TickInterval"/>; each tick dispatches every due
/// <c>scheduled_jobs</c> row to its handler. In-process
/// <see cref="BackgroundService"/> — dies with the API process (fine for the
/// self-hosted target).
/// </summary>
/// <remarks>
/// Runs over the <b>service-role (BYPASSRLS)</b> context — a background tick has
/// no request user, so the RLS app role would be fail-closed and see no
/// ledgers. (This is the fix for the original auto-snapshot no-op.) Single
/// instance assumed; multi-instance would add a per-ledger advisory lock.
/// </remarks>
public sealed class SchedulerService : BackgroundService
{
    /// <summary>Wake-up cadence. Each tick runs whatever is due.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _services;
    private readonly ServiceDbContextFactory _dbFactory;
    private readonly SchedulerRunner _runner;
    private readonly ILogger<SchedulerService> _logger;
    private readonly TimeProvider _clock;

    public SchedulerService(
        IServiceProvider services,
        ServiceDbContextFactory dbFactory,
        SchedulerRunner runner,
        ILogger<SchedulerService> logger,
        TimeProvider clock)
    {
        _services = services;
        _dbFactory = dbFactory;
        _runner = runner;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SchedulerService starting; tick interval {Interval}.", TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SchedulerService tick failed; will retry next interval.");
            }

            try
            {
                await Task.Delay(TickInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SchedulerService stopping.");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        await using var db = _dbFactory.Create();
        var now = _clock.GetUtcNow().UtcDateTime;

        var handlers = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
            .ToDictionary(h => h.JobType, StringComparer.Ordinal);
        // Resolved once and shared by both loops: the global loop announces a disable
        // too, and two GetRequiredService calls in one tick would be two instances.
        var publisher =
            scope.ServiceProvider.GetRequiredService<Notifications.NotificationPublisher>();

        var count = await _runner
            .RunDueAsync(db, handlers, now, _logger, cancellationToken, publisher, _dbFactory.Create)
            .ConfigureAwait(false);

        // Global (non-ledger) jobs share this one loop — e.g. the whole-DB
        // backup (mig 139 / ADR-0060), which has no owning ledger.
        var globalHandlers = scope.ServiceProvider.GetServices<IGlobalScheduledJobHandler>()
            .ToDictionary(h => h.JobType, StringComparer.Ordinal);
        count += await _runner
            .RunDueGlobalAsync(db, globalHandlers, now, _logger, cancellationToken, publisher, _dbFactory.Create)
            .ConfigureAwait(false);

        if (count > 0)
            _logger.LogInformation("SchedulerService ran {Count} due job(s).", count);

        // MONITORS, not jobs. Deliberately not entries in global_scheduled_jobs:
        // there is nothing to configure, and nobody should be able to disable the
        // thing whose whole purpose is noticing that the configurable jobs stopped.
        // Their own publishes are throttled, so running on every tick costs a
        // directory listing and announces at most once per window.
        //
        // Failures here are swallowed on purpose: a monitor that throws must not
        // take down the tick that runs the real jobs.
        try
        {
            await scope.ServiceProvider
                .GetRequiredService<Notifications.BackupAgeMonitor>()
                .CheckAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Backup-age monitor failed; will retry next tick.");
        }

        // The off-host counterpart: has a backup actually reached Google Drive? A failed
        // push leaves the backup itself succeeding, so nothing else notices a Drive that
        // has stopped taking copies (FU-063).
        try
        {
            await scope.ServiceProvider
                .GetRequiredService<Notifications.DriveSyncMonitor>()
                .CheckAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Drive-sync monitor failed; will retry next tick.");
        }

        // Expired and spent WebAuthn challenges. Nothing removed them before (2026-10-12
        // audit, S5), and the anonymous /login/begin adds one per call, so the table only
        // grew — into every backup with it. Housekeeping, not a job: there is nothing to
        // configure or switch off.
        try
        {
            var swept = await scope.ServiceProvider
                .GetRequiredService<Db.Services.ChallengeStore>()
                .SweepAsync(cancellationToken)
                .ConfigureAwait(false);
            if (swept > 0)
                _logger.LogDebug("Swept {Count} expired or used WebAuthn challenge(s).", swept);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "WebAuthn challenge sweep failed; will retry next tick.");
        }

        try
        {
            var drifted = await scope.ServiceProvider
                .GetRequiredService<Notifications.ConsistencyMonitor>()
                .CheckAllAsync(cancellationToken)
                .ConfigureAwait(false);
            if (drifted > 0)
                _logger.LogWarning("{Count} ledger(s) have projection drift.", drifted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Consistency monitor failed; will retry next tick.");
        }
    }
}
