using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The MCP <c>activity</c> tool (ADR-0080): investment headers collapsed into one
/// event each via the shared <c>InvestmentEventProjector</c> — the same aggregation
/// the register renders, reused server-side. Verifies the collapse end-to-end over a
/// real Buy+Fee, and that the tool runs under the caller's RLS scope.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpInvestmentActivityTests
{
    private readonly PostgresFixture _fixture;

    public McpInvestmentActivityTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task SeedBuyWithFeeAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId, Guid feeAccountId)
    {
        // Shares x Price = 1000 principal; a $5 fee posts to feeAccountId. Net cash
        // out on the brokerage is 1005 (principal + fee).
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = Day(2026, 1, 10),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                FeeAccountId = feeAccountId,
                FeeAmount = 5m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Activity_collapses_a_buy_with_fee_into_one_event()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "IDXA");
        var feeCategory = await ledger.AddCategoryAsync("Investment Fees", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await SeedBuyWithFeeAsync(client, ledger, brokerage.Id, securityId, feeCategory.Id);

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var result = await InvestmentTools.Activity(new InvestmentReportingRepository(db), ledger.LedgerId);

        var e = Assert.Single(result.Events);
        Assert.Equal("buy", e.Action);
        Assert.Equal("IDXA", e.SecurityTicker);
        Assert.Equal("Index Fund A", e.SecurityName);
        Assert.Equal(10m, e.Quantity);
        Assert.Equal(100m, e.UnitPrice);
        // Net cash on the brokerage = principal + fee, both cash-out (negative).
        Assert.Equal(-1005m, e.Amount);
        Assert.Equal(5m, e.Fee);
        Assert.Equal("Brokerage", e.AccountName);
    }

    [Fact]
    public async Task Activity_is_RLS_scoped_and_denies_cross_ledger_reads()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBrokerage = await alice.AddInvestmentAccountAsync("Alice Brokerage");
        var aliceSecurity = await alice.AddSecurityAsync("Index Fund A", "IDXA");
        var aliceFee = await alice.AddCategoryAsync("Investment Fees", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);
        await SeedBuyWithFeeAsync(client, alice, aliceBrokerage.Id, aliceSecurity, aliceFee.Id);

        // Alice's RLS-scoped repo — the posture an MCP bearer for alice runs under.
        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var repo = new InvestmentReportingRepository(aliceDb);

        // Positive control: alice sees her own activity.
        var own = await InvestmentTools.Activity(repo, alice.LedgerId);
        Assert.NotEmpty(own.Events);

        // Cross-ledger: alice passing bob's ledgerId gets nothing — RLS is the
        // boundary, not the caller-supplied id.
        var cross = await InvestmentTools.Activity(repo, bob.LedgerId);
        Assert.Empty(cross.Events);
    }
}
