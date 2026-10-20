using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>GET /api/ledgers/{id}/reports/summary</c> — the SPA's way into the
/// aggregation layer (FU-008, ADR-0104).
/// </summary>
/// <remarks>
/// A GET with query parameters rather than a POST with a spec body: every
/// <c>ReportSpec</c> field is a scalar, so a report stays a URL you can link
/// and reload. These tests are therefore also about the PARSING — the spec
/// arrives as a dozen strings, and a silently-defaulted field would produce a
/// plausible report answering a question nobody asked.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReportSummaryEndpointTests
{
    private readonly PostgresFixture _fixture;

    public ReportSummaryEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string Url(Guid ledgerId, string query) =>
        $"/api/ledgers/{ledgerId}/reports/summary?{query}";

    [Fact]
    public async Task It_aggregates_spending_by_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 120m, May);
        await ledger.AddTransactionPairAsync(dining.Id, bank.Id, 45m, May);

        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "measure=Spending&groupBy=Category"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(165m, body.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(2, body.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task The_tag_dimension_is_reachable_from_the_query_string()
    {
        // Tag is the dimension this release added, and a report surface that
        // cannot name it leaves Tag Summary unbuildable from the SPA.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var txn = await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 80m, May);
        await ledger.AddTagAsync(txn.FromTxnId, "Holiday");

        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var body = await client.GetFromJsonAsync<JsonElement>(
            Url(ledger.LedgerId, "measure=Spending&groupBy=Tag"));

        var names = body.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("groupName").GetString()).ToList();
        Assert.Contains("Holiday", names);
    }

    [Fact]
    public async Task A_sliding_window_is_honoured_through_the_query_string()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var now = DateTime.UtcNow;
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 25m, now.AddMonths(-1));
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 400m, now.AddMonths(-9));

        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var body = await client.GetFromJsonAsync<JsonElement>(
            Url(ledger.LedgerId, "dateMode=Sliding&windowCount=3&windowUnit=Month"));

        // Only the recent spend, not both — the window reached the repository rather than being
        // dropped somewhere in the parse.
        Assert.Equal(25m, body.GetProperty("total").GetDecimal());
    }

    [Theory]
    [InlineData("measure=Sideways")]
    [InlineData("groupBy=Colour")]
    [InlineData("bucket=Fortnight")]
    [InlineData("windowCount=lots")]
    [InlineData("from=whenever")]
    public async Task A_parameter_it_cannot_read_is_refused_rather_than_defaulted(string query)
    {
        // The alternative — fall back to the default — returns a confident,
        // plausible report answering a question the caller did not ask, and
        // nothing on the page would say so.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_daily_bucket_on_a_non_category_dimension_is_a_400_not_a_500()
    {
        // The repository throws for this on purpose: honouring it would return
        // MONTHLY buckets labelled as days. Unhandled it would surface as a
        // 500, which reads as a server fault rather than a bad request.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "bucket=Day&groupBy=Payee"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
