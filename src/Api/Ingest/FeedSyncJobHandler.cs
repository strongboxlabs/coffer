using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Ingest;

/// <summary>
/// Scheduled-job handler for <c>feed-sync</c> (mig 215): one daily pass over every bank
/// feed connection on the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Sequential, like the manual sync-all, and for the same reason: several SimpleFIN
/// endpoints hit at once invites rate limiting on the bank side.
/// </para>
/// <para>
/// The sync algorithm itself is safe to run unattended — dedup is on
/// (ledger_id, provider_key, external_id) including soft-hidden rows, the start date is
/// computed from a per-account watermark with a seven-day overlap, and errored accounts
/// do not advance their watermark. So running it a second time changes nothing.
/// </para>
/// </remarks>
public sealed class FeedSyncJobHandler : IScheduledJobHandler
{
    private readonly SyncConnectionLock _connectionLock;
    private readonly IEnumerable<IPullProvider> _pullProviders;
    private readonly IEnumerable<IFileProvider> _fileProviders;
    private readonly Coffer.Api.Quotes.Yahoo.YahooFinanceQuoteProvider _yahoo;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<FeedSyncJobHandler> _logger;

    public FeedSyncJobHandler(
        SyncConnectionLock connectionLock,
        IEnumerable<IPullProvider> pullProviders,
        IEnumerable<IFileProvider> fileProviders,
        Coffer.Api.Quotes.Yahoo.YahooFinanceQuoteProvider yahoo,
        ILoggerFactory loggers,
        ILogger<FeedSyncJobHandler> logger)
    {
        _connectionLock = connectionLock;
        _pullProviders = pullProviders;
        _fileProviders = fileProviders;
        _yahoo = yahoo;
        _loggers = loggers;
        _logger = logger;
    }

    /// <summary>
    /// Builds the orchestrator over the WORKER'S context, not the injected one.
    /// </summary>
    /// <remarks>
    /// This is the difference between working and silently doing nothing. DI's
    /// AppDbContext is the APP role with AppUserDbConnectionInterceptor attached
    /// (Program.cs), so in a background tick — where there is no request and no
    /// app.user_id — RLS hides every row. An injected IngestOrchestrator would have
    /// reported a clean run over zero connections, forever, on every ledger.
    ///
    /// Hence IScheduledJobHandler's contract: "Implementations build whatever they need
    /// over the supplied (service-role) context." QuoteRefreshJobHandler and
    /// SnapshotJobHandler both do the same, and the provider set mirrors Program.cs for
    /// the same reason theirs does. Everything injected here is db-independent — the
    /// SimpleFIN pull provider needs only a client and the key service, and the file
    /// providers are format parsers.
    /// </remarks>
    private IngestOrchestrator OrchestratorOver(AppDbContext db) => new(
        db,
        _connectionLock,
        _pullProviders,
        _fileProviders,
        new Coffer.Api.Quotes.QuoteOrchestrator(
            db,
            new Coffer.Api.Quotes.IQuotePullProvider[]
            {
                new Coffer.Api.Quotes.SimpleFin.SimpleFinHoldingsQuoteProvider(
                    db, _loggers.CreateLogger<Coffer.Api.Quotes.SimpleFin.SimpleFinHoldingsQuoteProvider>()),
                _yahoo,
            },
            Array.Empty<Coffer.Api.Quotes.IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            _loggers.CreateLogger<Coffer.Api.Quotes.QuoteOrchestrator>()),
        _loggers.CreateLogger<IngestOrchestrator>());

    public string JobType => JobTypes.FeedSync;

    /// <summary>
    /// Statuses this job will not touch.
    /// </summary>
    /// <remarks>
    /// <c>needs_reauth</c> means the bank has revoked consent: the only thing that fixes
    /// it is a person reconnecting. Retrying it daily would fail forever, and — because
    /// the run stamps <c>last_synced_at</c> on every attempt — it would keep re-stamping a
    /// connection that synced nothing, so the UI would show a recent sync time for stale
    /// data. Worse, five such days trip the scheduler's five-strike auto-disable and take
    /// the WORKING connections down with the broken one.
    ///
    /// <c>disconnected</c> is the operator having deliberately stopped it.
    ///
    /// Skipping is not the same as ignoring: a skipped connection is counted and named in
    /// the degraded reason, so it is visible on the settings page rather than only in a
    /// log — and the job still reports healthy, because a connection awaiting a human is
    /// not the schedule failing.
    /// </remarks>
    private static readonly string[] SkippedStatuses = ["needs_reauth", "disconnected"];

    public async Task<JobRunOutcome> RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken cancellationToken)
    {
        var connections = await db.FeedConnections
            .AsNoTracking()
            .Where(c => c.LedgerId == ledgerId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (connections.Count == 0)
        {
            // Nothing to do is not a failure. A ledger can have the schedule enabled and
            // no connections yet, and reporting that as a problem would mark its switch
            // down over a state nobody needs to fix.
            return JobRunOutcome.Ok();
        }

        var orchestrator = OrchestratorOver(db);
        var awaitingAPerson = new List<Guid>();
        var faulted = new List<string>();
        var synced = 0;

        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SkippedStatuses.Contains(connection.Status, StringComparer.Ordinal))
            {
                awaitingAPerson.Add(connection.Id);
                continue;
            }

            // Not wrapped in a try/catch: a provider fault is a typed RESULT now, not an
            // exception, precisely so this loop and the manual sync-all both continue past
            // one unreachable bank without each restating the rule.
            var outcome = await orchestrator
                .RunPullAsync(ledgerId, connection.Id, configuredByUserId,
                    cancellationToken: cancellationToken, triggeredVia: "scheduled")
                .ConfigureAwait(false);

            if (outcome.IsSuccess) synced++;
            else faulted.Add($"{connection.Id:N} ({outcome.Failure})");
        }

        if (faulted.Count > 0)
        {
            // Degraded, not failed: the connections that worked did work, and the
            // scheduler must not disable a ledger's whole feed because one bank was down
            // for five days. The switch still reports DOWN, which is the honest answer —
            // some of this ledger's balances did not refresh.
            _logger.LogWarning(
                "Scheduled feed sync for ledger {LedgerId}: {Synced} synced, {Faulted} faulted.",
                ledgerId, synced, faulted.Count);
            return JobRunOutcome.Degraded(
                $"{faulted.Count} of {connections.Count} connection(s) did not sync: "
                + string.Join("; ", faulted));
        }

        if (awaitingAPerson.Count == connections.Count)
        {
            // EVERY connection is waiting on a human, so nothing was refreshed at all.
            // That has to read as degraded even though no individual sync failed — this is
            // the same shape as the quote refresh where every provider was down, and the
            // reason "no exception was thrown" is not a success criterion.
            return JobRunOutcome.Degraded(
                $"All {connections.Count} connection(s) need reconnecting; nothing was synced.");
        }

        if (awaitingAPerson.Count > 0)
        {
            // Some synced, some await a person. Reported, not silent — but Ok, because a
            // connection awaiting consent is not the schedule failing, and marking the
            // switch down every day for it would teach its owner to ignore the switch.
            _logger.LogInformation(
                "Scheduled feed sync for ledger {LedgerId}: {Synced} synced, "
                + "{Skipped} awaiting reconnection.",
                ledgerId, synced, awaitingAPerson.Count);
        }

        return JobRunOutcome.Ok();
    }
}
