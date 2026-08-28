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
    private readonly ILoggerFactory _loggers;

    public QuoteRefreshJobHandler(YahooFinanceQuoteProvider yahoo, ILoggerFactory loggers)
    {
        _yahoo = yahoo;
        _loggers = loggers;
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
        AppDbContext db, Guid ledgerId, Guid configuredByUserId, CancellationToken cancellationToken)
    {
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[]
            {
                new SimpleFinHoldingsQuoteProvider(
                    db, _loggers.CreateLogger<SimpleFinHoldingsQuoteProvider>()),
                _yahoo,
            },
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            _loggers.CreateLogger<QuoteOrchestrator>());
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

        // Deliberately NOT "any error at all". A delisted ticker or a symbol no provider
        // covers produces an error on a perfectly healthy run, and reporting that as
        // degraded every single day is how a monitor teaches its owner to ignore it.
        if (providerFailures == 0) return JobRunOutcome.Ok();

        // Every provider down is the case a heartbeat must never call healthy: nothing
        // was refreshed at all. One of several failing still did work — worth saying
        // anyway, because the same provider failing daily is how prices go stale while
        // everything looks fine.
        var allDown = outcome.ProviderKeys.Count > 0
                      && providerFailures >= outcome.ProviderKeys.Count;

        return JobRunOutcome.Degraded(allDown
            ? $"No quote provider succeeded ({providerFailures} of "
              + $"{outcome.ProviderKeys.Count} errored). Detail is in the server log."
            : $"A quote provider failed ({providerFailures} of "
              + $"{outcome.ProviderKeys.Count}). Detail is in the server log.");
    }

    /// <summary>The code <c>QuoteOrchestrator</c> stamps on a provider that threw.</summary>
    internal const string ProviderExceptionCode = "provider-exception";
}
