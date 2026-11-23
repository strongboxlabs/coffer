using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// Every stored quote names the currency its source gave it (ADR-0114 D5): a SimpleFIN
/// holdings price is in its feed account's currency, a source that names none or an
/// unrecognised one stores nothing, and the write path refuses a code no stored price may
/// carry whichever provider handed it over.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteCurrencyTests : DatabaseTest
{
    public QuoteCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const long BalanceDate = 1779537600;

    [Fact]
    public async Task A_SimpleFIN_holdings_price_is_stored_in_its_feed_accounts_currency()
    {
        var (ledger, securityId, _) = await HeldSecurityAsync();
        await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson("CAD", "ETFA", 10m, 6750m));

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Equal(1, outcome.PricesInserted);
        Assert.Empty(outcome.Errors);
        var row = await StoredPriceAsync(securityId);
        Assert.Equal("CAD", row.CurrencyCode);
        Assert.Equal(675m, row.Price);
    }

    [Fact]
    public async Task A_lower_case_feed_currency_is_stored_upper_case()
    {
        var (ledger, securityId, _) = await HeldSecurityAsync();
        await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson("eur", "ETFA", 10m, 6750m));

        await RunHoldingsAsync(ledger);

        Assert.Equal("EUR", (await StoredPriceAsync(securityId)).CurrencyCode);
    }

    [Theory]
    [InlineData(null, "currency-missing")]
    [InlineData("https://example.test/points", "currency-unknown")]
    public async Task A_feed_account_without_a_currency_code_prices_none_of_its_holdings(
        string? currency, string expectedCode)
    {
        var (ledger, securityId, _) = await HeldSecurityAsync();
        await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson(currency, "ETFA", 10m, 6750m));

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Equal(0, outcome.PricesInserted);
        var err = Assert.Single(outcome.Errors);
        Assert.Equal(expectedCode, err.Code);
        Assert.Contains(securityId, outcome.SecuritiesUnresolved);
        await using var db = Db.NewDbContext();
        Assert.False(await db.SecurityPrices.AnyAsync(p => p.SecurityId == securityId));

        // The run's error row names its source and the feed account, so the Activity log
        // can group it and name the account; the message no longer carries the id.
        var row = await db.LedgerOperationErrors.AsNoTracking().SingleAsync(e => e.LedgerId == ledger.LedgerId);
        Assert.Equal(expectedCode, row.Code);
        Assert.Equal(SimpleFinHoldingsQuoteProvider.Key, row.ProviderKey);
        Assert.Equal("ACT-1", row.SimpleFinAccountId);
        Assert.DoesNotContain("ACT-1", row.Message, StringComparison.Ordinal);
        if (currency is null)
            Assert.Equal(SimpleFinHoldingsQuoteProvider.MissingUnmappedMessage, row.Message);
    }

    [Fact]
    public async Task A_feed_account_with_no_currency_is_priced_in_the_currency_of_the_account_it_is_mapped_to()
    {
        // ADR-0114 D5 (amendment): the mapped Coffer account says what currency the feed
        // account holds, as it does for its transactions. CAD, not the USD default, so
        // the stored code can only have come from the account.
        var (ledger, securityId, brokerage) = await HeldSecurityAsync("CAD");
        var connectionId = await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson(null, "ETFA", 10m, 6750m));
        await MapAsync(ledger, brokerage.Id, connectionId, "ACT-1");

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Empty(outcome.Errors);
        Assert.Equal(1, outcome.PricesInserted);
        var row = await StoredPriceAsync(securityId);
        Assert.Equal("CAD", row.CurrencyCode);
        Assert.Equal(675m, row.Price);
    }

    [Fact]
    public async Task A_mapped_feed_account_that_names_a_currency_that_is_not_a_code_still_prices_nothing()
    {
        // Only a missing currency falls back. One the feed did name, but that is not a
        // currency code, is a currency Coffer cannot read, not an absent one.
        var (ledger, securityId, brokerage) = await HeldSecurityAsync("CAD");
        var connectionId = await SeedFeedAccountAsync(
            ledger, "ACT-1", AccountJson("https://example.test/points", "ETFA", 10m, 6750m));
        await MapAsync(ledger, brokerage.Id, connectionId, "ACT-1");

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Equal(0, outcome.PricesInserted);
        Assert.Equal(SimpleFinHoldingsQuoteProvider.CurrencyUnknownCode, Assert.Single(outcome.Errors).Code);
        await using var db = Db.NewDbContext();
        Assert.False(await db.SecurityPrices.AnyAsync(p => p.SecurityId == securityId));
    }

    [Fact]
    public async Task An_account_mapped_on_another_connection_does_not_lend_its_currency()
    {
        // The binding is the pair (connection, feed account id), as the sync reads it: the
        // same feed id on a second connection is a different feed account.
        var (ledger, _, brokerage) = await HeldSecurityAsync("CAD");
        await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson(null, "ETFA", 10m, 6750m));
        var otherConnection = await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson("USD", "OTHER", 1m, 1m));
        await MapAsync(ledger, brokerage.Id, otherConnection, "ACT-1");

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Equal(0, outcome.PricesInserted);
        Assert.Equal(SimpleFinHoldingsQuoteProvider.CurrencyMissingCode, Assert.Single(outcome.Errors).Code);
    }

    [Fact]
    public async Task A_feed_account_with_no_matching_holding_reports_no_currency_error()
    {
        // A cash account the feed reports without a currency is not a pricing failure:
        // nothing in it was going to be priced.
        var (ledger, _, _) = await HeldSecurityAsync();
        await SeedFeedAccountAsync(ledger, "ACT-1", AccountJson(null, "OTHER", 10m, 100m));

        var outcome = await RunHoldingsAsync(ledger);

        Assert.Empty(outcome.Errors);
    }

    [Theory]
    [InlineData("GBp")]
    [InlineData("usd")]
    [InlineData("")]
    [InlineData("US")]
    public async Task The_write_path_refuses_a_currency_code_no_stored_price_may_carry(string currency)
    {
        var (ledger, securityId, _) = await HeldSecurityAsync();

        await using (var db = Db.NewDbContext())
        {
            var outcome = await Orchestrator(db, new FixedProvider(securityId, currency))
                .RunPullAsync(ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "manual", ledger.UserId);

            Assert.Equal(0, outcome.PricesInserted);
            var err = Assert.Single(outcome.Errors);
            Assert.Equal(QuoteOrchestrator.CurrencyInvalidCode, err.Code);
            Assert.Equal(securityId, err.SecurityId);
            Assert.Contains(securityId, outcome.SecuritiesUnresolved);
        }

        await using var read = Db.NewDbContext();
        Assert.False(await read.SecurityPrices.AnyAsync(p => p.SecurityId == securityId));
    }

    [Fact]
    public async Task A_refetch_that_corrects_a_days_currency_updates_the_row()
    {
        // A row a pre-fix fetch stamped USD is re-stamped when the same day is fetched
        // again: change detection compares the currency as well as the price.
        var (ledger, securityId, _) = await HeldSecurityAsync();
        await using (var db = Db.NewDbContext())
        {
            await Orchestrator(db, new FixedProvider(securityId, "USD"))
                .RunPullAsync(ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "manual", ledger.UserId);
        }
        await using (var db = Db.NewDbContext())
        {
            var outcome = await Orchestrator(db, new FixedProvider(securityId, "GBP"))
                .RunPullAsync(ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "manual", ledger.UserId);
            Assert.Equal(1, outcome.PricesUpdated);
        }

        Assert.Equal("GBP", (await StoredPriceAsync(securityId)).CurrencyCode);
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<(SyntheticLedger Ledger, Guid SecurityId, AccountRow Brokerage)> HeldSecurityAsync(
        string currencyCode = "USD")
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", currencyCode: currencyCode);
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        return (ledger, securityId, brokerage);
    }

    /// <summary>Map a Coffer account to a feed account the way the Bank feeds tab does.</summary>
    private static async Task MapAsync(SyntheticLedger ledger, Guid accountId, Guid connectionId, string externalId)
    {
        await using var db = ledger.NewDbContext();
        var outcome = await new AccountsRepository(db, new LegDerivedRecomputeService(db), TimeProvider.System)
            .BindFeedMappingAsync(ledger.LedgerId, accountId, connectionId, externalId);
        Assert.Equal(AccountsRepository.BindFeedMappingResult.Ok, outcome.Result);
    }

    private async Task<QuoteRunOutcome> RunHoldingsAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        var provider = new SimpleFinHoldingsQuoteProvider(
            db, NullLogger<SimpleFinHoldingsQuoteProvider>.Instance, TimeProvider.System);
        return await Orchestrator(db, provider)
            .RunPullAsync(ledger.LedgerId, SimpleFinHoldingsQuoteProvider.Key, "post-sync", ledger.UserId);
    }

    private static QuoteOrchestrator Orchestrator(Coffer.Api.Db.AppDbContext db, IQuotePullProvider provider) =>
        new(db,
            [provider],
            [],
            new UserPreferencesRepository(db, TimeProvider.System),
            NullLogger<QuoteOrchestrator>.Instance, TimeProvider.System);

    private async Task<SecurityPriceRow> StoredPriceAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking().SingleAsync(p => p.SecurityId == securityId);
    }

    /// <summary>A feed connection holding one feed account; returns the connection's id.</summary>
    private static async Task<Guid> SeedFeedAccountAsync(SyntheticLedger ledger, string externalId, string rawPayload)
    {
        await using var db = ledger.NewDbContext();
        var connectionId = Guid.NewGuid();
        db.FeedConnections.Add(new FeedConnectionRow
        {
            Id = connectionId,
            LedgerId = ledger.LedgerId,
            Provider = "simplefin",
            Status = "active",
        });
        db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
        {
            Id = Guid.NewGuid(),
            FeedConnectionId = connectionId,
            LedgerId = ledger.LedgerId,
            ExternalId = externalId,
            Name = "Synthetic Brokerage",
            LastProviderRawPayload = rawPayload,
            LastSeenAt = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        return connectionId;
    }

    /// <summary>A SimpleFIN account payload with one holding. A null currency omits the
    /// property.</summary>
    private static string AccountJson(string? currency, string symbol, decimal shares, decimal marketValue)
    {
        var currencyProperty = currency is null ? string.Empty : $"\"currency\": \"{currency}\",";
        return $$"""
            {
                "id": "ACT-test",
                "name": "Synthetic Brokerage",
                {{currencyProperty}}
                "balance": "10000.00",
                "balance-date": {{BalanceDate}},
                "holdings": [
                    {
                        "id": "HOL-1",
                        "symbol": "{{symbol}}",
                        "shares": "{{shares}}",
                        "market_value": "{{marketValue}}"
                    }
                ],
                "transactions": []
            }
            """;
    }

    /// <summary>Hands the write path one quote in a given currency, under the no-egress key so
    /// opt-in gating does not skip it.</summary>
    private sealed class FixedProvider : IQuotePullProvider
    {
        private readonly Guid _securityId;
        private readonly string _currency;

        public FixedProvider(Guid securityId, string currency)
        {
            _securityId = securityId;
            _currency = currency;
        }

        public string ProviderKey => SimpleFinHoldingsQuoteProvider.Key;
        public string DisplayName => ProviderKey;
        public bool RequiresOptIn => false;

        public Task<QuoteResult> PullAsync(QuotePullContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new QuoteResult(
                [new QuoteEntry(_securityId, 12.5m,
                    new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc), _currency, PriceSource.Fetch)],
                []));
    }
}
