using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>GET /api/ledgers/{id}/reports/returns-series</c> (ADR-0104 D6): the parsing, the
/// refusals, the sliding window and the wire shape. The figures are the engine's, pinned in
/// <c>ReturnsSeriesTests</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnsSeriesEndpointTests : DatabaseTest
{
    public ReturnsSeriesEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string Url(Guid ledgerId, string query = "") =>
        $"/api/ledgers/{ledgerId}/reports/returns-series{(query.Length == 0 ? "" : "?" + query)}";

    private async Task<(SyntheticLedger Ledger, Guid BrokerageId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 2, 14));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, brokerage.HoldingsAccountId!.Value, security, 100m, 50m, Utc(2024, 2, 14));
        await ledger.AddSecurityPriceAsync(security, 55m, Utc(2024, 3, 15));
        await ledger.AddSecurityPriceAsync(security, 60m, Utc(2024, 11, 15));
        return (ledger, brokerage.Id);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task It_answers_since_inception_in_camel_case_with_no_parameters()
    {
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var body = await BodyAsync(await client.GetAsync(Url(ledger.LedgerId, "today=2024-12-15")));

        Assert.Equal("month", body.GetProperty("interval").GetString());
        Assert.True(body.TryGetProperty("from", out var from));
        Assert.Equal(2024, from.GetDateTime().Year);
        Assert.Equal(2, from.GetDateTime().Month);
        Assert.True(body.TryGetProperty("to", out _));
        Assert.True(body.TryGetProperty("computedAt", out _));
        Assert.True(body.TryGetProperty("engineVersion", out _));

        var series = Assert.Single(body.GetProperty("series").EnumerateArray().ToList());
        Assert.Equal("ledger", series.GetProperty("scope").GetString());
        Assert.False(series.TryGetProperty("mixedCurrency", out _));   // ADR-0114 D7: the marker is gone
        Assert.True(series.TryGetProperty("currencyCode", out _));

        var points = series.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(11, points.Count);   // February to December 2024
        var last = points[^1];
        foreach (var name in new[]
                 {
                     "periodEnd", "index", "indexUnavailableReason", "periodReturn",
                     "periodReturnUnavailableReason", "coveredDays", "gain", "gainUnavailableReason",
                     "mwrTrailingYear", "mwrUnavailableReason",
                 })
            Assert.True(last.TryGetProperty(name, out _), $"missing {name}");
        // The year ending here opened before the February funding: no money-weighted
        // figure, and the reason says why.
        Assert.Equal(JsonValueKind.Null, last.GetProperty("mwrTrailingYear").ValueKind);
        Assert.Equal("under_a_year_covered", last.GetProperty("mwrUnavailableReason").GetString());
        Assert.Equal(JsonValueKind.Number, last.GetProperty("index").ValueKind);
    }

    [Fact]
    public async Task Interval_accountId_and_include_accounts_are_honoured()
    {
        var (ledger, brokerageId) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var quarterly = await BodyAsync(await client.GetAsync(
            Url(ledger.LedgerId, "interval=Quarter&includeAccounts=true&today=2024-12-15")));
        Assert.Equal("quarter", quarterly.GetProperty("interval").GetString());
        var lines = quarterly.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(brokerageId, lines[1].GetProperty("accountId").GetGuid());

        var one = await BodyAsync(await client.GetAsync(
            Url(ledger.LedgerId, $"accountId={brokerageId}&today=2024-12-15")));
        var only = Assert.Single(one.GetProperty("series").EnumerateArray().ToList());
        Assert.Equal("account", only.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task A_sliding_window_resolves_against_the_callers_day()
    {
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var body = await BodyAsync(await client.GetAsync(Url(ledger.LedgerId,
            "dateMode=Sliding&windowCount=3&windowUnit=Month&windowAlign=true&today=2024-12-15")));

        Assert.Equal(Utc(2024, 10, 1), body.GetProperty("from").GetDateTime().ToUniversalTime());
        var points = Assert.Single(body.GetProperty("series").EnumerateArray().ToList())
            .GetProperty("points").EnumerateArray().ToList();
        // October and November whole, December up to the caller's today.
        Assert.Equal(3, points.Count);
        Assert.Equal(2024, points[^1].GetProperty("periodEnd").GetDateTime().Year);
        Assert.Equal(15, points[^1].GetProperty("periodEnd").GetDateTime().Day);
    }

    [Fact]
    public async Task An_explicit_window_bounds_the_series()
    {
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var body = await BodyAsync(await client.GetAsync(Url(ledger.LedgerId,
            "from=2024-03-01T00:00:00Z&to=2024-06-30T23:59:59Z&today=2024-12-15")));

        var points = Assert.Single(body.GetProperty("series").EnumerateArray().ToList())
            .GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(4, points.Count);
    }

    [Theory]
    [InlineData("interval=Day")]               // refused by the engine, not answered in months
    [InlineData("interval=None")]
    [InlineData("interval=Fortnightly")]
    [InlineData("accountId=not-a-guid")]
    [InlineData("from=not-a-date")]
    [InlineData("to=not-a-date")]
    [InlineData("today=15-12-2024")]
    [InlineData("dateMode=Rolling")]
    [InlineData("windowUnit=Fortnight")]
    [InlineData("windowCount=three")]
    [InlineData("dateMode=Sliding")]           // a sliding window with no count
    [InlineData("dateMode=Sliding&windowCount=0")]
    [InlineData("from=1990-01-01T00:00:00Z&to=2050-01-01T00:00:00Z&interval=Month&today=2050-01-01")]
    public async Task A_bad_request_is_a_400_and_not_an_answer_to_another_question(string query)
    {
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_accountId_that_is_not_one_of_the_ledgers_brokerages_is_a_400_and_not_an_empty_series()
    {
        // The scalar report refuses the same ids (ReturnsEndpointTests), so the two agree:
        // unknown, another ledger's, a bank account and a holdings sibling are refused,
        // and the ledger's own brokerage answers (the positive control).
        var (ledger, brokerageId) = await SeedAsync();
        var bank = await ledger.AddBankAccountAsync("Checking");
        var sibling = (await Db.NewDbContext().Accounts.FindAsync(brokerageId))!.HoldingsAccountId!.Value;
        var (_, otherBrokerage) = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var own = await client.GetAsync(Url(ledger.LedgerId, $"accountId={brokerageId}&today=2024-12-15"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        foreach (var id in new[] { Guid.NewGuid(), bank.Id, sibling, otherBrokerage })
        {
            var response = await client.GetAsync(Url(ledger.LedgerId, $"accountId={id}&today=2024-12-15"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("investment account of this ledger", text, StringComparison.Ordinal);
            Assert.DoesNotContain(otherBrokerage.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_daily_interval_names_what_is_accepted()
    {
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "interval=Day"));

        Assert.Contains("month, quarter or year", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ledger_with_no_investments_is_a_200_with_no_index()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var body = await BodyAsync(await client.GetAsync(Url(ledger.LedgerId, "today=2024-12-15")));

        var series = Assert.Single(body.GetProperty("series").EnumerateArray().ToList());
        foreach (var point in series.GetProperty("points").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, point.GetProperty("index").ValueKind);
            Assert.Equal(JsonValueKind.String, point.GetProperty("indexUnavailableReason").ValueKind);
        }
    }

    [Fact]
    public async Task Another_ledgers_series_is_not_readable()
    {
        var (alice, _) = await SeedAsync();
        var (bob, bobBrokerage) = await SeedAsync();
        // The session cookie is the identity here; dev auth would stand in for a user
        // who can read every ledger.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, alice);

        // Positive control: the caller's own ledger answers with a series.
        var own = await BodyAsync(await client.GetAsync(Url(alice.LedgerId, "includeAccounts=true&today=2024-12-15")));
        Assert.NotEmpty(own.GetProperty("series").EnumerateArray());

        var cross = await client.GetAsync(Url(bob.LedgerId, "includeAccounts=true&today=2024-12-15"));

        // Empty, not an error and not bob's data: RLS shows no brokerage, so the ledger
        // line carries no index and no account line exists.
        var text = await cross.Content.ReadAsStringAsync();
        Assert.DoesNotContain(bobBrokerage.ToString(), text, StringComparison.OrdinalIgnoreCase);
        if (cross.StatusCode == HttpStatusCode.OK)
        {
            var line = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("series").EnumerateArray().ToList());
            foreach (var point in line.GetProperty("points").EnumerateArray())
                Assert.Equal(JsonValueKind.Null, point.GetProperty("index").ValueKind);
        }
    }
}
