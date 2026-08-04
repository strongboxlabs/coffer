using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// Ledger overview aggregate (ADR-0056 slice 1): <c>GET /api/ledgers/{id}/overview</c>
/// returns net worth, per-account balances grouped by type, and an investment
/// roll-up. Net worth is a straight sum (liabilities stored negative); the
/// investment contribution = brokerage cash + holdings market value.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OverviewEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public OverviewEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Overview_sums_net_worth_across_types_with_investment_value()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        // Opening balances; the transaction pair below exercises the
        // balance_after path on Checking/Savings (1000 + 300 = 1300; 0 - 300).
        var checkingId = await AddAccountAsync(ledger, "bank", "Account A", 1000m);
        var savingsId = await AddAccountAsync(ledger, "bank", "Account B", 0m);
        await AddAccountAsync(ledger, "credit_card", "Card X", -200m);   // liability: negative
        await AddAccountAsync(ledger, "asset", "Asset Y", 5000m);

        // Investment: brokerage cash 0 + holdings 10 × 150 = 1500.
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage Z");
        var securityId = await ledger.AddSecurityAsync("Fund One", ticker: "ETFA");
        await ledger.AddHoldingAsync(brokerage.HoldingsAccountId!.Value, securityId, 10m, 1000m);
        await ledger.AddSecurityPriceAsync(
            securityId, 150m, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        // +300 into Checking, -300 from Savings (a transfer, net-zero overall).
        await ledger.AddTransactionPairAsync(
            checkingId, savingsId, 300m, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<LedgerOverviewDto>())!;

        // assets = bank(1000) + asset(5000) + investment(1500) = 7500
        // liabilities = credit_card(-200);  net worth = 7300
        Assert.Equal(7500m, dto.TotalAssets);
        Assert.Equal(-200m, dto.TotalLiabilities);
        Assert.Equal(7300m, dto.NetWorth);
        Assert.Equal(1500m, dto.InvestmentsValue);
        Assert.False(dto.MixedCurrency);

        // Investment roll-up (holdings only): 1500 value, 1000 cost, +500 / +50%.
        Assert.Equal(1500m, dto.Portfolio.Value);
        Assert.Equal(1000m, dto.Portfolio.CostBasis);
        Assert.Equal(500m, dto.Portfolio.UnrealizedGain);
        Assert.Equal(50m, dto.Portfolio.PercentChange);

        // Bank group subtotal = 1300 + (-300) = 1000; Checking shows 1300
        // (proves the shared balance view's balance_after path).
        var bank = dto.AccountGroups.Single(g => g.AccountType == "bank");
        Assert.Equal(1000m, bank.Subtotal);
        Assert.Equal(1300m, bank.Accounts.Single(a => a.Id == checkingId).Balance);

        // The investment group reports the brokerage at cash + holdings = 1500,
        // and the Holdings sibling is NOT listed as its own account.
        var investment = dto.AccountGroups.Single(g => g.AccountType == "investment");
        Assert.Equal(1500m, investment.Subtotal);
        Assert.Single(investment.Accounts);
        Assert.Equal(brokerage.Id, investment.Accounts[0].Id);
    }

    [Fact]
    public async Task Overview_flags_mixed_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddAccountAsync(ledger, "bank", "USD Account", 100m, "USD");
        await AddAccountAsync(ledger, "bank", "EUR Account", 50m, "EUR");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var dto = (await (await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview"))
            .Content.ReadFromJsonAsync<LedgerOverviewDto>())!;
        Assert.True(dto.MixedCurrency);
    }

    [Fact]
    public async Task Overview_returns_422_on_unknown_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{Guid.NewGuid()}/overview");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Overview_includes_a_closed_account_that_still_holds_value()
    {
        // ADR-0085: net worth reflects real value; is_active is a UI-surfacing
        // flag, not a valuation gate. A closed account still holding value counts.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await AddAccountAsync(ledger, "bank", "Open", 1000m);
        var closedId = await AddAccountAsync(ledger, "bank", "Closed but funded", 4000m);
        await ledger.SetIsActiveAsync(closedId, isActive: false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/overview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<LedgerOverviewDto>())!;

        // 1000 + 4000 = 5000 — the closed account still counts in the total AND
        // its type subtotal (pre-ADR-0085 it was dropped and net worth was 1000).
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
