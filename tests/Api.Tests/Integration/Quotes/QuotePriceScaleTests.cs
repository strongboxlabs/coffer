using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// A quote is stored at the scale its column has, and re-fetching an unchanged price
/// reports no change.
/// </summary>
/// <remarks>
/// <c>security_prices.price</c> is NUMERIC(19,4) (mig 155). A provider that hands over
/// <c>decimal.Round(price, 12)</c> makes Postgres round again on assignment, so the
/// in-memory value no longer equals the stored one. That does not corrupt a price —
/// it breaks CHANGE DETECTION: the orchestrator's <c>existingRow.Price != q.Price</c>
/// could never match, so every refetch would rewrite the row and count an update that
/// changed nothing. Float-derived closes normally carry a non-zero fifth decimal, so
/// this would fire essentially every time.
/// <para>
/// The guarantee is asserted at the ORCHESTRATOR, not per provider. Three providers
/// each restating "round to 4" is three chances to get it wrong. Pinning it at the
/// single write path is what makes a future provider unable to reintroduce it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class QuotePriceScaleTests : DatabaseTest
{
    public QuotePriceScaleTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>A float-derived close, exactly the shape mig 155 was written for.</summary>
    private const decimal LongTailPrice = 3.450000047684m;

    [Fact]
    public async Task A_long_tailed_price_is_stored_at_scale_4_and_refetching_it_reports_no_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        var asOf = new DateTime(2026, 4, 1, 20, 0, 0, DateTimeKind.Utc);

        // First run inserts.
        await using (var db = Db.NewDbContext())
        {
            var result = await Orchestrator(db, securityId, asOf)
                .RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);
            Assert.Equal(1, result.PricesInserted);
        }

        await using (var db = Db.NewDbContext())
        {
            var stored = await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == securityId)
                .Select(p => p.Price)
                .SingleAsync();

            // Four decimals, not the provider's twelve: rounded before the write, so what is in the
            // row is what the writer believes it wrote.
            Assert.Equal(decimal.Round(LongTailPrice, 4), stored);
        }

        // Second run with the SAME price must be a no-op, not an update reported every
        // single time, forever.
        await using (var db = Db.NewDbContext())
        {
            var result = await Orchestrator(db, securityId, asOf)
                .RunAllPullsAsync(ledger.LedgerId, "manual", ledger.UserId);
            Assert.Equal(0, result.PricesInserted);
            Assert.Equal(0, result.PricesUpdated);
        }
    }

    private QuoteOrchestrator Orchestrator(
        Coffer.Api.Db.AppDbContext db, Guid securityId, DateTime asOf) =>
        new(db,
            [new LongTailProvider(securityId, asOf)],
            [],
            new UserPreferencesRepository(db, TimeProvider.System),
            NullLogger<QuoteOrchestrator>.Instance, TimeProvider.System);

    /// <summary>Returns one quote carrying more decimals than the column can hold.</summary>
    private sealed class LongTailProvider : IQuotePullProvider
    {
        private readonly Guid _securityId;
        private readonly DateTime _asOf;

        public LongTailProvider(Guid securityId, DateTime asOf)
        {
            _securityId = securityId;
            _asOf = asOf;
        }

        // Borrows the no-egress key so opt-in gating (ADR-0057) does not skip it.
        public string ProviderKey => SimpleFinHoldingsQuoteProvider.Key;
        public string DisplayName => ProviderKey;
        public bool RequiresOptIn => false;

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new QuoteResult(
                [new QuoteEntry(_securityId, LongTailPrice, _asOf, "USD", PriceSource.Fetch)],
                []));
    }
}
