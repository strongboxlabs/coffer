using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// A future-dated trade must not make the consistency check cry wolf.
/// </summary>
/// <remarks>
/// <para>
/// The holdings check derived its expectation from
/// <c>HoldingsCostBasisAsOf(ledgerId, DateTime.UtcNow, ...)</c>, whose SQL filters
/// events with <c>posted_at &lt;= p_as_of</c>. The WRITER has no such bound:
/// <c>recompute_holdings_cost_basis</c> calls
/// <c>holdings_fifo_walk(account, security, NULL)</c> and stores every event whenever
/// posted. So the check compared stored-including-future against
/// expected-excluding-future.
/// </para>
/// <para>
/// A scheduled transaction is a supported state, not an edge case — the register's
/// "scheduled" tab is defined as headers posted after now. So one future-dated trade
/// pinned the holdings projection permanently unhealthy, and the repair could not
/// clear it: the repair walks unbounded, re-stores exactly what is already there, and
/// the next check reports the identical mismatch. A check that disagrees with its own
/// repair is worse than no check, because it trains you to ignore it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyFutureDatedTests
{
    private readonly PostgresFixture _fixture;

    public ConsistencyFutureDatedTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Holdings_stay_healthy_when_a_trade_is_posted_in_the_future()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // One settled buy and one SCHEDULED buy. The scheduled one is what used to
        // make the check disagree with the stored rows it was checking.
        foreach (var at in new[] { DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(30) })
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    Action = "buy",
                    SecurityId = security,
                    Shares = 10m,
                    Price = 100m,
                    PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        await using var db = _fixture.NewDbContext();
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db), new HoldingsRecomputeService(db));

        var report = await consistency.CheckAsync(ledger.LedgerId);
        var holdings = Assert.Single(
            report.Projections, p => p.Projection == ConsistencyProjections.Holdings);

        // Before the fix this reported the scheduled buy's 10 shares and its basis as
        // drift — stored 20 shares, "expected" 10 — on every run, unfixably.
        Assert.True(
            holdings.Healthy,
            "holdings reported drift for a future-dated trade: "
            + string.Join("; ", holdings.Mismatches.Select(
                m => $"{m.Field} stored {m.Stored} expected {m.Expected}")));
    }
}
