using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// Ledger overview aggregate (ADR-0056 slice 1): <c>GET /api/ledgers/{id}/overview</c>
/// returns net worth, per-account balances grouped by type, and an investment
/// roll-up. Net worth is a straight sum (liabilities stored negative); the
/// investment contribution = brokerage cash + holdings market value.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OverviewEndpointsTests : DatabaseTest
{
    public OverviewEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Overview_sums_net_worth_across_types_with_investment_value()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        // Opening balances; the transaction pair below exercises the
        // balance_after path on Checking/Savings (each opening, plus or minus the
        // transfer).
        var checkingId = await AddAccountAsync(ledger, "bank", "Account A", 1000m);
        var savingsId = await AddAccountAsync(ledger, "bank", "Account B", 0m);
        await AddAccountAsync(ledger, "credit_card", "Card X", -200m);   // liability: negative
        await AddAccountAsync(ledger, "asset", "Asset Y", 5000m);

        // Investment: no brokerage cash, plus holdings quantity × latest price.
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage Z");
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(
            securityId, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        // Into Checking, out of Savings (a transfer, net-zero overall).
        await ledger.AddTransactionPairAsync(
            checkingId, savingsId, 300m, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<LedgerOverviewDto>())!;

        // assets = bank + asset + investment value
        // liabilities = credit_card (stored negative);  net worth = assets + liabilities
        Assert.Equal(7500m, dto.TotalAssets);
        Assert.Equal(-200m, dto.TotalLiabilities);
        Assert.Equal(7300m, dto.NetWorth);
        Assert.Equal(1500m, dto.InvestmentsValue);
        Assert.Empty(dto.RateUnavailable);

        // Investment roll-up (holdings only): the gain is value less cost, and the
        // percent is that gain over cost.
        Assert.Equal(1500m, dto.Portfolio.Value);
        Assert.Equal(1000m, dto.Portfolio.CostBasis);
        Assert.Equal(500m, dto.Portfolio.UnrealizedGain);
        Assert.Equal(50m, dto.Portfolio.PercentChange);

        // Bank group subtotal = Checking + Savings; Checking shows its opening plus
        // the transfer (proves the shared balance view's balance_after path).
        var bank = dto.AccountGroups.Single(g => g.AccountType == "bank");
        Assert.Equal(1000m, bank.Subtotal);
        Assert.Equal(1300m, bank.Accounts.Single(a => a.Id == checkingId).Balance);

        // The investment group reports the brokerage at cash + holdings value,
        // and the Holdings sibling is NOT listed as its own account.
        var investment = dto.AccountGroups.Single(g => g.AccountType == "investment");
        Assert.Equal(1500m, investment.Subtotal);
        Assert.Single(investment.Accounts);
        Assert.Equal(brokerage.Id, investment.Accounts[0].Id);
    }

    /// <summary>Seeds a fetched EUR→USD rate a few days back, covered through today, as
    /// the rate fetcher writes them (ADR-0114 D6).</summary>
    private async Task SeedEurUsdAsync(SyntheticLedger ledger, decimal rate)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        db.FxRates.Add(new Coffer.Api.Db.Entities.FxRateRow
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
            RateDate = today.AddDays(-3), Rate = rate, Source = Coffer.Api.Db.Entities.FxRateSource.Frankfurter,
        });
        db.FxPairCoverage.Add(new Coffer.Api.Db.Entities.FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = today,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A ledger whose accounts span currencies converts every combined figure into its
    /// reporting currency at the latest rate (ADR-0114 D7); each account row stays in its
    /// own currency.
    /// </summary>
    [Fact]
    public async Task Overview_converts_combined_figures_and_keeps_account_rows_native()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);   // reporting currency USD
        await AddAccountAsync(ledger, "bank", "USD Account", 100m, "USD");
        await AddAccountAsync(ledger, "bank", "EUR Account", 50m, "EUR");
        await SeedEurUsdAsync(ledger, 1.1m);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var dto = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview"))
            .Content.ReadFromJsonAsync<LedgerOverviewDto>())!;
        Assert.Equal("USD", dto.CurrencyCode);
        Assert.Equal(155m, dto.NetWorth);
        Assert.Equal(155m, dto.TotalAssets);
        Assert.Empty(dto.RateUnavailable);
        var bank = dto.AccountGroups.Single(g => g.AccountType == "bank");
        Assert.Equal(155m, bank.Subtotal);
        var eur = bank.Accounts.Single(a => a.CurrencyCode == "EUR");
        Assert.Equal(50m, eur.Balance);
    }

    /// <summary>
    /// A missing rate fails closed (ADR-0114 D9): every combined figure that needs it is
    /// null and names the pair, never a partial sum; the account rows keep their own
    /// figures.
    /// </summary>
    [Fact]
    public async Task Overview_with_a_missing_rate_is_null_and_names_it_never_a_partial_sum()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddAccountAsync(ledger, "bank", "USD Account", 100m, "USD");
        await AddAccountAsync(ledger, "bank", "EUR Account", 50m, "EUR");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var dto = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview"))
            .Content.ReadFromJsonAsync<LedgerOverviewDto>())!;
        Assert.Null(dto.NetWorth);
        Assert.Null(dto.TotalAssets);
        var why = Assert.Single(dto.RateUnavailable);
        Assert.Equal(("EUR", "USD", RateUnavailable.Code), (why.FromCurrency, why.ToCurrency, why.Reason));
        var bank = dto.AccountGroups.Single(g => g.AccountType == "bank");
        Assert.Null(bank.Subtotal);
        Assert.Equal(100m, bank.Accounts.Single(a => a.CurrencyCode == "USD").Balance);
        Assert.Equal(50m, bank.Accounts.Single(a => a.CurrencyCode == "EUR").Balance);
    }

    /// <summary>The portfolio is in the ledger's reporting currency, whatever its
    /// brokerages are kept in (ADR-0114 D7).</summary>
    [Fact]
    public async Task Portfolio_figures_are_in_the_reporting_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Brokerage A", AccountType = "investment", CurrencyCode = "GBP" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var dto = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview"))
            .Content.ReadFromJsonAsync<LedgerOverviewDto>())!;
        Assert.Equal("EUR", dto.CurrencyCode);
        Assert.Equal("EUR", dto.Portfolio.CurrencyCode);
        Assert.Equal("GBP", dto.AccountGroups.Single(g => g.AccountType == "investment").Accounts[0].CurrencyCode);
    }

    [Fact]
    public async Task Overview_returns_422_on_unknown_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{Guid.NewGuid()}/overview");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Overview_includes_a_closed_account_that_still_holds_value()
    {
        // ADR-0085: net worth reflects real value; is_active is a UI-surfacing
        // flag, not a valuation gate. A closed account still holding value counts.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await AddAccountAsync(ledger, "bank", "Open", 1000m);
        var closedId = await AddAccountAsync(ledger, "bank", "Closed but funded", 4000m);
        await ledger.SetIsActiveAsync(closedId, isActive: false);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<LedgerOverviewDto>())!;

        // Both openings sum — the closed account still counts in the total AND
        // its type subtotal (pre-ADR-0085 it was dropped and net worth was the open
        // account's balance alone).
        Assert.Equal(5000m, dto.NetWorth);
        var bank = dto.AccountGroups.Single(g => g.AccountType == "bank");
        Assert.Equal(5000m, bank.Subtotal);
        Assert.Contains(bank.Accounts, a => a.Id == closedId && a.Balance == 4000m);
    }

    private static async Task<Guid> AddAccountAsync(
        SyntheticLedger ledger, string type, string name, decimal opening, string currency = "USD")
    {
        var id = Guid.NewGuid();
        await using var db = ledger.NewDbContext();
        db.Accounts.Add(new AccountRow
        {
            Id = id,
            LedgerId = ledger.LedgerId,
            Name = name,
            AccountType = type,
            CurrencyCode = currency,
            OpeningBalance = opening,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
