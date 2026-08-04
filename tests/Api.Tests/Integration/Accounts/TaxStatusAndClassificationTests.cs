using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// Account tax_status (ADR-0066) + rich security classification (ADR-0067) edit
/// round-trips through the API: valid values persist + read back; an invalid
/// tax_status is rejected with the stable 422 code.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TaxStatusAndClassificationTests
{
    private readonly PostgresFixture _fixture;

    public TaxStatusAndClassificationTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Account_update_sets_and_returns_tax_status()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Retirement");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}",
            new UpdateAccountRequest
            {
                Name = "Retirement",
                CurrencyCode = "USD",
                IsActive = true,
                TaxStatus = "tax_deferred",
            });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("tax_deferred", doc.RootElement.GetProperty("taxStatus").GetString());
    }

    [Fact]
    public async Task Account_update_rejects_invalid_tax_status()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Retirement");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage.Id}",
            new UpdateAccountRequest
            {
                Name = "Retirement",
                CurrencyCode = "USD",
                IsActive = true,
                TaxStatus = "roth_ish",   // not in the enum
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        Assert.Equal("account-tax-status-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Security_patch_sets_and_returns_classification_dimensions()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var sec = await ledger.AddSecurityAsync(name: "Large Cap Index", ticker: "LCI");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}",
            new PatchSecurityRequest
            {
                AssetClass = "equity",
                VehicleType = "etf",
                Region = "us",
                EquitySize = "large",
                EquityStyle = "blend",
                TaxCharacter = "taxable",
            });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var get = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{sec}");
        var detail = await get.Content.ReadFromJsonAsync<SecurityDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal("equity", detail!.AssetClass);
        Assert.Equal("etf", detail.VehicleType);
        Assert.Equal("us", detail.Region);
        Assert.Equal("large", detail.EquitySize);
        Assert.Equal("blend", detail.EquityStyle);
        Assert.Equal("taxable", detail.TaxCharacter);
        // Any classification edit marks the row manually curated (ADR-0067 D5).
        Assert.Equal("manual", detail.ClassificationSource);
    }
}
