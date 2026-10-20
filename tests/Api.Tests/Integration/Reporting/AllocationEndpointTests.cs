using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>GET /api/ledgers/{id}/reports/allocation</c> (FU-008).
/// </summary>
/// <remarks>
/// <para>The composition shape, and the only report where a proportional chart
/// is honest — shares of a whole cannot go negative, where every measure on
/// <c>/summary</c> can.</para>
///
/// <para>Most of what is asserted here is the HONESTY of the payload rather
/// than its arithmetic, which the repository's own tests already cover. A pie
/// chart is the most confidently-wrong thing a report can draw, and this
/// response carries the two fields that say when not to trust it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AllocationEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AllocationEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime Buy = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Priced = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string Url(Guid ledgerId, string query = "") =>
        $"/api/ledgers/{ledgerId}/reports/allocation{(query.Length == 0 ? "" : "?" + query)}";

    /// <summary>A brokerage holding one equity and one bond, 75/25 by value.</summary>
    private async Task<SyntheticLedger> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;

        var equity = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        var bond = await ledger.AddSecurityAsync("BNDB", "BNDB", assetClass: "fixed_income");
        // Bought rather than injected: allocation values through the same as-of
        // feeder returns uses, which replays LEGS, so a position with no
        // transaction behind it is a state production cannot reach.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, equity, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(equity, 150m, Priced);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, bond, 5m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(bond, 100m, Priced);
        return ledger;
    }

    [Fact]
    public async Task It_buckets_by_asset_class_by_default()
    {
        var ledger = await SeedAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2000m, body.RootElement.GetProperty("totalMarketValue").GetDecimal());

        var buckets = body.RootElement.GetProperty("buckets").EnumerateArray().ToList();
        var equity = buckets.Single(b => b.GetProperty("bucket").GetString() == "equity");
        Assert.Equal(75m, equity.GetProperty("percent").GetDecimal());
    }

    [Fact]
    public async Task It_carries_the_two_fields_that_say_when_not_to_draw_a_pie()
    {
        // `undecomposedMultiAssets` and `excludedBrokerageCash` are the whole
        // reason this passes the repository result through rather than
        // reducing it to buckets. A fund with no component weights sits
        // entirely in one opaque bucket and understates every other one — the
        // DTO records a real case, equity shown at a small fraction of its true
        // share — and totalMarketValue is SECURITIES ONLY, so uninvested cash is
        // missing from any chart that treats the buckets as the portfolio.
        var ledger = await SeedAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync(Url(ledger.LedgerId))).Content.ReadAsStringAsync());

        Assert.True(body.RootElement.TryGetProperty("undecomposedMultiAssets", out var undecomposed));
        Assert.Equal(JsonValueKind.Array, undecomposed.ValueKind);
        Assert.True(body.RootElement.TryGetProperty("excludedBrokerageCash", out _));
        // Provenance too: a chart is a claim about an instant, so the response
        // has to say which instant and which engine produced it.
        Assert.True(body.RootElement.TryGetProperty("asOf", out _));
        Assert.True(body.RootElement.TryGetProperty("engineVersion", out _));
    }

    [Theory]
    [InlineData("asset_class")]
    [InlineData("assetClass")]
    [InlineData("region")]
    [InlineData("vehicle")]
    [InlineData("vehicle_type")]
    [InlineData("security")]
    [InlineData("account")]
    public async Task It_accepts_the_MCP_tool_spellings(string dimension)
    {
        // One vocabulary for both callers. Without this the SPA would need the
        // PascalCase enum names while the LLM uses snake_case, and the two
        // would drift the first time a dimension is added.
        var ledger = await SeedAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, $"dimension={dimension}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_misspelled_dimension_is_refused_rather_than_defaulted()
    {
        // ADR-0063 D4. Falling back to asset_class would answer a question
        // nobody asked and label it as the one they did.
        var ledger = await SeedAsync();
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "dimension=assetclas"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("asset_class, region, vehicle, security or account",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
