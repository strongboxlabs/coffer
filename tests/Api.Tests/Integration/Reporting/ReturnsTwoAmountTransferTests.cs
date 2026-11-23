using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The returns report over transfers between currencies, each of which records both
/// amounts (ADR-0114 D13): at account scope a flow is the brokerage leg's own amount, in
/// the brokerage's currency, never the other account's figure; at ledger scope it is that
/// amount converted at its own day's rate (ADR-0114 D7), never the other leg's figure and
/// never the latest rate; and a rollover between two brokerages in different currencies is
/// inside the ledger's perimeter, so it is no flow there, though its two legs differ.
/// </summary>
/// <remarks>
/// Every transfer is written through <c>POST /investment-transactions</c> with its
/// <c>transferAmount</c>, on a synthetic ledger whose reporting currency is the dollar;
/// the rates are stored as the rate fetcher writes them.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsTwoAmountTransferTests : DatabaseTest
{
    public ReturnsTwoAmountTransferTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static DateOnly ServerToday => DateOnly.FromDateTime(DateTime.UtcNow);

    private static readonly DateTime FundedOn = Utc(2025, 3, 3);
    private static readonly DateTime RolledOn = Utc(2025, 6, 2);
    private static readonly DateTime WindowEnd = Utc(2025, 12, 31);

    /// <summary>The euro brokerage's own leg of its funding, from the dollar bank.</summary>
    private const decimal FundingEuros = 1_000m;

    /// <summary>The dollar bank's leg of that funding: its own figure, which is neither the
    /// euro leg at the funding day's rate nor at the latest.</summary>
    private const decimal FundingDollars = -1_150m;

    /// <summary>The euro brokerage's leg of the rollover into the dollar brokerage.</summary>
    private const decimal RolloverEuros = -400m;

    /// <summary>The dollar brokerage's leg of that rollover, its own figure.</summary>
    private const decimal RolloverDollars = 460m;

    private const decimal RateAtFunding = 1.10m;
    private const decimal RateAtRollover = 1.20m;
    private const decimal LatestRate = 1.25m;

    private sealed record Seed(SyntheticLedger Ledger, Guid UsdBank, Guid EurBrokerage, Guid UsdBrokerage);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task TransferAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerage, decimal amount,
        Guid transferAccount, decimal transferAmount, DateTime postedAt)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage,
                Action = "transfer",
                PostedAt = postedAt,
                Amount = amount,
                TransferAccountId = transferAccount,
                TransferAmount = transferAmount,
            });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Stores euro-to-dollar rates on the given days, covered through the server's
    /// UTC today, as the rate fetcher writes them (ADR-0114 D6).</summary>
    private async Task SeedRatesAsync(SyntheticLedger ledger, params (DateOnly Day, decimal Rate)[] rates)
    {
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        foreach (var (day, rate) in rates)
        {
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = day, Rate = rate, Source = FxRateSource.Frankfurter,
            });
        }
        db.FxPairCoverage.Add(new FxPairCoverageRow
        {
            LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD", FetchedThroughDate = ServerToday,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A dollar ledger: the euro brokerage is funded from the dollar bank, then rolls part
    /// of its cash into the dollar brokerage, each transfer with both amounts. With
    /// <paramref name="rollover"/> false the rollover is left out.
    /// </summary>
    private async Task<Seed> SeedAsync(bool rollover = true)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "EUR");
        var usd = await ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
        await SeedRatesAsync(ledger,
            (DateOnly.FromDateTime(FundedOn), RateAtFunding),
            (DateOnly.FromDateTime(RolledOn), RateAtRollover),
            (ServerToday.AddDays(-3), LatestRate));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await TransferAsync(client, ledger, eur.Id, FundingEuros, bank.Id, FundingDollars, FundedOn);
        if (rollover)
            await TransferAsync(client, ledger, eur.Id, RolloverEuros, usd.Id, RolloverDollars, RolledOn);
        return new Seed(ledger, bank.Id, eur.Id, usd.Id);
    }

    private async Task<ReturnsResult> ReturnsAsync(SyntheticLedger ledger, Guid? accountId)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: null, toUtc: WindowEnd, nowUtc: WindowEnd);
    }

    private static ContributionSourceTotals SourceOf(ReturnsResult r, string source) =>
        Assert.Single(r.ContributionsBySource, s => s.Source == source);

    /// <summary>
    /// At account scope each brokerage's flow is its own leg, in its own currency: the euro
    /// brokerage counts its funding and its rollover in euros, and the dollar brokerage
    /// counts its own leg of the rollover, which is not the euro leg negated. Mutation
    /// caught: a flow read from the other account's leg.
    /// </summary>
    [Fact]
    public async Task At_account_scope_a_flow_is_the_brokerage_legs_own_amount()
    {
        var seed = await SeedAsync();

        var eur = await ReturnsAsync(seed.Ledger, seed.EurBrokerage);
        var usd = await ReturnsAsync(seed.Ledger, seed.UsdBrokerage);

        Assert.Equal("EUR", eur.CurrencyCode);
        Assert.Equal(FundingEuros + RolloverEuros, eur.NetContributions);
        Assert.Equal(FundingEuros, SourceOf(eur, "external_accounts").In);
        Assert.Equal(RolloverEuros, SourceOf(eur, "other_investment_accounts").Out);
        Assert.Equal(FundingEuros + RolloverEuros, eur.EndValue);

        Assert.Equal("USD", usd.CurrencyCode);
        Assert.Equal(RolloverDollars, usd.NetContributions);
        Assert.Equal(RolloverDollars, SourceOf(usd, "other_investment_accounts").In);
        Assert.Equal(RolloverDollars, usd.EndValue);
    }

    /// <summary>
    /// At ledger scope the funding is the euro leg converted at the funding day's rate,
    /// not the bank's own dollar figure and not the euro leg at the latest rate. Mutation
    /// caught: a ledger flow read from the bank's leg, or converted at the latest rate.
    /// </summary>
    [Fact]
    public async Task At_ledger_scope_a_flow_is_the_brokerage_leg_converted_on_its_day()
    {
        var seed = await SeedAsync(rollover: false);

        var ledger = await ReturnsAsync(seed.Ledger, null);

        Assert.Equal("USD", ledger.CurrencyCode);
        Assert.Empty(ledger.RateUnavailable);
        // Anchors: the three candidate figures all differ.
        Assert.NotEqual(-FundingDollars, FundingEuros * RateAtFunding);
        Assert.NotEqual(FundingEuros * LatestRate, FundingEuros * RateAtFunding);
        Assert.Equal(FundingEuros * RateAtFunding, ledger.NetContributions);
        Assert.Equal(FundingEuros * RateAtFunding, SourceOf(ledger, "external_accounts").In);
    }

    /// <summary>
    /// A rollover between the two brokerages is no flow at ledger scope, though its legs
    /// are in different currencies and differ once converted: only the funding crosses the
    /// ledger's perimeter. Mutation caught: the perimeter test dropping a leg that faces a
    /// brokerage in scope, which counts the rollover's euro leg converted as a withdrawal.
    /// </summary>
    [Fact]
    public async Task At_ledger_scope_a_rollover_between_currencies_is_no_flow()
    {
        var seed = await SeedAsync();

        var ledger = await ReturnsAsync(seed.Ledger, null);

        Assert.Empty(ledger.RateUnavailable);
        Assert.Equal(FundingEuros * RateAtFunding, ledger.NetContributions);
        Assert.DoesNotContain(ledger.ContributionsBySource, s => s.Source == "other_investment_accounts");
        // Anchor: the rollover is in the ledger, and moved money between the brokerages.
        var eur = await ReturnsAsync(seed.Ledger, seed.EurBrokerage);
        Assert.Equal(RolloverEuros, SourceOf(eur, "other_investment_accounts").Out);
    }
}
