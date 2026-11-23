using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// <c>GET /api/ledgers/{id}/reports/returns</c> (FU-008) — the last of the
/// three surfaces.
/// </summary>
/// <remarks>
/// <para><b>A number, not a curve.</b> The result is TWR and MWR for one
/// window. A performance line over time is a different calculation with its
/// own endpoint (<c>/reports/returns-series</c>), and these tests pin that
/// this surface says so honestly instead of implying a line from two
/// points.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsEndpointTests : DatabaseTest
{
    public ReturnsEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Buy = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Priced = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowFrom = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Contributed = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static string Url(Guid ledgerId, string query = "") =>
        $"/api/ledgers/{ledgerId}/reports/returns{(query.Length == 0 ? "" : "?" + query)}";

    private async Task<(SyntheticLedger Ledger, Guid BrokerageId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var equity = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, equity, 10m, 100m, Buy);
        await ledger.AddSecurityPriceAsync(equity, 150m, Priced);
        return (ledger, brokerage.Id);
    }

    [Fact]
    public async Task It_answers_since_inception_when_no_window_is_given()
    {
        // Both dates optional here, unlike net-worth history. Omitted is not
        // an invented range: the scope resolver derives the start from the
        // ledger's own first flow, so "since inception" is exact.
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger", body.RootElement.GetProperty("scope").GetString());
        Assert.True(body.RootElement.TryGetProperty("startDate", out _));
        Assert.True(body.RootElement.TryGetProperty("endDate", out _));
    }

    [Fact]
    public async Task An_unavailable_rate_comes_back_with_its_reason_beside_it()
    {
        // THE ASSERTION THIS FILE EXISTS FOR. Either rate can be null, and the
        // response says why. A caller that read only the rate would plot a
        // zero where the honest answer is "not computable" — which on a chart
        // is a flat line through a period that had no valuations at all.
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync(Url(ledger.LedgerId))).Content.ReadAsStringAsync());

        Assert.True(body.RootElement.TryGetProperty("timeWeightedReturn", out var twr));
        Assert.True(body.RootElement.TryGetProperty("timeWeightedUnavailableReason", out var twrWhy));
        // Exactly one of the pair is present: a rate without a reason, or a
        // reason without a rate. Both null would be a silent failure and both
        // set would be a contradiction.
        Assert.True(
            (twr.ValueKind is JsonValueKind.Number) ^ (twrWhy.ValueKind is JsonValueKind.String),
            "expected either a time-weighted rate or a reason it is unavailable, not both or neither");
    }

    [Fact]
    public async Task Net_contributions_carries_both_halves_and_its_split()
    {
        // netContributions is a NET and can hide large offsetting movements,
        // so the surface must carry the parts. in + out == net exactly.
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync(Url(ledger.LedgerId))).Content.ReadAsStringAsync());

        var net = body.RootElement.GetProperty("netContributions").GetDecimal();
        var cin = body.RootElement.GetProperty("contributionsIn").GetDecimal();
        var cout = body.RootElement.GetProperty("contributionsOut").GetDecimal();
        Assert.Equal(net, cin + cout);
    }

    [Fact]
    public async Task Gain_is_on_the_wire_and_is_what_the_contributions_do_not_explain()
    {
        // Its own seed, with money moving in inside the window: on a seed with no
        // contribution the identity holds whichever way the net is applied. Read as
        // raw JSON, so the figure checked is the one the server sent.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var equity = await ledger.AddSecurityAsync("IDXC", "IDXC", assetClass: "equity");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        async Task PostAsync(string path, object body) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/{path}", body)).StatusCode);
        CreateTransactionRequest Contribution(decimal amount, DateTime at) => new()
        {
            PostedAt = at,
            SourceAccountId = bank.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = brokerage.Id, Amount = -amount }],
        };

        await PostAsync("transactions", Contribution(1_000m, Buy));
        await PostAsync("investment-transactions", new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = equity,
            Shares = 10m, Price = 100m, PostedAt = Buy,
        });
        await PostAsync("transactions", Contribution(500m, Contributed));
        await ledger.AddSecurityPriceAsync(equity, 150m, Priced);

        using var body = JsonDocument.Parse(await (await client.GetAsync(
                Url(ledger.LedgerId, $"from={WindowFrom:yyyy-MM-ddTHH:mm:ssZ}&today=2026-12-31")))
            .Content.ReadAsStringAsync());

        var start = body.RootElement.GetProperty("startValue").GetDecimal();
        var end = body.RootElement.GetProperty("endValue").GetDecimal();
        var net = body.RootElement.GetProperty("netContributions").GetDecimal();
        // The anchor: money did move inside the window.
        Assert.NotEqual(0m, net);
        Assert.True(body.RootElement.TryGetProperty("gain", out var gain));
        Assert.Equal(JsonValueKind.Number, gain.ValueKind);
        Assert.Equal(end - start - net, gain.GetDecimal());
    }

    [Fact]
    public async Task Ledger_scope_lists_every_brokerage_the_window_covered()
    {
        // Picking accounts by CURRENT balance silently drops any that held
        // money when the window opened and was emptied before it closed, such
        // as an account whose holdings were transferred out mid-window.
        var (ledger, brokerageId) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync(Url(ledger.LedgerId))).Content.ReadAsStringAsync());

        var accounts = body.RootElement.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Contains(accounts, a => a.GetProperty("accountId").GetGuid() == brokerageId);
    }

    [Fact]
    public async Task One_brokerage_narrows_the_scope()
    {
        var (ledger, brokerageId) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        using var body = JsonDocument.Parse(
            await (await client.GetAsync(Url(ledger.LedgerId, $"accountId={brokerageId}")))
                .Content.ReadAsStringAsync());

        Assert.Equal("account", body.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task A_malformed_accountId_is_a_400_and_not_a_whole_ledger_report()
    {
        // Falling back to ledger scope on an unparseable id would answer a
        // broader question than the caller asked and label it as theirs.
        var (ledger, _) = await SeedAsync();
        await using var factory = new ApiFactory(Db);
        using var client = await ClientAsync(factory, ledger);

        var response = await client.GetAsync(Url(ledger.LedgerId, "accountId=not-a-guid"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_accountId_that_is_not_one_of_the_ledgers_brokerages_is_a_400_and_not_an_empty_report()
    {
        // A silent empty scope reads as "nothing invested" and hides a mistyped or stale
        // id. Unknown, another ledger's, a bank account and a holdings sibling are all
        // refused; the ledger's own brokerage answers (the positive control).
        var (ledger, brokerageId) = await SeedAsync();
        var bank = await ledger.AddBankAccountAsync("Checking");
        var sibling = (await Db.NewDbContext().Accounts.FindAsync(brokerageId))!.HoldingsAccountId!.Value;
        var (other, otherBrokerage) = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var own = await client.GetAsync(Url(ledger.LedgerId, $"accountId={brokerageId}"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        foreach (var id in new[] { Guid.NewGuid(), bank.Id, sibling, otherBrokerage })
        {
            var response = await client.GetAsync(Url(ledger.LedgerId, $"accountId={id}"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("investment account of this ledger", text, StringComparison.Ordinal);
            Assert.DoesNotContain(otherBrokerage.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.NotEqual(other.LedgerId, ledger.LedgerId);
    }
}
