using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Snapshots;

/// <summary>
/// A snapshot round-trip returns every money figure identical, at every magnitude.
/// </summary>
/// <remarks>
/// The snapshot payload serialises money through JSON and back, so a 12dp quantity or
/// a basis derived from a 12dp <c>unit_cost</c> crosses two type boundaries per trip.
/// <c>Create_then_restore_round_trips_the_ledger_state</c> asserts row COUNTS, which a
/// restore that reinserted every row with a corrupted amount would satisfy.
/// <para>
/// The stress lane has a values comparison at ledger scale
/// (<c>SnapshotRestoreLatencyTests</c>), but <c>Integration.Stress</c> is excluded from
/// both the CI shards and preflight, so it only runs when someone invokes it by hand.
/// This one runs on every push, which is the point of putting it here.
/// </para>
/// <para>
/// The position is bought through the API so <c>lots</c> exist to be captured — the
/// raw seeder writes none (see <c>SyntheticLedger.AddBoundaryPositionAsync</c>), and a
/// round-trip that captured no lots would compare zero against zero and pass.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SnapshotMoneyRoundTripTests
{
    private readonly PostgresFixture _fixture;

    public SnapshotMoneyRoundTripTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Every money aggregate a restore could silently corrupt.</summary>
    private sealed record Money(
        decimal LegAmount, decimal Quantity, decimal CostBasis,
        decimal LotQuantity, decimal LotUnitCost, decimal RealizedGain);

    private async Task<Money> ReadMoneyAsync(Guid ledgerId)
    {
        await using var db = _fixture.NewDbContext();
        return new Money(
            LegAmount: await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledgerId).SumAsync(l => l.Amount),
            Quantity: await db.Holdings.AsNoTracking()
                .Where(h => h.LedgerId == ledgerId).SumAsync(h => h.Quantity),
            CostBasis: await db.Holdings.AsNoTracking()
                .Where(h => h.LedgerId == ledgerId).SumAsync(h => h.CostBasis),
            LotQuantity: await db.Lots.AsNoTracking()
                .Where(l => l.LedgerId == ledgerId).SumAsync(l => l.Quantity),
            LotUnitCost: await db.Lots.AsNoTracking()
                .Where(l => l.LedgerId == ledgerId).SumAsync(l => l.UnitCost),
            RealizedGain: await db.RealizedGains.AsNoTracking()
                .Where(g => g.LedgerId == ledgerId).SumAsync(g => g.RealizedGain));
    }

    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Snapshot_restore_returns_every_money_figure_identical(Boundary.Position p)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Bond Fund", "BND");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<HttpResponseMessage> PostAsync(CreateInvestmentTransactionRequest req) =>
            await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);

        // Buy the whole position, then sell half — so the snapshot carries an open lot,
        // a consumed one, and a realized_gains row, all at the magnitude under test.
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(new()
        {
            BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security,
            Shares = p.Quantity, Price = p.BuyPrice, PostedAt = Utc(2020, 1, 1),
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(new()
        {
            BrokerageAccountId = brokerage.Id, Action = "sell", SecurityId = security,
            Shares = -(p.Quantity / 2m), Price = p.SellPrice, PostedAt = Utc(2024, 1, 1),
        })).StatusCode);

        var before = await ReadMoneyAsync(ledger.LedgerId);

        // The fixture must actually hold money, or the comparison below is vacuous.
        Assert.NotEqual(0m, before.CostBasis);
        Assert.NotEqual(0m, before.LotQuantity);
        Assert.NotEqual(0m, before.RealizedGain);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("boundary"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>();
        Assert.NotNull(created?.Snapshot);

        // Mutate after the snapshot so the restore has something to undo — otherwise
        // "identical" could hold simply because nothing ever changed.
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(new()
        {
            BrokerageAccountId = brokerage.Id, Action = "sell", SecurityId = security,
            Shares = -(p.Quantity / 4m), Price = p.SellPrice, PostedAt = Utc(2025, 1, 1),
        })).StatusCode);
        var mutated = await ReadMoneyAsync(ledger.LedgerId);
        Assert.NotEqual(before, mutated);

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{created!.Snapshot!.Id}/restore",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        var after = await ReadMoneyAsync(ledger.LedgerId);
        Assert.Equal(before, after);
    }
}
