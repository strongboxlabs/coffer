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

    /// <summary>
    /// Creates the auto snapshot, and reports a skipped one as <c>Degraded</c>.
    /// </summary>
    /// <remarks>
    /// The result used to be discarded. <c>SkippedDueToFullPool</c> means all five slots
    /// hold manual snaps, so auto coverage has silently paused — the ledger keeps
    /// reporting a healthy daily job while its snapshots stop advancing, which is exactly
    /// the state a dead-man's switch is supposed to catch and would not have.
    /// </remarks>
    public async Task<JobRunOutcome> RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken cancellationToken)
    {
        var repo = new LedgerSnapshotsRepository(
            db, _loggers.CreateLogger<LedgerSnapshotsRepository>());
        var result = await repo.CreateAsync(
            ledgerId, kind: "auto", createdByUserId: configuredByUserId,
            description: null, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            LedgerSnapshotsRepository.CreateOutcome.Created => JobRunOutcome.Ok(),
            LedgerSnapshotsRepository.CreateOutcome.SkippedDueToFullPool => JobRunOutcome.Degraded(
                "Automatic snapshot skipped: all five slots hold manual snapshots. "
                + "Delete one to resume automatic coverage."),
            // AtCap is the MANUAL rejection path and cannot arrive here; treated as
            // degraded rather than ignored, because a new enum member reaching this
            // switch silently would be the same class of bug as the discarded result.
            _ => JobRunOutcome.Degraded(
                "Automatic snapshot did not run: " + result.Outcome + "."),
        };
    }
}
