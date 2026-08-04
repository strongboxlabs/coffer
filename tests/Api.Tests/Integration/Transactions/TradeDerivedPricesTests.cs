using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// ADR-0084: an investment TRADE leg written through the normal EF write path
/// (native investment-transactions endpoint / MCP) seeds a <c>trade</c>-source
/// row into <c>security_prices</c> via the <see cref="TradePriceFromLegInterceptor"/>.
/// The rank gate (<c>manual == fetch &gt; trade &gt; simplefin &gt; import</c>)
/// means a trade beats an import/simplefin row but a fetch/manual price for the
/// day is never clobbered. Each test bootstraps an atomic
/// <see cref="SyntheticLedger"/> and scopes every query to its ledger id.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TradeDerivedPricesTests
{
    private readonly PostgresFixture _fixture;

    public TradeDerivedPricesTests(PostgresFixture fixture)
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

    private async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        decimal shares, decimal price, DateTime postedAt)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = shares,
                Price = price,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    private async Task<List<SecurityPriceRow>> PriceRowsAsync(SyntheticLedger ledger, Guid securityId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == securityId)
            .ToListAsync();
    }

    [Fact]
    public async Task Buy_through_the_write_path_seeds_a_trade_source_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        // unit_price = amount / |shares| = round(10 * 100, 2) / 10 = 100 (ADR-0073).
        Assert.Equal(100m, row.Price);
        Assert.Equal(new DateOnly(2026, 5, 15), row.PriceDate);
    }

    [Fact]
    public async Task Manual_write_overwrites_a_trade_price_for_the_same_day()
    {
        // Rank gate (ADR-0084 D1): manual outranks trade, so a later manual price
        // reclaims the day — a Yahoo close would do the same via the feed.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var manual = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices",
            new { price = 123.45m, priceDate = new DateOnly(2026, 5, 15) });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);   // still one row per (security, day)
        Assert.Equal(PriceSource.Manual, row.Source);
        Assert.Equal(123.45m, row.Price);
    }

    [Fact]
    public async Task Trade_does_not_clobber_a_pre_existing_manual_price()
    {
        // The reverse direction: a manual price already owns the day; a same-day
        // trade must NOT overwrite it (the function's DO UPDATE ... WHERE gate
        // excludes manual/fetch rows).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        var day = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        await ledger.AddSecurityPriceAsync(securityId, 999.99m, day, source: PriceSource.Manual);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Manual, row.Source);
        Assert.Equal(999.99m, row.Price);
    }

    [Fact]
    public async Task Trade_overwrites_a_pre_existing_import_price()
    {
        // A trade is a truer observation than the one-time import seed (ADR-0084
        // D5): the same-day import row is replaced by the execution price.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        var day = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        await ledger.AddSecurityPriceAsync(securityId, 50m, day, source: PriceSource.Import);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        Assert.Equal(100m, row.Price);
    }

    [Fact]
    public async Task Dividend_cash_writes_no_trade_price_row()
    {
        // A priceless leg (dividend_cash carries pamt = 0 -> unit_price 0/NULL) is
        // not a trade and seeds nothing (ADR-0084 D2).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var income = await ledger.AddCategoryAsync("Dividends", kind: "income");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc),
                Action = "dividend_cash",
                SecurityId = securityId,
                CategoryAccountId = income.Id,
                Amount = 42m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var rows = await PriceRowsAsync(ledger, securityId);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Trade_price_date_is_the_utc_calendar_day_of_posted_at()
    {
        // A late-UTC-day trade still lands on that UTC date (ADR-0084 D3): the
        // interceptor normalizes posted_at to UTC before deriving the day.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 23, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 5m, price: 200m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        Assert.Equal(new DateOnly(2026, 5, 15), row.PriceDate);
    }
}
