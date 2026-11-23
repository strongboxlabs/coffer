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
public sealed class ReportSummaryEndpointTests : DatabaseTest
{
    public ReportSummaryEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 120m, May);
        await ledger.AddTransactionPairAsync(dining.Id, bank.Id, 45m, May);

        await using var factory = new ApiFactory(Db);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var txn = await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 80m, May);
        // A one-split's tags live on a leg, never on its header: here the bank's.
        await ledger.AddLegTagAsync(txn.ToTxnId, "Holiday");

        await using var factory = new ApiFactory(Db);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");
        var now = DateTime.UtcNow;
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 25m, now.AddMonths(-1));
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 400m, now.AddMonths(-9));

        await using var factory = new ApiFactory(Db);
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A rolled-up parent with postings of its own and one child, beside a smaller
    /// category with no children, all in one month.
    /// </summary>
    private static async Task SeedParentChildAndOtherAsync(SyntheticLedger ledger)
    {
        var bank = await ledger.AddBankAccountAsync("Account A");
        var parent = await ledger.AddCategoryAsync("Category A");
        var child = await ledger.AddCategoryAsync("Category A1", parentId: parent.Id);
        var other = await ledger.AddCategoryAsync("Category B");
        await ledger.AddTransactionPairAsync(parent.Id, bank.Id, 30m, May);
        await ledger.AddTransactionPairAsync(child.Id, bank.Id, 70m, May);
        await ledger.AddTransactionPairAsync(other.Id, bank.Id, 10m, May);
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task Without_top_n_the_response_has_the_shape_it_had_before_the_rest()
    {
        // A report with no cut is read as it always was: no rest, and rows that carry
        // neither drawn nor isDirect, in the order the SPA and saved callers know. Each row
        // names the currency its amount is in, after the amount (ADR-0114 D7), and then
        // its full name (ADR-0104 amendment 2026-11-20).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedParentChildAndOtherAsync(ledger);

        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        foreach (var rollup in new[] { "false", "true" })
        {
            var body = await client.GetFromJsonAsync<JsonElement>(
                Url(ledger.LedgerId, $"measure=Spending&groupBy=Category&bucket=Month&rollup={rollup}"));

            Assert.Equal(
                ["rows", "total", "fromUtc", "toUtc", "periodTotals", "groupTotals", "currencyCode"],
                PropertyNames(body));
            var rows = body.GetProperty("rows").EnumerateArray().ToList();
            Assert.All(rows, row => Assert.Equal(
                ["period", "groupId", "groupName", "parentId", "amount", "currencyCode", "groupPath"], PropertyNames(row)));

            // The child is named by its whole path on the wire, its parent first.
            var child = Assert.Single(rows, row => row.GetProperty("groupName").GetString() == "Category A1");
            Assert.Equal(
                ["Category A", "Category A1"],
                child.GetProperty("groupPath").EnumerateArray().Select(n => n.GetString()!).ToArray());
        }
    }

    [Fact]
    public async Task With_top_n_every_row_says_whether_it_is_drawn_and_the_rest_is_on_the_wire()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await SeedParentChildAndOtherAsync(ledger);

        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        // Rolled up, a cut of two keeps the parent and its child; the parent is drawn
        // by the child and by a row of its own postings, and the other category is the rest.
        var body = await client.GetFromJsonAsync<JsonElement>(
            Url(ledger.LedgerId, "measure=Spending&groupBy=Category&bucket=Month&rollup=true&topN=2"));

        // A rest of one series names it.
        var rest = body.GetProperty("rest");
        Assert.Equal(["count", "total", "byPeriod", "only"], PropertyNames(rest));
        Assert.Equal(1, rest.GetProperty("count").GetInt32());
        var only = rest.GetProperty("only");
        Assert.Equal(["groupId", "groupName", "isDirect", "groupPath"], PropertyNames(only));
        Assert.Equal("Category B", only.GetProperty("groupName").GetString());
        Assert.Equal(["Category B"], only.GetProperty("groupPath").EnumerateArray().Select(n => n.GetString()!).ToArray());
        Assert.False(only.GetProperty("isDirect").GetBoolean());
        var period = Assert.Single(rest.GetProperty("byPeriod").EnumerateArray());
        Assert.Equal(["period", "amount"], PropertyNames(period));
        Assert.Equal(rest.GetProperty("total").GetDecimal(), period.GetProperty("amount").GetDecimal());

        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        // Every row says whether it is drawn: true or false, never absent or null.
        Assert.All(rows, row => Assert.Contains(
            row.GetProperty("drawn").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False }));

        // isDirect appears on the parent's own-postings row only, and is true there.
        var direct = Assert.Single(rows, row => row.TryGetProperty("isDirect", out _));
        Assert.True(direct.GetProperty("isDirect").GetBoolean());
        Assert.True(direct.GetProperty("drawn").GetBoolean());
        Assert.Equal(30m, direct.GetProperty("amount").GetDecimal());

        // For the period, the drawn rows and the rest come to the period total.
        var drawn = rows.Where(row => row.GetProperty("drawn").GetBoolean())
            .Sum(row => row.GetProperty("amount").GetDecimal());
        var periodTotal = Assert.Single(body.GetProperty("periodTotals").EnumerateArray());
        Assert.Equal(periodTotal.GetProperty("amount").GetDecimal(), drawn + period.GetProperty("amount").GetDecimal());

        // A cut that keeps every category leaves an empty rest, which names nothing.
        var everything = await client.GetFromJsonAsync<JsonElement>(
            Url(ledger.LedgerId, "measure=Spending&groupBy=Category&bucket=Month&rollup=true&topN=3"));
        var empty = everything.GetProperty("rest");
        Assert.Equal(0, empty.GetProperty("count").GetInt32());
        Assert.Equal(["count", "total", "byPeriod"], PropertyNames(empty));
    }

    [Fact]
    public async Task A_daily_bucket_on_a_non_category_dimension_is_a_400_not_a_500()
    {
        // The repository throws for this on purpose: honouring it would return
        // MONTHLY buckets labelled as days. Unhandled it would surface as a
        // 500, which reads as a server fault rather than a bad request.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "bucket=Day&groupBy=Payee"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
