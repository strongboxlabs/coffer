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

    public SchedulerService(
        IServiceProvider services,
        ServiceDbContextFactory dbFactory,
        SchedulerRunner runner,
        ILogger<SchedulerService> logger)
    {
        _services = services;
        _dbFactory = dbFactory;
        _runner = runner;
        _logger = logger;
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
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
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
        var now = DateTime.UtcNow;

        var handlers = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
            .ToDictionary(h => h.JobType, StringComparer.Ordinal);
        // Resolved once and shared by both loops: the global loop announces a disable
        // too, and two GetRequiredService calls in one tick would be two instances.
        var publisher =
            scope.ServiceProvider.GetRequiredService<Notifications.NotificationPublisher>();

        var count = await _runner
            .RunDueAsync(db, handlers, now, _logger, cancellationToken, publisher)
            .ConfigureAwait(false);

        // Global (non-ledger) jobs share this one loop — e.g. the whole-DB
        // backup (mig 139 / ADR-0060), which has no owning ledger.
        var globalHandlers = scope.ServiceProvider.GetServices<IGlobalScheduledJobHandler>()
            .ToDictionary(h => h.JobType, StringComparer.Ordinal);
        count += await _runner
            .RunDueGlobalAsync(db, globalHandlers, now, _logger, cancellationToken, publisher)
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
