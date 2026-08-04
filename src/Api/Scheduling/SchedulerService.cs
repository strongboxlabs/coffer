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
        var count = await _runner
            .RunDueAsync(db, handlers, now, _logger, cancellationToken)
            .ConfigureAwait(false);

        // Global (non-ledger) jobs share this one loop — e.g. the whole-DB
        // backup (mig 139 / ADR-0060), which has no owning ledger.
        var globalHandlers = scope.ServiceProvider.GetServices<IGlobalScheduledJobHandler>()
            .ToDictionary(h => h.JobType, StringComparer.Ordinal);
        count += await _runner
            .RunDueGlobalAsync(db, globalHandlers, now, _logger, cancellationToken)
            .ConfigureAwait(false);

        if (count > 0)
            _logger.LogInformation("SchedulerService ran {Count} due job(s).", count);
    }
}
