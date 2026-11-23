using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// What one returns window counts: an event dated on either edge lands in exactly one
/// window, and in that window's return or its flows, never in a valuation alone; and
/// the window's gain is what its flows do not explain.
/// </summary>
/// <remarks>
/// The register stores a date as midnight UTC, and the home screen asks for a window
/// that opens at a local date's midnight UTC, so an event dated a window's first day
/// sits exactly on its start instant. Valued AT that instant, it would land in the
/// start value and count in no window's return; valued at an explicit end, a flow
/// dated there would sit in the end value with no flow to explain it. Each case
/// below pins one edge.
/// <para>
/// Every write goes through the API, as the app makes it, so the balance, holdings
/// and trade-price interceptors run.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReturnsWindowTests : DatabaseTest
{
    public ReturnsWindowTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // A calendar year, asked for as the home screen asks: from the first day's
    // midnight UTC to the end of the caller's last day.
    private static readonly DateTime WindowStart = Utc(2026, 1, 1);
    private static readonly DateOnly WindowLastDay = new(2026, 12, 31);

    // Before the window: cash in, part of it spent on a position whose price nothing
    // later changes, so the portfolio is worth exactly the cash put in.
    private static readonly DateTime FundedOn = Utc(2025, 6, 2);
    private static readonly DateTime BoughtOn = Utc(2025, 6, 3);
    private const decimal Funding = 1_000m;
    private const decimal Shares = 10m;
    private const decimal SharePrice = 50m;
    private const decimal ValueBeforeTheWindow = Funding;

    private sealed record Portfolio(Guid BankId, Guid BrokerageId, Guid SecurityId, Guid IncomeId);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Money from the bank into the brokerage, entered from the bank's register.</summary>
    private static async Task ContributeAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid brokerageId, decimal amount, DateTime postedAt)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                SourceAccountId = bankId,
                Postings = [new TransactionPosting { CounterpartyAccountId = brokerageId, Amount = -amount }],
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task InvestAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Portfolio> FundBeforeTheWindowAsync(SyntheticLedger ledger, HttpClient client)
    {
        var bank = await ledger.AddBankAccountAsync("Account A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        var income = await ledger.AddCategoryAsync("Category A", kind: "income");

        await ContributeAsync(client, ledger, bank.Id, brokerage.Id, Funding, FundedOn);
        await InvestAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            Action = "buy",
            SecurityId = security,
            Shares = Shares,
            Price = SharePrice,
            PostedAt = BoughtOn,
        });
        return new Portfolio(bank.Id, brokerage.Id, security, income.Id);
    }

    private InvestmentReportingRepository NewRepo() => new(Db.NewDbContext(), TimeProvider.System);

    /// <summary>
    /// A dividend dated the window's first day is that window's return. The start value
    /// is the portfolio before it, so the time-weighted and money-weighted rates and the
    /// change in value all carry it.
    /// </summary>
    [Fact]
    public async Task A_dividend_on_the_first_day_is_return_in_that_window()
    {
        const decimal dividend = 100m;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var p = await FundBeforeTheWindowAsync(ledger, client);

        await InvestAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = p.BrokerageId,
            Action = "dividend_cash",
            SecurityId = p.SecurityId,
            CategoryAccountId = p.IncomeId,
            Amount = dividend,
            PostedAt = WindowStart,
        });

        var r = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: null, today: WindowLastDay);

        // The anchor: the dividend reached the end value at all.
        Assert.Equal(ValueBeforeTheWindow + dividend, r.EndValue);
        Assert.Equal(ValueBeforeTheWindow, r.StartValue);
        Assert.Equal(0m, r.NetContributions);
        Assert.Equal(dividend, r.Gain);

        // The year's return is the dividend over the value it was earned on.
        var expected = (double)(dividend / ValueBeforeTheWindow);
        Assert.NotNull(r.TimeWeightedReturn);
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value - expected) < 1e-6,
            $"expected TWR {expected}, got {r.TimeWeightedReturn}");
        Assert.NotNull(r.MoneyWeightedReturn);
        Assert.True(Math.Abs(r.MoneyWeightedReturn!.Value - expected) < 1e-4,
            $"expected MWR {expected}, got {r.MoneyWeightedReturn}");
    }

    /// <summary>
    /// A contribution dated exactly at an explicit end belongs to the next window: it is
    /// in neither this window's end value nor its flows, so it cannot read as return,
    /// and the window that starts there counts it as a flow.
    /// </summary>
    [Fact]
    public async Task A_contribution_at_an_explicit_end_belongs_to_the_next_window()
    {
        const decimal contribution = 500m;
        var end = Utc(2026, 7, 1);
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var p = await FundBeforeTheWindowAsync(ledger, client);

        await ContributeAsync(client, ledger, p.BankId, p.BrokerageId, contribution, end);

        var after = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: end, toUtc: null, today: WindowLastDay);
        // The anchor: the contribution is real, and the window starting at it has it.
        Assert.Equal(contribution, after.NetContributions);
        Assert.Equal(ValueBeforeTheWindow, after.StartValue);
        Assert.Equal(ValueBeforeTheWindow + contribution, after.EndValue);

        var before = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: end, today: WindowLastDay);
        Assert.Equal(end, before.EndDate);
        Assert.Equal(ValueBeforeTheWindow, before.EndValue);
        Assert.Equal(0m, before.NetContributions);
        Assert.Equal(0m, before.Gain);
        Assert.NotNull(before.TimeWeightedReturn);
        Assert.True(Math.Abs(before.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {before.TimeWeightedReturn}");
    }

    /// <summary>
    /// Allocation reconciles with a window that has an explicit end at the instant that
    /// window's end value is taken, the one before the end; allocation AT the end counts
    /// what is dated there, which belongs to the next window.
    /// </summary>
    [Fact]
    public async Task Allocation_reconciles_with_an_explicit_end_at_the_instant_before_it()
    {
        const decimal contribution = 500m;
        var end = Utc(2026, 7, 1);
        var instantBefore = end.AddTicks(-TimeSpan.TicksPerMicrosecond);
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var p = await FundBeforeTheWindowAsync(ledger, client);
        await ContributeAsync(client, ledger, p.BankId, p.BrokerageId, contribution, end);

        var returns = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: end, today: WindowLastDay);

        // The anchor: allocation at the end itself holds the contribution dated there.
        var atEnd = await NewRepo().AllocationAsync(ledger.LedgerId, asOfUtc: end);
        Assert.Equal(returns.EndValue + contribution, atEnd.TotalMarketValue + atEnd.ExcludedBrokerageCash);

        var before = await NewRepo().AllocationAsync(ledger.LedgerId, asOfUtc: instantBefore);
        Assert.Equal(returns.EndValue, before.TotalMarketValue + before.ExcludedBrokerageCash);
    }

    /// <summary>
    /// A contribution dated exactly at the window's start is counted once, as a flow: the
    /// start value is the portfolio before it, for the report and for its account's row.
    /// </summary>
    [Fact]
    public async Task A_contribution_at_the_start_is_counted_once_as_a_flow()
    {
        const decimal contribution = 500m;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var p = await FundBeforeTheWindowAsync(ledger, client);

        await ContributeAsync(client, ledger, p.BankId, p.BrokerageId, contribution, WindowStart);

        var r = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: null, today: WindowLastDay);

        Assert.Equal(ValueBeforeTheWindow + contribution, r.EndValue);
        Assert.Equal(contribution, r.NetContributions);
        Assert.Equal(ValueBeforeTheWindow, r.StartValue);
        Assert.Equal(0m, r.Gain);

        var row = Assert.Single(r.Accounts!);
        Assert.Equal(p.BrokerageId, row.AccountId);
        Assert.Equal(r.StartValue, row.StartValue);

        Assert.NotNull(r.TimeWeightedReturn);
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {r.TimeWeightedReturn}");
        Assert.NotNull(r.MoneyWeightedReturn);
        Assert.True(Math.Abs(r.MoneyWeightedReturn!.Value) < 1e-6,
            $"expected ~0% MWR, got {r.MoneyWeightedReturn}");
    }

    /// <summary>
    /// The window's gain is the position's change in market value plus the dividend
    /// less its fee, at every magnitude, with a contribution inside the window: the
    /// contribution is the only flow, and the dividend and fee stay in the return.
    /// </summary>
    /// <remarks>
    /// Bought at the buy price, the position has reached the sell price before the
    /// window opens and rises by the same ratio again inside it, so the start value
    /// comes from the price dated before the first day rather than from the trade.
    /// Each price is dated on a day with no trade, because a trade writes its own price
    /// row for its day and a price is unique per security and day.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Boundary.Positions), MemberType = typeof(Boundary))]
    public async Task Gain_is_the_price_move_plus_the_dividend_less_the_fee(Boundary.Position p)
    {
        const decimal contribution = 500m;
        const decimal dividend = 120m;
        const decimal fee = 7.5m;
        var startPrice = p.SellPrice;
        var endPrice = decimal.Round(p.SellPrice * p.Ratio, Boundary.PriceScale);

        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        var income = await ledger.AddCategoryAsync("Category A", kind: "income");
        var fees = await ledger.AddCategoryAsync("Category B", kind: "expense");

        // Before the window: exactly the cost funded and spent, so no cash is left.
        await ContributeAsync(client, ledger, bank.Id, brokerage.Id, p.Basis, FundedOn);
        await InvestAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            Action = "buy",
            SecurityId = security,
            Shares = p.Quantity,
            Price = p.BuyPrice,
            PostedAt = BoughtOn,
        });
        await ledger.AddSecurityPriceAsync(security, startPrice, Utc(2025, 12, 15));

        // Inside it: money in from the bank, a dividend with a fee, and a new price.
        await ContributeAsync(client, ledger, bank.Id, brokerage.Id, contribution, Utc(2026, 3, 2));
        await InvestAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            Action = "dividend_cash",
            SecurityId = security,
            CategoryAccountId = income.Id,
            Amount = dividend,
            FeeAccountId = fees.Id,
            FeeAmount = fee,
            PostedAt = Utc(2026, 6, 1),
        });
        await ledger.AddSecurityPriceAsync(security, endPrice, Utc(2026, 9, 15));

        var r = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: null, today: WindowLastDay);

        var startMarketValue = ReportingScale.MarketValue(p.Quantity, startPrice);
        var endMarketValue = ReportingScale.MarketValue(p.Quantity, endPrice);

        // The anchors: both ends are the position at its price plus the cash that moved.
        Assert.Equal("ledger", r.Scope);
        Assert.Equal(startMarketValue, r.StartValue);
        Assert.Equal(endMarketValue + contribution + dividend - fee, r.EndValue);

        Assert.Equal(contribution, r.NetContributions);
        Assert.Equal(endMarketValue - startMarketValue + dividend - fee, r.Gain);
        Assert.Equal(r.EndValue - r.StartValue - r.NetContributions, r.Gain);
    }

    /// <summary>
    /// A window with no explicit end counts up to and including the last instant of the
    /// caller's today, the instant the overview values investments at. A contribution
    /// dated at that instant is in the end value, so it is a flow of the window too.
    /// </summary>
    [Fact]
    public async Task A_contribution_at_the_last_counted_instant_is_a_flow_of_the_window()
    {
        const decimal contribution = 500m;
        var lastCounted = ScheduledBoundary.LastCountedInstant(WindowLastDay);
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var p = await FundBeforeTheWindowAsync(ledger, client);

        await ContributeAsync(client, ledger, p.BankId, p.BrokerageId, contribution, lastCounted);

        var r = await NewRepo().ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: WindowStart, toUtc: null, today: WindowLastDay);

        // The anchor: the end is that instant, and the contribution is valued into it.
        Assert.Equal(lastCounted, r.EndDate);
        Assert.Equal(ValueBeforeTheWindow + contribution, r.EndValue);
        Assert.Equal(contribution, r.NetContributions);
        Assert.Equal(0m, r.Gain);
        Assert.NotNull(r.TimeWeightedReturn);
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {r.TimeWeightedReturn}");
    }
}
