using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// "Current balance" stops where the register's Scheduled begins (migration 259,
/// ADR-0056 amendment): a row the register shows as Scheduled for the caller's date is
/// not in it until that date reaches it, and an unpriced position is valued by the one
/// no-price rule (ADR-0084 amendment).
/// </summary>
/// <remarks>
/// <para>
/// Before 259 the current balance was the running balance after the LAST register
/// row, whatever its date, and the holdings were the projection, which counts every
/// trade and split ever entered. So one future-dated row, a scheduled payment or a
/// trade typed in ahead of its settlement, moved the sidebar, the dashboard, net
/// worth and the MCP figures today, while the net-worth history, which values every
/// point as of its own date, did not. Two numbers for "now", and nothing to say
/// which one to believe.
/// </para>
/// <para>
/// Every case holds a row dated before the boundary beside one dated after it, so a
/// cut in the wrong place (nothing counted, or everything) fails as loudly as no cut
/// at all. The cases that send no date sit weeks either side of the wall clock, so the
/// server's fallback date cannot race them; the boundary cases send the caller's date
/// (or pin the clock) and put a row ON the boundary day, the only place a cut that is
/// off by a day, or taken in the wrong time zone, can show.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrentBalanceAsOfNowTests : DatabaseTest
{
    public CurrentBalanceAsOfNowTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static DateTime Past(int days) => DateTime.SpecifyKind(Today.AddDays(-days), DateTimeKind.Utc);

    private static DateTime Future(int days) => DateTime.SpecifyKind(Today.AddDays(days), DateTimeKind.Utc);

    // ---- bank side --------------------------------------------------------------

    [Fact]
    public async Task A_bank_row_dated_after_now_is_left_out_of_every_current_balance()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        await ledger.AddTransactionPairAsync(checking.Id, expense.Id, -100m, Past(10));
        await ledger.AddTransactionPairAsync(checking.Id, expense.Id, -250m, Future(10));

        // The opening balance less the past row. Not the opening balance: the past row
        // counts. Not the opening balance less both: the future row does not, yet.
        const decimal today = 900m;

        await using (var db = Db.NewDbContext())
        {
            var balances = new AccountBalancesRepository(db, TimeProvider.System);
            Assert.Equal(today, await balances.GetCurrentBalanceAsync(ledger.LedgerId, checking.Id, today: null));
            Assert.Equal(today, (await balances.GetCurrentBalancesAsync(ledger.LedgerId, activeOnly: false, today: null))[checking.Id]);
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var sidebar = await SidebarAsync(client, ledger);
        Assert.Equal(today, sidebar[checking.Id]);

        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(today, overview.AccountGroups.SelectMany(g => g.Accounts).Single(a => a.Id == checking.Id).Balance);
        Assert.Equal(today, overview.NetWorth);

        // As an MCP bearer runs them: the tools over coffer_app, RLS scoped to the user.
        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var reporting = NewReportingRepository(userDb);
        var listed = await AccountsTools.ListAccounts(reporting, ledger.LedgerId);
        Assert.Equal(today, listed.Single(a => a.Id == checking.Id).Balance);
        var netWorth = await AccountsTools.NetWorth(reporting, ledger.LedgerId);
        Assert.Equal(today, netWorth.NetWorth);
        Assert.Equal(today, netWorth.Breakdown.Single(l => l.AccountId == checking.Id).Balance);
    }

    [Fact]
    public async Task The_register_still_lists_the_future_row_with_its_running_balance()
    {
        // The cut is the CURRENT balance's, not the register's: a scheduled row stays
        // visible where it was entered, carrying the balance it will leave.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        await ledger.AddTransactionPairAsync(checking.Id, expense.Id, -100m, Past(10));
        await ledger.AddTransactionPairAsync(checking.Id, expense.Id, -250m, Future(10));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={checking.Id}&limit=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = Flatten((await response.Content.ReadFromJsonAsync<RegisterPage>())!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(900m, rows.Single(r => r.PostedAt.Date == Past(10)).BalanceAfter);
        Assert.Equal(650m, rows.Single(r => r.PostedAt.Date == Future(10)).BalanceAfter);

        // And the sidebar, read in the same breath, stops at the past row.
        Assert.Equal(900m, (await SidebarAsync(client, ledger))[checking.Id]);
    }

    // ---- investments ------------------------------------------------------------

    [Fact]
    public async Task A_future_buy_sell_and_price_do_not_move_todays_investment_value()
    {
        var seed = await SeedInvestmentLedgerAsync();
        var ledger = seed.Ledger;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The portfolio card: positions as held today, at today's prices.
        var card = await PortfolioCardAsync(client, ledger, seed.BrokerageId);
        Assert.Equal(TodayCash, card.Summary.CashBalance);
        Assert.Equal(TodayHoldingsValue, card.Summary.PortfolioValue);
        Assert.Equal(TodayCostBasis, card.Summary.CostBasis);
        Assert.Equal(TodayCash + TodayHoldingsValue, card.Summary.Total);

        var a = card.Positions.Single(p => p.SecurityId == seed.SecurityA);
        Assert.Equal(10m, a.Quantity);               // the future buy is not held yet
        Assert.Equal(1_000m, a.CostBasis);
        Assert.Equal(110m, a.CurrentPrice);          // not the future-dated price
        Assert.Equal(DateOnly.FromDateTime(Past(10)), a.PriceAsOf);
        Assert.Equal(1_100m, a.CurrentValue);

        // Sold in full by a future sale, so the projection holds none of it. Today
        // it is still held.
        var b = card.Positions.Single(p => p.SecurityId == seed.SecurityB);
        Assert.Equal(4m, b.Quantity);
        Assert.Equal(200m, b.CostBasis);
        Assert.Equal(55m, b.CurrentPrice);
        Assert.Equal(220m, b.CurrentValue);

        // The dashboard and the sidebar value the brokerage the same way.
        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(TodayCash + TodayHoldingsValue,
            overview.AccountGroups.SelectMany(g => g.Accounts).Single(x => x.Id == seed.BrokerageId).Balance);
        Assert.Equal(TodayHoldingsValue, overview.Portfolio.Value);
        Assert.Equal(TodayCostBasis, overview.Portfolio.CostBasis);
        Assert.Equal(TodayBank + TodayCash + TodayHoldingsValue, overview.NetWorth);

        var sidebar = await SidebarAsync(client, ledger);
        Assert.Equal(TodayCash + TodayHoldingsValue, sidebar[seed.BrokerageId]);
    }

    [Fact]
    public async Task A_split_dated_after_now_does_not_multiply_todays_holding()
    {
        // No future trade on this account at all, only a future split: the projection
        // has already applied it, and today's price is on the pre-split basis, so the
        // projection times today's price would count the split before it happens.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500m, Past(60));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 2m, 100m, Past(30));
        await ledger.AddSecuritySplitAsync(security, 3m, Future(20));
        await ledger.RecomputeHoldingsAsync();
        await ledger.AddSecurityPriceAsync(security, 100m, Past(10));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var card = await PortfolioCardAsync(client, ledger, brokerage.Id);
        var position = Assert.Single(card.Positions);
        Assert.Equal(2m, position.Quantity);
        Assert.Equal(200m, card.Summary.PortfolioValue);
        Assert.Equal(500m, card.Summary.Total);      // cash 300 + the position

        Assert.Equal(500m, (await SidebarAsync(client, ledger))[brokerage.Id]);
        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(200m, overview.Portfolio.Value);
        Assert.Equal(1_000m, overview.NetWorth);     // bank 500 + brokerage 500
    }

    // ---- agreement --------------------------------------------------------------

    [Fact]
    public async Task Every_surface_agrees_on_today_for_a_ledger_with_future_rows()
    {
        // Each figure above is pinned on its own; this pins that they are one figure.
        // The net-worth history is the independent witness: it cuts its last point at
        // its own instant through the as-of feeders (account_balance_as_of_instants and
        // holdings_market_value_as_of_set), not through ScheduledBoundary or
        // holdings_as_of_now. 259 did change that feeder's no-price tiers, which every
        // position here has a close for, so they do not decide this case.
        var seed = await SeedInvestmentLedgerAsync();
        var ledger = seed.Ledger;
        const decimal brokerageToday = TodayCash + TodayHoldingsValue;
        const decimal netWorthToday = TodayBank + brokerageToday;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var sidebar = await SidebarAsync(client, ledger);
        var overview = await OverviewAsync(client, ledger);
        var dashboard = overview.AccountGroups.SelectMany(g => g.Accounts).ToDictionary(a => a.Id, a => a.Balance);
        var card = await PortfolioCardAsync(client, ledger, seed.BrokerageId);

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var reporting = NewReportingRepository(userDb);
        var listed = (await AccountsTools.ListAccounts(reporting, ledger.LedgerId))
            .ToDictionary(a => a.Id, a => a.Balance);
        var netWorth = await AccountsTools.NetWorth(reporting, ledger.LedgerId);
        var mcpCard = await AccountsTools.AccountPortfolio(
            NewHoldingsRepository(userDb), ledger.LedgerId, seed.BrokerageId);
        var snapshot = await InvestmentTools.HoldingsSnapshot(
            new InvestmentReportingRepository(userDb, TimeProvider.System), ledger.LedgerId, seed.BrokerageId);
        var history = await reporting.NetWorthHistoryAsync(
            ledger.LedgerId, Past(400), DateTime.UtcNow, ReportTimeBucket.Year);

        foreach (var id in new[] { seed.BankId, seed.BrokerageId })
        {
            Assert.Equal(dashboard[id], sidebar[id]);
            Assert.Equal(dashboard[id], listed[id]);
            Assert.Equal(dashboard[id], netWorth.Breakdown.Single(l => l.AccountId == id).Balance);
        }
        Assert.Equal(TodayBank, dashboard[seed.BankId]);
        Assert.Equal(brokerageToday, dashboard[seed.BrokerageId]);
        Assert.Equal(brokerageToday, card.Summary.Total);
        Assert.Equal(brokerageToday, mcpCard.Summary.Total);
        Assert.Equal(TodayHoldingsValue, snapshot.TotalMarketValue);
        Assert.Equal(TodayCostBasis, snapshot.TotalCostBasis);

        Assert.Equal(netWorthToday, overview.NetWorth);
        Assert.Equal(netWorthToday, netWorth.NetWorth);
        Assert.Equal(netWorthToday, history.Points[^1].NetWorth);
    }

    [Fact]
    public async Task Each_portfolio_card_holds_only_its_own_positions_beside_brokerages_with_future_rows()
    {
        // holdings_as_of_now answers from two places: the projection for an account
        // with nothing dated later, the FIFO walk at now for one with something. A
        // ledger-wide read (the dashboard) has both non-empty in one call, and a
        // card's read is scoped to one account. Two walked accounts, so a walk that
        // ignored the scope would put one's positions on the other's card, and one
        // projection-read account, so a projection read that ignored it would too.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 30_000m);
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var b = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var c = await ledger.AddInvestmentAccountAsync("Brokerage C");
        var aSibling = a.HoldingsAccountId!.Value;
        var bSibling = b.HoldingsAccountId!.Value;
        var cSibling = c.HoldingsAccountId!.Value;
        var x = await ledger.AddSecurityAsync("Fund One", "FNDA");
        var y = await ledger.AddSecurityAsync("Fund Two", "FNDB");
        var z = await ledger.AddSecurityAsync("Fund Three", "FNDC");

        foreach (var brokerage in new[] { a.Id, b.Id, c.Id })
            await ledger.AddTransactionPairAsync(brokerage, bank.Id, 5_000m, Past(60));
        await ledger.AddHoldingAsync(aSibling, x, 0m, 0m);
        await ledger.AddHoldingAsync(bSibling, y, 0m, 0m);
        await ledger.AddHoldingAsync(cSibling, z, 0m, 0m);

        // A: a past buy and a future buy of more (walked).
        await ledger.AddInvestmentBuyAsync(a.Id, aSibling, x, 10m, 100m, Past(30));
        await ledger.AddInvestmentBuyAsync(a.Id, aSibling, x, 5m, 100m, Future(30));
        // B: past trades only (read from the projection).
        await ledger.AddInvestmentBuyAsync(b.Id, bSibling, y, 20m, 50m, Past(30));
        // C: a past buy, sold in full by a future sale (walked).
        await ledger.AddInvestmentBuyAsync(c.Id, cSibling, z, 8m, 25m, Past(30));
        await ledger.AddInvestmentSellAsync(c.Id, cSibling, z, 8m, 30m, Future(30));
        await ledger.RecomputeHoldingsAsync();

        await ledger.AddSecurityPriceAsync(x, 110m, Past(10));
        await ledger.AddSecurityPriceAsync(y, 60m, Past(10));
        await ledger.AddSecurityPriceAsync(z, 40m, Past(10));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var cardA = await PortfolioCardAsync(client, ledger, a.Id);
        var cardB = await PortfolioCardAsync(client, ledger, b.Id);
        var cardC = await PortfolioCardAsync(client, ledger, c.Id);

        var positionA = Assert.Single(cardA.Positions);
        Assert.Equal(x, positionA.SecurityId);
        Assert.Equal(10m, positionA.Quantity);
        Assert.Equal(1_000m, positionA.CostBasis);
        Assert.Equal(1_100m, cardA.Summary.PortfolioValue);
        Assert.Equal(4_000m + 1_100m, cardA.Summary.Total);

        var positionB = Assert.Single(cardB.Positions);
        Assert.Equal(y, positionB.SecurityId);
        Assert.Equal(20m, positionB.Quantity);
        Assert.Equal(1_000m, positionB.CostBasis);
        Assert.Equal(1_200m, cardB.Summary.PortfolioValue);
        Assert.Equal(4_000m + 1_200m, cardB.Summary.Total);

        var positionC = Assert.Single(cardC.Positions);
        Assert.Equal(z, positionC.SecurityId);
        Assert.Equal(8m, positionC.Quantity);
        Assert.Equal(200m, positionC.CostBasis);
        Assert.Equal(320m, cardC.Summary.PortfolioValue);
        Assert.Equal(4_800m + 320m, cardC.Summary.Total);

        // B has nothing dated later, so its card is the projection's answer.
        await using (var db = Db.NewDbContext())
        {
            var projected = Assert.Single(await db.Holdings.AsNoTracking()
                .Where(h => h.AccountId == bSibling && h.Quantity != 0m).ToListAsync());
            Assert.Equal(projected.Quantity, positionB.Quantity);
            Assert.Equal(projected.CostBasis, positionB.CostBasis);
        }

        // The dashboard reads every account in one call; each brokerage's figure there
        // is its card's.
        var overview = await OverviewAsync(client, ledger);
        var dashboard = overview.AccountGroups.SelectMany(g => g.Accounts).ToDictionary(r => r.Id, r => r.Balance);
        var sidebar = await SidebarAsync(client, ledger);
        foreach (var (id, card) in new[] { (a.Id, cardA), (b.Id, cardB), (c.Id, cardC) })
        {
            Assert.Equal(card.Summary.Total, dashboard[id]);
            Assert.Equal(card.Summary.Total, sidebar[id]);
        }
        Assert.Equal(1_100m + 1_200m + 320m, overview.Portfolio.Value);
        Assert.Equal(15_000m + 5_100m + 5_200m + 5_120m, overview.NetWorth);
    }

    [Fact]
    public async Task A_past_split_newer_than_the_latest_price_is_valued_on_the_split_basis()
    {
        // The latest price was observed before a split that has since happened, so it
        // is on the pre-split basis while the quantity held is on the post-split one:
        // common for a security priced by hand or not fed. The net-worth history's
        // feeder restates such a price onto the instant's split basis; every current
        // value must too, or it counts the split twice.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Past(90));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 60m, Past(60));
        await ledger.AddSecurityPriceAsync(security, 100m, Past(40));
        await ledger.AddSecuritySplitAsync(security, 2m, Past(20));
        await ledger.RecomputeHoldingsAsync();

        // Twenty shares since the split, at the observed price restated onto it.
        // Unadjusted, the position would be worth twice this.
        const decimal value = 1_000m;
        const decimal cash = 400m;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var card = await PortfolioCardAsync(client, ledger, brokerage.Id);
        var position = Assert.Single(card.Positions);
        Assert.Equal(20m, position.Quantity);
        Assert.Equal(50m, position.CurrentPrice);
        Assert.Equal(DateOnly.FromDateTime(Past(40)), position.PriceAsOf);
        Assert.Equal(value, position.CurrentValue);
        Assert.Equal(value, card.Summary.PortfolioValue);
        Assert.Equal(cash + value, card.Summary.Total);

        Assert.Equal(cash + value, (await SidebarAsync(client, ledger))[brokerage.Id]);
        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(cash + value,
            overview.AccountGroups.SelectMany(g => g.Accounts).Single(r => r.Id == brokerage.Id).Balance);
        Assert.Equal(value, overview.Portfolio.Value);

        var catalog = await CatalogAsync(client, ledger);
        Assert.Equal(20m, catalog[security].TotalQuantity);
        Assert.Equal(50m, catalog[security].LatestPrice);
        var detail = await SecurityDetailAsync(client, ledger, security);
        Assert.Equal(20m, detail.TotalQuantity);
        Assert.Equal(50m, detail.LatestPrice);

        // The independent witnesses: holdings_snapshot and the history's last point,
        // both through holdings_market_value_as_of_set.
        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var snapshot = await InvestmentTools.HoldingsSnapshot(
            new InvestmentReportingRepository(userDb, TimeProvider.System), ledger.LedgerId, brokerage.Id);
        Assert.Equal(value, snapshot.TotalMarketValue);
        var history = await NewReportingRepository(userDb).NetWorthHistoryAsync(
            ledger.LedgerId, Past(400), DateTime.UtcNow, ReportTimeBucket.Year);
        Assert.Equal(overview.NetWorth, history.Points[^1].NetWorth);
        Assert.Equal(4_000m + cash + value, overview.NetWorth);
    }

    [Fact]
    public async Task The_unbacked_price_advisory_values_what_is_held_now()
    {
        // Ledger settings show the advisory's holding value as what is held. It read
        // the projection, which counts a future-dated buy, at the newest price, which
        // can be dated later too.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Past(60));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 100m, Past(30));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 5m, 120m, Future(30));
        await ledger.RecomputeHoldingsAsync();
        // A trade-source price on a day no trade prices: the advisory's subject.
        await ledger.AddSecurityPriceAsync(security, 110m, Past(10), PriceSource.Trade);
        await ledger.AddSecurityPriceAsync(security, 200m, Future(31));

        // Today's ten shares at today's price; not fifteen at the future one.
        const decimal heldNow = 1_100m;

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var consistency = new LedgerConsistencyRepository(
            userDb, new RegisterRepository(userDb, TimeProvider.System), new HoldingsRecomputeService(userDb),
            TimeProvider.System);
        var advisory = Assert.Single((await consistency.CheckAsync(ledger.LedgerId)).UnbackedPrices);
        Assert.Equal(security, advisory.SecurityId);
        Assert.Equal(heldNow, advisory.HoldingValue);

        // The figure the portfolio card shows for the same security.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var card = await PortfolioCardAsync(client, ledger, brokerage.Id);
        Assert.Equal(heldNow, Assert.Single(card.Positions).CurrentValue);
    }

    [Fact]
    public async Task The_unbacked_price_advisory_values_its_holding_in_the_reporting_currency()
    {
        // The holding value is the position at today's price, in the price's currency,
        // which here is not the holding account's but is the ledger's reporting currency:
        // an identity conversion (ADR-0114 D7).
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "GBP");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var held = await ledger.AddSecurityAsync("Fund One", "FNDA");
        var unheld = await ledger.AddSecurityAsync("Fund Two", "FNDB");
        await ledger.AddHoldingAsync(sibling, held, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, held, 10m, 100m, Past(30));
        await ledger.RecomputeHoldingsAsync();
        // Trade-source prices on days no trade prices: the advisory's subjects.
        await ledger.AddSecurityPriceAsync(held, 110m, Past(20), PriceSource.Trade, currencyCode: "EUR");
        await ledger.AddSecurityPriceAsync(held, 120m, Past(10), PriceSource.Fetch, currencyCode: "EUR");
        await ledger.AddSecurityPriceAsync(unheld, 9m, Past(10), PriceSource.Trade, currencyCode: "CHF");

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var consistency = new LedgerConsistencyRepository(
            userDb, new RegisterRepository(userDb, TimeProvider.System), new HoldingsRecomputeService(userDb),
            TimeProvider.System);
        var advisories = (await consistency.CheckAsync(ledger.LedgerId)).UnbackedPrices
            .ToDictionary(u => u.SecurityId);

        // Anchor: ten shares at today's close.
        Assert.Equal(1_200m, advisories[held].HoldingValue);
        Assert.Equal("EUR", advisories[held].CurrencyCode);
        Assert.Empty(advisories[held].RateUnavailable);
        // Held by nobody, so worth nothing, in the reporting currency.
        Assert.Equal(0m, advisories[unheld].HoldingValue);
        Assert.Equal("EUR", advisories[unheld].CurrencyCode);
    }

    [Fact]
    public async Task An_unpriced_advisory_holding_converts_each_holders_cost_and_fails_closed_without_a_rate()
    {
        // Shares that arrived by share movements, which price nothing (ADR-0084 D6), into
        // two brokerages in different currencies, and one trade-source price dated after
        // today that no trade derives: the advisory's subject, but no current price. So
        // the holding is carried at its cost basis, which is in the holders' currencies,
        // not the price row's, and not the ledger's.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var equity = await ledger.AddCategoryAsync("Category A");
        var first = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "GBP");
        var second = await ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "JPY");
        // Holds nothing; sorts first, so the ledger's currency is neither holder's.
        await ledger.AddInvestmentAccountAsync("Brokerage C", currencyCode: "AUD");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        await ledger.AddHoldingAsync(first.HoldingsAccountId!.Value, security, 0m, 0m);
        await ledger.AddHoldingAsync(second.HoldingsAccountId!.Value, security, 0m, 0m);
        await ledger.AddSharesInAsync(
            first.Id, first.HoldingsAccountId!.Value, equity.Id, security, 10m, 500m, Past(30));
        await ledger.AddSharesInAsync(
            second.Id, second.HoldingsAccountId!.Value, equity.Id, security, 4m, 200m, Past(20));
        await ledger.RecomputeHoldingsAsync();
        await ledger.AddSecurityPriceAsync(security, 60m, Future(10), PriceSource.Trade, currencyCode: "EUR");

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var consistency = new LedgerConsistencyRepository(
            userDb, new RegisterRepository(userDb, TimeProvider.System), new HoldingsRecomputeService(userDb),
            TimeProvider.System);
        // No rates yet: the sum of two currencies has no value, and names both pairs
        // (ADR-0114 D9), never the cost of one holder alone.
        var missing = Assert.Single((await consistency.CheckAsync(ledger.LedgerId)).UnbackedPrices);
        Assert.Equal(security, missing.SecurityId);
        Assert.Null(missing.HoldingValue);
        Assert.Equal("USD", missing.CurrencyCode);
        Assert.Equal(
            ["GBP", "JPY"],
            missing.RateUnavailable.Select(r => r.FromCurrency).Order(StringComparer.Ordinal).ToArray());

        // With the rates: each holder's cost basis converts at the latest rate and sums.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var writer = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            foreach (var (from, rate) in new[] { ("GBP", 1.25m), ("JPY", 0.01m) })
            {
                writer.FxRates.Add(new FxRateRow
                {
                    Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = "USD",
                    RateDate = today.AddDays(-1), Rate = rate, Source = FxRateSource.Frankfurter,
                });
                writer.FxPairCoverage.Add(new FxPairCoverageRow
                {
                    LedgerId = ledger.LedgerId, FromCurrency = from, ToCurrency = "USD", FetchedThroughDate = today,
                });
            }
            await writer.SaveChangesAsync();
        }
        var advisory = Assert.Single((await consistency.CheckAsync(ledger.LedgerId)).UnbackedPrices);
        Assert.Equal(500m * 1.25m + 200m * 0.01m, advisory.HoldingValue);
        Assert.Empty(advisory.RateUnavailable);
    }

    [Fact]
    public async Task The_securities_catalog_and_detail_show_todays_holding_and_price()
    {
        var seed = await SeedInvestmentLedgerAsync();
        var ledger = seed.Ledger;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The catalog shows quantity times price as each security's value, and sorts
        // by it: neither the future buy of A, the future sale of B, nor either future
        // price may move it.
        var catalog = await CatalogAsync(client, ledger);
        Assert.Equal(10m, catalog[seed.SecurityA].TotalQuantity);
        Assert.Equal(110m, catalog[seed.SecurityA].LatestPrice);
        Assert.Equal(DateOnly.FromDateTime(Past(10)), catalog[seed.SecurityA].LatestPriceAsOf);
        Assert.Equal(4m, catalog[seed.SecurityB].TotalQuantity);
        Assert.Equal(55m, catalog[seed.SecurityB].LatestPrice);

        var a = await SecurityDetailAsync(client, ledger, seed.SecurityA);
        Assert.Equal(10m, a.TotalQuantity);
        Assert.Equal(1_000m, a.TotalCostBasis);
        Assert.Equal(110m, a.LatestPrice);
        Assert.Equal(DateOnly.FromDateTime(Past(10)), a.LatestPriceAsOf);
        // The recent-prices list is a history, so the future-dated price is still in it.
        Assert.Equal(200m, a.RecentPrices[0].Price);
        Assert.Equal(DateOnly.FromDateTime(Future(30)), a.RecentPrices[0].AsOf);

        var b = await SecurityDetailAsync(client, ledger, seed.SecurityB);
        Assert.Equal(4m, b.TotalQuantity);
        Assert.Equal(200m, b.TotalCostBasis);
        Assert.Equal(55m, b.LatestPrice);
    }

    // ---- the register's boundary ------------------------------------------------

    // A caller ten hours west of UTC, on the evening of a date whose UTC date is
    // already the next one. The register marks a row dated on that next date as
    // Scheduled for this caller until the caller's own date turns, hours after the
    // UTC date did. A custom zone, so the case reads the same on every host.
    private static readonly TimeZoneInfo WestOfUtc = TimeZoneInfo.CreateCustomTimeZone(
        "Test-West", TimeSpan.FromHours(-10), "Test-West", "Test-West");

    private static DateOnly CallersToday(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, WestOfUtc).DateTime);

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // The boundary day: the caller's tomorrow on the evening below, and already the
    // UTC date then. Far from the wall clock on purpose: nothing here may depend on it.
    private static readonly DateTime BoundaryDay = Utc(2026, 3, 8);

    private sealed record BoundarySeed(
        SyntheticLedger Ledger, Guid BankId, Guid BrokerageId, Guid SecurityId);

    // A bank account funds a brokerage; each has one row dated before the boundary
    // day and one dated on it: a payment on the bank, a buy on the brokerage, and a
    // price for the security.
    private async Task<BoundarySeed> SeedBoundaryLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var expense = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2026, 2, 1));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -100m, Utc(2026, 3, 1));
        await ledger.AddTransactionPairAsync(bank.Id, expense.Id, -250m, BoundaryDay);

        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 100m, Utc(2026, 2, 15));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 5m, 120m, BoundaryDay);
        await ledger.RecomputeHoldingsAsync();

        await ledger.AddSecurityPriceAsync(security, 110m, Utc(2026, 3, 1));
        await ledger.AddSecurityPriceAsync(security, 140m, BoundaryDay);

        return new BoundarySeed(ledger, bank.Id, brokerage.Id, security);
    }

    // Before the caller's date turns: the boundary rows are Scheduled, so none counts.
    private const decimal BankBefore = 4_900m;           // opening, less the transfer and the past payment
    private const decimal BrokerageCashBefore = 4_000m;  // the transfer, less the past buy
    private const decimal HoldingsBefore = 1_100m;       // ten shares at the past price
    // After it turns: every boundary row counts.
    private const decimal BankAfter = 4_650m;
    private const decimal BrokerageCashAfter = 3_400m;
    private const decimal HoldingsAfter = 2_100m;        // fifteen shares at the boundary day's price

    [Fact]
    public async Task A_row_on_the_boundary_day_counts_exactly_when_the_register_stops_calling_it_scheduled()
    {
        var seed = await SeedBoundaryLedgerAsync();
        var ledger = seed.Ledger;

        // The caller's evening: their date is the day before the boundary day, while
        // the UTC date already is the boundary day.
        var evening = new DateTimeOffset(BoundaryDay.AddHours(5), TimeSpan.Zero);
        var before = CallersToday(evening);
        Assert.Equal(DateOnly.FromDateTime(BoundaryDay).AddDays(-1), before);
        Assert.Equal(DateOnly.FromDateTime(BoundaryDay), DateOnly.FromDateTime(evening.UtcDateTime));
        // The next afternoon: the caller's date has turned.
        var after = CallersToday(evening.AddHours(19));
        Assert.Equal(DateOnly.FromDateTime(BoundaryDay), after);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The register, asked with the caller's date, as the SPA asks it.
        Assert.Equal(1, (await StatusCountsAsync(client, ledger, seed.BankId, before)).Scheduled);
        Assert.Equal(1, (await StatusCountsAsync(client, ledger, seed.BrokerageId, before)).Scheduled);
        Assert.Equal(0, (await StatusCountsAsync(client, ledger, seed.BankId, after)).Scheduled);
        Assert.Equal(0, (await StatusCountsAsync(client, ledger, seed.BrokerageId, after)).Scheduled);

        foreach (var (today, bank, cash, holdings) in new[]
                 {
                     (before, BankBefore, BrokerageCashBefore, HoldingsBefore),
                     (after, BankAfter, BrokerageCashAfter, HoldingsAfter),
                 })
        {
            var sidebar = await SidebarAsync(client, ledger, today);
            Assert.Equal(bank, sidebar[seed.BankId]);
            Assert.Equal(cash + holdings, sidebar[seed.BrokerageId]);

            var overview = await OverviewAsync(client, ledger, today);
            var dashboard = overview.AccountGroups.SelectMany(g => g.Accounts).ToDictionary(a => a.Id, a => a.Balance);
            Assert.Equal(bank, dashboard[seed.BankId]);
            Assert.Equal(cash + holdings, dashboard[seed.BrokerageId]);
            Assert.Equal(holdings, overview.Portfolio.Value);
            Assert.Equal(bank + cash + holdings, overview.NetWorth);

            var card = await PortfolioCardAsync(client, ledger, seed.BrokerageId, today);
            Assert.Equal(cash, card.Summary.CashBalance);
            Assert.Equal(holdings, card.Summary.PortfolioValue);
            Assert.Equal(cash + holdings, card.Summary.Total);
        }
    }

    [Fact]
    public async Task Mcp_and_a_caller_with_no_date_follow_the_registers_own_fallback()
    {
        // MCP sends no date, so the register's fallback decides: the server's date in
        // UTC, read from the injected clock. Before the UTC date reaches the boundary
        // day its rows are Scheduled there and count nowhere; after, they count.
        var seed = await SeedBoundaryLedgerAsync();
        var ledger = seed.Ledger;
        var clock = new TestClock(new DateTimeOffset(BoundaryDay.AddHours(-4), TimeSpan.Zero));

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var register = new RegisterRepository(userDb, clock);

        foreach (var (moveBy, scheduled, bank, cash, holdings) in new[]
                 {
                     (TimeSpan.Zero, 1, BankBefore, BrokerageCashBefore, HoldingsBefore),
                     (TimeSpan.FromHours(5), 0, BankAfter, BrokerageCashAfter, HoldingsAfter),
                 })
        {
            clock.Advance(moveBy);
            Assert.Equal(scheduled, (await register.GetStatusCountsAsync(ledger.LedgerId, seed.BankId)).Scheduled);
            Assert.Equal(scheduled, (await register.GetStatusCountsAsync(ledger.LedgerId, seed.BrokerageId)).Scheduled);

            var reporting = NewReportingRepository(userDb, clock);
            var listed = (await AccountsTools.ListAccounts(reporting, ledger.LedgerId))
                .ToDictionary(a => a.Id, a => a.Balance);
            Assert.Equal(bank, listed[seed.BankId]);
            Assert.Equal(cash + holdings, listed[seed.BrokerageId]);

            var netWorth = await AccountsTools.NetWorth(reporting, ledger.LedgerId);
            Assert.Equal(bank + cash + holdings, netWorth.NetWorth);

            var portfolio = await AccountsTools.AccountPortfolio(
                NewHoldingsRepository(userDb, clock), ledger.LedgerId, seed.BrokerageId);
            Assert.Equal(cash, portfolio.Summary.CashBalance);
            Assert.Equal(holdings, portfolio.Summary.PortfolioValue);
        }
    }

    [Fact]
    public async Task A_row_posted_later_today_counts_in_holdings_snapshot_and_allocation_as_everywhere_else()
    {
        // posted_at can carry a time of day (a feed's own timestamp). The register calls
        // a row Scheduled by its date, so a row posted later today, read now, is not
        // Scheduled and every current value counts it. holdings_snapshot and allocation
        // take an instant, and with none asked must read the register's "now", the last
        // instant it does not call Scheduled, not the clock's instant, which would leave
        // that row out.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 5_000m, Utc(2026, 2, 1));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, security, 10m, 100m, Utc(2026, 2, 15));
        await ledger.AddInvestmentBuyAsync(
            brokerage.Id, sibling, security, 5m, 120m, BoundaryDay.AddHours(18));
        await ledger.RecomputeHoldingsAsync();
        await ledger.AddSecurityPriceAsync(security, 110m, Utc(2026, 3, 1));

        // Read at 15:00 UTC on the row's own date, hours before its time of day.
        var clock = new TestClock(new DateTimeOffset(BoundaryDay.AddHours(15), TimeSpan.Zero));
        const decimal cash = 3_400m;          // the transfer, less both buys
        const decimal holdings = 1_650m;      // fifteen shares at the close
        const decimal costBasis = 1_600m;

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var register = new RegisterRepository(userDb, clock);
        Assert.Equal(0, (await register.GetStatusCountsAsync(ledger.LedgerId, brokerage.Id)).Scheduled);

        var portfolio = await AccountsTools.AccountPortfolio(
            NewHoldingsRepository(userDb, clock), ledger.LedgerId, brokerage.Id);
        Assert.Equal(cash, portfolio.Summary.CashBalance);
        Assert.Equal(holdings, portfolio.Summary.PortfolioValue);
        Assert.Equal(costBasis, portfolio.Summary.CostBasis);

        var investments = new InvestmentReportingRepository(userDb, clock);
        var snapshot = await InvestmentTools.HoldingsSnapshot(investments, ledger.LedgerId, brokerage.Id);
        Assert.Equal(portfolio.Summary.PortfolioValue, snapshot.TotalMarketValue);
        Assert.Equal(portfolio.Summary.CostBasis, snapshot.TotalCostBasis);
        Assert.Equal(15m, Assert.Single(snapshot.Holdings).Quantity);

        var allocation = await InvestmentTools.Allocation(investments, ledger.LedgerId);
        Assert.Equal(portfolio.Summary.PortfolioValue, allocation.TotalMarketValue);
        Assert.Equal(portfolio.Summary.CashBalance, allocation.ExcludedBrokerageCash);
    }

    // ---- unpriced positions -----------------------------------------------------

    [Fact]
    public async Task An_unpriced_position_is_valued_at_the_securitys_last_trade_price_today_and_in_history()
    {
        // No security_prices row at all: the raw seeds bypass the interceptor that
        // writes trade prices (ADR-0084 D2). Two brokerages trade the one security, so
        // its last trade in the LEDGER is the second brokerage's, at a price neither
        // position was bought at in full.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 20_000m);
        var a = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var b = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var aSibling = a.HoldingsAccountId!.Value;
        var bSibling = b.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(a.Id, bank.Id, 5_000m, Past(60));
        await ledger.AddTransactionPairAsync(b.Id, bank.Id, 1_000m, Past(60));
        await ledger.AddHoldingAsync(aSibling, security, 0m, 0m);
        await ledger.AddHoldingAsync(bSibling, security, 0m, 0m);
        await ledger.AddInvestmentBuyAsync(a.Id, aSibling, security, 10m, 100m, Past(40));
        await ledger.AddInvestmentBuyAsync(a.Id, aSibling, security, 10m, 120m, Past(20));
        await ledger.AddInvestmentBuyAsync(b.Id, bSibling, security, 2m, 125m, Past(10));
        await ledger.RecomputeHoldingsAsync();

        const decimal lastTrade = 125m;
        const decimal aValue = 2_500m;          // twenty shares at the last trade's price
        const decimal aCost = 2_200m;           // not the cost basis
        const decimal bValue = 250m;
        const decimal aTotal = 2_800m + aValue; // the cash left, plus the position
        const decimal bTotal = 750m + bValue;
        const decimal netWorth = 14_000m + aTotal + bTotal;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var card = await PortfolioCardAsync(client, ledger, a.Id);
        var position = Assert.Single(card.Positions);
        Assert.Equal(20m, position.Quantity);
        Assert.Equal(aCost, position.CostBasis);
        Assert.Equal(lastTrade, position.CurrentPrice);
        Assert.Equal(DateOnly.FromDateTime(Past(10)), position.PriceAsOf);
        Assert.Equal(aValue, position.CurrentValue);
        Assert.Equal(aValue, card.Summary.PortfolioValue);
        Assert.Equal(aTotal, card.Summary.Total);

        var sidebar = await SidebarAsync(client, ledger);
        Assert.Equal(aTotal, sidebar[a.Id]);
        Assert.Equal(bTotal, sidebar[b.Id]);
        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(aValue + bValue, overview.Portfolio.Value);
        Assert.Equal(netWorth, overview.NetWorth);

        var catalog = await CatalogAsync(client, ledger);
        Assert.Equal(lastTrade, catalog[security].LatestPrice);
        Assert.Equal(lastTrade, (await SecurityDetailAsync(client, ledger, security)).LatestPrice);

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var reporting = NewReportingRepository(userDb, TimeProvider.System);
        Assert.Equal(netWorth, (await AccountsTools.NetWorth(reporting, ledger.LedgerId)).NetWorth);
        var snapshot = await InvestmentTools.HoldingsSnapshot(
            new InvestmentReportingRepository(userDb, TimeProvider.System), ledger.LedgerId, a.Id);
        Assert.Equal(aValue, snapshot.TotalMarketValue);
        var history = await reporting.NetWorthHistoryAsync(
            ledger.LedgerId, Past(400), DateTime.UtcNow, ReportTimeBucket.Year);
        Assert.Equal(netWorth, history.Points[^1].NetWorth);
        Assert.Equal(0, history.Points[^1].UnpricedSecurityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_position_that_never_traded_at_a_price_is_carried_at_cost_basis_today_and_in_history(
        bool movementCarriesUnitPrice)
    {
        // Shares that arrived by a share movement, and nothing else: no close and no
        // trade, so every valuation — the net-worth history included — carries the
        // position at its cost basis, not at zero.
        //
        // With a unit price on the movement (an in-kind transfer carrying its basis per
        // share) it is still no trade: a tier that took any leg's unit price would
        // price this position from the movement. The one no-price rule
        // takes executions only, so the position stays unpriced, carried at cost, and
        // flagged.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        var equity = await ledger.AddCategoryAsync("Category B");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Past(60));
        await ledger.AddHoldingAsync(sibling, security, 0m, 0m);
        await ledger.AddSharesInAsync(
            brokerage.Id, sibling, equity.Id, security, 30m, 900m, Past(30), carryUnitPrice: movementCarriesUnitPrice);
        await ledger.RecomputeHoldingsAsync();

        const decimal costBasis = 900m;
        const decimal total = 1_000m + costBasis;
        const decimal netWorth = 4_000m + total;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var card = await PortfolioCardAsync(client, ledger, brokerage.Id);
        var position = Assert.Single(card.Positions);
        Assert.Equal(30m, position.Quantity);
        Assert.Equal(costBasis, position.CostBasis);
        Assert.Null(position.CurrentPrice);
        Assert.Null(position.PriceAsOf);
        Assert.Equal(costBasis, card.Summary.PortfolioValue);
        Assert.Equal(total, card.Summary.Total);
        Assert.Equal(total, (await SidebarAsync(client, ledger))[brokerage.Id]);
        var overview = await OverviewAsync(client, ledger);
        Assert.Equal(netWorth, overview.NetWorth);

        await using var userDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var reporting = NewReportingRepository(userDb, TimeProvider.System);
        var history = await reporting.NetWorthHistoryAsync(
            ledger.LedgerId, Past(400), DateTime.UtcNow, ReportTimeBucket.Year);
        Assert.Equal(netWorth, history.Points[^1].NetWorth);
        // Still flagged: carried at cost is not priced.
        Assert.Equal(1, history.Points[^1].UnpricedSecurityCount);
        var snapshot = await InvestmentTools.HoldingsSnapshot(
            new InvestmentReportingRepository(userDb, TimeProvider.System), ledger.LedgerId, brokerage.Id);
        Assert.Equal(costBasis, snapshot.TotalMarketValue);
    }

    // ---- the investment ledger --------------------------------------------------

    // A bank account funds a brokerage weeks ago. The brokerage bought two securities
    // weeks ago, and has a buy of one and a sale of the other dated weeks ahead, with a
    // price for each dated ahead too. Today it holds what the past trades left, at the
    // past prices.
    private const decimal TodayBank = 10_000m;            // the opening balance, less the transfer out
    private const decimal TodayCash = 8_800m;             // the transfer in, less the two past buys
    private const decimal TodayHoldingsValue = 1_320m;    // each past position at its past price
    private const decimal TodayCostBasis = 1_200m;        // what the two past buys cost

    private sealed record InvestmentSeed(
        SyntheticLedger Ledger, Guid BankId, Guid BrokerageId, Guid SecurityA, Guid SecurityB);

    private async Task<InvestmentSeed> SeedInvestmentLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 20_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var sibling = brokerage.HoldingsAccountId!.Value;
        var a = await ledger.AddSecurityAsync("Fund One", "FNDA");
        var b = await ledger.AddSecurityAsync("Fund Two", "FNDB");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Past(60));

        // The write path creates the projection row; a raw seed has to (see
        // RecomputeHoldingsAsync).
        await ledger.AddHoldingAsync(sibling, a, 0m, 0m);
        await ledger.AddHoldingAsync(sibling, b, 0m, 0m);

        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, a, 10m, 100m, Past(30));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, b, 4m, 50m, Past(30));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, sibling, a, 5m, 120m, Future(30));
        await ledger.AddInvestmentSellAsync(brokerage.Id, sibling, b, 4m, 60m, Future(30));
        await ledger.RecomputeHoldingsAsync();

        await ledger.AddSecurityPriceAsync(a, 110m, Past(10));
        await ledger.AddSecurityPriceAsync(a, 200m, Future(30));
        await ledger.AddSecurityPriceAsync(b, 55m, Past(10));
        await ledger.AddSecurityPriceAsync(b, 70m, Future(30));

        return new InvestmentSeed(ledger, bank.Id, brokerage.Id, a, b);
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    // The caller's local date, as the SPA sends it; none means the server's fallback.
    private static string TodayQuery(DateOnly? today) =>
        today is { } d ? $"?today={d:yyyy-MM-dd}" : string.Empty;

    private static async Task<Dictionary<Guid, decimal>> SidebarAsync(
        HttpClient client, SyntheticLedger ledger, DateOnly? today = null)
    {
        var response = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/accounts/balances{TodayQuery(today)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountBalanceDto[]>())!
            .ToDictionary(b => b.AccountId, b => b.Balance);
    }

    private static async Task<LedgerOverviewDto> OverviewAsync(
        HttpClient client, SyntheticLedger ledger, DateOnly? today = null) =>
        (await client.GetFromJsonAsync<LedgerOverviewDto>(
            $"/api/ledgers/{ledger.LedgerId}/overview{TodayQuery(today)}"))!;

    private static async Task<HoldingsViewDto> PortfolioCardAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, DateOnly? today = null) =>
        (await client.GetFromJsonAsync<HoldingsViewDto>(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{brokerageId}/holdings{TodayQuery(today)}"))!;

    private static async Task<RegisterStatusCounts> StatusCountsAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, DateOnly today) =>
        (await client.GetFromJsonAsync<RegisterStatusCounts>(
            $"/api/ledgers/{ledger.LedgerId}/transactions/status-counts"
            + $"?account_id={accountId}&today={today:yyyy-MM-dd}"))!;

    private static async Task<Dictionary<Guid, SecuritySummaryDto>> CatalogAsync(
        HttpClient client, SyntheticLedger ledger) =>
        (await client.GetFromJsonAsync<List<SecuritySummaryDto>>(
            $"/api/ledgers/{ledger.LedgerId}/securities"))!
        .ToDictionary(s => s.Id);

    private static async Task<SecurityDetailDto> SecurityDetailAsync(
        HttpClient client, SyntheticLedger ledger, Guid securityId) =>
        (await client.GetFromJsonAsync<SecurityDetailDto>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}"))!;

    private static IReadOnlyList<RegisterRowDto> Flatten(RegisterPage page)
    {
        var rows = new List<RegisterRowDto>();
        foreach (var entry in page.Entries)
        {
            if (entry.Kind == RegisterEntryDto.KindTxn && entry.Txn is not null)
                rows.Add(entry.Txn);
            else if (entry.Legs is not null)
                rows.AddRange(entry.Legs);
        }
        return rows;
    }

    // As DI builds them for an MCP call, on the clock given (the server's by default).
    private static AccountsReportingRepository NewReportingRepository(
        AppDbContext db, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var balances = new AccountBalancesRepository(db, clock);
        var investment = new InvestmentReportingRepository(db, clock);
        var overview = new OverviewRepository(db, balances, investment);
        return new AccountsReportingRepository(db, balances, investment, overview);
    }

    private static HoldingsRepository NewHoldingsRepository(AppDbContext db, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        return new(db, new AccountBalancesRepository(db, clock), new InvestmentReportingRepository(db, clock));
    }
}
