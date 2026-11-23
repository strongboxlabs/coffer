using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.MarketSeries;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Shares moved between brokerages in different currencies convert each lot's basis at the
/// derived rate for its own acquisition day (ADR-0114 D13, ADR-0065): rounded once at the
/// destination's places, with the destination lot's unit cost that figure over its
/// quantity, and the move still realizes no gain. A lot with no rate refuses the whole
/// move with <c>rate-unavailable</c>, naming each missing stored leg and its day.
/// </summary>
/// <remarks>
/// Every trade, move and edit goes through the API on synthetic ledgers, and every rate is
/// typed through <c>POST /fx-rates</c>, the hand-entry path, so no stored rate is one the
/// API could not have written. A hand-entered day extends its pair's coverage to that day
/// (ADR-0114 D9), so each lot's day is covered by the latest rate typed.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InKindTransferCurrencyTests : DatabaseTest
{
    public InKindTransferCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private readonly MarketDataFake _fake = new(TimeProvider.System);

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    // Every day is before the server's UTC today, so each reads its own day's rate.
    private static DateOnly FirstLotDay => Today.AddDays(-60);
    private static DateOnly SecondLotDay => Today.AddDays(-40);
    private static DateOnly EarlierMoveDay => Today.AddDays(-30);
    private static DateOnly MoveDay => Today.AddDays(-10);

    private const decimal FirstLotRate = 1.1m;
    private const decimal SecondLotRate = 1.25m;
    private const decimal EarlierMoveRate = 1.4m;

    private static DateTime At(DateOnly day) => day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class World : IAsyncDisposable
    {
        public required SyntheticLedger Ledger { get; init; }
        public required ApiFactory Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required Guid Security { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync(string reporting)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: reporting);
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");
        var factory = _fake.Install(new ApiFactory(Db).WithoutDevAuth());
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World { Ledger = ledger, Factory = factory, Client = client, Security = security };
    }

    private static async Task TypeRateAsync(World w, string from, string to, DateOnly day, decimal rate)
    {
        var response = await w.Client.PostAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/fx-rates",
            new { fromCurrency = from, toCurrency = to, date = Iso(day), rate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The source's two lots' days at their own rates into the destination's
    /// currency, and a rate on the earlier move's day that no lot may read.</summary>
    private static async Task TypeLotRatesAsync(World w)
    {
        await TypeRateAsync(w, "EUR", "USD", FirstLotDay, FirstLotRate);
        await TypeRateAsync(w, "EUR", "USD", SecondLotDay, SecondLotRate);
        await TypeRateAsync(w, "EUR", "USD", EarlierMoveDay, EarlierMoveRate);
    }

    private static async Task<HttpResponseMessage> PostAsync(World w, CreateInvestmentTransactionRequest request) =>
        await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions", request);

    private static async Task<Guid> CreatedAsync(World w, CreateInvestmentTransactionRequest request)
    {
        var response = await PostAsync(w, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    private static Task<Guid> BuyAsync(World w, AccountRow brokerage, decimal shares, decimal amount, DateOnly day) =>
        CreatedAsync(w, new()
        {
            BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = w.Security,
            Shares = shares, Price = amount / shares, Amount = amount, PostedAt = At(day),
        });

    private static CreateInvestmentTransactionRequest Move(World w, AccountRow from, AccountRow to, decimal shares, DateOnly day) =>
        new()
        {
            BrokerageAccountId = from.Id, Action = "transfer_shares", SecurityId = w.Security,
            Shares = shares, TransferAccountId = to.Id, PostedAt = At(day),
        };

    /// <summary>Two lots in a brokerage in the source currency: three shares for a whole
    /// basis that does not divide by three, then two shares, on two days with different
    /// rates.</summary>
    private static async Task TwoLotsAsync(World w, AccountRow brokerage)
    {
        await BuyAsync(w, brokerage, 3m, 100m, FirstLotDay);
        await BuyAsync(w, brokerage, 2m, 50.01m, SecondLotDay);
    }

    /// <summary>Each lot's basis at its own day's rate, rounded once at the destination's
    /// two places, half away from zero. The first lot's basis is three times its stored
    /// repeating unit cost, converted unrounded.</summary>
    private static readonly decimal[] ConvertedLots =
    [
        Math.Round(3m * 33.333333333333m * FirstLotRate, 2, MidpointRounding.AwayFromZero),
        Math.Round(50.01m * SecondLotRate, 2, MidpointRounding.AwayFromZero),
    ];

    private async Task<IReadOnlyList<TxnLegRow>> HoldingsLegsAsync(Guid holdingsAccountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.AccountId == holdingsAccountId
                        && db.TxnHeaders.Any(h => h.Id == l.HeaderId && h.Action == "transfer_shares"))
            .OrderBy(l => l.Amount)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<LotRow>> OpenLotsAsync(Guid holdingsAccountId, Guid security)
    {
        await using var db = Db.NewDbContext();
        return await db.Lots.AsNoTracking()
            .Where(l => !l.IsClosed && db.Holdings.Any(h =>
                h.Id == l.HoldingId && h.AccountId == holdingsAccountId && h.SecurityId == security))
            .OrderBy(l => l.AcquiredAt)
            .ToListAsync();
    }

    /// <summary>The destination's two moved legs are the two converted lots, and its lots
    /// keep their days with each unit cost the converted basis over its quantity.</summary>
    private async Task AssertConvertedAsync(World w, AccountRow dest)
    {
        var destHoldings = dest.HoldingsAccountId!.Value;
        var legs = await HoldingsLegsAsync(destHoldings);
        Assert.Equal(ConvertedLots.Order(), legs.Select(l => l.Amount));

        var lots = await OpenLotsAsync(destHoldings, w.Security);
        Assert.Equal(2, lots.Count);
        Assert.Equal(At(FirstLotDay), lots[0].AcquiredAt);
        Assert.Equal(3m, lots[0].Quantity);
        Assert.Equal(Math.Round(ConvertedLots[0] / 3m, 10), Math.Round(lots[0].UnitCost, 10));
        Assert.Equal(At(SecondLotDay), lots[1].AcquiredAt);
        Assert.Equal(2m, lots[1].Quantity);
        Assert.Equal(Math.Round(ConvertedLots[1] / 2m, 10), Math.Round(lots[1].UnitCost, 10));

        await using var db = Db.NewDbContext();
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == w.Security);
        Assert.Equal(5m, holding.Quantity);
        Assert.Equal(ConvertedLots.Sum(), holding.CostBasis);
    }

    private async Task AssertNoRealizedGainAsync(World w)
    {
        var realized = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(w.Ledger.LedgerId, null, null, null, null);
        Assert.Empty(realized.Rows);
    }

    [Fact]
    public async Task Each_lot_converts_at_its_own_acquisition_days_rate_with_no_realized_gain()
    {
        await using var w = await WorldAsync("USD");
        var source = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
        await TypeLotRatesAsync(w);
        await TwoLotsAsync(w, source);

        await CreatedAsync(w, Move(w, source, dest, 5m, MoveDay));

        await AssertConvertedAsync(w, dest);
        // The source gives up each lot's own basis, in its own currency.
        var sourceLegs = await HoldingsLegsAsync(source.HoldingsAccountId!.Value);
        Assert.Equal(new[] { -100m, -50.01m }, sourceLegs.Select(l => l.Amount));
        await AssertNoRealizedGainAsync(w);
    }

    [Fact]
    public async Task A_lot_that_came_in_by_an_earlier_move_converts_at_its_acquisition_day_not_the_moves()
    {
        await using var w = await WorldAsync("USD");
        var first = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var second = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "EUR");
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage C", currencyCode: "USD");
        await TypeLotRatesAsync(w);
        await BuyAsync(w, first, 3m, 100m, FirstLotDay);

        // In one currency, on a day that has a rate of its own: the lot keeps its day.
        await CreatedAsync(w, Move(w, first, second, 3m, EarlierMoveDay));
        await CreatedAsync(w, Move(w, second, dest, 3m, MoveDay));

        var legs = await HoldingsLegsAsync(dest.HoldingsAccountId!.Value);
        Assert.Equal(ConvertedLots[0], Assert.Single(legs).Amount);
        var lot = Assert.Single(await OpenLotsAsync(dest.HoldingsAccountId!.Value, w.Security));
        Assert.Equal(At(FirstLotDay), lot.AcquiredAt);
    }

    [Fact]
    public async Task An_edit_of_the_move_and_a_conversion_of_a_sell_and_buy_store_the_same_figures()
    {
        // The edit: a move of the first lot only, widened to both lots.
        await using (var w = await WorldAsync("USD"))
        {
            var source = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
            var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
            await TypeLotRatesAsync(w);
            await TwoLotsAsync(w, source);
            var moveId = await CreatedAsync(w, Move(w, source, dest, 3m, MoveDay));

            var patch = await w.Client.SendAsync(new HttpRequestMessage(
                HttpMethod.Patch, $"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{moveId}")
            {
                Content = JsonContent.Create(new PatchInvestmentTransactionRequest
                {
                    BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = w.Security,
                    Shares = 5m, TransferAccountId = dest.Id, PostedAt = At(MoveDay),
                }),
            });
            Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

            await AssertConvertedAsync(w, dest);
            await AssertNoRealizedGainAsync(w);
        }

        // The conversion, through the MCP tool: a sell in the source and a buy in the
        // destination of the same shares on one day.
        await using (var w = await WorldAsync("USD"))
        {
            var source = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
            var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
            await TypeLotRatesAsync(w);
            await TwoLotsAsync(w, source);
            var sell = await CreatedAsync(w, new()
            {
                BrokerageAccountId = source.Id, Action = "sell", SecurityId = w.Security,
                Shares = -5m, Price = 40m, Amount = 200m, PostedAt = At(MoveDay),
            });
            var buy = await CreatedAsync(w, new()
            {
                BrokerageAccountId = dest.Id, Action = "buy", SecurityId = w.Security,
                Shares = 5m, Price = 50m, Amount = 250m, PostedAt = At(MoveDay),
            });

            await using var db = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
            var result = await McpWriteTools.ConvertInKindTransfer(
                McpTestGuard.Writable(), new InvestmentTransactionsRepository(db, TimeProvider.System), w.Ledger.LedgerId, sell, buy);
            Assert.True(result.Ok, result.Error);

            await AssertConvertedAsync(w, dest);
            await AssertNoRealizedGainAsync(w);
        }
    }

    [Fact]
    public async Task A_lot_with_no_rate_refuses_the_whole_move_naming_each_missing_stored_leg()
    {
        // A cross through the reporting currency, so each lot reads two stored legs. The
        // source currency's leg is typed for the first lot's day only; the destination
        // currency's leg not at all.
        await using var w = await WorldAsync("USD");
        var source = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "GBP");
        await TypeRateAsync(w, "EUR", "USD", FirstLotDay, FirstLotRate);
        await TwoLotsAsync(w, source);

        var response = await PostAsync(w, Move(w, source, dest, 5m, MoveDay));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BusinessError.Codes.RateUnavailable, doc.RootElement.GetProperty("code").GetString());
        var missing = doc.RootElement.GetProperty("rateUnavailable").EnumerateArray()
            .Select(e => (
                e.GetProperty("fromCurrency").GetString(),
                e.GetProperty("toCurrency").GetString(),
                e.GetProperty("date").GetString()))
            .ToList();
        Assert.Equal(
            new (string?, string?, string?)[]
            {
                ("GBP", "USD", Iso(FirstLotDay)),
                ("EUR", "USD", Iso(SecondLotDay)),
                ("GBP", "USD", Iso(SecondLotDay)),
            },
            missing);

        // Nothing was written: no move, and the source still holds both lots.
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking()
                .AnyAsync(h => h.LedgerId == w.Ledger.LedgerId && h.Action == "transfer_shares"));
        }
        Assert.Equal(2, (await OpenLotsAsync(source.HoldingsAccountId!.Value, w.Security)).Count);

        // The MCP conversion of the same move as a sell and a buy says so in its result.
        var sell = await CreatedAsync(w, new()
        {
            BrokerageAccountId = source.Id, Action = "sell", SecurityId = w.Security,
            Shares = -5m, Price = 40m, Amount = 200m, PostedAt = At(MoveDay),
        });
        var buy = await CreatedAsync(w, new()
        {
            BrokerageAccountId = dest.Id, Action = "buy", SecurityId = w.Security,
            Shares = 5m, Price = 50m, Amount = 250m, PostedAt = At(MoveDay),
        });
        await using var mcpDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        var result = await McpWriteTools.ConvertInKindTransfer(
            McpTestGuard.Writable(), new InvestmentTransactionsRepository(mcpDb, TimeProvider.System), w.Ledger.LedgerId, sell, buy);
        Assert.False(result.Ok);
        Assert.Contains("RateUnavailable", result.Error, StringComparison.Ordinal);
        Assert.Contains($"GBP->USD on {Iso(FirstLotDay)}", result.Error, StringComparison.Ordinal);
        Assert.Contains($"EUR->USD on {Iso(SecondLotDay)}", result.Error, StringComparison.Ordinal);
    }

    // The anchor: in one currency nothing converts, so no rate is needed and each lot's
    // basis and unit cost carry unchanged.
    [Fact]
    public async Task A_move_in_one_currency_with_no_rates_stored_carries_the_basis_unchanged()
    {
        await using var w = await WorldAsync("USD");
        var source = await w.Ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "EUR");
        await TwoLotsAsync(w, source);
        var sourceLots = await OpenLotsAsync(source.HoldingsAccountId!.Value, w.Security);

        await CreatedAsync(w, Move(w, source, dest, 5m, MoveDay));

        var legs = await HoldingsLegsAsync(dest.HoldingsAccountId!.Value);
        Assert.Equal(new[] { 50.01m, 100m }, legs.Select(l => l.Amount));
        var lots = await OpenLotsAsync(dest.HoldingsAccountId!.Value, w.Security);
        Assert.Equal(sourceLots.Select(l => (l.AcquiredAt, l.Quantity, l.UnitCost)),
            lots.Select(l => (l.AcquiredAt, l.Quantity, l.UnitCost)));
        await AssertNoRealizedGainAsync(w);
    }
}
