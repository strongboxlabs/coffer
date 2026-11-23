using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// A trade price is in the currency of the brokerage that made the trade (migration 274,
/// ADR-0084 D2, 2026-11-04 amendment), never of the holdings account its line sits on. A
/// brokerage's currency edit re-derives its trades' days in the new currency and gives its
/// holdings account the same currency (ADR-0019 Rule 4, 2026-11-04 amendment); the
/// consistency check reports a trade price in another currency than its brokerage's, and,
/// on its own, a holdings account in another currency than its brokerage's.
/// </summary>
/// <remarks>
/// Every currency named here is the brokerage's own, set when it is made or by an edit;
/// nothing converts (FU-106). A dollar row on a day a euro trade prices is the row the
/// writer before migration 274 stored for every trade, and a brokerage whose holdings
/// account kept the old code is what an edit before migration 274 left; both are planted
/// through EF as the consistency tests plant a stale derived row.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TradePriceCurrencyTests : DatabaseTest
{
    private static readonly DateOnly Today = new(2026, 5, 20);
    private static readonly DateTime TradeDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public TradePriceCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

    private static ProjectionConsistency Projection(LedgerConsistencyReport report, string name) =>
        Assert.Single(report.Projections, p => p.Projection == name);

    private static string Describe(ProjectionConsistency p) =>
        string.Join("; ", p.Mismatches.Select(m => $"{m.Scope}: {m.Field} stored {m.Stored} expected {m.Expected}"));

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return client;
    }

    private static async Task<Guid> BrokerageAsync(HttpClient client, SyntheticLedger ledger, string name, string currency)
    {
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = name, AccountType = "investment", CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId, decimal price)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = TradeDay,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = price,
                Amount = 10m * price,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private async Task<SecurityPriceRow> PriceRowAsync(Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking().Where(p => p.SecurityId == securityId).SingleAsync();
    }

    private async Task<(string Brokerage, string Holdings)> CurrenciesAsync(Guid brokerageId)
    {
        await using var db = Db.NewDbContext();
        var brokerage = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == brokerageId);
        var holdings = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == brokerage.HoldingsAccountId);
        return (brokerage.CurrencyCode, holdings.CurrencyCode);
    }

    private static async Task<SecurityDetailDto> DetailAsync(HttpClient client, SyntheticLedger ledger, Guid securityId) =>
        (await client.GetFromJsonAsync<SecurityDetailDto>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}?today={Today:yyyy-MM-dd}"))!;

    /// <summary>
    /// A buy, its edit and the repair each store the day's price in the brokerage's
    /// currency, and the security's detail and catalog row name the price in it.
    /// </summary>
    [Fact]
    public async Task A_trade_in_a_euro_brokerage_prices_its_day_in_euros_through_an_edit_and_the_repair()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security A", ticker: "SECA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerage = await BrokerageAsync(client, ledger, "Brokerage A", "EUR");
        var headerId = await BuyAsync(client, ledger, brokerage, securityId, 10m);

        var bought = await PriceRowAsync(securityId);
        Assert.Equal(PriceSource.Trade, bought.Source);
        Assert.Equal(10m, bought.Price);
        Assert.Equal("EUR", bought.CurrencyCode);

        // The trade's row is the only price, so it is the current price the detail and
        // the catalog name.
        var detail = await DetailAsync(client, ledger, securityId);
        Assert.Equal(10m, detail.LatestPrice);
        Assert.Equal("EUR", detail.PriceCurrencyCode);
        var row = (await client.GetFromJsonAsync<List<SecuritySummaryDto>>(
                $"/api/ledgers/{ledger.LedgerId}/securities?today={Today:yyyy-MM-dd}"))!
            .Single(r => r.Id == securityId);
        Assert.Equal("EUR", row.PriceCurrencyCode);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage,
                PostedAt = TradeDay,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 12m,
                Amount = 120m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");
        var edited = await PriceRowAsync(securityId);
        Assert.Equal(12m, edited.Price);
        Assert.Equal("EUR", edited.CurrencyCode);

        // A dollar row at an old price planted on the day, as a version before migration 274
        // left a day it stored and a later trade re-priced: one mismatch per figure, the
        // price and the currency, and the repair writes the trade's price in its currency.
        await using (var seed = Db.NewDbContext())
        {
            await seed.SecurityPrices.Where(p => p.SecurityId == securityId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 11m).SetProperty(p => p.CurrencyCode, "USD"));
        }
        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);
        var before = Projection(await consistency.CheckAsync(ledger.LedgerId), ConsistencyProjections.TradePrices);
        Assert.Collection(before.Mismatches,
            m =>
            {
                Assert.Equal("price disagrees with the day's last trade", m.Field);
                Assert.Equal(11m, m.Stored);
                Assert.Equal(12m, m.Expected);
                Assert.Equal(ConsistencyReasons.ValueDiffers, m.Reason);
            },
            m =>
            {
                Assert.Equal("price in USD, the day's last trade in EUR", m.Field);
                Assert.Equal(0m, m.Stored);
                Assert.Equal(0m, m.Expected);
                Assert.Equal(ConsistencyReasons.ValueDiffers, m.Reason);
            });

        var after = (await consistency.RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(after.Healthy, "the repair left drift behind: " + Describe(after));
        var repaired = await PriceRowAsync(securityId);
        Assert.Equal(12m, repaired.Price);
        Assert.Equal("EUR", repaired.CurrencyCode);
    }

    /// <summary>
    /// A brokerage's currency edit carries to its holdings account and to the prices its
    /// trades set, in the edit itself, so the check finds nothing after it.
    /// </summary>
    [Fact]
    public async Task Changing_a_brokerages_currency_moves_its_holdings_and_its_trade_prices_with_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerage = await BrokerageAsync(client, ledger, "Brokerage B", "USD");
        await BuyAsync(client, ledger, brokerage, securityId, 10m);
        // Premise: the trade priced its day in the brokerage's currency as it stood.
        Assert.Equal("USD", (await PriceRowAsync(securityId)).CurrencyCode);

        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage}",
            new UpdateAccountRequest { CurrencyCode = "EUR" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(("EUR", "EUR"), await CurrenciesAsync(brokerage));
        var row = await PriceRowAsync(securityId);
        Assert.Equal("EUR", row.CurrencyCode);
        Assert.Equal(10m, row.Price);
        Assert.Equal("EUR", (await DetailAsync(client, ledger, securityId)).PriceCurrencyCode);

        await using var db = Db.NewDbContext();
        var report = await Consistency(db).CheckAsync(ledger.LedgerId);
        Assert.True(report.Healthy, "an edit left findings: " + string.Join(" | ",
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + Describe(p))));
    }

    /// <summary>
    /// A brokerage's currency corrected before its first trade: the edit moves its holdings
    /// account though no line sits on it yet, and the first buy prices its day in the new
    /// currency.
    /// </summary>
    [Fact]
    public async Task Changing_a_brokerages_currency_before_any_trade_moves_its_holdings_and_prices_the_first_trade_in_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security H", ticker: "SECH");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerage = await BrokerageAsync(client, ledger, "Brokerage H", "USD");
        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage}",
            new UpdateAccountRequest { CurrencyCode = "EUR" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(("EUR", "EUR"), await CurrenciesAsync(brokerage));
        await using (var db = Db.NewDbContext())
        {
            var holdings = Projection(await Consistency(db).CheckAsync(ledger.LedgerId),
                ConsistencyProjections.HoldingsCurrency);
            Assert.True(holdings.Healthy, "the edit left the holdings account behind: " + Describe(holdings));
            // Anchor: the holdings account is examined, so healthy is the comparison's verdict.
            Assert.Equal(1, holdings.Checked);
        }

        await BuyAsync(client, ledger, brokerage, securityId, 10m);
        var row = await PriceRowAsync(securityId);
        Assert.Equal(PriceSource.Trade, row.Source);
        Assert.Equal(10m, row.Price);
        Assert.Equal("EUR", row.CurrencyCode);

        await using var check = Db.NewDbContext();
        var report = await Consistency(check).CheckAsync(ledger.LedgerId);
        Assert.True(report.Healthy, "the first trade left findings: " + string.Join(" | ",
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + Describe(p))));
    }

    /// <summary>
    /// A rank-comparable row in another currency than the day's trade is a value mismatch
    /// on the currency, named in the field, and the repair writes the brokerage's currency.
    /// </summary>
    [Theory]
    [InlineData(PriceSource.Trade)]
    [InlineData(PriceSource.Import)]
    [InlineData(PriceSource.Simplefin)]
    public async Task A_dollar_price_on_a_euro_trades_day_is_reported_and_the_repair_writes_euros(string source)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage C", currencyCode: "EUR");
        var securityId = await ledger.AddSecurityAsync("Security C", "SECC");
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, securityId, 10m, 25m, TradeDay);
        await ledger.AddSecurityPriceAsync(securityId, 25m, TradeDay, source, currencyCode: "USD");

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);
        var report = await consistency.CheckAsync(ledger.LedgerId);
        // The price agrees, so the one finding is the currency.
        var mismatch = Assert.Single(Projection(report, ConsistencyProjections.TradePrices).Mismatches);
        Assert.Equal("price in USD, the day's last trade in EUR", mismatch.Field);
        Assert.Equal(ConsistencyReasons.ValueDiffers, mismatch.Reason);
        Assert.Equal(0m, mismatch.Stored);
        Assert.Equal(0m, mismatch.Expected);
        Assert.Equal(securityId, mismatch.SecurityId);
        Assert.Equal(brokerage.Id, mismatch.AccountId);
        Assert.Equal(DateOnly.FromDateTime(TradeDay), mismatch.PriceDate);
        // The holdings account is in its brokerage's currency: not that projection's.
        Assert.True(Projection(report, ConsistencyProjections.HoldingsCurrency).Healthy);

        var after = (await consistency.RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(after.Healthy, "the repair left drift behind: " + Describe(after));
        var row = await PriceRowAsync(securityId);
        Assert.Equal("EUR", row.CurrencyCode);
        Assert.Equal(25m, row.Price);
        Assert.Equal(PriceSource.Trade, row.Source);
    }

    /// <summary>
    /// A feed close or a manual price owns its day, whatever currency it is in: the check
    /// does not compare it, and the repair, run for another day of the same security, does
    /// not touch it.
    /// </summary>
    [Theory]
    [InlineData(PriceSource.Fetch)]
    [InlineData(PriceSource.Manual)]
    public async Task A_fetched_or_manual_price_in_another_currency_is_not_reported_or_repaired(string source)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage D", currencyCode: "EUR");
        var securityId = await ledger.AddSecurityAsync("Security D", "SECD");
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, securityId, 10m, 25m, TradeDay);
        await ledger.AddSecurityPriceAsync(securityId, 25m, TradeDay, source, currencyCode: "USD");
        // A later trade whose day has no price, so the repair has a day to write.
        var laterDay = TradeDay.AddDays(3);
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, securityId, 10m, 30m, laterDay);

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);
        var trade = Projection(await consistency.CheckAsync(ledger.LedgerId), ConsistencyProjections.TradePrices);
        // Both days are examined, and the one finding is the later day's: the owned day is
        // checked and passed.
        Assert.Equal(2, trade.Checked);
        var mismatch = Assert.Single(trade.Mismatches);
        Assert.Equal(DateOnly.FromDateTime(laterDay), mismatch.PriceDate);
        Assert.Equal("no price for the day's trade", mismatch.Field);

        var after = (await consistency.RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(after.Healthy, "the repair left drift behind: " + Describe(after));

        await using var read = Db.NewDbContext();
        var rows = await read.SecurityPrices.AsNoTracking()
            .Where(p => p.SecurityId == securityId)
            .OrderBy(p => p.PriceDate)
            .ToListAsync();
        Assert.Collection(rows,
            owned =>
            {
                Assert.Equal(25m, owned.Price);
                Assert.Equal("USD", owned.CurrencyCode);
                Assert.Equal(source, owned.Source);
            },
            // Anchor: the repair ran, and wrote the later day in the brokerage's currency.
            repaired =>
            {
                Assert.Equal(30m, repaired.Price);
                Assert.Equal("EUR", repaired.CurrencyCode);
                Assert.Equal(PriceSource.Trade, repaired.Source);
            });
    }

    /// <summary>The earlier edit's write: the brokerage's row alone, its holdings account
    /// left in the code it had.</summary>
    private async Task DriftAsync(Guid brokerageId, string currency)
    {
        await using var seed = Db.NewDbContext();
        await seed.Accounts.Where(a => a.Id == brokerageId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, currency));
    }

    /// <summary>
    /// A brokerage whose holdings account kept the code it had before a currency edit, as an
    /// edit before migration 274 left it: a trade prices its day in the brokerage's
    /// currency, the trade-price check finds nothing, and the holdings_currency projection
    /// reports the pair on its own. Its repair gives the holdings account the brokerage's
    /// currency and writes no price; the trade-price repair writes the prices.
    /// </summary>
    [Fact]
    public async Task A_brokerage_whose_holdings_kept_the_old_currency_prices_its_trades_in_its_own()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security E", ticker: "SECE");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerage = await BrokerageAsync(client, ledger, "Brokerage E", "USD");
        await DriftAsync(brokerage, "EUR");
        Assert.Equal(("EUR", "USD"), await CurrenciesAsync(brokerage));

        await BuyAsync(client, ledger, brokerage, securityId, 10m);
        var bought = await PriceRowAsync(securityId);
        Assert.Equal(PriceSource.Trade, bought.Source);
        Assert.Equal(10m, bought.Price);
        Assert.Equal("EUR", bought.CurrencyCode);

        await using var db = Db.NewDbContext();
        var consistency = Consistency(db);
        var report = await consistency.CheckAsync(ledger.LedgerId);
        var trade = Projection(report, ConsistencyProjections.TradePrices);
        Assert.True(trade.Healthy, "the trade-price check reported a drifted pair: " + Describe(trade));
        // Anchor: the day is examined, so healthy is the comparison's verdict.
        Assert.Equal(1, trade.Checked);
        var holdings = Projection(report, ConsistencyProjections.HoldingsCurrency);
        var mismatch = Assert.Single(holdings.Mismatches);
        Assert.Equal("Brokerage E", mismatch.Scope);
        Assert.Equal("its holdings are recorded in USD, but the brokerage is in EUR", mismatch.Field);
        Assert.Equal(brokerage, mismatch.AccountId);
        Assert.Equal(0m, mismatch.Stored);
        Assert.Equal(0m, mismatch.Expected);
        Assert.Equal(1, holdings.Checked);

        // A dollar row at another price, as a version before migration 274 left a day: the
        // holdings repair must leave it as it is, so a repair that wrote prices shows here.
        await using (var seed = Db.NewDbContext())
        {
            await seed.SecurityPrices.Where(p => p.SecurityId == securityId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 11m).SetProperty(p => p.CurrencyCode, "USD"));
        }

        await consistency.RepairAsync(ledger.LedgerId, ConsistencyProjections.HoldingsCurrency);

        Assert.Equal(("EUR", "EUR"), await CurrenciesAsync(brokerage));
        var untouched = await PriceRowAsync(securityId);
        Assert.Equal((11m, "USD"), (untouched.Price, untouched.CurrencyCode));
        var afterHoldings = await consistency.CheckAsync(ledger.LedgerId);
        Assert.True(Projection(afterHoldings, ConsistencyProjections.HoldingsCurrency).Healthy);
        Assert.Equal(2, Projection(afterHoldings, ConsistencyProjections.TradePrices).Mismatches.Count);

        var repaired = (await consistency.RepairAsync(ledger.LedgerId, ConsistencyProjections.TradePrices)).ProjectionAfter();
        Assert.True(repaired.Healthy, "the trade-price repair left drift behind: " + Describe(repaired));
        var row = await PriceRowAsync(securityId);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        var after = await consistency.CheckAsync(ledger.LedgerId);
        Assert.True(after.Healthy, "the repairs left findings: " + string.Join(" | ",
            after.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + Describe(p))));
    }

    /// <summary>
    /// A brokerage whose holdings account kept the old code, edited back to that code: the
    /// holdings account already agrees, and the trades' days are re-derived in the code the
    /// edit sets all the same, as their prices were in the brokerage's.
    /// </summary>
    [Fact]
    public async Task Editing_a_drifted_brokerage_back_to_its_holdings_currency_re_derives_its_trades()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security I", ticker: "SECI");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerage = await BrokerageAsync(client, ledger, "Brokerage I", "USD");
        await DriftAsync(brokerage, "EUR");
        await BuyAsync(client, ledger, brokerage, securityId, 10m);
        // Premise: the trade priced its day in the brokerage's currency.
        Assert.Equal("EUR", (await PriceRowAsync(securityId)).CurrencyCode);

        var edit = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts/{brokerage}",
            new UpdateAccountRequest { CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(("USD", "USD"), await CurrenciesAsync(brokerage));
        var row = await PriceRowAsync(securityId);
        Assert.Equal((10m, "USD"), (row.Price, row.CurrencyCode));
        await using var db = Db.NewDbContext();
        var report = await Consistency(db).CheckAsync(ledger.LedgerId);
        Assert.True(report.Healthy, "the edit left findings: " + string.Join(" | ",
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + Describe(p))));
    }

    /// <summary>
    /// One security bought on one day in two brokerages of different currencies: the day
    /// is priced by the rule's execution, the last by seq, in its brokerage's currency,
    /// and the check agrees.
    /// </summary>
    [Fact]
    public async Task A_day_traded_in_two_currencies_takes_the_currency_of_the_trade_the_rule_picks()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var euro = await ledger.AddInvestmentAccountAsync("Brokerage F", currencyCode: "EUR");
        var pound = await ledger.AddInvestmentAccountAsync("Brokerage G", currencyCode: "GBP");
        var securityId = await ledger.AddSecurityAsync("Security F", "SECF");
        await ledger.AddInvestmentBuyAsync(euro.Id, euro.HoldingsAccountId!.Value, securityId, 10m, 25m, TradeDay);
        await ledger.AddInvestmentBuyAsync(pound.Id, pound.HoldingsAccountId!.Value, securityId, 10m, 21m, TradeDay);
        await ledger.RecomputeTradePricesAsync();

        var row = await PriceRowAsync(securityId);
        Assert.Equal(21m, row.Price);
        Assert.Equal("GBP", row.CurrencyCode);

        await using var db = Db.NewDbContext();
        var trade = Projection(await Consistency(db).CheckAsync(ledger.LedgerId), ConsistencyProjections.TradePrices);
        Assert.True(trade.Healthy, "the check picked another trade than the writer: " + Describe(trade));
    }

    /// <summary>
    /// The writer that took no currency is gone (migration 274). A <c>DROP FUNCTION</c>
    /// naming a signature that matches nothing drops nothing, so the catalog is asked.
    /// </summary>
    [Fact]
    public async Task The_upsert_that_took_no_currency_is_dropped()
    {
        await using var db = Db.NewDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT to_regprocedure('security_price_upsert_from_trade(uuid, uuid, date, numeric)') IS NULL,
                   to_regprocedure('security_price_upsert_from_trade(uuid, uuid, date, numeric, text)') IS NULL
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        // Anchor: the form that takes the currency is there to be found.
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(0));
    }

    /// <summary>
    /// The rule's price alone is gone (migration 274): the reseed reads the rule, which names
    /// the currency with the price, and a second statement of the rule that nothing calls is
    /// one nothing keeps in step. A <c>DROP FUNCTION</c> naming a signature that matches
    /// nothing drops nothing, so the catalog is asked.
    /// </summary>
    [Fact]
    public async Task The_day_rule_that_named_no_currency_is_dropped()
    {
        await using var db = Db.NewDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT to_regprocedure('fn_trade_price_for_day(uuid, uuid, date)') IS NULL,
                   to_regprocedure('fn_trade_execution_for_day(uuid, uuid, date)') IS NULL
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        // Anchor: the rule, which the reseed reads, is there to be found.
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(0));
    }
}
