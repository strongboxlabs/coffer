using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// FIFO cost basis + realized gains (ADR-0064). Drives the live investment-create
/// path (which runs the recompute engine) with a buy-low / buy-high / partial-sell
/// history, then checks that realized gain consumes the OLDEST lot (FIFO) and the
/// remaining holding basis is the newer lot — the discriminator vs average cost.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RealizedGainsTests
{
    private readonly PostgresFixture _fixture;

    public RealizedGainsTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Partial_sale_realizes_fifo_gain_and_leaves_newer_lot_basis()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Trade(string action, decimal shares, decimal price, DateTime at)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    Action = action,
                    SecurityId = security,
                    Shares = shares,
                    Price = price,
                    PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // Buy 10 @ $100 (lot 1), buy 10 @ $200 (lot 2), then sell 10 @ $250.
        await Trade("buy", 10m, 100m, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("buy", 10m, 200m, new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("sell", -10m, 250m, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        // Realized gain: FIFO consumes the $100 lot → proceeds 2500 − cost 1000 = 1500.
        // (Average cost would give 2500 − 1500 = 1000 — this asserts FIFO.)
        var realized = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);
        Assert.Equal(security, row.SecurityId);
        Assert.Equal(2500m, row.Proceeds);
        Assert.Equal(1000m, row.CostBasisSold);
        Assert.Equal(1500m, row.RealizedGain);
        Assert.Equal(1500m, realized.TotalRealizedGain);

        // Remaining holding: 10 shares at the newer lot's basis 2000 (FIFO), not the
        // average-cost 1500.
        await using var db = _fixture.NewDbContext();
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdings && h.SecurityId == security);
        Assert.Equal(10m, holding.Quantity);
        Assert.Equal(2000m, holding.CostBasis);
    }

    [Fact]
    public async Task Large_fractional_position_realized_gains_reads_without_decimal_overflow()
    {
        // Regression for mig 182 — representative magnitude, not a kiddie-pool.
        // The FIFO recompute computes cost_basis_sold = consumed_qty × unit_cost.
        // Both are NUMERIC(25,12) (quantity always; unit_cost since mig 180), so a
        // FRACTIONAL-share lot times a 12dp unit_cost yields up to 24 decimal places.
        // At a 7-figure position that is ~30 significant digits — past .NET decimal's
        // ceiling — so before mig 182 (unconstrained numeric columns) the recompute
        // stored the 24dp value and RealizedGainsAsync threw System.OverflowException
        // reading it back (the prod `realized_gains` failure). mig 182 constrains the
        // money columns to NUMERIC(19,2), so the value is stored 2dp and reads fine.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Bond Fund", "BND");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Trade(string action, decimal shares, decimal price, DateTime at)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = action, SecurityId = security,
                    Shares = shares, Price = price, PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // 123,456.789012 fractional shares (12dp) → basis ~ $1,000,000.09 → unit_cost
        // ~8.10 carries 12 decimals (the basis doesn't divide evenly), then the full
        // sale's cost_basis_sold = 123456.789012 × unit_cost(12dp) is a 24dp, 7-figure
        // value — the overflow boundary.
        await Trade("buy", 123456.789012m, 8.10m, new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("sell", -123456.789012m, 9.00m, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        // The read itself is the regression: pre-mig-182 this line threw OverflowException.
        var realized = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);

        // Money is stored at 2dp (no 24dp noise survived) ...
        Assert.Equal(Math.Round(row.Proceeds, 2), row.Proceeds);
        Assert.Equal(Math.Round(row.CostBasisSold, 2), row.CostBasisSold);
        Assert.Equal(Math.Round(row.RealizedGain, 2), row.RealizedGain);
        // ... and equals proceeds − cost, in the right 7-figure ballpark (±1¢ for the
        // 12dp unit_cost round-trip). basis = round(123456.789012 × 8.10) = 999,999.99;
        // proceeds = round(123456.789012 × 9.00) = 1,111,111.10.
        Assert.True(Math.Abs(row.CostBasisSold - 999999.99m) <= 0.01m, $"cost {row.CostBasisSold}");
        Assert.True(Math.Abs(row.Proceeds - 1111111.10m) <= 0.01m, $"proceeds {row.Proceeds}");
        Assert.Equal(row.Proceeds - row.CostBasisSold, row.RealizedGain);
    }

    [Fact]
    public async Task Sale_straddling_one_year_splits_gain_short_vs_long_term()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Trade(string action, decimal shares, decimal price, DateTime at)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    Action = action,
                    SecurityId = security,
                    Shares = shares,
                    Price = price,
                    PostedAt = at,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // Lot 1: 10 @ $100 held > 2 years (long-term). Lot 2: 10 @ $200 held ~1
        // month (short-term). Sell 15 @ $300 → FIFO consumes all 10 of lot 1 (LT)
        // + 5 of lot 2 (ST), straddling the 1-year line.
        await Trade("buy", 10m, 100m, new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("buy", 10m, 200m, new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("sell", -15m, 300m, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        var realized = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);

        // Total: proceeds 4500, cost 2000 (1000 + 1000), gain 2500.
        Assert.Equal(4500m, row.Proceeds);
        Assert.Equal(2000m, row.CostBasisSold);
        Assert.Equal(2500m, row.RealizedGain);

        // Long-term: 10 old shares → cost 1000, proceeds 4500 * 10/15 = 3000,
        // gain 2000. Short-term: 5 new shares → cost 1000, proceeds 1500, gain 500.
        Assert.Equal(2000m, row.RealizedGainLongTerm);
        Assert.Equal(500m, row.RealizedGainShortTerm);
        Assert.Equal(2000m, realized.TotalRealizedGainLongTerm);
        Assert.Equal(500m, realized.TotalRealizedGainShortTerm);
    }
}
