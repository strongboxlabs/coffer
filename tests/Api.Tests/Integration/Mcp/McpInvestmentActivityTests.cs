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
public sealed class McpInvestmentActivityTests : DatabaseTest
{
    public McpInvestmentActivityTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task SeedBuyWithFeeAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId, Guid feeAccountId)
    {
        // Principal is Shares x Price; the fee posts to feeAccountId. Net cash out
        // on the brokerage is principal + fee.
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "IDXA");
        var feeCategory = await ledger.AddCategoryAsync("Investment Fees", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await SeedBuyWithFeeAsync(client, ledger, brokerage.Id, securityId, feeCategory.Id);

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var result = await InvestmentTools.Activity(new InvestmentReportingRepository(db, TimeProvider.System), ledger.LedgerId);

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

    private static async Task<Guid> CreateTransferAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    /// <summary>
    /// A transfer between currencies carries the other side's own figure and currency on
    /// its activity row, as the investment register's transfer slot does (ADR-0114 D14,
    /// "The register row"); a transfer in one currency and an event with no transfer carry
    /// neither. Each row is found by its header id, so a missing row cannot pass as a null
    /// figure. Mutation caught: the activity row dropping the other side, or keeping it on
    /// a transfer in one currency.
    /// </summary>
    [Fact]
    public async Task Activity_carries_the_other_side_of_a_transfer_between_currencies_only()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "USD");
        var euroBank = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var dollarBank = await ledger.AddBankAccountAsync("Bank C", currencyCode: "USD");
        var securityId = await ledger.AddSecurityAsync("Index Fund A", "IDXA");
        var feeCategory = await ledger.AddCategoryAsync("Investment Fees", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var across = await CreateTransferAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", PostedAt = Day(2026, 3, 2),
            Amount = -100m, TransferAccountId = euroBank.Id, TransferAmount = 91.37m,
        });
        var within = await CreateTransferAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", PostedAt = Day(2026, 3, 3),
            Amount = -40m, TransferAccountId = dollarBank.Id,
        });
        await SeedBuyWithFeeAsync(client, ledger, brokerage.Id, securityId, feeCategory.Id);

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var result = await InvestmentTools.Activity(new InvestmentReportingRepository(db, TimeProvider.System), ledger.LedgerId);
        Assert.Equal(3, result.Events.Count);

        var acrossRow = Assert.Single(result.Events, e => e.HeaderId == across);
        Assert.Equal("Bank B", acrossRow.TransferAccount);
        Assert.Equal(-100m, acrossRow.Amount);
        Assert.Equal(91.37m, acrossRow.TransferAmount);
        Assert.Equal("EUR", acrossRow.TransferCurrencyCode);

        var withinRow = Assert.Single(result.Events, e => e.HeaderId == within);
        Assert.Equal("Bank C", withinRow.TransferAccount);
        Assert.Equal(-40m, withinRow.Amount);
        Assert.Null(withinRow.TransferAmount);
        Assert.Null(withinRow.TransferCurrencyCode);

        var buyRow = Assert.Single(result.Events, e => e.Action == "buy");
        Assert.Null(buyRow.TransferAccount);
        Assert.Null(buyRow.TransferAmount);
        Assert.Null(buyRow.TransferCurrencyCode);
    }

    [Fact]
    public async Task Activity_is_RLS_scoped_and_denies_cross_ledger_reads()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        var aliceBrokerage = await alice.AddInvestmentAccountAsync("Alice Brokerage");
        var aliceSecurity = await alice.AddSecurityAsync("Index Fund A", "IDXA");
        var aliceFee = await alice.AddCategoryAsync("Investment Fees", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);
        await SeedBuyWithFeeAsync(client, alice, aliceBrokerage.Id, aliceSecurity, aliceFee.Id);

        // Alice's RLS-scoped repo — the posture an MCP bearer for alice runs under.
        await using var aliceDb = Db.NewAppDbContextAsUser(alice.UserId);
        var repo = new InvestmentReportingRepository(aliceDb, TimeProvider.System);

        // Positive control: alice sees her own activity.
        var own = await InvestmentTools.Activity(repo, alice.LedgerId);
        Assert.NotEmpty(own.Events);

        // Cross-ledger: alice passing bob's ledgerId gets nothing — RLS is the
        // boundary, not the caller-supplied id.
        var cross = await InvestmentTools.Activity(repo, bob.LedgerId);
        Assert.Empty(cross.Events);
    }
}
