using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// The currencies the security detail's hero figures are in: the price's, from the
/// close it was read from, the holding accounts', and the reporting currency for the
/// cost basis, which combines the accounts and converts (ADR-0114 D7). The catalog's row for the security
/// names the same price currency, since its price and amount are the same figures.
/// Accounts, trades and prices are made through the API, the way a person makes them.
/// </summary>
/// <remarks>
/// Each ledger also holds a bank account in a currency that sorts before the
/// brokerages' and is held by nothing, so the ledger's own display currency differs
/// from the holders': a holders' currency read off the ledger instead fails.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SecurityDetailCurrencyTests : DatabaseTest
{
    private const string LedgerOnlyCurrency = "AUD";
    private static readonly DateOnly Today = new(2026, 5, 20);

    public SecurityDetailCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return client;
    }

    private static async Task<Guid> AccountAsync(HttpClient client, SyntheticLedger ledger, string name, string type, string currency)
    {
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = name, AccountType = type, CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task BuyAsync(HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 10m,
                Amount = 100m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private static async Task PriceAsync(
        HttpClient client, SyntheticLedger ledger, Guid securityId, string currency, DateOnly? day = null)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices",
            new CreateSecurityPriceRequest { Price = 12m, PriceDate = day ?? new DateOnly(2026, 5, 10), CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private static async Task<SecurityDetailDto> DetailAsync(HttpClient client, SyntheticLedger ledger, Guid securityId) =>
        (await client.GetFromJsonAsync<SecurityDetailDto>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}?today={Today:yyyy-MM-dd}"))!;

    private static async Task<SecuritySummaryDto> CatalogRowAsync(HttpClient client, SyntheticLedger ledger, Guid securityId) =>
        (await client.GetFromJsonAsync<List<SecuritySummaryDto>>(
            $"/api/ledgers/{ledger.LedgerId}/securities?today={Today:yyyy-MM-dd}"))!
            .Single(r => r.Id == securityId);

    [Fact]
    public async Task The_price_is_in_its_closes_currency_and_the_cost_in_the_holding_accounts()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security A", ticker: "SECA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var brokerage = await AccountAsync(client, ledger, "Brokerage A", "investment", "GBP");
        await BuyAsync(client, ledger, brokerage, securityId);
        await PriceAsync(client, ledger, securityId, "EUR");
        // A close dated after the day is not the latest price, so not its currency.
        await PriceAsync(client, ledger, securityId, "CHF", new DateOnly(2026, 6, 1));

        var detail = await DetailAsync(client, ledger, securityId);

        // Anchors: the close of the tenth is the latest price, and the buy is held.
        Assert.Equal(12m, detail.LatestPrice);
        Assert.Equal(new DateOnly(2026, 5, 10), detail.LatestPriceAsOf);
        Assert.Equal(10m, detail.TotalQuantity);

        Assert.Equal("EUR", detail.PriceCurrencyCode);
        Assert.Equal("GBP", detail.HoldingsCurrencyCode);
        // The cost basis is in the reporting currency (ADR-0114 D7); with no GBP rate it is
        // null and names the lot's day (ADR-0114 D9).
        Assert.Equal(LedgerOnlyCurrency, detail.CostBasisCurrencyCode);
        Assert.Null(detail.TotalCostBasis);
        var why = Assert.Single(detail.CostBasisRateUnavailable);
        Assert.Equal(("GBP", LedgerOnlyCurrency, new DateOnly(2026, 5, 1)), (why.FromCurrency, why.ToCurrency, why.Date));

        // The catalog prints the same price, so in the same currency.
        var row = await CatalogRowAsync(client, ledger, securityId);
        Assert.Equal(12m, row.LatestPrice);
        Assert.Equal("EUR", row.PriceCurrencyCode);
    }

    [Fact]
    public async Task Holding_accounts_in_two_currencies_convert_the_cost_basis_lot_by_lot()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var euro = await AccountAsync(client, ledger, "Brokerage A", "investment", "EUR");
        var pound = await AccountAsync(client, ledger, "Brokerage B", "investment", "GBP");
        await BuyAsync(client, ledger, euro, securityId);
        await BuyAsync(client, ledger, pound, securityId);
        await PriceAsync(client, ledger, securityId, "EUR");

        var detail = await DetailAsync(client, ledger, securityId);

        // Anchor: both buys are held.
        Assert.Equal(20m, detail.TotalQuantity);

        // Ordinal order, so a set spanning currencies names the same code every time.
        Assert.Equal("EUR", detail.HoldingsCurrencyCode);
        Assert.Equal("EUR", detail.PriceCurrencyCode);
        // The cost basis combines both brokerages, so it converts into the reporting
        // currency, never a sum of two (ADR-0114 D7); with no rates, null naming both.
        Assert.Null(detail.TotalCostBasis);
        Assert.Equal(new[] { "EUR", "GBP" },
            detail.CostBasisRateUnavailable.Select(w => w.FromCurrency).Order(StringComparer.Ordinal));

        // With a rate for each on the buy day, each lot converts at it.
        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            foreach (var (from, rate) in new[] { ("EUR", 1.6m), ("GBP", 1.9m) })
            {
                db.FxRates.Add(new Coffer.Api.Db.Entities.FxRateRow
                {
                    Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = LedgerOnlyCurrency,
                    RateDate = new DateOnly(2026, 5, 1), Rate = rate, Source = Coffer.Api.Db.Entities.FxRateSource.Frankfurter,
                });
                db.FxPairCoverage.Add(new Coffer.Api.Db.Entities.FxPairCoverageRow
                {
                    LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = LedgerOnlyCurrency,
                    FetchedThroughDate = DateOnly.FromDateTime(DateTime.UtcNow),
                });
            }
            await db.SaveChangesAsync();
        }
        var converted = await DetailAsync(client, ledger, securityId);
        Assert.Equal((100m * 1.6m) + (100m * 1.9m), converted.TotalCostBasis);
        Assert.Empty(converted.CostBasisRateUnavailable);
    }

    private static async Task RateAsync(
        HttpClient client, SyntheticLedger ledger, string from, string to, DateOnly day, decimal rate)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/fx-rates",
            new { fromCurrency = from, toCurrency = to, date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), rate });
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"{from}->{to} {day:yyyy-MM-dd} returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private static (string, string)[] Pairs(IEnumerable<RateUnavailable> why) =>
        [.. why.Select(w => (w.FromCurrency, w.ToCurrency)).Order()];

    /// <summary>
    /// The hero's market value and gain combine every holding account, as the cost basis
    /// does, so all three are in the reporting currency (ADR-0114 D7): each position from
    /// the price's currency into its brokerage's at the latest rate, then into the
    /// reporting currency at the latest rate, while the price stays in its own. A missing
    /// rate makes null only the figures that need it, naming the pair (ADR-0114 D9).
    /// Rates are typed through the rates API, the way a person adds one.
    /// </summary>
    [Fact]
    public async Task The_market_value_and_gain_convert_each_position_through_its_brokerage_into_the_reporting_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security K", ticker: "SECK");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var pound = await AccountAsync(client, ledger, "Brokerage A", "investment", "GBP");
        var home = await AccountAsync(client, ledger, "Brokerage B", "investment", LedgerOnlyCurrency);
        await BuyAsync(client, ledger, pound, securityId);
        await BuyAsync(client, ledger, home, securityId);
        await PriceAsync(client, ledger, securityId, "EUR");

        var noRates = await DetailAsync(client, ledger, securityId);

        // Anchors: both buys are held at the euro close of the tenth.
        Assert.Equal((20m, (decimal?)12m, "EUR"), (noRates.TotalQuantity, noRates.LatestPrice, noRates.PriceCurrencyCode));

        Assert.Equal(LedgerOnlyCurrency, noRates.MarketValueCurrencyCode);
        Assert.Equal(LedgerOnlyCurrency, noRates.UnrealizedGainCurrencyCode);
        Assert.Null(noRates.MarketValue);
        Assert.Equal(new[] { ("EUR", LedgerOnlyCurrency), ("EUR", "GBP") }, Pairs(noRates.MarketValueRateUnavailable));
        Assert.Null(noRates.UnrealizedGain);
        Assert.Equal(new[] { ("EUR", LedgerOnlyCurrency), ("EUR", "GBP"), ("GBP", LedgerOnlyCurrency) },
            Pairs(noRates.UnrealizedGainRateUnavailable));

        // The price's rates alone are not enough: the pound brokerage's value still has to
        // convert as a whole, so the value names that pair, at the latest rate.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await RateAsync(client, ledger, "EUR", "GBP", yesterday, 0.85m);
        await RateAsync(client, ledger, "EUR", LedgerOnlyCurrency, yesterday, 1.6m);
        var priceRates = await DetailAsync(client, ledger, securityId);
        Assert.Null(priceRates.MarketValue);
        Assert.Equal(new[] { ("GBP", LedgerOnlyCurrency) }, Pairs(priceRates.MarketValueRateUnavailable));

        // With the brokerage's latest rate the value is whole. The basis still needs the
        // buy day's, so it and the gain are null, naming that day; the value is not.
        await RateAsync(client, ledger, "GBP", LedgerOnlyCurrency, yesterday, 1.9m);
        var valued = await DetailAsync(client, ledger, securityId);
        // The pound brokerage's lots go euro to pound to the ledger's currency, each step at
        // its own rate; the other brokerage's lots go euro to the ledger's currency directly.
        Assert.Equal(193.8m + 192m, valued.MarketValue);
        Assert.Empty(valued.MarketValueRateUnavailable);
        Assert.Null(valued.TotalCostBasis);
        Assert.Null(valued.UnrealizedGain);
        var why = Assert.Single(valued.UnrealizedGainRateUnavailable);
        Assert.Equal(("GBP", LedgerOnlyCurrency, new DateOnly(2026, 5, 1)), (why.FromCurrency, why.ToCurrency, why.Date));

        await RateAsync(client, ledger, "GBP", LedgerOnlyCurrency, new DateOnly(2026, 5, 1), 1.95m);
        var whole = await DetailAsync(client, ledger, securityId);
        Assert.Equal(385.8m, whole.MarketValue);
        Assert.Equal((100m * 1.95m) + 100m, whole.TotalCostBasis);
        Assert.Equal(385.8m - 295m, whole.UnrealizedGain);
        Assert.Empty(whole.UnrealizedGainRateUnavailable);
        // The price itself never converts.
        Assert.Equal((12m, "EUR"), (whole.LatestPrice!.Value, whole.PriceCurrencyCode));
    }

    /// <summary>
    /// The security's trades each carry the currency of the brokerage they name, so the
    /// page reads no account list to format them: two brokerages in two currencies, one of
    /// them closed, and neither in the ledger's own display currency.
    /// </summary>
    [Fact]
    public async Task Each_trade_carries_its_brokerages_currency_a_closed_one_included()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security D", ticker: "SECD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var euro = await AccountAsync(client, ledger, "Brokerage A", "investment", "EUR");
        var pound = await AccountAsync(client, ledger, "Brokerage B", "investment", "GBP");
        await BuyAsync(client, ledger, euro, securityId);
        await BuyAsync(client, ledger, pound, securityId);
        var closed = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{pound}/active", new PatchAccountActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        var page = (await client.GetFromJsonAsync<SecurityTransactionsPage>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/transactions"))!;

        // Anchor: both buys are listed, each under the brokerage it was made in.
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("EUR", Assert.Single(page.Items, t => t.AccountId == euro).CurrencyCode);
        Assert.Equal("GBP", Assert.Single(page.Items, t => t.AccountId == pound).CurrencyCode);
    }

    private static async Task PostAsync(HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"{request.Action} returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private static async Task<List<SecurityTransactionDto>> TradesAsync(HttpClient client, SyntheticLedger ledger, Guid securityId) =>
        (await client.GetFromJsonAsync<SecurityTransactionsPage>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/transactions?limit=100"))!.Items.ToList();

    // Transactions that touch two brokerages. Each write gives its legs fresh random
    // ids, so which leg is canonical, and the order the database reads a header's legs
    // in, differ from one transaction to the next: a pick among the header's
    // brokerages that ignores the canonical leg lands on the wrong one in some of them.
    private const int Repeats = 12;

    /// <summary>
    /// An in-kind transfer between brokerages in two currencies has a leg on each, and
    /// its row is named under the brokerage that owns the leg it shows, in that
    /// brokerage's currency: shares leaving under the source, shares arriving under the
    /// destination, never the other side.
    /// </summary>
    [Fact]
    public async Task An_in_kind_transfers_row_names_the_brokerage_that_owns_its_leg()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security I", ticker: "SECI");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var euro = await AccountAsync(client, ledger, "Brokerage A", "investment", "EUR");
        var pound = await AccountAsync(client, ledger, "Brokerage B", "investment", "GBP");
        await BuyAsync(client, ledger, euro, securityId);
        // A move between brokerages in two currencies converts each lot's basis at the
        // rate for its acquisition day, derived through the reporting currency, and is
        // refused without it (ADR-0114 D13): each brokerage's leg on the buy day.
        foreach (var (from, rate) in new[] { ("EUR", 1.6m), ("GBP", 1.9m) })
            await RateAsync(client, ledger, from, LedgerOnlyCurrency, new DateOnly(2026, 5, 1), rate);
        for (var i = 0; i < Repeats; i++)
        {
            await PostAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = euro,
                TransferAccountId = pound,
                Action = LedgerActions.TransferShares,
                SecurityId = securityId,
                Shares = 0.5m,
                PostedAt = new DateTime(2026, 5, 2 + i, 12, 0, 0, DateTimeKind.Utc),
            });
        }

        var trades = await TradesAsync(client, ledger, securityId);

        // Anchors: the buy and every transfer are listed, and each transfer's row shows
        // a leg moving shares, out of the source or into the destination.
        Assert.Equal(1 + Repeats, trades.Count);
        var transfers = trades.Where(t => t.Action == LedgerActions.TransferShares).ToList();
        Assert.Equal(Repeats, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(0.5m, Math.Abs(t.Quantity!.Value)));

        Assert.All(transfers, t =>
        {
            var (owner, currency, name) = t.Quantity < 0
                ? (euro, "EUR", "Brokerage A")
                : (pound, "GBP", "Brokerage B");
            Assert.Equal(owner, t.AccountId);
            Assert.Equal(name, t.AccountName);
            Assert.Equal(currency, t.CurrencyCode);
        });
    }

    /// <summary>
    /// A buy, sell or dividend funded from another brokerage's cash has a leg on that
    /// brokerage too. The row is the trade's, so it is named under the brokerage the
    /// trade was made in, in its currency, never the funding one.
    /// </summary>
    [Fact]
    public async Task A_trade_funded_from_another_brokerage_names_its_own()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security J", ticker: "SECJ");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        var euro = await AccountAsync(client, ledger, "Brokerage A", "investment", "EUR");
        var pound = await AccountAsync(client, ledger, "Brokerage B", "investment", "GBP");
        var madeIncome = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Category A", AccountType = "category", CategoryKind = "income" });
        Assert.Equal(HttpStatusCode.Created, madeIncome.StatusCode);
        var income = (await madeIncome.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // The funding brokerage is in another currency, so each trade carries that
        // brokerage's own figure as transferAmount, opposite in sign to the trade's cash
        // on the brokerage it was made in (ADR-0114 D13): cash leaves the funding
        // brokerage for a buy and arrives in it for a sell or a dividend.
        for (var i = 0; i < Repeats; i++)
        {
            var day = new DateTime(2026, 5, 2 + i, 12, 0, 0, DateTimeKind.Utc);
            await PostAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = euro, TransferAccountId = pound, Action = LedgerActions.BuyXfr,
                SecurityId = securityId, Shares = 2m, Price = 10m, Amount = 20m, TransferAmount = -17.4m, PostedAt = day,
            });
            await PostAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = euro, TransferAccountId = pound, Action = LedgerActions.SellXfr,
                SecurityId = securityId, Shares = -1m, Price = 10m, Amount = 10m, TransferAmount = 8.7m, PostedAt = day,
            });
            await PostAsync(client, ledger, new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = euro, TransferAccountId = pound, CategoryAccountId = income,
                Action = LedgerActions.DivXfr, SecurityId = securityId, Amount = 5m, TransferAmount = 4.35m, PostedAt = day,
            });
        }

        var trades = await TradesAsync(client, ledger, securityId);

        // Anchor: every buy, sell and dividend is listed.
        Assert.Equal(3 * Repeats, trades.Count);

        Assert.All(trades, t =>
        {
            Assert.Equal(euro, t.AccountId);
            Assert.Equal("Brokerage A", t.AccountName);
            Assert.Equal("EUR", t.CurrencyCode);
        });
    }

    [Fact]
    public async Task A_security_nobody_holds_takes_the_ledgers_currency_and_a_zero_basis()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var securityId = await ledger.AddSecurityAsync(name: "Security C", ticker: "SECC");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);

        var detail = await DetailAsync(client, ledger, securityId);

        // Anchors: nothing held, and no price at all.
        Assert.Equal(0m, detail.TotalQuantity);
        Assert.Null(detail.LatestPrice);

        Assert.Equal(LedgerOnlyCurrency, detail.HoldingsCurrencyCode);
        Assert.Equal((LedgerOnlyCurrency, (decimal?)0m), (detail.CostBasisCurrencyCode, detail.TotalCostBasis));
        Assert.Empty(detail.CostBasisRateUnavailable);
        // No price: no market value and no gain, and no rate to name.
        Assert.Equal((null, LedgerOnlyCurrency, null), (detail.MarketValue, detail.MarketValueCurrencyCode, detail.UnrealizedGain));
        Assert.Empty(detail.MarketValueRateUnavailable);
        Assert.Empty(detail.UnrealizedGainRateUnavailable);
        // No price row: the price's currency is the holders'.
        Assert.Equal(LedgerOnlyCurrency, detail.PriceCurrencyCode);
        Assert.Equal(LedgerOnlyCurrency, (await CatalogRowAsync(client, ledger, securityId)).PriceCurrencyCode);
    }

    [Fact]
    public async Task Each_catalog_row_is_in_its_own_securitys_price_currency()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var euroPriced = await ledger.AddSecurityAsync(name: "Security D", ticker: "SECD");
        var francPriced = await ledger.AddSecurityAsync(name: "Security E", ticker: "SECE");
        var unpriced = await ledger.AddSecurityAsync(name: "Security F", ticker: "SECF");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        await PriceAsync(client, ledger, euroPriced, "EUR");
        await PriceAsync(client, ledger, francPriced, "CHF");

        // The catalog is one read for every security; each row still names its own.
        Assert.Equal("EUR", (await CatalogRowAsync(client, ledger, euroPriced)).PriceCurrencyCode);
        Assert.Equal("CHF", (await CatalogRowAsync(client, ledger, francPriced)).PriceCurrencyCode);
        // Neither close is the third security's: with no price row and no holder, the ledger's.
        Assert.Equal(LedgerOnlyCurrency, (await CatalogRowAsync(client, ledger, unpriced)).PriceCurrencyCode);
        foreach (var id in new[] { euroPriced, francPriced, unpriced })
        {
            Assert.Equal(
                (await DetailAsync(client, ledger, id)).PriceCurrencyCode,
                (await CatalogRowAsync(client, ledger, id)).PriceCurrencyCode);
        }
    }

    [Fact]
    public async Task A_security_with_no_current_price_takes_its_newest_price_rows_currency()
    {
        // The ledger's reporting currency is the bank account's (ADR-0114 D7), as a ledger
        // created in it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: LedgerOnlyCurrency);
        var later = await ledger.AddSecurityAsync(name: "Security G", ticker: "SECG");
        var closed = await ledger.AddSecurityAsync(name: "Security H", ticker: "SECH");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await AccountAsync(client, ledger, "Account A", "bank", LedgerOnlyCurrency);
        // Only a close dated after the day: no current price, but a price row to name it.
        await PriceAsync(client, ledger, later, "CHF", new DateOnly(2026, 6, 1));
        // A close on or before the day and a newer one after it: the close the price was
        // read from names it, not the newest row.
        await PriceAsync(client, ledger, closed, "EUR");
        await PriceAsync(client, ledger, closed, "GBP", new DateOnly(2026, 6, 1));

        var detail = await DetailAsync(client, ledger, later);

        // Anchors: no current price for the one, the close of the tenth for the other.
        Assert.Null(detail.LatestPrice);
        Assert.Equal(new DateOnly(2026, 5, 10), (await DetailAsync(client, ledger, closed)).LatestPriceAsOf);

        Assert.Equal("CHF", detail.PriceCurrencyCode);
        Assert.Equal("CHF", (await CatalogRowAsync(client, ledger, later)).PriceCurrencyCode);
        Assert.Equal("EUR", (await DetailAsync(client, ledger, closed)).PriceCurrencyCode);
        Assert.Equal("EUR", (await CatalogRowAsync(client, ledger, closed)).PriceCurrencyCode);
    }
}
