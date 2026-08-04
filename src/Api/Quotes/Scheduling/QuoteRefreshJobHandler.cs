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

    public Task RunAsync(
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
        return orchestrator.RunAllPullsAsync(ledgerId, "scheduled", configuredByUserId, cancellationToken);
    }
}
