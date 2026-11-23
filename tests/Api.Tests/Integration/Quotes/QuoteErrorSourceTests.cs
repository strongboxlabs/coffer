using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// A quote refresh fans out to several sources and records their errors as one run. Each
/// error names the source that raised it (migration 303), so the Activity log can group
/// them: an error a provider returned, one from a provider that threw, and one the write
/// path raised on a provider's quote all carry that provider's key, on the outcome and on
/// the stored row.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteErrorSourceTests : DatabaseTest
{
    public QuoteErrorSourceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task Every_error_of_a_refresh_names_the_provider_that_raised_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        QuoteRunOutcome outcome;
        await using (var db = Db.NewDbContext())
        {
            outcome = await new QuoteOrchestrator(
                    db,
                    [
                        new ErrorProvider("source-a", "a-failed"),
                        new BadCurrencyProvider("source-b", securityId),
                        new ThrowingProvider("source-c"),
                    ],
                    [],
                    new UserPreferencesRepository(db, TimeProvider.System),
                    NullLogger<QuoteOrchestrator>.Instance, TimeProvider.System)
                .RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);
        }

        var expected = new Dictionary<string, string>
        {
            ["a-failed"] = "source-a",
            [QuoteOrchestrator.CurrencyInvalidCode] = "source-b",
            ["provider-exception"] = "source-c",
        };
        Assert.Equal(expected, outcome.Errors.ToDictionary(e => e.Code, e => e.ProviderKey!));

        await using var read = Db.NewDbContext();
        var rows = await read.LedgerOperationErrors.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(e => e.Code, e => e.ProviderKey!);
        Assert.Equal(expected, rows);
    }

    private sealed class ErrorProvider(string key, string code) : IQuotePullProvider
    {
        public string ProviderKey => key;
        public string DisplayName => key;
        public bool RequiresOptIn => false;

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new QuoteResult([], [new QuoteError(null, string.Empty, code, "It failed.")]));
    }

    /// <summary>Hands the write path a quote in a minor unit, which it refuses with an error.</summary>
    private sealed class BadCurrencyProvider(string key, Guid securityId) : IQuotePullProvider
    {
        public string ProviderKey => key;
        public string DisplayName => key;
        public bool RequiresOptIn => false;

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new QuoteResult(
                [new QuoteEntry(securityId, 12.5m, new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc), "GBp", PriceSource.Fetch)],
                []));
    }

    private sealed class ThrowingProvider(string key) : IQuotePullProvider
    {
        public string ProviderKey => key;
        public string DisplayName => key;
        public bool RequiresOptIn => false;

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider is down.");
    }
}
