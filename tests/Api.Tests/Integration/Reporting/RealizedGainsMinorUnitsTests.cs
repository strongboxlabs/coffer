using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Money;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The realized-gains writer and its walk round each money figure once, half away from
/// zero, at the minor units of the account's currency (migration 301, ADR-0114 D15),
/// exactly as the API's <see cref="CurrencyMinorUnits.RoundAwayFromZero"/> does, and
/// agree with each other, so the consistency check sees no drift.
/// </summary>
/// <remarks>
/// <para>Each brokerage buys a lot of two shares whose cost is an odd number of minor
/// units, so selling one share lands its cost basis exactly on a midpoint at the
/// account's places, and a later lot of three whose per-share cost has a tail below one
/// minor unit, which the second sale reaches. The code an account can no longer take
/// (CLF, four places on List One) is held at 2 places, as the API holds it.</para>
/// <para>A banker's-rounding expectation would fail the midpoint assertions, and a writer
/// still rounding at 2 places fails them on the 0- and 3-place accounts.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RealizedGainsMinorUnitsTests : DatabaseTest
{
    private static readonly DateTime FirstBuy = new(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Money =
        ["proceeds", "cost_basis_sold", "realized_gain", "proceeds_lt", "cost_basis_sold_lt", "realized_gain_lt"];

    public RealizedGainsMinorUnitsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("USD", 2)]
    [InlineData("BHD", 3)]
    [InlineData("CLF", 2)]
    public async Task Every_realized_gain_figure_is_rounded_once_at_the_account_currency_places(string code, int places)
    {
        Assert.Equal(places, CurrencyMinorUnits.PlacesOf(code));
        var minor = MinorUnit(places);

        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", currencyCode: code);
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund");

        // Lot A: two shares costing an odd number of minor units. Lot B: three shares
        // whose cost divides into a per-share figure with a tail below one minor unit.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, security, 2m, 1000.5m * minor, FirstBuy);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, security, 3m, 1000.333333333334m * minor, FirstBuy.AddDays(500));
        // One share of lot A, held past a year: its basis is the midpoint.
        var midpointSale = await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, security, 1m, 1500m * minor, FirstBuy.AddDays(400));
        // Lot A's last share, long-term, and one of lot B, short-term.
        var tailSale = await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, security, 2m, 1500m * minor, FirstBuy.AddDays(600));
        await ledger.AddHoldingAsync(holdings, security, 0m, 0m);
        await ledger.RecomputeHoldingsAsync();

        var raw = await RawGainsAsync(holdings, security);
        var stored = await StoredAsync($"SELECT sell_leg_id, {string.Join(", ", Money)} FROM realized_gains WHERE account_id = @a AND security_id = @s", holdings, security);
        var walked = await StoredAsync($"SELECT sell_leg_id, {string.Join(", ", Money)} FROM realized_gains_walk(@a, @s)", holdings, security);
        Assert.Equal(2, stored.Count);

        // The seed is what it claims: the first sale's basis sits on a midpoint, the
        // second sale's carries a tail.
        Assert.Equal(1000.5m * minor, raw[midpointSale]["cost_basis_sold"]);
        Assert.NotEqual(decimal.Round(raw[tailSale]["cost_basis_sold"], places + 1), raw[tailSale]["cost_basis_sold"]);

        foreach (var (leg, figures) in stored)
        {
            foreach (var column in Money)
            {
                var expected = CurrencyMinorUnits.RoundAwayFromZero(raw[leg][column], code);
                Assert.True(expected == figures[column],
                    $"{code} {column}: stored {figures[column]}, rounded once at {places} places {expected} (from {raw[leg][column]})");
                Assert.True(CurrencyMinorUnits.Fits(figures[column], code), $"{code} {column} {figures[column]} has more places than {places}");
            }
        }

        // Half away from zero on the midpoint, in both the total and the long-term half.
        Assert.Equal(1001m * minor, stored[midpointSale]["cost_basis_sold"]);
        Assert.Equal(1001m * minor, stored[midpointSale]["cost_basis_sold_lt"]);
        Assert.Equal(500m * minor, stored[midpointSale]["realized_gain"]);
        Assert.Equal(2001m * minor, stored[tailSale]["cost_basis_sold"]);

        // The walk returns exactly what the writer stored, so the check sees no drift.
        Assert.Equal(stored.Keys.Order(), walked.Keys.Order());
        foreach (var (leg, figures) in stored)
            foreach (var column in Money)
                Assert.True(figures[column] == walked[leg][column], $"{code} {column}: stored {figures[column]}, walk {walked[leg][column]}");

        await using var db = Db.NewDbContext();
        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);
        var gains = Assert.Single((await consistency.CheckAsync(ledger.LedgerId)).Projections,
            p => p.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(gains.Healthy, string.Join("; ", gains.Mismatches.Select(m => $"{m.Field} {m.Stored} {m.Expected}")));
    }

    /// <summary>
    /// The boundary magnitude (<see cref="Boundary.LargeFractional"/>: a fractional 12dp
    /// share count whose basis carries every decimal of its unit cost) on a 0-place and a
    /// 3-place brokerage: the figures read back without overflow, each at the account's
    /// places.
    /// </summary>
    [Theory]
    [InlineData("JPY")]
    [InlineData("BHD")]
    public async Task A_boundary_sale_is_stored_at_the_account_currency_places(string code)
    {
        var p = Boundary.LargeFractional;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage", currencyCode: code);
        var holdings = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund");

        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, security, p.Quantity, p.BuyPrice, FirstBuy);
        await ledger.AddInvestmentSellAsync(brokerage.Id, holdings, security, p.Quantity, p.SellPrice, FirstBuy.AddDays(400));
        await ledger.AddHoldingAsync(holdings, security, 0m, 0m);
        await ledger.RecomputeHoldingsAsync();

        // The whole position is sold, so the basis is the buy's cost, which the seed
        // rounded at the account's places as the API rounds a computed figure.
        var basis = CurrencyMinorUnits.RoundAwayFromZero(p.Quantity * p.BuyPrice, code);
        var proceeds = CurrencyMinorUnits.RoundAwayFromZero(p.Quantity * p.SellPrice, code);

        await using var db = Db.NewDbContext();
        var row = await db.RealizedGains.AsNoTracking().SingleAsync(g => g.AccountId == holdings);
        Assert.Equal(proceeds, row.Proceeds);
        Assert.Equal(basis, row.CostBasisSold);
        Assert.Equal(proceeds - basis, row.RealizedGain);
        Assert.Equal(row.Proceeds, row.ProceedsLt);
        Assert.True(CurrencyMinorUnits.Fits(row.CostBasisSold, code));
        Assert.True(CurrencyMinorUnits.Fits(row.RealizedGain, code));

        var consistency = new LedgerConsistencyRepository(
            db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);
        var gains = Assert.Single((await consistency.CheckAsync(ledger.LedgerId)).Projections,
            g => g.Projection == ConsistencyProjections.RealizedGains);
        Assert.True(gains.Healthy, string.Join("; ", gains.Mismatches.Select(m => $"{m.Field} {m.Stored} {m.Expected}")));
    }

    private static decimal MinorUnit(int places)
    {
        var unit = 1m;
        for (var i = 0; i < places; i++) unit /= 10m;
        return unit;
    }

    /// <summary>The walk's figures before any rounding at a currency's places, by sale leg.</summary>
    /// <remarks>Read at 16 places: a 12-place quantity times a 12-place unit cost carries
    /// more digits than <see cref="decimal"/> reads. Rounding at 16 cannot move a figure
    /// across a midpoint at 3 places or fewer, and leaves an exact midpoint exact.</remarks>
    private Task<Dictionary<Guid, Dictionary<string, decimal>>> RawGainsAsync(Guid account, Guid security) =>
        StoredAsync(
            $"SELECT (g).sell_leg_id, {string.Join(", ", Money.Select(c => $"ROUND((g).{c}, 16)"))} "
            + "FROM holdings_fifo_walk(@a, @s, NULL) w, unnest(w.o_gains) AS g",
            account, security);

    private async Task<Dictionary<Guid, Dictionary<string, decimal>>> StoredAsync(string sql, Guid account, Guid security)
    {
        await using var connection = Db.OpenServiceConnection();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("a", account);
        command.Parameters.AddWithValue("s", security);
        var result = new Dictionary<Guid, Dictionary<string, decimal>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var figures = new Dictionary<string, decimal>(StringComparer.Ordinal);
            for (var i = 0; i < Money.Length; i++) figures[Money[i]] = reader.GetDecimal(i + 1);
            result[reader.GetGuid(0)] = figures;
        }
        return result;
    }
}
