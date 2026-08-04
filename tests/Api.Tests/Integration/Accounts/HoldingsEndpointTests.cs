using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// End-to-end checks for the Portfolio View read endpoint
/// (<c>GET /api/ledgers/{ledgerId}/accounts/{accountId}/holdings</c> —
/// slice A1). Per-test <see cref="SyntheticLedger"/> with an investment
/// account + Holdings sibling + securities + price snapshots gives each
/// test its own atomic fixture; computed summary numbers are asserted on
/// the response so the per-position math is exercised end-to-end.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HoldingsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public HoldingsEndpointTests(PostgresFixture fixture)
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
    public async Task Returns_positions_with_latest_price_joined()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Test Brokerage");
        var idxa = await ledger.AddSecurityAsync(
            name: "Index Fund A",
            ticker: "IDXA",
            assetClass: "equity");
        await ledger.AddHoldingAsync(
            holdingsAccountId: brokerage.HoldingsAccountId!.Value,
            securityId: idxa,
            quantity: 100m,
            costBasis: 23360m); // $233.60 average cost
        // Two prices to confirm the endpoint picks the latest by date.
        await ledger.AddSecurityPriceAsync(idxa, 200m, new DateTime(2026, 4, 1));
        await ledger.AddSecurityPriceAsync(idxa, 250.00m, new DateTime(2026, 5, 17));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/holdings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = (await response.Content.ReadFromJsonAsync<HoldingsViewDto>())!;
        Assert.Equal(brokerage.Id, view.AccountId);
        Assert.Equal("Test Brokerage", view.AccountName);
        Assert.Single(view.Positions);

        var p = view.Positions[0];
        Assert.Equal("IDXA", p.Ticker);
        Assert.Equal(100m, p.Quantity);
        Assert.Equal(23360m, p.CostBasis);
        Assert.Equal(233.60m, p.CostPerShare);
        Assert.Equal(250.00m, p.CurrentPrice);
        Assert.Equal(25000m, p.CurrentValue);
        Assert.Equal(1640m, p.UnrealizedGain);
        // 1640 / 23360 * 100 ≈ 7.0205
        Assert.NotNull(p.PercentChange);
        Assert.Equal(7.0205m, Math.Round(p.PercentChange!.Value, 4));

        // Summary aggregates: one position, no cash legs yet.
        Assert.Equal(25000m, view.Summary.PortfolioValue);
        Assert.Equal(23360m, view.Summary.CostBasis);
        Assert.Equal(1640m, view.Summary.UnrealizedGain);
        Assert.Equal(0m, view.Summary.CashBalance);
        Assert.Equal(25000m, view.Summary.Total);
    }

    [Fact]
    public async Task Filters_out_zero_quantity_positions()
    {
        // The importer leaves a holdings row at quantity=0 when a position
        // is fully sold (ADR-0018 Rule 4 — no lot closing). The Portfolio
        // View must hide those rows; surfacing them would clutter the
        // table with empty entries that have no current meaning.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var open = await ledger.AddSecurityAsync(name: "Open Position", ticker: "AAA");
        var sold = await ledger.AddSecurityAsync(name: "Sold Out", ticker: "BBB");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, open, 10m, 1000m);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, sold, 0m, 0m);
        await ledger.AddSecurityPriceAsync(open, 120m, new DateTime(2026, 5, 17));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/holdings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = (await response.Content.ReadFromJsonAsync<HoldingsViewDto>())!;
        Assert.Single(view.Positions);
        Assert.Equal("AAA", view.Positions[0].Ticker);
    }

    [Fact]
    public async Task Position_with_no_price_carries_cost_basis_into_portfolio_value()
    {
        // Manual-entry / pre-feed-integration territory: a security with
        // no price snapshot has null Current* fields, but the summary's
        // PortfolioValue treats it as held-at-cost so the total doesn't
        // silently drop to zero for un-priced holdings.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var unpriced = await ledger.AddSecurityAsync(name: "No Price Yet", ticker: "ZZZ");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, unpriced, 5m, 500m);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/holdings");
        var view = (await response.Content.ReadFromJsonAsync<HoldingsViewDto>())!;

        var p = Assert.Single(view.Positions);
        Assert.Null(p.CurrentPrice);
        Assert.False(p.PriceAsOf.HasValue);
        Assert.Null(p.CurrentValue);
        Assert.Null(p.UnrealizedGain);
        Assert.Null(p.PercentChange);
        Assert.Equal(100m, p.CostPerShare); // 500 / 5

        Assert.Equal(500m, view.Summary.PortfolioValue); // carries cost basis
        Assert.Equal(500m, view.Summary.CostBasis);
        Assert.Equal(0m,   view.Summary.UnrealizedGain);
    }

    [Fact]
    public async Task Empty_brokerage_returns_zero_summary_with_no_positions()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Empty Brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/holdings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = (await response.Content.ReadFromJsonAsync<HoldingsViewDto>())!;
        Assert.Empty(view.Positions);
        Assert.Equal(0m, view.Summary.PortfolioValue);
        Assert.Equal(0m, view.Summary.CostBasis);
        Assert.Equal(0m, view.Summary.CashBalance);
        Assert.Equal(0m, view.Summary.Total);
    }

    [Fact]
    public async Task Positions_sorted_by_ticker_ascending()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var zzz = await ledger.AddSecurityAsync(name: "Zzz", ticker: "ZZZ");
        var aaa = await ledger.AddSecurityAsync(name: "Aaa", ticker: "AAA");
        var mid = await ledger.AddSecurityAsync(name: "Mid", ticker: "MID");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, zzz, 1m, 100m);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, aaa, 1m, 100m);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, mid, 1m, 100m);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}/holdings");
        var view = (await response.Content.ReadFromJsonAsync<HoldingsViewDto>())!;

        Assert.Equal(new[] { "AAA", "MID", "ZZZ" },
            view.Positions.Select(p => p.Ticker).ToArray());
    }

    [Fact]
    public async Task Returns_422_when_account_is_not_investment_type()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/holdings");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-investment", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Returns_422_when_account_not_in_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var stranger = Guid.NewGuid();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{stranger}/holdings");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBrokerage = await alice.AddInvestmentAccountAsync("Alice Brokerage");
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var response = await bobClient.GetAsync(
            $"/api/ledgers/{alice.LedgerId}/accounts/{aliceBrokerage.Id}/holdings");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible", doc.RootElement.GetProperty("code").GetString());
    }
}
