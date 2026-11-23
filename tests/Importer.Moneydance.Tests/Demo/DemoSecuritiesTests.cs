using System.Text;
using System.Text.Json.Nodes;
using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The Demo's securities and investment route: the nine securities and fifteen
/// holdings with their type codes, business-daily prices, the real split, rows in
/// Moneydance's native shapes priced at the close, FIFO lots with no oversell, and
/// the dated brokerage and retirement events.
/// </summary>
/// <remarks>
/// <see cref="Plain"/> runs the investment route alone, so its opening buys and
/// events are seen with no other cash arriving. <see cref="Funded"/> runs every
/// pattern (payroll, contributions, the HSA's bills), with Checking's opening
/// balance raised so no contribution waits on the household's spending.
/// </remarks>
public sealed class DemoSecuritiesTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    // Enough for every contribution of the window with no paycheck behind them.
    private const long FundedCheckingOpening = 100_000_00L;

    private static readonly string[] Adr0027Actions =
        ["buy", "buyx", "sell", "sellx", "div", "divr", "divx", "inc", "exp", "bank"];

    private static readonly Lazy<DemoBuild> Build = new(() => DemoExportGenerator.Generate(DemoSpec.Full(AsOf)));

    // The investment route alone: no paycheck, contribution or bill moves cash in.
    private static readonly Lazy<DemoSimulation> Plain =
        new(() => DemoExportGenerator.Run(DemoSpec.Full(AsOf), InvestmentsOnly));

    private static readonly Lazy<DemoSimulation> Funded = new(() => DemoExportGenerator.Run(
        DemoSpec.Full(AsOf), FundedPatterns, RaiseChecking));

    private static readonly Lazy<DemoBuild> FundedBuild = new(() => DemoExportGenerator.Generate(
        DemoSpec.Full(AsOf), FundedPatterns, RaiseChecking));

    private static IReadOnlyList<IDemoPattern> InvestmentsOnly(DemoMarket market) => [new InvestmentsPattern(market)];

    // Every pattern, with checking raised so no contribution waits on spending.
    private static IReadOnlyList<IDemoPattern> FundedPatterns(DemoMarket market) => DemoExportGenerator.Patterns(market);

    private static void RaiseChecking(DemoBook book) =>
        book.Account(DemoAccountRoles.JointChecking).OpeningBalance = FundedCheckingOpening;

    // --- actions -------------------------------------------------------------------

    [Fact]
    public void Only_ADR0027_actions_are_written()
    {
        foreach (var build in new[] { Build.Value, FundedBuild.Value })
        {
            using var export = build.ToExport();
            Assert.Empty(ActionsOutsideAdr0027(export));
        }

        using var plain = Build.Value.ToExport();
        var written = InvestmentTxns(plain).Select(t => t.InvestTxnType!).ToHashSet(StringComparer.Ordinal);
        foreach (var action in new[] { "buy", "buyx", "sell", "sellx", "div", "divr", "divx", "bank" })
            Assert.Contains(action, written);
    }

    [Fact]
    public void The_action_check_fails_on_an_injected_short()
    {
        // The mutation: one short row added to the generated document. The check that
        // passes above must name it, or it passes on any document.
        var document = JsonNode.Parse(Build.Value.Json)!.AsObject();
        var items = document["all_items"]!.AsArray();
        var holding = DemoHoldings.Holding(Plain.Value.Book, DemoAccountRoles.Brokerage, DemoSecurities.Vti);
        items.Add(new JsonObject
        {
            ["obj_type"] = "txn",
            ["id"] = "00000000-0000-8000-8000-000000000001",
            ["acctid"] = Plain.Value.Book.Account(DemoAccountRoles.Brokerage).Id,
            ["desc"] = "Injected short",
            ["dt"] = "20260105",
            ["invest.txntype"] = "short",
            ["xfer_type"] = "xfrtp_shortcover",
            ["0.id"] = "00000000-0000-8000-8000-000000000002",
            ["0.acctid"] = holding.Id,
            ["0.samt"] = "-10000",
            ["0.pamt"] = "250000",
            ["0.invest.splittype"] = "sec",
        });

        using var mutated = MdItemReader.ReadString(document.ToJsonString());
        Assert.Equal(new[] { "short" }, ActionsOutsideAdr0027(mutated));
    }

    // --- lots ----------------------------------------------------------------------

    [Fact]
    public void No_holding_is_oversold_and_every_self_check_passes()
    {
        foreach (var book in new[] { Plain.Value.Book, Funded.Value.Book })
        {
            Assert.Empty(book.FindProblems());
            var holdings = book.Accounts.Where(a => a.IsHolding).ToList();
            Assert.Equal(15, holdings.Count);
            foreach (var holding in holdings)
            {
                _ = book.Disposals(holding.Id);
                Assert.True(book.Shares(holding.Id, AsOf) >= 0, $"{holding.Role} ends short");
            }
        }
    }

    [Fact]
    public void An_oversell_is_a_self_check_failure()
    {
        // Selling a unit more than the brokerage holds must fail the book, not pass
        // quietly as a short position.
        var (book, market) = DemoExportGenerator.Run(DemoSpec.Full(AsOf), DemoExportGenerator.Patterns);
        var desk = new DemoInvestmentDesk(book, market);
        var holding = desk.Holding(DemoAccountRoles.Brokerage, DemoSecurities.Vti);
        var day = DemoCalendar.OnOrPreviousBusinessDay(AsOf);
        book.Post(new TestPattern(), desk.Sell(DemoAccountRoles.Brokerage, DemoSecurities.Vti, day,
            book.Shares(holding.Id, day) + 1));

        Assert.Contains(book.FindProblems(), p => p.Contains("oversells", StringComparison.Ordinal));
    }

    // --- securities and holdings -----------------------------------------------------

    [Fact]
    public void Nine_securities_carry_only_their_ticker_decimals_name_and_visibility()
    {
        using var export = Build.Value.ToExport();
        var securities = export.AllItems.Where(i => i.ObjType == "curr").Select(MdCurr.From).Where(c => c.IsSecurity).ToList();

        Assert.Equal(9, securities.Count);
        Assert.Equal(8, securities.Count(c => c.Ticker is not null));
        Assert.Contains(securities, c => c.Ticker is null && c.Name == "Stable Value Fund");
        foreach (var item in export.AllItems.Where(i => i.ObjType == "curr" && i.GetString("type") == "s"))
        {
            Assert.Null(item.GetString("sec_type"));
            Assert.Null(item.GetString("sec_subtype"));
            Assert.Null(item.GetString("sec_exchange"));
        }
    }

    [Fact]
    public void Fifteen_holdings_carry_the_type_codes_that_give_each_ticker_its_vehicle()
    {
        using var export = Build.Value.ToExport();
        var currById = export.AllItems.Where(i => i.ObjType == "curr").Select(MdCurr.From)
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var holdings = export.AllItems.Where(i => i.ObjType == "acct").Select(MdAcct.From)
            .Where(a => a.IsSecuritySubAccount).ToList();

        Assert.Equal(15, holdings.Count);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VTI"] = "etf", ["VXUS"] = "etf", ["BND"] = "etf", ["VNQ"] = "etf",
            ["VFFVX"] = "mutual_fund", ["VMFXX"] = "money_market", ["NVDA"] = "stock", ["MSFT"] = "stock",
            ["(none)"] = "mutual_fund",
        };
        foreach (var holding in holdings)
        {
            var curr = currById[holding.CurrId!];
            var fields = holding.Security!;
            var classification = MdSecurityType.Translate(fields.SecType, fields.SecSubtype);
            Assert.True(classification.Recognised);
            Assert.Equal(expected[curr.Ticker ?? "(none)"], classification.VehicleType);
            Assert.Equal(curr.Ticker is null, fields.SecExchange is null);
        }

        var perAccount = holdings.GroupBy(h => h.ParentId!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var ids = Build.Value.Manifest.AccountIds;
        Assert.Equal(7, perAccount[ids[DemoAccountRoles.Brokerage]]);
        Assert.Equal(3, perAccount[ids[DemoAccountRoles.RolloverIra]]);
        Assert.Equal(1, perAccount[ids[DemoAccountRoles.FamilyHsa]]);
        Assert.Equal(1, perAccount[ids[DemoAccountRoles.OldPlanA]]);
        Assert.Equal(1, perAccount[ids[DemoAccountRoles.NewPlanA]]);
        Assert.Equal(2, perAccount[ids[DemoAccountRoles.PlanB]]);
    }

    // --- prices --------------------------------------------------------------------

    [Fact]
    public void Price_rows_fall_on_every_business_day_and_on_business_days_only()
    {
        using var export = Build.Value.ToExport();
        var market = Plain.Value.Market;
        var calendar = Plain.Value.Book.Calendar;
        var businessDays = calendar.BusinessDaysInWindow().Select(DemoCalendar.MdDate).ToList();
        var snaps = export.AllItems.Where(i => i.ObjType == "csnap").Select(MdCsnap.From).ToList();

        foreach (var security in DemoSecurities.All)
        {
            var currencyId = market.CurrencyId(security.Key);
            var rows = snaps.Where(s => s.CurrId == currencyId).ToList();
            if (security.Model == DemoPriceModel.StableValue)
            {
                Assert.Empty(rows);
                continue;
            }

            Assert.Equal(businessDays, rows.Select(r => r.Date).ToList());
            foreach (var row in rows)
            {
                var date = DateOnly.ParseExact(row.Date.ToString(System.Globalization.CultureInfo.InvariantCulture), "yyyyMMdd");
                Assert.True(DemoCalendar.IsBusinessDay(date));
                Assert.Equal(market.Close(security.Key, date), decimal.Round(1m / row.Rate!.Value, 4));
            }
        }

        Assert.All(snaps.Where(s => s.CurrId == market.CurrencyId(DemoSecurities.MoneyMarket)),
            s => Assert.Equal(1.0000m, decimal.Round(1m / s.Rate!.Value, 4)));
    }

    [Fact]
    public void The_target_date_fund_is_a_blend_and_stable_value_month_ends_reach_the_manifest()
    {
        var manifest = Build.Value.Manifest;
        Assert.Equal(1m, manifest.TargetDateLookThrough.Values.Sum());
        Assert.Equal(new[] { "BND", "VTI", "VXUS" }, manifest.TargetDateLookThrough.Keys.Order(StringComparer.Ordinal));

        var calendar = Plain.Value.Book.Calendar;
        Assert.Equal(60, manifest.StableValueMonthEnds.Count);
        for (var i = 0; i < manifest.StableValueMonthEnds.Count; i++)
        {
            var end = manifest.StableValueMonthEnds[i];
            Assert.Equal(calendar.MonthEnd(i), end.Date);
            if (i > 0) Assert.True(end.UnitValue > manifest.StableValueMonthEnds[i - 1].UnitValue);
        }
    }

    // --- the split -----------------------------------------------------------------

    [Fact]
    public void The_split_is_written_once_and_no_row_touches_the_stock_on_its_day()
    {
        using var export = Build.Value.ToExport();
        var splits = export.AllItems.Where(i => i.ObjType == "csplit").ToList();
        var split = Assert.Single(splits);
        var nvda = Plain.Value.Market.CurrencyId(DemoSecurities.Nvda);

        Assert.Equal(nvda, split.GetString("curr"));
        Assert.Equal("10.0", split.GetString("ratio"));
        Assert.Equal("1", split.GetString("oldshrs"));
        Assert.Equal("10", split.GetString("newshrs"));
        Assert.Equal(20240610, split.GetInt("dt"));
        Assert.Equal(DemoCalendar.NoonEpochMillis(new DateOnly(2024, 6, 10)), split.GetLong("ts"));

        var nvdaHoldings = export.AllItems.Where(i => i.ObjType == "acct").Select(MdAcct.From)
            .Where(a => a.IsSecuritySubAccount && a.CurrId == nvda).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(nvdaHoldings);
        foreach (var book in new[] { Plain.Value.Book, Funded.Value.Book })
        {
            Assert.DoesNotContain(book.Transactions, t =>
                t.Date == DemoSecurities.SplitDate && t.Splits.Any(s => nvdaHoldings.Contains(s.AccountId)));

            var holding = DemoHoldings.Holding(book, DemoAccountRoles.Brokerage, DemoSecurities.Nvda).Id;
            var before = book.Shares(holding, DemoSecurities.SplitDate.AddDays(-1));
            Assert.True(before > 0);
            Assert.Equal(before * 10, book.Shares(holding, DemoSecurities.SplitDate));
        }
    }

    [Fact]
    public void The_split_is_inside_the_window_until_mid_2029()
    {
        Assert.NotNull(new DemoMarket(new DemoCalendar(new DateOnly(2029, 6, 30)), new DemoRngFactory(1), new DemoIds(1)).SplitDateInWindow);
        Assert.Null(new DemoMarket(new DemoCalendar(new DateOnly(2029, 7, 1)), new DemoRngFactory(1), new DemoIds(1)).SplitDateInWindow);
    }

    // --- trades --------------------------------------------------------------------

    [Fact]
    public void Trades_execute_at_the_close_on_business_days()
    {
        foreach (var (book, market) in new[] { Plain.Value, Funded.Value })
        {
            var holdingKeys = DemoHoldings.Pairs.ToDictionary(
                p => DemoHoldings.Holding(book, p.AccountRole, p.SecurityKey).Id, p => p.SecurityKey, StringComparer.Ordinal);
            var trades = 0;
            foreach (var txn in book.Transactions.Where(t => t.Route == DemoRoute.Investment))
            {
                foreach (var split in txn.Splits.Where(s => s.SplitType == "sec" && s.SplitAmount != 0))
                {
                    trades++;
                    Assert.True(DemoCalendar.IsBusinessDay(txn.Date));
                    var key = holdingKeys[split.AccountId];
                    var security = DemoSecurities.Get(key);
                    var price = market.Close(key, txn.Date);
                    var cash = -split.ParentAmount;
                    if (split.SplitAmount > 0)
                    {
                        var shares = decimal.Round(cash / 100m / price * security.ShareUnit, 0, MidpointRounding.AwayFromZero);
                        Assert.Equal(shares, split.SplitAmount);
                    }
                    else
                    {
                        var proceeds = decimal.Round(-split.SplitAmount * price * 100m / security.ShareUnit, 0, MidpointRounding.AwayFromZero);
                        Assert.Equal(proceeds, split.ParentAmount);
                    }
                }

                var hasFee = txn.Splits.Any(s => s.SplitType == "fee");
                var stockTrade = txn.Splits.Any(s => s.SplitType == "sec" && s.SplitAmount != 0
                    && DemoSecurities.Get(holdingKeys[s.AccountId]).IsStock);
                Assert.Equal(stockTrade, hasFee);
            }
            Assert.True(trades > 50, $"only {trades} trades");
        }
    }

    [Fact]
    public void Opening_positions_are_bought_on_the_first_business_day_from_the_opening_cash()
    {
        var book = Plain.Value.Book;
        var first = DemoCalendar.OnOrNextBusinessDay(book.Calendar.WindowStart);
        foreach (var role in new[] { DemoAccountRoles.Brokerage, DemoAccountRoles.OldPlanA, DemoAccountRoles.PlanB })
        {
            var account = book.Account(role);
            var buys = book.Transactions.Where(t => t.AccountId == account.Id && t.Date == first && t.InvestTxnType == "buy").ToList();
            Assert.Equal(DemoHoldings.SecuritiesOf(role).Count, buys.Count);
            var left = book.BalanceAtEndOf(account.Id, first);
            var reserve = role == DemoAccountRoles.Brokerage ? InvestmentTerms.BrokerageCashReserve : 0L;
            Assert.Equal(reserve, left);
        }
    }

    // --- events --------------------------------------------------------------------

    [Fact]
    public void The_rollover_empties_the_old_plan_into_three_buys_funded_from_it()
    {
        var book = Plain.Value.Book;
        var calendar = book.Calendar;
        var plan = book.Account(DemoAccountRoles.OldPlanA);
        var ira = book.Account(DemoAccountRoles.RolloverIra);
        var month = HouseholdTerms.RolloverMonth;
        bool InMonth(DemoTxn t) => t.Date >= calendar.MonthStart(month) && t.Date <= calendar.MonthEnd(month);

        Assert.Single(book.Transactions, t => t.AccountId == plan.Id && t.InvestTxnType == "sell" && InMonth(t));
        var buyx = book.Transactions.Where(t => t.AccountId == ira.Id && t.InvestTxnType == "buyx" && InMonth(t)).ToList();
        Assert.Equal(3, buyx.Count);
        Assert.All(buyx, t => Assert.Contains(t.Splits, s => s.SplitType == "xfr" && s.AccountId == plan.Id));

        Assert.Equal(0, book.BalanceAtEndOf(plan.Id, AsOf));
        var holding = DemoHoldings.Holding(book, plan.Role, DemoSecurities.TargetDate).Id;
        Assert.Equal(0, book.Shares(holding, AsOf));
        var disposals = book.Disposals(holding);
        Assert.Contains(disposals, d => d.LongTerm);
        Assert.Contains(disposals, d => !d.LongTerm);

        Assert.DoesNotContain(book.Transactions, t => t.Splits.Any(s =>
            s.AccountId == book.Base.Category(MdCategoryType.Income, "Retirement Income", "IRA Distributions")));
    }

    /// <summary>The re-homed "sell + fee" shape: one brokerage stock sale, which
    /// carries a Trading Commission fee split beside its security split, in the
    /// written export.</summary>
    [Fact]
    public void A_stock_trim_is_a_sell_that_carries_its_commission()
    {
        var book = Plain.Value.Book;
        var trim = Assert.Single(book.Marked(InvestmentsPattern.StockTrimMark));
        Assert.Equal(InvestmentTerms.StockTrimMonth, book.Calendar.LifeMonthOf(trim.Date));
        Assert.Equal("sell", trim.InvestTxnType);
        var holding = DemoHoldings.Holding(book, DemoAccountRoles.Brokerage, InvestmentTerms.StockTrimSecurity).Id;
        Assert.Contains(trim.Splits, s => s.SplitType == "sec" && s.AccountId == holding && s.SplitAmount < 0);
        var fee = Assert.Single(trim.Splits, s => s.SplitType == "fee");
        Assert.Equal(book.Base.Category(MdCategoryType.Expense, "Investment", "Trading Commission"), fee.AccountId);
        Assert.Equal(DemoInvestmentDesk.Commission(InvestmentTerms.StockTrimSecurity), fee.SplitAmount);

        using var export = Build.Value.ToExport();
        var written = export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From)
            .Where(t => t.InvestTxnType == "sell")
            .Select(t => t.Splits.Select(s => s.InvestSplitType).Order(StringComparer.Ordinal).ToArray())
            .ToList();
        Assert.Contains(written, kinds => kinds.SequenceEqual(new[] { "fee", "sec" }));
    }

    [Fact]
    public void The_reit_fund_sells_out_for_the_down_payment_with_both_terms()
    {
        var book = Plain.Value.Book;
        var calendar = book.Calendar;
        var brokerage = book.Account(DemoAccountRoles.Brokerage);
        var checking = book.Account(DemoAccountRoles.JointChecking);
        var reit = DemoHoldings.Holding(book, DemoAccountRoles.Brokerage, DemoSecurities.Vnq).Id;

        var lots = book.OpenLots(reit, calendar.MonthEnd(HouseholdTerms.HouseOpens - 1));
        Assert.Equal(2, lots.Count);
        Assert.Equal(InvestmentTerms.SecondReitLotMonth, calendar.LifeMonthOf(lots[1].Acquired));

        var disposals = book.Disposals(reit);
        Assert.Equal(2, disposals.Count);
        Assert.Contains(disposals, d => d.LongTerm);
        Assert.Contains(disposals, d => !d.LongTerm);
        Assert.All(disposals, d => Assert.Equal(HouseholdTerms.HouseOpens, calendar.LifeMonthOf(d.Disposed)));
        Assert.Equal(0, book.Shares(reit, AsOf));

        var day = disposals[0].Disposed;
        var bank = Assert.Single(book.Transactions, t => t.AccountId == brokerage.Id && t.InvestTxnType == "bank");
        Assert.Equal(day, bank.Date);
        Assert.Equal(checking.Id, Assert.Single(bank.Splits).AccountId);
        var sellx = Assert.Single(book.Transactions, t => t.AccountId == brokerage.Id && t.InvestTxnType == "sellx");
        Assert.Equal(day, sellx.Date);
        Assert.Contains(sellx.Splits, s => s.SplitType == "xfr" && s.AccountId == checking.Id && s.SplitAmount > 0);
        Assert.DoesNotContain(book.Transactions, t => t.Date > day && t.Splits.Any(s => s.AccountId == reit && s.SplitAmount != 0));
    }

    [Fact]
    public void The_tagged_events_carry_their_tags()
    {
        var book = Plain.Value.Book;
        var calendar = book.Calendar;

        var rebalance = Assert.Single(book.Transactions, t =>
            t.AccountId == book.Account(DemoAccountRoles.RolloverIra).Id && t.InvestTxnType == "buy"
            && calendar.LifeMonthOf(t.Date) == InvestmentTerms.IraRebalanceMonth);
        Assert.Equal(9, rebalance.Tags!.Split(',').Length);
        Assert.Contains(book.Transactions, t => t.Date == rebalance.Date && t.AccountId == rebalance.AccountId && t.InvestTxnType == "sell");

        var tagged = Assert.Single(book.Transactions, t =>
            t.AccountId == book.Account(DemoAccountRoles.Brokerage).Id && t.Tags is not null
            && calendar.LifeMonthOf(t.Date) == InvestmentTerms.TaggedBuyMonth);
        Assert.Equal("buy", tagged.InvestTxnType);
        Assert.Equal(12, tagged.Tags!.Split(',').Length);
        Assert.Equal(10, tagged.Splits.Single(s => s.SplitType == "sec").Tags!.Split(',').Length);
        Assert.Contains(tagged.Splits, s => s.SplitType == "fee");
        Assert.All(InvestmentTerms.RebalanceTags, tag => Assert.True(tag.Length < 64));
        Assert.Contains(InvestmentTerms.RebalanceTags, tag => tag.Length >= 48);
    }

    // --- dividends -----------------------------------------------------------------

    [Fact]
    public void Dividends_take_the_shape_of_their_account()
    {
        var book = Plain.Value.Book;
        var calendar = book.Calendar;
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        string Holding(string role, string key) => DemoHoldings.Holding(book, role, key).Id;
        IEnumerable<DemoTxn> On(string holdingId, string action) =>
            book.Transactions.Where(t => t.InvestTxnType == action && t.Splits.Any(s => s.AccountId == holdingId));

        Assert.True(On(Holding(DemoAccountRoles.Brokerage, DemoSecurities.Vti), "div").Count() >= 19);
        Assert.True(On(Holding(DemoAccountRoles.Brokerage, DemoSecurities.Bnd), "div").Count() >= 58);
        var msft = On(Holding(DemoAccountRoles.Brokerage, DemoSecurities.Msft), "divx").ToList();
        Assert.True(msft.Count >= 19);
        Assert.All(msft, t => Assert.Contains(t.Splits, s => s.SplitType == "xfr" && s.AccountId == checking));
        Assert.Empty(On(Holding(DemoAccountRoles.Brokerage, DemoSecurities.Msft), "div"));
        Assert.True(On(Holding(DemoAccountRoles.Brokerage, DemoSecurities.MoneyMarket), "divr").Count() >= 59);
        Assert.NotEmpty(On(Holding(DemoAccountRoles.RolloverIra, DemoSecurities.Bnd), "divr"));
        Assert.DoesNotContain(book.Transactions, t =>
            t.AccountId != book.Account(DemoAccountRoles.Brokerage).Id && t.InvestTxnType is "div" or "divx");

        var longTerm = book.Base.Category(MdCategoryType.Income, "Investment", "Long-Term Cap. Gains");
        var shortTerm = book.Base.Category(MdCategoryType.Income, "Investment", "Short-Term Cap. Gains");
        var gains = book.Transactions.Where(t => t.InvestTxnType == "divr").ToList();
        var longYears = gains.Where(t => t.Splits.Any(s => s.AccountId == longTerm)).Select(t => t.Date.Year).Distinct().Count();
        Assert.Equal(5, longYears);
        var shortYears = gains.Where(t => t.Splits.Any(s => s.AccountId == shortTerm))
            .Select(t => DemoCalendar.LifeYearOf(calendar.LifeMonthOf(t.Date))).Distinct().Order().ToList();
        Assert.Equal(new[] { 1, 3, 5 }, shortYears);
        Assert.All(gains.Where(t => t.Splits.Any(s => s.AccountId == longTerm || s.AccountId == shortTerm)),
            t => Assert.Equal(12, t.Date.Month));
    }

    // --- arriving cash ---------------------------------------------------------------

    [Fact]
    public void Each_brokerage_contribution_is_invested_the_next_business_day()
    {
        var book = Funded.Value.Book;
        var brokerage = book.Account(DemoAccountRoles.Brokerage).Id;
        var contributions = book.Transactions.Where(t => t.Pattern == BrokerageContributionPattern.PatternName).ToList();
        // One a month; the current month's falls after AsOf.
        Assert.Equal(60, contributions.Count);
        Assert.All(contributions, t => Assert.Null(t.InvestTxnType));

        var buys = book.Transactions.Where(t => t.AccountId == brokerage && t.InvestTxnType == "buy"
            && t.Memo == "Monthly purchase").ToList();
        foreach (var contribution in contributions)
        {
            var next = DemoCalendar.NextBusinessDayAfter(contribution.Date);
            if (next > AsOf) continue;
            Assert.Contains(buys, b => b.Date == next);
        }
        Assert.DoesNotContain(Plain.Value.Book.Transactions, t => t.Memo == "Monthly purchase");
    }

    // --- the AsOf sweep ------------------------------------------------------------

    [Theory]
    [InlineData("2028-02-29")]
    [InlineData("2026-12-31")]
    [InlineData("2027-01-01")]
    [InlineData("2026-09-07")]
    [InlineData("2027-03-14")]
    [InlineData("2029-07-01")]
    [InlineData("2024-06-30")]
    public void The_investment_route_holds_on_any_AsOf(string asOfText)
    {
        var asOf = DateOnly.ParseExact(asOfText, "yyyy-MM-dd");
        var spec = DemoSpec.Full(asOf);
        foreach (var (book, market) in new[]
        {
            DemoExportGenerator.Run(spec, InvestmentsOnly),
            DemoExportGenerator.Run(spec, FundedPatterns, RaiseChecking),
        })
        {
            Assert.Empty(book.FindProblems());
            Assert.All(book.Transactions, t => Assert.True(t.Date <= asOf));
            Assert.Equal(market.SplitDateInWindow is not null, book.SecuritySplits.Count == 1);
            Assert.Equal(0, book.Shares(DemoHoldings.Holding(book, DemoAccountRoles.Brokerage, DemoSecurities.Vnq).Id, asOf));
            Assert.Equal(3, book.Transactions.Count(t => t.InvestTxnType == "buyx"));
        }
    }

    private static List<MdTxn> InvestmentTxns(MdExport export) =>
        export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From)
            .Where(t => !string.IsNullOrEmpty(t.InvestTxnType)).ToList();

    private static List<string> ActionsOutsideAdr0027(MdExport export) =>
        InvestmentTxns(export).Select(t => t.InvestTxnType!).Where(a => !Adr0027Actions.Contains(a)).Distinct().ToList();

    private sealed class TestPattern : IDemoPattern
    {
        public string Name => "test-oversell";

        public int Rank => 99;

        public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
        {
        }
    }
}
