using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Quotes;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Quotes;

/// <summary>
/// End-to-end checks for the quote-provider family (ADR-0033).
/// Covers the orchestrator's persistence path + the SimpleFIN-
/// holdings provider's parse logic + the
/// <c>POST /api/ledgers/{id}/quotes/refresh</c> endpoint, all
/// against a synthetic ledger seeded with a SimpleFIN-style
/// raw payload.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuotesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public QuotesEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Refresh_extracts_prices_from_SimpleFIN_holdings_payload()
    {
        // Seed: brokerage + security + a feed_connection_accounts
        // row carrying a hand-crafted raw payload that mirrors
        // SimpleFIN's holdings[] shape. Endpoint should fan out
        // to the SimpleFinHoldingsQuoteProvider, which parses the
        // payload, computes price = market_value / shares, and
        // upserts into security_prices.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        await SeedFeedConnectionAccountAsync(
            ledger,
            externalId: "ACT-1",
            rawPayload: BuildSimpleFinAccountJson(
                holdings: new[]
                {
                    (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m),
                },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        Assert.Equal(1, outcome.PricesInserted);
        Assert.Equal(0, outcome.PricesUpdated);
        Assert.Empty(outcome.Errors);
        Assert.Contains("simplefin-holdings", outcome.ProviderKeys);

        // Verify the persisted price: 6750 / 10 = 675.00.
        await using var db = _fixture.NewDbContext();
        var price = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(675m, price.Price);
        // Matches the balance_date_unix the payload reported.
        Assert.Equal(
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(1779537600).UtcDateTime),
            price.PriceDate);
    }

    [Fact]
    public async Task Refresh_updates_existing_price_on_second_call()
    {
        // First refresh inserts; second refresh with the same
        // (security, date) UPSERTs in place (no new row). Tests
        // the orchestrator's load-then-decide persistence path.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        const long balanceDate = 1779537600;

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDate));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // First refresh — inserts.
        var resp1 = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(1, (await resp1.Content.ReadFromJsonAsync<QuoteRunOutcome>())!.PricesInserted);

        // Rewrite the raw payload with a different price.
        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6800m) },
                balanceDate));

        // Second refresh — updates the existing (security, date)
        // row in place.
        var resp2 = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var outcome2 = (await resp2.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;
        Assert.Equal(0, outcome2.PricesInserted);
        Assert.Equal(1, outcome2.PricesUpdated);

        await using var db = _fixture.NewDbContext();
        var rows = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(680m, rows[0].Price);
    }

    [Fact]
    public async Task Refresh_does_not_overwrite_a_manual_price()
    {
        // Source ladder (ADR-0070): a SimpleFIN holdings price (rank 1) must NOT
        // clobber a hand-entered 'manual' price (rank 2) for the same
        // (security, day). The provider would compute 675.00; the manual 999.99
        // survives untouched.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        const long balanceDate = 1779537600;
        var priceDate = DateTimeOffset.FromUnixTimeSeconds(balanceDate).UtcDateTime;

        await ledger.AddSecurityPriceAsync(
            securityId, 999.99m, priceDate, source: PriceSource.Manual);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDate));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        // Manual outranks simplefin → neither inserted-over nor updated.
        Assert.Equal(0, outcome.PricesInserted);
        Assert.Equal(0, outcome.PricesUpdated);

        await using var db = _fixture.NewDbContext();
        var row = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(999.99m, row.Price);
        Assert.Equal(PriceSource.Manual, row.Source);
    }

    [Fact]
    public async Task Refresh_overwrites_an_import_price_with_simplefin()
    {
        // Source ladder (ADR-0070 D2/D3): 'import' is the floor and 'simplefin'
        // outranks it, so a SimpleFIN holdings price REPLACES an importer-seeded
        // price for the same (security, day) — the MD seed is provisional.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        const long balanceDate = 1779537600;
        var priceDate = DateTimeOffset.FromUnixTimeSeconds(balanceDate).UtcDateTime;

        await ledger.AddSecurityPriceAsync(
            securityId, 999.99m, priceDate, source: PriceSource.Import);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDate));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        // Day already had a row → 0 inserted, 1 updated (simplefin > import).
        Assert.Equal(0, outcome.PricesInserted);
        Assert.Equal(1, outcome.PricesUpdated);

        await using var db = _fixture.NewDbContext();
        var row = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(675m, row.Price);
        Assert.Equal(PriceSource.Simplefin, row.Source);
    }

    [Fact]
    public async Task Refresh_rounds_simplefin_price_to_four_decimals()
    {
        // SimpleFIN reports market_value + a share count (not a per-share price),
        // so market_value / shares can leave sub-cent noise. The provider rounds
        // to 4dp (ADR-0070): 100 / 3 = 33.33333… lands at 33.3333, not the
        // 12dp crud the prices table used to show.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 3m, 100m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 3m, MarketValue: 100m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var price = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(33.3333m, price.Price);
    }

    [Fact]
    public async Task Refresh_surfaces_unresolved_securities()
    {
        // Two securities in the ledger; only one has a matching
        // holding in the payload. The other appears in
        // outcome.SecuritiesUnresolved so the SPA can flag a
        // "couldn't refresh" pill.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var resolvedId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        var unresolvedId = await ledger.AddSecurityAsync("Apple Inc", ticker: "AAPL");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, resolvedId, 10m, 1000m);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, unresolvedId, 5m, 500m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        Assert.Equal(1, outcome.PricesInserted);
        Assert.Contains(unresolvedId, outcome.SecuritiesUnresolved);
        Assert.DoesNotContain(resolvedId, outcome.SecuritiesUnresolved);
    }

    [Fact]
    public async Task Refresh_resolves_via_quote_symbol_when_set()
    {
        // ADR-0054 D2: the orchestrator sends quote_symbol (not the display
        // ticker) to the provider. The security's ticker is "XYZ" but its
        // quote_symbol is "ETFA"; the payload holds "ETFA", so it resolves
        // ONLY because quote_symbol — not the ticker — was used.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync(
            "Index ETF A", ticker: "XYZ", quoteSymbol: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        Assert.Equal(1, outcome.PricesInserted);
        Assert.DoesNotContain(securityId, outcome.SecuritiesUnresolved);

        await using var db = _fixture.NewDbContext();
        var price = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .SingleAsync();
        Assert.Equal(675m, price.Price);
    }

    [Fact]
    public async Task Refresh_excludes_securities_with_auto_price_off()
    {
        // ADR-0054 D2: auto_price=false makes the security manual-only — not
        // in the pull working set, so it is neither priced nor reported as
        // unresolved even though a matching holding exists in the payload.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync(
            "Index ETF A", ticker: "ETFA", autoPrice: false);
        // Held — so auto_price=false is the ONLY reason it's excluded.
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        Assert.Equal(0, outcome.PricesInserted);
        Assert.DoesNotContain(securityId, outcome.SecuritiesUnresolved);

        await using var db = _fixture.NewDbContext();
        var any = await db.SecurityPrices.AsNoTracking()
            .AnyAsync(p => p.SecurityId == securityId);
        Assert.False(any);
    }

    [Fact]
    public async Task Refresh_skips_holdings_with_zero_shares()
    {
        // The security IS held (so it's in the working set), but the SimpleFIN
        // payload reports shares=0, market_value=0 (a stale/sold snapshot). The
        // provider abstains (division-by-zero protection); no price recorded,
        // and the security surfaces as unresolved.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 0m, MarketValue: 0m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;
        Assert.Equal(0, outcome.PricesInserted);
        Assert.Contains(securityId, outcome.SecuritiesUnresolved);

        await using var db = _fixture.NewDbContext();
        var any = await db.SecurityPrices.AsNoTracking()
            .AnyAsync(p => p.SecurityId == securityId);
        Assert.False(any);
    }

    [Fact]
    public async Task Refresh_excludes_securities_not_held()
    {
        // The refresh values what you hold: a security with a resolvable symbol
        // + auto_price=true but NO live position is excluded from the working
        // set (wasted egress otherwise). Neither priced nor reported as
        // unresolved, even though the payload holds a matching symbol.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        // No AddHoldingAsync → not held.
        _ = brokerage;

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        var outcome = (await resp.Content.ReadFromJsonAsync<QuoteRunOutcome>())!;

        Assert.Equal(0, outcome.PricesInserted);
        Assert.DoesNotContain(securityId, outcome.SecuritiesUnresolved);

        await using var db = _fixture.NewDbContext();
        var any = await db.SecurityPrices.AsNoTracking()
            .AnyAsync(p => p.SecurityId == securityId);
        Assert.False(any);
    }

    [Fact]
    public async Task Refresh_returns_422_on_unknown_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var foreignLedgerId = Guid.NewGuid();
        var resp = await client.PostAsync(
            $"/api/ledgers/{foreignLedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_writes_a_quote_ledger_operation()
    {
        // ADR-0055: every refresh records a ledger_operations row (family=quote)
        // with counts + who/when, so a refresh is auditable even when it
        // changed nothing.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);

        await SeedFeedConnectionAccountAsync(ledger, "ACT-1",
            BuildSimpleFinAccountJson(
                new[] { (Symbol: "ETFA", Shares: 10m, MarketValue: 6750m) },
                balanceDateUnix: 1779537600));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/quotes/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var run = await db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledger.LedgerId && r.Family == "quote")
            .SingleAsync();
        Assert.Equal("quote-refresh", run.ProviderKey);
        Assert.Equal("manual", run.TriggeredVia);
        Assert.NotNull(run.TriggeredByUserId);
        Assert.Equal("completed", run.Status);
        Assert.NotNull(run.CompletedAt);

        var details = LedgerOperationDetails.Deserialize<QuoteRunDetails>(run.DetailsJson);
        Assert.Equal(1, details.PricesInserted);   // ETFA priced from the payload
    }

    // ----- fixtures -----

    /// <summary>
    /// Insert (or replace) a feed_connection_accounts row
    /// carrying the supplied raw payload. Bypasses the SimpleFIN
    /// sync orchestrator (no need to mock SimpleFIN HTTP); just
    /// seeds what the orchestrator would have stored.
    /// </summary>
    private static async Task SeedFeedConnectionAccountAsync(
        SyntheticLedger ledger, string externalId, string rawPayload)
    {
        await using var db = ledger.NewDbContext();

        // Ensure there's a feed_connections row to point at.
        // feed_connection_accounts.feed_connection_id is NOT NULL.
        // One throwaway connection per test ledger is fine.
        var connectionId = await db.FeedConnections
            .Where(c => c.LedgerId == ledger.LedgerId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();
        if (connectionId is null)
        {
            connectionId = Guid.NewGuid();
            db.FeedConnections.Add(new FeedConnectionRow
            {
                Id = connectionId.Value,
                LedgerId = ledger.LedgerId,
                Provider = "simplefin",
                Status = "active",
            });
            await db.SaveChangesAsync();
        }

        var existing = await db.FeedConnectionAccounts
            .Where(a => a.LedgerId == ledger.LedgerId && a.ExternalId == externalId)
            .ToListAsync();
        if (existing.Count > 0) db.FeedConnectionAccounts.RemoveRange(existing);
        db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
        {
            Id = Guid.NewGuid(),
            FeedConnectionId = connectionId.Value,
            LedgerId = ledger.LedgerId,
            ExternalId = externalId,
            Name = "Test Brokerage",
            LastProviderRawPayload = rawPayload,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string BuildSimpleFinAccountJson(
        (string Symbol, decimal Shares, decimal MarketValue)[] holdings,
        long balanceDateUnix)
    {
        var holdingsJson = string.Join(",\n", holdings.Select(h => $$"""
                {
                    "id": "HOL-{{Guid.NewGuid()}}",
                    "symbol": "{{h.Symbol}}",
                    "description": "{{h.Symbol}} Position",
                    "shares": "{{h.Shares}}",
                    "market_value": "{{h.MarketValue}}",
                    "cost_basis": "0",
                    "purchase_price": "0"
                }
            """));
        return $$"""
            {
                "id": "ACT-test",
                "name": "Test Brokerage",
                "currency": "USD",
                "balance": "10000.00",
                "balance-date": {{balanceDateUnix}},
                "holdings": [{{holdingsJson}}],
                "transactions": []
            }
            """;
    }
}
