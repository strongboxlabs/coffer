using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// Closes, once at startup, every Demo marker a restart left <c>running</c> (ADR-0115 D9).
/// </summary>
/// <remarks>
/// <para>Demo jobs live only in memory, so at startup none is running, and a marker still
/// <c>running</c> belongs to an examples phase the restart ended. Each is closed as
/// <c>partial</c>, naming the steps its details do not record as added, so the hub lists
/// the Demo as an ordinary ledger with those examples missing. One whose details record
/// every step added had finished all but its own closing, and is closed as
/// <c>completed</c>.</para>
/// <para>One marker at a time, a handful at most, each closed only while it is still
/// running. A failure is logged, never thrown: startup does not wait on a record of
/// examples.</para>
/// </remarks>
public sealed class DemoMarkerSweep : IHostedService
{
    private readonly ServiceDbContextFactory _dbFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<DemoMarkerSweep> _logger;

    public DemoMarkerSweep(ServiceDbContextFactory dbFactory, TimeProvider clock, ILogger<DemoMarkerSweep> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "The Demo marker sweep failed; interrupted Demos stay listed as still building.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Close every running Demo marker; how many were closed.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DemoMarkerRow> running;
        await using (var db = _dbFactory.Create())
        {
            running = await new LedgerOperationsRepository(db).RunningDemoMarkersAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var closed = 0;
        foreach (var marker in running)
        {
            var (status, error) = CloseAs(marker.DetailsJson);
            await using var db = _dbFactory.Create();
            var done = await new LedgerOperationsRepository(db).CloseDemoMarkerAsync(
                    marker.LedgerId,
                    marker.Id,
                    status,
                    marker.DetailsJson,
                    error,
                    _clock.GetUtcNow().UtcDateTime,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!done) continue;
            closed++;
            _logger.LogWarning("Demo marker {MarkerId} on ledger {LedgerId} was left running by a restart; closed as {Status}.",
                marker.Id, marker.LedgerId, status);
        }
        return closed;
    }

    /// <summary>
    /// How the sweep closes a marker a restart left running, from its details:
    /// <c>completed</c> with no error when they record every step added, else
    /// <c>partial</c> with the interrupted message naming the steps they do not.
    /// </summary>
    internal static (string Status, string? Error) CloseAs(string? detailsJson) =>
        DemoExamplesMissing.From(DemoMarker.Partial, detailsJson, jobIsLive: false) is null
            ? (DemoMarker.Completed, null)
            : (DemoMarker.Partial, DemoMarker.InterruptedMessage(detailsJson));
}
