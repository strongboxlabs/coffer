using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>GET /api/ledgers/{id}/reports/net-worth-history</c> (FU-008).
/// </summary>
/// <remarks>
/// <para>The first of the three surfaces <see cref="Coffer.Api.Db.Repositories.AccountsReportingRepository"/>
/// and its investment counterpart were missing. The repository has been there
/// since migration 172 and only the MCP server could reach it, which is why
/// the report catalog lists no net worth: a card that 404s blames the reader's
/// ledger.</para>
///
/// <para>These are about the PARSING and the refusals as much as the figures.
/// The window is required here where <c>/summary</c>'s is optional, and a
/// daily interval is a 400 rather than a plausible monthly answer.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class NetWorthHistoryEndpointTests
{
    private readonly PostgresFixture _fixture;

    public NetWorthHistoryEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string Url(Guid ledgerId, string query) =>
        $"/api/ledgers/{ledgerId}/reports/net-worth-history?{query}";

    private const string Window = "from=2026-01-01T00:00:00Z&to=2026-04-01T00:00:00Z";

    [Fact]
    public async Task It_returns_a_point_per_period_with_the_opening_balance_carried()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        // Untouched on purpose: a net-worth series has to value an account
        // from its opening balance when no posting has ever moved it, which is
        // the same property UntouchedAccountTests pins one layer down.
        await ledger.AddBankAccountAsync("Savings", openingBalance: 2_500m);

        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, $"{Window}&interval=Month"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var points = body.RootElement.GetProperty("points");
        Assert.True(points.GetArrayLength() >= 3, "expected a point per month in the window");
        foreach (var p in points.EnumerateArray())
            Assert.Equal(2_500m, p.GetProperty("netWorth").GetDecimal());
    }

    [Fact]
    public async Task A_daily_interval_is_refused_rather_than_answered_in_months()
    {
        // THE ASSERTION THIS FILE EXISTS FOR. Day has no arm in the period
        // walker, so it fell through to the month default and the caller got
        // monthly points under a daily label, with nothing in the response
        // saying so. A 400 is the honest answer; the quiet one was months.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, $"{Window}&interval=Day"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("month, quarter or year", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("to=2026-04-01T00:00:00Z")]                       // no from
    [InlineData("from=2026-01-01T00:00:00Z")]                     // no to
    public async Task The_window_is_required(string query)
    {
        // /summary treats an absent window as "everything", which is a real
        // report. An unbounded net-worth SERIES would have to invent its own
        // bounds, and the point cap over an invented range is a coin toss.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, query));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_range_past_the_point_cap_is_a_400_and_not_a_500()
    {
        // The repository throws for its own good reasons; an unhandled throw
        // here would present a caller's over-wide range as a server fault.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId,
            "from=1990-01-01T00:00:00Z&to=2050-01-01T00:00:00Z&interval=Month"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_interval_names_what_is_accepted()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, $"{Window}&interval=Fortnightly"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Month, Quarter or Year", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}
