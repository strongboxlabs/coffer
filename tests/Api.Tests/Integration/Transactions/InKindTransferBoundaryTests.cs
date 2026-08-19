using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// An in-kind transfer carries quantity and basis intact at every magnitude.
/// </summary>
/// <remarks>
/// A transfer reconstructs the carried basis as <c>round(quantity × unit_cost, 2)</c>,
/// so it runs migration 180's arithmetic twice — once deriving <c>unit_cost</c> from
/// the source basis, once rebuilding a total from it. A fractional quantity makes both
/// steps lossy, which is the drift that surfaced on a production in-kind move.
/// <para>
/// <c>Transfer_carries_basis_penny_perfect_for_a_large_high_precision_lot</c> pins
/// that regression with its own hand-picked lot; this runs the property across the
/// shared magnitude matrix, so a case added to <see cref="Boundary"/> extends it too.
/// </para>
/// <para>
/// The position is bought through the API rather than via
/// <c>SyntheticLedger.AddBoundaryPositionAsync</c>. The raw seeder writes holdings and
/// basis but no <c>lots</c> — migration 202 made the FIFO walk pure, so the lots table
/// is written by the write path, not by <c>recompute_holdings_cost_basis</c>. Since
/// <c>transfer_shares</c> CONSUMES lots, a raw-seeded position is rejected as
/// insufficient. That cost a debugging detour; the helper now documents it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InKindTransferBoundaryTests
{
    private readonly PostgresFixture _fixture;

    public InKindTransferBoundaryTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Transfer_carries_quantity_and_basis_at_any_magnitude(Boundary.Position p)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var sourceHoldings = source.HoldingsAccountId!.Value;
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Bond Fund", "BND");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<HttpResponseMessage> PostAsync(CreateInvestmentTransactionRequest req) =>
            await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);

        var buy = await PostAsync(new()
        {
            BrokerageAccountId = source.Id,
            Action = "buy",
            SecurityId = security,
            Shares = p.Quantity,
            Price = p.BuyPrice,
            PostedAt = Utc(2015, 1, 1),
        });
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);

        // Ground truth to carry: the source basis as the write path recorded it.
        await using (var db = _fixture.NewDbContext())
        {
            var src = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == sourceHoldings && h.SecurityId == security);
            Assert.Equal(p.Quantity, src.Quantity);
            Assert.True(Math.Abs(src.CostBasis - p.Basis) <= p.Tolerance,
                $"{p.Name}: source basis {src.CostBasis}, expected {p.Basis}");
        }

        var transfer = await PostAsync(new()
        {
            BrokerageAccountId = source.Id,
            Action = "transfer_shares",
            SecurityId = security,
            Shares = p.Quantity,
            TransferAccountId = dest.Id,
            PostedAt = Utc(2025, 1, 1),
        });
        var body = await transfer.Content.ReadAsStringAsync();
        Assert.True(transfer.StatusCode == HttpStatusCode.Created,
            $"{p.Name}: transfer returned {(int)transfer.StatusCode} — {body}");

        await using (var db = _fixture.NewDbContext())
        {
            var dst = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);

            // Quantity carries exactly — a 12dp position must not lose a share.
            Assert.Equal(p.Quantity, dst.Quantity);

            // Basis carries penny-perfect. Mig 180 widened unit_cost to (25,12)
            // precisely so the round-trip through unit_cost does not drift.
            Assert.Equal(decimal.Round(dst.CostBasis, Boundary.MoneyScale), dst.CostBasis);
            Assert.True(Math.Abs(dst.CostBasis - p.Basis) <= p.Tolerance,
                $"{p.Name}: carried basis {dst.CostBasis}, expected {p.Basis}");

            // Emptied, not duplicated: a carry that rounded generously would show up
            // as basis appearing from nowhere across the two accounts.
            var src = await db.Holdings.AsNoTracking()
                .SingleOrDefaultAsync(h => h.AccountId == sourceHoldings && h.SecurityId == security);
            Assert.Equal(0m, src?.Quantity ?? 0m);
            Assert.Equal(0m, src?.CostBasis ?? 0m);

            // ADR-0065 D1: a transfer is not a sale, so it realizes nothing at any size.
            Assert.Empty(await db.RealizedGains.AsNoTracking()
                .Where(g => g.LedgerId == ledger.LedgerId).ToListAsync());
        }
    }
}
