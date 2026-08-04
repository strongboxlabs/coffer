using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// The post-sync quote pull must touch ONLY the <c>simplefin-holdings</c>
/// provider — a SimpleFIN bank sync should never fan out to external
/// market-data providers (Yahoo et al.), which would turn every sync into
/// outbound egress (ADR-0054). <c>RunPullAsync</c> runs exactly the one named
/// provider (the post-sync entry point); <c>RunAllPullsAsync</c> (the explicit
/// / scheduled refresh) fans to all.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PostSyncQuoteScopeTests
{
    private readonly PostgresFixture _fixture;

    public PostSyncQuoteScopeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RunPullAsync_invokes_only_the_named_provider()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedHeldSecurityAsync(ledger);

        var holdings = new SpyPullProvider(SimpleFinHoldingsQuoteProvider.Key);
        var external = new SpyPullProvider("yahoo");
        await using var db = _fixture.NewDbContext();
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[] { holdings, external },
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            NullLogger<QuoteOrchestrator>.Instance);

        await orchestrator.RunPullAsync(
            ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "post-sync", ledger.UserId);

        Assert.True(holdings.Invoked, "simplefin-holdings should run post-sync");
        Assert.False(external.Invoked, "an external provider must NOT run post-sync");

        // The run is still recorded as a quote refresh (ADR-0055), attributed
        // post-sync — so the Activity timeline shows it identically to before.
        await using var read = _fixture.NewDbContext();
        var run = await read.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledger.LedgerId && r.Family == "quote")
            .SingleAsync();
        Assert.Equal("quote-refresh", run.ProviderKey);
        Assert.Equal("post-sync", run.TriggeredVia);
        Assert.Equal(ledger.UserId, run.TriggeredByUserId);
    }

    [Fact]
    public async Task RunAllPullsAsync_fans_out_to_every_provider()
    {
        // The contrast that makes the single-provider scoping meaningful: the
        // explicit refresh path DOES reach every registered provider.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedHeldSecurityAsync(ledger);

        var holdings = new SpyPullProvider(SimpleFinHoldingsQuoteProvider.Key);
        var external = new SpyPullProvider("yahoo");
        await using var db = _fixture.NewDbContext();
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[] { holdings, external },
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            NullLogger<QuoteOrchestrator>.Instance);

        await orchestrator.RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

        Assert.True(holdings.Invoked);
        Assert.True(external.Invoked);
    }

    [Fact]
    public async Task Quote_run_records_which_source_moved_the_prices()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedHeldSecurityAsync(ledger);

        // A provider that actually prices the held security, tagged as the
        // SimpleFIN feed source (ADR-0070). D: the run must attribute it.
        var provider = new PricingPullProvider(
            SimpleFinHoldingsQuoteProvider.Key, PriceSource.Simplefin);
        await using var db = _fixture.NewDbContext();
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[] { provider },
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            NullLogger<QuoteOrchestrator>.Instance);

        await orchestrator.RunPullAsync(
            ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "post-sync", ledger.UserId);

        await using var read = _fixture.NewDbContext();
        var run = await read.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledger.LedgerId && r.Family == "quote")
            .SingleAsync();
        // The run attributes its one written price to the SimpleFIN feed — so the
        // Activity log can name the provider instead of a generic "quote refresh".
        var details = LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
        Assert.Equal(1, details.PricesInserted);
        Assert.Equal(1, details.PricesFromSimplefin);
        Assert.Equal(0, details.PricesFromFetch);
    }

    private static async Task SeedHeldSecurityAsync(SyntheticLedger ledger)
    {
        // A held, auto-priceable security → the pull working set is non-empty,
        // so a provider is actually invoked (an empty set short-circuits).
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
    }

    /// <summary>Records whether it was invoked; prices nothing.</summary>
    private sealed class SpyPullProvider : IQuotePullProvider
    {
        public SpyPullProvider(string key, bool requiresOptIn = false)
        {
            ProviderKey = key;
            RequiresOptIn = requiresOptIn;
        }

        public string ProviderKey { get; }
        public string DisplayName => ProviderKey;
        public bool RequiresOptIn { get; }
        public bool Invoked { get; private set; }

        public Task<QuoteResult> PullAsync(
            QuotePullContext context, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.FromResult(
                new QuoteResult(Array.Empty<QuoteEntry>(), Array.Empty<QuoteError>()));
        }
    }

    /// <summary>Prices every requested security at a fixed value, tagged with a
    /// chosen ADR-0070 source — so the run's per-source attribution is exercised.</summary>
    private sealed class PricingPullProvider : IQuotePullProvider
    {
        private readonly string _source;
        public PricingPullProvider(string key, string source)
        {
            ProviderKey = key;
            _source = source;
        }
        public string ProviderKey { get; }
        public string DisplayName => ProviderKey;
        public bool RequiresOptIn => false;
        public Task<QuoteResult> PullAsync(
            QuotePullContext context, CancellationToken cancellationToken)
        {
            var quotes = context.Securities
                .Select(s => new QuoteEntry(
                    SecurityId: s.SecurityId,
                    Price: 123.45m,
                    PriceAsOfUtc: new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc),
                    CurrencyCode: "USD",
                    Source: _source))
                .ToArray();
            return Task.FromResult(new QuoteResult(quotes, Array.Empty<QuoteError>()));
        }
    }
}
