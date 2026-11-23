using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Quotes.Yahoo;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Quotes.Scheduling;

/// <summary>
/// Scheduled-job handler for <c>quote-refresh</c> (ADR-0054 B): runs a full
/// quote refresh for the ledger as the schedule's configuring user
/// (<c>triggered_via='scheduled'</c>). Builds the orchestrator over the worker's
/// service-role context; the provider set mirrors Program.cs (the db-bound
/// simplefin-holdings is built over that context, Yahoo is db-independent DI).
/// </summary>
public sealed class QuoteRefreshJobHandler : IScheduledJobHandler
{
    private readonly YahooFinanceQuoteProvider _yahoo;
    private readonly YahooBenchmarkClient _benchmarks;
    private readonly Frankfurter.FrankfurterRatesClient _rates;
    private readonly ILoggerFactory _loggers;
    private readonly TimeProvider _clock;

    /// <param name="benchmarks">Benchmark growth (ADR-0114 D11), one client per handler
    /// scope and so per run: its throttle is per instance.</param>
    /// <param name="rates">Exchange rates (ADR-0114 D11).</param>
    public QuoteRefreshJobHandler(
        YahooFinanceQuoteProvider yahoo,
        YahooBenchmarkClient benchmarks,
        Frankfurter.FrankfurterRatesClient rates,
        ILoggerFactory loggers,
        TimeProvider clock)
    {
        _yahoo = yahoo;
        _benchmarks = benchmarks;
        _rates = rates;
        _loggers = loggers;
        _clock = clock;
    }

    public string JobType => JobTypes.QuoteRefresh;

    /// <summary>
    /// Runs the refresh, and reports a provider outage as <c>Degraded</c> rather than
    /// letting it pass for success.
    /// </summary>
    /// <remarks>
    /// <c>QuoteOrchestrator</c> catches every provider exception into its error list and
    /// returns normally, so the runner's try/catch cannot see one. Scoped to
    /// <c>provider-exception</c> specifically, and to the case where every provider
    /// errored: a benign per-security miss (a delisted ticker, a symbol no provider
    /// covers) is an ordinary outcome of a healthy run and must not be reported as
    /// anything else, or the monitor built on top of this cries wolf from day one.
    /// </remarks>
    public async Task<JobRunOutcome> RunAsync(
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, JobRunClock clock, CancellationToken cancellationToken)
    {
        var prefs = new UserPreferencesRepository(db, _clock);
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[]
            {
                new SimpleFinHoldingsQuoteProvider(
                    db, _loggers.CreateLogger<SimpleFinHoldingsQuoteProvider>(), _clock),
                _yahoo,
            },
            Array.Empty<IQuotePushProvider>(),
            prefs,
            _loggers.CreateLogger<QuoteOrchestrator>(),
            _clock,
            // ADR-0114 D11: benchmarks and rates ride the scheduled refresh, under the
            // schedule's user's consent (ADR-0114 D8), as quotes do.
            new MarketSeries.MarketSeriesFetcher(
                db, _benchmarks, _rates, prefs, _loggers.CreateLogger<MarketSeries.MarketSeriesFetcher>(), _clock));
        var outcome = await orchestrator
            .RunAllPullsAsync(ledgerId, "scheduled", configuredByUserId, cancellationToken)
            .ConfigureAwait(false);

        return Classify(outcome);
    }

    /// <summary>The code under the trap, separated so it can be tested directly.</summary>
    /// <remarks>
    /// Internal rather than private because the behaviour it encodes — a swallowed
    /// provider outage is NOT a successful run — is the entire reason this handler
    /// reports an outcome at all, and the handler builds its own providers internally,
    /// so there is no seam to inject a failing one through. Testing the classifier
    /// against the error shape <c>QuoteOrchestrator</c> actually produces is the honest
    /// coverage available here; QuoteOrchestrator's own tests pin that it produces it.
    /// </remarks>
    internal static JobRunOutcome Classify(QuoteRunOutcome outcome)
    {
        // Counted, not quoted. QuoteOrchestrator builds these messages as
        // $"{providerKey}: {ex.Message}", so joining them put raw provider exception text
        // into a summary that goes out to a configured webhook. The orchestrator already
        // logs each one in full on this side of the wire.
        var providerFailures = outcome.Errors
            .Count(e => string.Equals(e.Code, ProviderExceptionCode, StringComparison.Ordinal));

        // Exchange rates ride this job (ADR-0114 D11), so its switch is the one that
        // watches them, and a rate that did not land is part of the work not landing.
        // Unlike a per-security miss there is no benign case: a pair whose days were not
        // stored converts to nothing (fail closed) until a later run stores them.
        var rateFailures = outcome.Errors.Count(e => RatesNotStoredCodes.Contains(e.Code));

        // Deliberately NOT "any error at all". A delisted ticker or a symbol no provider
        // covers produces an error on a perfectly healthy run, and reporting that as
        // degraded every single day is how a monitor teaches its owner to ignore it. A
        // benchmark refused for what it is (an index, an unsupported instrument) is the
        // same kind of per-symbol miss, so benchmark codes are not counted either.
        if (providerFailures == 0 && rateFailures == 0) return JobRunOutcome.Ok();

        // Every provider down is the case a heartbeat must never call healthy: nothing
        // was refreshed at all. One of several failing still did work — worth saying
        // anyway, because the same provider failing daily is how prices go stale while
        // everything looks fine.
        var allDown = outcome.ProviderKeys.Count > 0
                      && providerFailures >= outcome.ProviderKeys.Count;

        var parts = new List<string>(2);
        if (providerFailures > 0)
        {
            // Kept word for word: migration 252 matches stored messages on the
            // "No quote provider succeeded" prefix.
            parts.Add(allDown
                ? $"No quote provider succeeded ({providerFailures} of "
                  + $"{outcome.ProviderKeys.Count} errored). Detail is in the server log."
                : $"A quote provider failed ({providerFailures} of "
                  + $"{outcome.ProviderKeys.Count}). Detail is in the server log.");
        }
        if (rateFailures > 0)
        {
            parts.Add($"Exchange rates were not stored ({rateFailures} "
                      + (rateFailures == 1 ? "error" : "errors")
                      + "). Detail is in Settings → Activity.");
        }
        var message = string.Join(" ", parts);

        // Nothing landed at all: every quote provider failed, or there was no quote work,
        // and no rate day was stored either. That is the run that must escalate.
        var noQuoteWork = outcome.ProviderKeys.Count == 0;
        var nothingDone = allDown || (noQuoteWork && rateFailures > 0 && outcome.RatesInserted == 0);

        return nothingDone ? JobRunOutcome.DidNothing(message) : JobRunOutcome.Degraded(message);
    }

    /// <summary>The code <c>QuoteOrchestrator</c> stamps on a provider that threw.</summary>
    internal const string ProviderExceptionCode = "provider-exception";

    /// <summary>
    /// The codes that mean exchange-rate days this run needed were not stored: the rate
    /// source refused, could not be read, or is misconfigured, or the whole market-series
    /// step threw or found the ledger's write lock held. Enumerated, so a code added later
    /// is not counted until it is placed on one side of this line.
    /// </summary>
    internal static readonly IReadOnlySet<string> RatesNotStoredCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        Frankfurter.FrankfurterRatesClient.FetchFailedCode,
        Frankfurter.FrankfurterRatesClient.ParseFailedCode,
        Frankfurter.FrankfurterRatesClient.BaseUrlInvalidCode,
        QuoteOrchestrator.MarketSeriesExceptionCode,
        MarketSeries.MarketSeriesFetcher.LedgerWriteBusyCode,
    };
}
