using System.Collections.Concurrent;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes.Frankfurter;
using Coffer.Api.Quotes.Yahoo;

namespace Coffer.Api.Quotes.MarketSeries;

/// <summary>
/// The immediate market-series backfill (ADR-0114 D11): started by any change that adds a
/// needed pair or series, or moves one's start earlier. A user turns the Frankfurter opt-in
/// on, changes the reporting currency, creates or edits an account into a currency new to
/// the ledger, or adds a benchmark; or a write adds a needed pair, or moves a pair's start,
/// the ledger's earliest day or the first invested day earlier
/// (<see cref="StartIfEarlierAsync"/>). Singleton.
/// </summary>
/// <remarks>
/// <para>It runs after the request that started it has answered, because a first fetch of a
/// pair asks one request per year of history and the person is not kept waiting for it. It
/// is the same fetch the quote refresh runs (<see cref="MarketSeriesFetcher"/>), under the
/// starting user's own consent per destination (ADR-0114 D8), so it fetches only what the ledger's
/// series lack and anything already stored is skipped.</para>
/// <para>It runs as the service role, on its own context: the request's is gone by then.
/// Every trigger is behind a write gate (owner or editor; owner for the reporting
/// currency), and the preference trigger checks the user can write before starting it. The
/// reminder auto-post starts it as the user who configured the schedule, under that user's
/// consent, as the scheduled quote refresh fetches.</para>
/// <para>Each run writes its own family-<c>quote</c> <c>ledger_operations</c> row,
/// <c>provider_key</c> <see cref="ProviderKey"/>, naming the user, with
/// <c>triggered_via = 'manual'</c> when a user started it and <c>'scheduled'</c> when the
/// auto-post did, <c>rates_inserted</c> and <c>benchmark_days_inserted</c>, and an error row
/// per failing symbol or pair. A user with neither destination on starts nothing and no row
/// is written.</para>
/// <para>Backfills of one ledger run one after another; the writes would be safe at once
/// (the fetcher's lock), but two at once would ask the same days twice. Nothing is kept
/// across a restart: a backfill cut short is finished by the next refresh, which fetches
/// from where the stored series end.</para>
/// <para>Never started by a feed sync (ADR-0054 D1): no feed-sync path holds a reference to
/// it. A file import is a user's upload, not a feed sync, and starts it.</para>
/// </remarks>
public sealed class MarketSeriesBackfill
{
    /// <summary>The <c>provider_key</c> of a standalone backfill's audit row.</summary>
    public const string ProviderKey = "market-series-backfill";

    private readonly IServiceScopeFactory _scopes;
    private readonly ServiceDbContextFactory _serviceDb;
    private readonly ILogger<MarketSeriesBackfill> _logger;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _perLedger = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public MarketSeriesBackfill(
        IServiceScopeFactory scopes,
        ServiceDbContextFactory serviceDb,
        IHostApplicationLifetime lifetime,
        ILogger<MarketSeriesBackfill> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _scopes = scopes;
        _serviceDb = serviceDb;
        _logger = logger;
        _clock = clock;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>Start a backfill of <paramref name="ledgerId"/> as <paramref name="userId"/>,
    /// recorded as <paramref name="triggeredVia"/>. Returns at once; failures are logged and
    /// recorded on the run's row.</summary>
    public void Start(Guid ledgerId, Guid userId, string triggeredVia = "manual")
    {
        var task = Task.Run(() => RunAsync(ledgerId, userId, triggeredVia));
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// After a write: reads the starts again and starts a backfill when the write added a
    /// needed pair or moved a start earlier than <paramref name="before"/>, read before it
    /// (<see cref="NeededFxPairs.StartsAsync"/>, ADR-0114 D11). An ordinary write changes
    /// none, so it starts nothing and writes no run row.
    /// </summary>
    /// <remarks>The write has landed when this runs, so a failed read is logged and starts
    /// nothing rather than failing the write: the next refresh fetches what it would have.
    /// Called with a context that sees the write, the request's or a fresh one.</remarks>
    internal async Task StartIfEarlierAsync(
        AppDbContext db, Guid ledgerId, Guid userId, NeededStarts before,
        CancellationToken cancellationToken, string triggeredVia = "manual")
    {
        NeededStarts after;
        try
        {
            after = await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read whether a write moved a start earlier: ledger={LedgerId}", ledgerId);
            return;
        }
        if (before.MovedEarlierBy(after)) Start(ledgerId, userId, triggeredVia);
    }

    /// <summary>Completes once no backfill is running, for a caller that must observe one's
    /// end (the tests, which assert what it fetched and stored).</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            var tasks = _running.Keys.ToList();
            foreach (var done in tasks.Where(t => t.IsCompleted)) _running.TryRemove(done, out _);
            var pending = tasks.Where(t => !t.IsCompleted).ToList();
            if (pending.Count == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Guid ledgerId, Guid userId, string triggeredVia)
    {
        var gate = _perLedger.GetOrAdd(ledgerId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(_stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            using var scope = _scopes.CreateScope();
            await using var db = _serviceDb.Create();
            var fetcher = new MarketSeriesFetcher(
                db,
                scope.ServiceProvider.GetRequiredService<YahooBenchmarkClient>(),
                scope.ServiceProvider.GetRequiredService<FrankfurterRatesClient>(),
                new UserPreferencesRepository(db, _clock),
                scope.ServiceProvider.GetRequiredService<ILogger<MarketSeriesFetcher>>(),
                _clock);

            var consent = await fetcher.ConsentAsync(ledgerId, userId, _stopping).ConfigureAwait(false);
            if (!consent.Any) return;

            var startedAt = _clock.GetUtcNow().UtcDateTime;
            MarketSeriesOutcome outcome;
            try
            {
                outcome = await fetcher.FetchAsync(ledgerId, userId, _stopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Market-series backfill failed: ledger={LedgerId}", ledgerId);
                outcome = new MarketSeriesOutcome(0, 0,
                    [new QuoteError(null, string.Empty, QuoteOrchestrator.MarketSeriesExceptionCode,
                        "The benchmark and exchange-rate backfill failed. Detail is in the server log.")]);
            }

            await QuoteOrchestrator.WriteRunAsync(
                db, _clock, ledgerId, ProviderKey, triggeredVia, userId, startedAt,
                new QuoteRunDetails
                {
                    RatesInserted = outcome.RatesInserted,
                    BenchmarkDaysInserted = outcome.BenchmarkDaysInserted,
                },
                QuoteOrchestrator.FromSource(outcome.Errors, QuoteOrchestrator.MarketSeriesProviderKey),
                _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down: the next refresh fetches from where the stored series end.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market-series backfill could not run: ledger={LedgerId}", ledgerId);
        }
        finally
        {
            gate.Release();
        }
    }
}
