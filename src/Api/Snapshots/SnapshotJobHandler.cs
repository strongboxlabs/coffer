using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Snapshots;

/// <summary>
/// Scheduled-job handler for <c>snapshot</c> (ADR-0037): creates an <c>auto</c>
/// snapshot for the ledger (the 5-cap eviction in
/// <see cref="LedgerSnapshotsRepository"/> applies). Replaces the original
/// fixed-weekly auto-snap worker (a no-op under RLS).
/// </summary>
public sealed class SnapshotJobHandler : IScheduledJobHandler
{
    private readonly ILoggerFactory _loggers;

    public SnapshotJobHandler(ILoggerFactory loggers)
    {
        _loggers = loggers;
    }

    public string JobType => JobTypes.Snapshot;

    public async Task RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken cancellationToken)
    {
        var repo = new LedgerSnapshotsRepository(
            db, _loggers.CreateLogger<LedgerSnapshotsRepository>());
        await repo.CreateAsync(
            ledgerId, kind: "auto", createdByUserId: configuredByUserId,
            description: null, cancellationToken).ConfigureAwait(false);
    }
}
