using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// Opt-in gating (ADR-0057): an external (RequiresOptIn) provider runs in
/// <c>RunAllPullsAsync</c> only when the acting user's <c>quotes</c> pref for
/// the ledger lists its key. The no-egress <c>simplefin-holdings</c> provider
/// always runs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteOptInGatingTests
{
    private const string ExternalKey = "spy-external";

    private readonly PostgresFixture _fixture;

    public QuoteOptInGatingTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Opt_in_provider_skipped_without_pref_runs_when_enabled()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedHeldSecurityAsync(ledger);

        // Case 1: no pref → external provider is skipped, holdings still runs.
        {
            var holdings = new SpyPullProvider(SimpleFinHoldingsQuoteProvider.Key, requiresOptIn: false);
            var external = new SpyPullProvider(ExternalKey, requiresOptIn: true);
            await using var db = _fixture.NewDbContext();
            var orchestrator = new QuoteOrchestrator(
                db,
                new IQuotePullProvider[] { holdings, external },
                Array.Empty<IQuotePushProvider>(),
                new UserPreferencesRepository(db),
                NullLogger<QuoteOrchestrator>.Instance);

            await orchestrator.RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

            Assert.True(holdings.Invoked, "no-egress provider always runs");
            Assert.False(external.Invoked, "opt-in provider skipped without a pref");
        }

        // Case 2: enable the external provider in the user's quotes pref → it runs.
        await using (var seed = _fixture.NewDbContext())
        {
            await new UserPreferencesRepository(seed).SetQuotesAsync(
                ledger.UserId, ledger.LedgerId,
                new QuotesPrefs { EnabledProviders = new[] { ExternalKey } });
        }
        {
            var holdings = new SpyPullProvider(SimpleFinHoldingsQuoteProvider.Key, requiresOptIn: false);
            var external = new SpyPullProvider(ExternalKey, requiresOptIn: true);
            await using var db = _fixture.NewDbContext();
            var orchestrator = new QuoteOrchestrator(
                db,
                new IQuotePullProvider[] { holdings, external },
                Array.Empty<IQuotePushProvider>(),
                new UserPreferencesRepository(db),
                NullLogger<QuoteOrchestrator>.Instance);

            await orchestrator.RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

            Assert.True(external.Invoked, "opt-in provider runs once enabled in the pref");
        }
    }

    [Fact]
    public async Task Egress_provider_excludes_non_public_quote_symbol()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // A public security (bare ticker — always public) and a private / feed-only
        // one (a 529-style portfolio number marked not public). Both held + auto-
        // priced, so both are in the working set.
        var publicId = await ledger.AddSecurityAsync("Public Fund", ticker: "ETFA");
        var privateId = await ledger.AddSecurityAsync(
            "Feed-only 529", ticker: null, quoteSymbol: "8918", quoteSymbolPublic: false);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, publicId, 10m, 1000m);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, privateId, 5m, 500m);

        // Enable the external (egress) provider so it actually runs.
        await using (var seed = _fixture.NewDbContext())
        {
            await new UserPreferencesRepository(seed).SetQuotesAsync(
                ledger.UserId, ledger.LedgerId,
                new QuotesPrefs { EnabledProviders = new[] { ExternalKey } });
        }

        var holdings = new SpyPullProvider(SimpleFinHoldingsQuoteProvider.Key, requiresOptIn: false);
        var external = new SpyPullProvider(ExternalKey, requiresOptIn: true);
        await using var db = _fixture.NewDbContext();
        var orchestrator = new QuoteOrchestrator(
            db,
            new IQuotePullProvider[] { holdings, external },
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db),
            NullLogger<QuoteOrchestrator>.Instance);

        await orchestrator.RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);

        // The no-egress feed provider sees BOTH — it prices the private symbol from
        // the feed payload.
        Assert.True(holdings.Invoked, "no-egress provider always runs");
        Assert.Contains(holdings.CapturedSecurities, s => s.SecurityId == publicId);
        Assert.Contains(holdings.CapturedSecurities, s => s.SecurityId == privateId);

        // The egress provider sees ONLY the public one — the private / feed-only
        // symbol is never sent to an external provider (ADR-0054 D2).
        Assert.True(external.Invoked, "opt-in provider runs once enabled");
        Assert.Contains(external.CapturedSecurities, s => s.SecurityId == publicId);
        Assert.DoesNotContain(external.CapturedSecurities, s => s.SecurityId == privateId);
    }

    private static async Task SeedHeldSecurityAsync(SyntheticLedger ledger)
    {
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
    }

    private sealed class SpyPullProvider : IQuotePullProvider
    {
        public SpyPullProvider(string key, bool requiresOptIn)
        {
            ProviderKey = key;
            RequiresOptIn = requiresOptIn;
        }

        public string ProviderKey { get; }
        public string DisplayName => ProviderKey;
        public bool RequiresOptIn { get; }
        public bool Invoked { get; private set; }
        public IReadOnlyList<QuoteSecurityRef> CapturedSecurities { get; private set; } =
            Array.Empty<QuoteSecurityRef>();

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken)
        {
            Invoked = true;
            CapturedSecurities = context.Securities;
            return Task.FromResult(new QuoteResult(Array.Empty<QuoteEntry>(), Array.Empty<QuoteError>()));
        }
    }
}
