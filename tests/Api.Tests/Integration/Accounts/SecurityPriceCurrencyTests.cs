using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// A hand-entered price names its currency (ADR-0114 D5): the one given, else the security's
/// latest price's, else its holding accounts' when they agree, else the add is refused.
/// Never an assumed USD. The prices page carries the same default for the editor to prefill.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityPriceCurrencyTests : DatabaseTest
{
    public SecurityPriceCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly Day = new(2026, 5, 1);

    [Fact]
    public async Task A_given_currency_is_stored_upper_cased()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day, currencyCode = " eur " });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("EUR", await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task An_omitted_currency_takes_the_latest_prices_currency()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        await ledger.AddSecurityPriceAsync(securityId, 9m,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), source: PriceSource.Fetch, currencyCode: "JPY");
        await ledger.AddSecurityPriceAsync(securityId, 9m,
            new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc), source: PriceSource.Fetch, currencyCode: "CHF");

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("CHF", await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task With_no_price_an_omitted_currency_takes_the_holding_accounts_currency()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        await HoldInAsync(ledger, securityId, "CAD");
        await HoldInAsync(ledger, securityId, "CAD");

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("CAD", await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task With_no_price_and_holding_accounts_that_disagree_an_omitted_currency_is_refused()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        await HoldInAsync(ledger, securityId, "CAD");
        await HoldInAsync(ledger, securityId, "GBP");

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day });

        await AssertRefusedAsync(resp, "security-price-currency-required");
        Assert.Null(await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task With_no_price_and_no_holding_an_omitted_currency_is_refused()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day, currencyCode = "  " });

        await AssertRefusedAsync(resp, "security-price-currency-required");
    }

    [Fact]
    public async Task A_sold_out_holding_does_not_settle_the_currency()
    {
        // "Holding" is a non-zero quantity: an account that once held it says nothing now.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        await HoldInAsync(ledger, securityId, "CAD", quantity: 0m);

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day });

        await AssertRefusedAsync(resp, "security-price-currency-required");
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("E")]
    public async Task A_currency_that_is_not_three_characters_is_refused(string currency)
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);

        var resp = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day, currencyCode = currency });

        await AssertRefusedAsync(resp, "security-price-currency-invalid");
        Assert.Null(await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task An_edit_validates_and_normalizes_the_currency()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        var created = await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day, currencyCode = "USD" });
        var priceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priceId").GetGuid();
        var path = $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices/{priceId}";

        var bad = await client.PatchAsJsonAsync(path, new { currencyCode = "EURO" });
        await AssertRefusedAsync(bad, "security-price-currency-invalid");
        Assert.Equal("USD", await StoredCurrencyAsync(securityId, Day));

        var good = await client.PatchAsJsonAsync(path, new { currencyCode = "gbp" });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        Assert.Equal("GBP", await StoredCurrencyAsync(securityId, Day));
    }

    [Fact]
    public async Task The_prices_page_carries_the_default_the_add_would_use()
    {
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        var (ledger, client, securityId) = await SetupAsync(factory);
        var path = $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices";

        var none = (await client.GetFromJsonAsync<SecurityPricesPage>(path))!;
        Assert.Null(none.DefaultCurrencyCode);

        await HoldInAsync(ledger, securityId, "SEK");
        var fromHolding = (await client.GetFromJsonAsync<SecurityPricesPage>(path))!;
        Assert.Equal("SEK", fromHolding.DefaultCurrencyCode);

        await PostAsync(client, ledger, securityId, new { price = 10m, priceDate = Day, currencyCode = "NOK" });
        var fromPrice = (await client.GetFromJsonAsync<SecurityPricesPage>(path))!;
        Assert.Equal("NOK", fromPrice.DefaultCurrencyCode);
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<(SyntheticLedger Ledger, HttpClient Client, Guid SecurityId)> SetupAsync(ApiFactory factory)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Index Fund A", ticker: "IDXA");
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return (ledger, client, securityId);
    }

    private static async Task HoldInAsync(SyntheticLedger ledger, Guid securityId, string currency, decimal quantity = 5m)
    {
        var brokerage = await ledger.AddInvestmentAccountAsync($"Brokerage {Guid.NewGuid():N}", currencyCode: currency);
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, quantity, 50m);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, SyntheticLedger ledger, Guid securityId, object body) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices", body);

    private async Task<string?> StoredCurrencyAsync(Guid securityId, DateOnly day)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId && p.PriceDate == day)
            .Select(p => p.CurrencyCode)
            .SingleOrDefaultAsync();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
