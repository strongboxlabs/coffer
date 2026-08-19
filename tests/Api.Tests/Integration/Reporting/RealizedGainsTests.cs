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

    /// <summary>
    /// Realized gains read back correctly at BOTH magnitudes (<see cref="Boundary"/>).
    /// </summary>
    /// <remarks>
    /// Regression for mig 182. The FIFO recompute computes
    /// <c>cost_basis_sold = consumed_qty × unit_cost</c>. Both are NUMERIC(25,12)
    /// (quantity always; unit_cost since mig 180), so a FRACTIONAL-share lot times a
    /// 12dp unit_cost yields up to 24 decimal places. At a 7-figure position that is
    /// ~30 significant digits — past .NET decimal's ceiling — so before mig 182
    /// (unconstrained numeric columns) the recompute stored the 24dp value and
    /// RealizedGainsAsync threw System.OverflowException reading it back (the prod
    /// `realized_gains` failure). mig 182 constrains the money columns to
    /// NUMERIC(19,2), so the value is stored 2dp and reads fine.
    /// <para>
    /// The typical case runs the same assertions at whole shares and three-figure
    /// money, so a failure distinguishes "broken at scale" from "broken everywhere".
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Realized_gains_read_without_decimal_overflow(Boundary.Position p)
    {
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

        // Buy the whole position, then sell all of it, so cost_basis_sold consumes the
        // lot in full — the multiplication that overflowed.
        await Trade("buy",   p.Quantity, p.BuyPrice,  new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await Trade("sell", -p.Quantity, p.SellPrice, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        // The read itself is the regression: pre-mig-182 this line threw OverflowException.
        var realized = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);

        // Money is stored at MoneyScale (no 24dp noise survived) ...
        Assert.Equal(Math.Round(row.Proceeds, Boundary.MoneyScale), row.Proceeds);
        Assert.Equal(Math.Round(row.CostBasisSold, Boundary.MoneyScale), row.CostBasisSold);
        Assert.Equal(Math.Round(row.RealizedGain, Boundary.MoneyScale), row.RealizedGain);
        // ... and matches the expected totals. The tolerance is a cent only where the
        // basis doesn't divide evenly, because unit_cost is re-derived from the stored
        // 2dp basis and the recomputed total can land either side.
        Assert.True(Math.Abs(row.CostBasisSold - p.Basis) <= p.Tolerance,
            $"{p.Name}: cost {row.CostBasisSold}, expected {p.Basis}");
        Assert.True(Math.Abs(row.Proceeds - p.Proceeds) <= p.Tolerance,
            $"{p.Name}: proceeds {row.Proceeds}, expected {p.Proceeds}");
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

    /// <summary>
    /// Every stored realized-gains figure must be bounded to the money scale. The
    /// columns are plain NUMERIC — nothing in the schema constrains them — and the
    /// values are products of a division (take x unit_cost, unit_cost = amount /
    /// quantity), a shape Postgres will take past 30 digits. System.Decimal holds
    /// 28-29 and Npgsql throws rather than truncate, which is precisely how
    /// holdings_snapshot broke in 0.63.0. Asserted so the property is enforced
    /// rather than being a lucky feature of the current data.
    /// </summary>
    [Fact]
    public async Task Stored_realized_gains_are_bounded_to_the_money_scale()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Fractional", "FRC");

        // Large, non-terminating, and consumed fractionally — the shape that pushes
        // take x unit_cost past the decimal limit.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500_000_000m, new DateTime(2020, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddHoldingAsync(holdings, sec, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, 7.000000000001m, 14_285_714.285714m, new DateTime(2020, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, holdings, sec, -3.333333333333m, 40_000_000m, new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await ledger.RecomputeHoldingsAsync();

        await using var conn = _fixture.OpenServiceConnection();
        var rows = (await Dapper.SqlMapper.QueryAsync<string>(conn,
            """
            SELECT unnest(ARRAY[
                proceeds::text, cost_basis_sold::text, realized_gain::text,
                proceeds_lt::text, cost_basis_sold_lt::text, realized_gain_lt::text])
            FROM realized_gains WHERE ledger_id = @l
            """, new { l = ledger.LedgerId })).ToList();

        Assert.NotEmpty(rows);
        foreach (var v in rows)
        {
            var dot = v.IndexOf('.');
            var scale = dot < 0 ? 0 : v.Length - dot - 1;
            Assert.True(scale <= 4, $"stored money value {v} has scale {scale}, expected <= 4");
            // And it must round-trip into a decimal, which is the failure that started this.
            Assert.True(decimal.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _),
                $"stored value {v} does not fit System.Decimal");
        }
    }
}
