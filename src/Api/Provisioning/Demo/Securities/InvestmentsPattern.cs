using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The investment side of the household, walked one business day at a time: the
/// opening positions, the dated brokerage and retirement events, every dividend on
/// its security's schedule (<see cref="InvestmentTerms"/>), and the buys that invest
/// cash arriving from other patterns.
/// </summary>
/// <remarks>
/// <para><b>One chronological pass.</b> A dividend is sized by the shares held the
/// day before it, a sale sells what is held, and the rollover sells out a plan whose
/// earlier dividends were reinvested; so one pass in date order posts every row,
/// and each reads the book as it stands. Within a day: the opening buys, the
/// events, the dividends, then the buys of arrived cash.</para>
///
/// <para><b>Arriving cash.</b> Before the pass, the pattern notes each day some other
/// pattern's row raised an investment account's cash (a payroll deferral, an
/// employer match, a contribution) and buys on the next business day. What it buys
/// with is the lowest the account's cash stands at from that day to AsOf, less the
/// account's reserve: so a later outflow another pattern already posted (an HSA
/// bill) is never overdrawn. Patterns that move cash into an investment account
/// must therefore post before this one.</para>
///
/// <para><b>Funding events.</b> The brokerage's later event buys sell money-market
/// shares for any shortfall, so they never depend on a contribution arriving.</para>
///
/// <para>Self-checks: no investment account's cash ends a day below zero; the old
/// plan ends with no cash and no shares; no row touches the splitting stock on its
/// split day; every row that moves shares falls on a business day.</para>
/// </remarks>
public sealed class InvestmentsPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "investments";

    /// <summary>The mark of the brokerage's one stock sale, the <c>sell</c> with a fee.</summary>
    public const string StockTrimMark = "stock-trim";

    private static readonly string[] Invested =
    [
        DemoAccountRoles.Brokerage, DemoAccountRoles.OldPlanA, DemoAccountRoles.PlanB,
        DemoAccountRoles.NewPlanA, DemoAccountRoles.RolloverIra, DemoAccountRoles.FamilyHsa,
    ];

    private readonly DemoMarket _market;

    /// <summary>The pattern over the Demo's market.</summary>
    public InvestmentsPattern(DemoMarket market) => _market = market ?? throw new ArgumentNullException(nameof(market));

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 50;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        new Pass(this, calendar, rng, book, _market).Run();

        book.AddCheck("investment cash", CashChecks);
        book.AddCheck("rollover", b => RolloverChecks(b));
        book.AddCheck("split day", b => SplitDayChecks(b));
        book.AddCheck("trade days", b => TradeDayChecks(b));
    }

    private static IEnumerable<string> CashChecks(DemoBook book)
    {
        foreach (var account in book.Accounts.Where(a => a.MdType == "v"))
        {
            var (lowest, when) = book.LowestDayEnd(account.Id, book.Calendar.WindowStart, book.Calendar.AsOf);
            if (lowest < 0) yield return $"{account.Role} cash ends {when:yyyy-MM-dd} below zero";
        }
    }

    private static IEnumerable<string> RolloverChecks(DemoBook book)
    {
        var plan = book.Account(DemoAccountRoles.OldPlanA);
        var cash = book.BalanceAtEndOf(plan.Id, book.Calendar.AsOf);
        if (cash != 0) yield return $"{plan.Role} keeps {cash} cents after the rollover";
        foreach (var key in DemoHoldings.SecuritiesOf(plan.Role))
        {
            var shares = book.Shares(DemoHoldings.Holding(book, plan.Role, key).Id, book.Calendar.AsOf);
            if (shares != 0) yield return $"{plan.Role} keeps {shares} units of {key} after the rollover";
        }
    }

    private static IEnumerable<string> SplitDayChecks(DemoBook book)
    {
        var holdings = DemoHoldings.Pairs
            .Where(p => p.SecurityKey == DemoSecurities.Nvda)
            .Select(p => DemoHoldings.Holding(book, p.AccountRole, p.SecurityKey).Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var txn in book.Transactions)
        {
            if (txn.Date == DemoSecurities.SplitDate && txn.Splits.Any(s => holdings.Contains(s.AccountId)))
                yield return $"{txn.Pattern} touches the splitting stock on its split day";
        }
    }

    private static IEnumerable<string> TradeDayChecks(DemoBook book)
    {
        foreach (var txn in book.Transactions)
        {
            if (txn.Route == DemoRoute.Investment
                && txn.Splits.Any(s => s.SplitType == "sec" && s.SplitAmount != 0)
                && !DemoCalendar.IsBusinessDay(txn.Date))
            {
                yield return $"{txn.Pattern} moves shares on {txn.Date:yyyy-MM-dd}, a day markets are shut";
            }
        }
    }

    // One run of the pattern over one book.
    private sealed class Pass
    {
        private readonly IDemoPattern _pattern;
        private readonly DemoCalendar _calendar;
        private readonly DemoRngFactory _rng;
        private readonly DemoBook _book;
        private readonly DemoMarket _market;
        private readonly DemoInvestmentDesk _desk;
        private readonly string _dividends;
        private readonly string _longTermGains;
        private readonly string _shortTermGains;

        public Pass(IDemoPattern pattern, DemoCalendar calendar, DemoRngFactory rng, DemoBook book, DemoMarket market)
        {
            _pattern = pattern;
            _calendar = calendar;
            _rng = rng;
            _book = book;
            _market = market;
            _desk = new DemoInvestmentDesk(book, market);
            _dividends = book.Base.Category(MdCategoryType.Income, "Investment", "Dividends");
            _longTermGains = book.Base.Category(MdCategoryType.Income, "Investment", "Long-Term Cap. Gains");
            _shortTermGains = book.Base.Category(MdCategoryType.Income, "Investment", "Short-Term Cap. Gains");
        }

        public void Run()
        {
            var buyDays = ArrivedCashBuyDays();
            var openingDay = DemoCalendar.OnOrNextBusinessDay(_calendar.WindowStart);
            var events = Events();
            var dividends = DividendDays();

            foreach (var day in _market.BusinessDays)
            {
                if (day == openingDay) OpeningPositions(day);
                if (events.TryGetValue(day, out var todays))
                {
                    foreach (var action in todays) action(day);
                }
                if (dividends.TryGetValue(day, out var payments))
                {
                    foreach (var pay in payments) pay(day);
                }
                foreach (var role in Invested)
                {
                    if (buyDays.TryGetValue(role, out var days) && days.Contains(day)) InvestArrivedCash(role, day);
                }
            }
        }

        private DemoTxn Post(DemoTxn txn) => _book.Post(_pattern, txn);

        // --- arriving cash ------------------------------------------------------------

        private Dictionary<string, HashSet<DateOnly>> ArrivedCashBuyDays()
        {
            var accounts = Invested.ToDictionary(r => _book.Account(r).Id, r => r, StringComparer.Ordinal);
            var days = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
            var hsaFrom = _calendar.MonthStart(InvestmentTerms.HsaBuysFrom);

            foreach (var txn in _book.Transactions)
            {
                if (txn.Pattern == _pattern.Name) continue;
                foreach (var (accountId, amount) in _book.CashEffects(txn))
                {
                    if (amount <= 0 || !accounts.TryGetValue(accountId, out var role)) continue;
                    var buyDay = DemoCalendar.NextBusinessDayAfter(txn.Date);
                    if (role == DemoAccountRoles.FamilyHsa && buyDay < hsaFrom) continue;
                    if (!days.TryGetValue(role, out var set)) days[role] = set = [];
                    set.Add(buyDay);
                }
            }
            return days;
        }

        private void InvestArrivedCash(string role, DateOnly day)
        {
            var account = _book.Account(role);
            if (!account.IsActiveOn(day) || day > _calendar.AsOf) return;

            if (role == DemoAccountRoles.Brokerage)
            {
                var available = Investable(role, day, InvestmentTerms.BrokerageCashReserve);
                if (available < InvestmentTerms.MinimumBrokerageBuy) return;
                var key = PickBrokerageSecurity(day);
                var cash = available - DemoInvestmentDesk.Commission(key);
                if (_desk.SharesFor(key, day, cash) > 0) Post(_desk.Buy(role, key, day, cash, memo: "Monthly purchase"));
                return;
            }

            var amount = Investable(role, day, reserve: 0);
            if (amount < InvestmentTerms.MinimumPlanBuy) return;
            BuyMix(role, day, amount, InvestmentTerms.PlanBuys(role), "Contribution purchase");
        }

        private string PickBrokerageSecurity(DateOnly day)
        {
            // One stream per buy day, so the pick on one day moves no other day's.
            var stream = _rng.Stream(
                _pattern.Name + ":brokerage-pick:" + day.Day.ToString("D2", CultureInfo.InvariantCulture), day);
            var draw = stream.NextUnit() * 100m;
            var key = InvestmentTerms.BrokeragePicks[^1].Key;
            var running = 0m;
            foreach (var (candidate, percent) in InvestmentTerms.BrokeragePicks)
            {
                running += percent;
                if (draw < running)
                {
                    key = candidate;
                    break;
                }
            }
            return _desk.CanTrade(key, day) ? key : DemoSecurities.Vti;
        }

        // The lowest the account's cash stands at from the day through AsOf, less the
        // reserve: what can be spent today without overdrawing a later posted outflow.
        private long Investable(string role, DateOnly day, long reserve)
        {
            var (lowest, _) = _book.LowestDayEnd(_book.Account(role).Id, day, _calendar.AsOf);
            return lowest - reserve;
        }

        // Spend the amount across the mix: each line its percent, rounded to the cent,
        // the last line the rest; a stock line pays its commission out of its share.
        private void BuyMix(
            string role, DateOnly day, long amount, IReadOnlyList<(string Key, decimal Percent)> mix, string memo)
        {
            var left = amount;
            for (var i = 0; i < mix.Count; i++)
            {
                var (key, percent) = mix[i];
                var share = i == mix.Count - 1 ? left : DemoAmortization.RoundCents(amount * percent / 100m);
                left -= share;
                var cash = share - DemoInvestmentDesk.Commission(key);
                if (cash <= 0 || !_desk.CanTrade(key, day) || _desk.SharesFor(key, day, cash) <= 0) continue;
                Post(_desk.Buy(role, key, day, cash, memo));
            }
        }

        // --- opening positions --------------------------------------------------------

        private void OpeningPositions(DateOnly day)
        {
            BuyMix(DemoAccountRoles.Brokerage, day,
                Investable(DemoAccountRoles.Brokerage, day, InvestmentTerms.BrokerageCashReserve),
                InvestmentTerms.BrokerageOpening, "Opening position");
            BuyMix(DemoAccountRoles.OldPlanA, day, Investable(DemoAccountRoles.OldPlanA, day, 0),
                InvestmentTerms.OldPlanAOpening, "Opening position");
            BuyMix(DemoAccountRoles.PlanB, day, Investable(DemoAccountRoles.PlanB, day, 0),
                InvestmentTerms.PlanBOpening, "Opening position");
        }

        // --- events -------------------------------------------------------------------

        private Dictionary<DateOnly, List<Action<DateOnly>>> Events()
        {
            var events = new Dictionary<DateOnly, List<Action<DateOnly>>>();
            void At(int lifeMonth, int dayOfMonth, Action<DateOnly> action)
            {
                var day = DemoCalendar.OnOrNextBusinessDay(_calendar.DayInMonth(lifeMonth, dayOfMonth));
                if (!events.TryGetValue(day, out var list)) events[day] = list = [];
                list.Add(action);
            }

            At(HouseholdTerms.RolloverMonth, InvestmentTerms.RolloverDay, Rollover);
            At(InvestmentTerms.SecondReitLotMonth, InvestmentTerms.SecondReitLotDay, SecondReitLot);
            At(HouseholdTerms.HouseOpens, InvestmentTerms.DownPaymentDay, DownPayment);
            At(InvestmentTerms.StockTrimMonth, InvestmentTerms.StockTrimDay, StockTrim);
            At(InvestmentTerms.IraRebalanceMonth, InvestmentTerms.IraRebalanceDay, IraRebalance);
            At(InvestmentTerms.TaggedBuyMonth, InvestmentTerms.TaggedBuyDay, TaggedBuy);
            return events;
        }

        // L16: the old plan sells its whole position; the IRA buys three funds the next
        // business day, each funded by a transfer from the old plan, which ends empty.
        private void Rollover(DateOnly day)
        {
            const string plan = DemoAccountRoles.OldPlanA;
            foreach (var key in DemoHoldings.SecuritiesOf(plan))
            {
                var shares = _book.Shares(_desk.Holding(plan, key).Id, day);
                if (shares > 0) Post(_desk.Sell(plan, key, day, shares, memo: "Rollover sale"));
            }

            var buyDay = DemoCalendar.NextBusinessDayAfter(day);
            var total = Investable(plan, buyDay, reserve: 0);
            if (total <= 0) return;
            var left = total;
            var mix = InvestmentTerms.IraMix;
            for (var i = 0; i < mix.Count; i++)
            {
                var (key, percent) = mix[i];
                var cash = i == mix.Count - 1 ? left : DemoAmortization.RoundCents(total * percent / 100m);
                left -= cash;
                Post(_desk.BuyX(DemoAccountRoles.RolloverIra, key, buyDay, cash, plan,
                    memo: "Rollover from Thornbury 401(k)"));
            }
        }

        // L20: a second lot of the REIT fund, held under a year when it is sold.
        private void SecondReitLot(DateOnly day) =>
            BrokerageEventBuy(day, DemoSecurities.Vnq, InvestmentTerms.SecondReitLot, memo: "Second lot", tags: null, securityTags: null);

        // L27: the REIT fund sells out, its proceeds move to checking by a cash move,
        // and part of the total-market position is sold straight to checking.
        private void DownPayment(DateOnly day)
        {
            const string brokerage = DemoAccountRoles.Brokerage;
            var reit = _book.Shares(_desk.Holding(brokerage, DemoSecurities.Vnq).Id, day);
            if (reit > 0)
            {
                var sale = Post(_desk.Sell(brokerage, DemoSecurities.Vnq, day, reit, memo: "Sold for the down payment"));
                var proceeds = sale.Splits[0].ParentAmount - DemoInvestmentDesk.Commission(DemoSecurities.Vnq);
                Post(_desk.Bank(brokerage, day, -proceeds, DemoAccountRoles.JointChecking,
                    "Transfer to Joint Checking", memo: "Down payment funds"));
            }

            var vti = _book.Shares(_desk.Holding(brokerage, DemoSecurities.Vti).Id, day);
            var part = decimal.ToInt64(decimal.Round(
                vti * InvestmentTerms.DownPaymentVtiPercent / 100m, 0, MidpointRounding.AwayFromZero));
            if (part > 0)
            {
                Post(_desk.SellX(brokerage, DemoSecurities.Vti, day, part, DemoAccountRoles.JointChecking,
                    memo: "Sold for the down payment"));
            }
        }

        // L33: the brokerage sells part of a stock, paying its commission out of the
        // proceeds; the cash stays and the next regular buy invests it.
        private void StockTrim(DateOnly day)
        {
            const string brokerage = DemoAccountRoles.Brokerage;
            const string key = InvestmentTerms.StockTrimSecurity;
            var held = _book.Shares(_desk.Holding(brokerage, key).Id, day);
            var shares = decimal.ToInt64(decimal.Round(
                held * InvestmentTerms.StockTrimPercent / 100m, 0, MidpointRounding.AwayFromZero));
            if (shares <= 0 || !_desk.CanTrade(key, day)) return;
            _book.Mark(StockTrimMark, Post(_desk.Sell(brokerage, key, day, shares, memo: "Trim position")));
        }

        // L38: the IRA sells part of its total-market fund and buys bonds with it.
        private void IraRebalance(DateOnly day)
        {
            const string ira = DemoAccountRoles.RolloverIra;
            var held = _book.Shares(_desk.Holding(ira, DemoSecurities.Vti).Id, day);
            var shares = decimal.ToInt64(decimal.Round(
                held * InvestmentTerms.IraRebalanceVtiPercent / 100m, 0, MidpointRounding.AwayFromZero));
            if (shares <= 0) return;
            var sale = Post(_desk.Sell(ira, DemoSecurities.Vti, day, shares, memo: "Rebalance"));
            Post(_desk.Buy(ira, DemoSecurities.Bnd, day, sale.Splits[0].ParentAmount, memo: "Rebalance",
                tags: string.Join(',', InvestmentTerms.RebalanceTags)));
        }

        // L46: the brokerage buy that carries the most tags.
        private void TaggedBuy(DateOnly day) =>
            BrokerageEventBuy(day, InvestmentTerms.TaggedBuySecurity, InvestmentTerms.TaggedBuy, memo: "Purchase with every tag kept",
                tags: string.Join(',', InvestmentTerms.TaggedBuyHeaderTags),
                securityTags: string.Join(',', InvestmentTerms.TaggedBuySecurityTags));

        // A brokerage buy of a set amount; any shortfall in cash is raised first by
        // selling money-market shares.
        private void BrokerageEventBuy(DateOnly day, string key, long amount, string memo, string? tags, string? securityTags)
        {
            const string brokerage = DemoAccountRoles.Brokerage;
            var needed = amount + DemoInvestmentDesk.Commission(key);
            var shortfall = needed - Investable(brokerage, day, InvestmentTerms.BrokerageCashReserve);
            if (shortfall > 0)
            {
                var held = _book.Shares(_desk.Holding(brokerage, DemoSecurities.MoneyMarket).Id, day);
                var units = _desk.SharesFor(DemoSecurities.MoneyMarket, day, shortfall);
                if (units > held) units = held;
                if (units > 0) Post(_desk.Sell(brokerage, DemoSecurities.MoneyMarket, day, units, memo: "Raise cash"));
            }

            var available = Investable(brokerage, day, InvestmentTerms.BrokerageCashReserve) - DemoInvestmentDesk.Commission(key);
            var cash = available < amount ? available : amount;
            if (cash <= 0 || _desk.SharesFor(key, day, cash) <= 0) return;
            Post(_desk.Buy(brokerage, key, day, cash, memo, tags, securityTags));
        }

        // --- dividends ----------------------------------------------------------------

        private Dictionary<DateOnly, List<Action<DateOnly>>> DividendDays()
        {
            var days = new Dictionary<DateOnly, List<Action<DateOnly>>>();
            void At(DateOnly day, Action<DateOnly> pay)
            {
                if (!_calendar.Contains(day)) return;
                if (!days.TryGetValue(day, out var list)) days[day] = list = [];
                list.Add(pay);
            }

            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var monthStart = _calendar.MonthStart(month);
                foreach (var schedule in InvestmentTerms.Dividends)
                {
                    if (!schedule.Months.Contains(monthStart.Month)) continue;
                    var nominal = _calendar.DayInMonth(month, schedule.Day);
                    var day = schedule.RollForward
                        ? DemoCalendar.OnOrNextBusinessDay(nominal)
                        : DemoCalendar.OnOrPreviousBusinessDay(nominal);
                    var fraction = schedule.FractionIn(monthStart.Month);
                    var key = schedule.SecurityKey;
                    At(day, d => PayEverywhere(key, d, fraction));
                }

                var lifeYear = DemoCalendar.LifeYearOf(month);
                var mmYields = InvestmentTerms.MoneyMarketYieldPercent;
                var mmYield = mmYields[lifeYear - 1 < mmYields.Count ? lifeYear - 1 : mmYields.Count - 1];
                At(DemoCalendar.OnOrPreviousBusinessDay(_calendar.MonthEnd(month)),
                    d => PayEverywhere(DemoSecurities.MoneyMarket, d, mmYield / 100m / 12m));

                if (monthStart.Month == 12)
                {
                    var day = DemoCalendar.OnOrPreviousBusinessDay(
                        _calendar.DayInMonth(month, InvestmentTerms.TargetDateDistributionDay));
                    var shortTerm = lifeYear % 2 == 1;
                    At(day, d => TargetDateDistributions(d, shortTerm));
                }
            }
            return days;
        }

        // A security's payment in every account that holds it: cash in the
        // brokerage (transferred to checking for the second stock), reinvested
        // everywhere else.
        private void PayEverywhere(string key, DateOnly day, decimal fraction)
        {
            foreach (var (role, held) in DemoHoldings.Pairs)
            {
                if (held != key) continue;
                var cash = PaymentOn(role, key, day, fraction);
                if (cash <= 0) continue;

                var category = _dividends;
                if (role != DemoAccountRoles.Brokerage)
                {
                    if (_desk.SharesFor(key, day, cash) > 0) Post(_desk.DivR(role, key, day, cash, category));
                }
                else if (key == DemoSecurities.Msft)
                {
                    Post(_desk.DivX(role, key, day, cash, category, DemoAccountRoles.JointChecking));
                }
                else if (key == DemoSecurities.MoneyMarket)
                {
                    if (_desk.SharesFor(key, day, cash) > 0) Post(_desk.DivR(role, key, day, cash, category));
                }
                else
                {
                    Post(_desk.Div(role, key, day, cash, category));
                }
            }
        }

        // December: the target-date fund's income, its long-term gain and, in odd life
        // years, a short-term gain, each reinvested.
        private void TargetDateDistributions(DateOnly day, bool shortTerm)
        {
            const string key = DemoSecurities.TargetDate;
            foreach (var (role, held) in DemoHoldings.Pairs)
            {
                if (held != key) continue;
                var parts = new List<(decimal Percent, string Category, string Memo)>
                {
                    (InvestmentTerms.TargetDateIncomePercent, _dividends, "Income distribution"),
                    (InvestmentTerms.TargetDateLongTermGainPercent, _longTermGains, "Long-term capital gain"),
                };
                if (shortTerm) parts.Add((InvestmentTerms.TargetDateShortTermGainPercent, _shortTermGains, "Short-term capital gain"));

                foreach (var (percent, category, memo) in parts)
                {
                    var cash = PaymentOn(role, key, day, percent / 100m);
                    if (cash > 0 && _desk.SharesFor(key, day, cash) > 0)
                        Post(_desk.DivR(role, key, day, cash, category, memo));
                }
            }
        }

        // Shares held at the end of the day before, times the close, times the
        // payment's fraction, to the cent; zero when the account is closed that day.
        private long PaymentOn(string role, string key, DateOnly day, decimal fraction)
        {
            if (!_book.Account(role).IsActiveOn(day)) return 0;
            var shares = _book.Shares(_desk.Holding(role, key).Id, day.AddDays(-1));
            if (shares <= 0) return 0;
            var unit = DemoSecurities.Get(key).ShareUnit;
            return DemoAmortization.RoundCents(shares * _market.Close(key, day) * fraction * 100m / unit);
        }
    }
}
