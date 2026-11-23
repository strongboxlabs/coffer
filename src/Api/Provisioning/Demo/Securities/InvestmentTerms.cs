namespace Coffer.Api.Provisioning.Demo;

/// <summary>One security's dividend schedule: the months it pays in, the day of
/// the month, and its yearly yield as a percent of the position's value.</summary>
/// <param name="SecurityKey">The security.</param>
/// <param name="Months">Calendar months (1-12) it pays in.</param>
/// <param name="Day">The day of the month; a day markets are shut rolls to the
/// business day before it, or after it when <paramref name="RollForward"/>.</param>
/// <param name="YieldPercent">The yearly yield, as a percent.</param>
/// <param name="DecemberWeight">December's payment as a multiple of the others.</param>
/// <param name="RollForward">True to roll a shut day forward rather than back.</param>
public sealed record DemoDividendSchedule(
    string SecurityKey, IReadOnlyList<int> Months, int Day, decimal YieldPercent,
    decimal DecemberWeight = 1m, bool RollForward = false)
{
    /// <summary>The fraction of the position's value one payment in
    /// <paramref name="month"/> pays.</summary>
    public decimal FractionIn(int month)
    {
        var total = 0m;
        foreach (var m in Months) total += m == 12 ? DecemberWeight : 1m;
        var weight = month == 12 ? DecemberWeight : 1m;
        return YieldPercent / 100m * weight / total;
    }
}

/// <summary>
/// The investment side's synthetic build constants: opening allocations, the cash
/// reserves and minimum buys, the regular buys' mixes, the dividend schedules and
/// the dated brokerage and retirement events.
/// </summary>
/// <remarks>
/// Every figure here is an invented build parameter, kept in code only. Amounts are
/// cents; allocations are percents that sum to one hundred.
/// </remarks>
public static class InvestmentTerms
{
    // --- cash kept back and the smallest buy ------------------------------------

    /// <summary>The cash the brokerage keeps uninvested.</summary>
    public const long BrokerageCashReserve = 250_00L;

    /// <summary>The smallest regular brokerage buy.</summary>
    public const long MinimumBrokerageBuy = 100_00L;

    /// <summary>The smallest buy a retirement or health plan makes.</summary>
    public const long MinimumPlanBuy = 10_00L;

    /// <summary>The life month the HSA's buys start; before it the HSA holds its
    /// contributions as cash for the delivery bill.</summary>
    public const int HsaBuysFrom = 41;

    // --- opening positions (first business day, from the opening cash) -------------

    /// <summary>The brokerage's opening allocation.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> BrokerageOpening { get; } =
    [
        (DemoSecurities.Vti, 30m), (DemoSecurities.Vxus, 15m), (DemoSecurities.Bnd, 15m),
        (DemoSecurities.Vnq, 10m), (DemoSecurities.MoneyMarket, 10m),
        (DemoSecurities.Nvda, 10m), (DemoSecurities.Msft, 10m),
    ];

    /// <summary>Earner A's old plan's opening allocation.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> OldPlanAOpening { get; } =
        [(DemoSecurities.TargetDate, 100m)];

    /// <summary>Earner B's plan's opening allocation.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> PlanBOpening { get; } =
        [(DemoSecurities.TargetDate, 80m), (DemoSecurities.StableValue, 20m)];

    // --- regular buys of arriving cash ----------------------------------------------

    /// <summary>The weights a regular brokerage buy picks its one security by.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> BrokeragePicks { get; } =
    [
        (DemoSecurities.Vti, 35m), (DemoSecurities.Vxus, 20m), (DemoSecurities.Bnd, 20m),
        (DemoSecurities.Msft, 15m), (DemoSecurities.Nvda, 10m),
    ];

    /// <summary>A plan's buy mix by account role.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> PlanBuys(string accountRole) => accountRole switch
    {
        DemoAccountRoles.OldPlanA or DemoAccountRoles.NewPlanA => [(DemoSecurities.TargetDate, 100m)],
        DemoAccountRoles.PlanB => [(DemoSecurities.TargetDate, 85m), (DemoSecurities.StableValue, 15m)],
        DemoAccountRoles.RolloverIra => IraMix,
        DemoAccountRoles.FamilyHsa => [(DemoSecurities.Vti, 100m)],
        _ => throw new ArgumentOutOfRangeException(nameof(accountRole), accountRole, "not a plan the Demo invests"),
    };

    /// <summary>The IRA's mix, for the rollover and for its contributions.</summary>
    public static IReadOnlyList<(string Key, decimal Percent)> IraMix { get; } =
        [(DemoSecurities.Vti, 60m), (DemoSecurities.Vxus, 30m), (DemoSecurities.Bnd, 10m)];

    // --- dated events ---------------------------------------------------------------

    /// <summary>The day of the rollover month the old plan sells out; the IRA buys
    /// the next business day.</summary>
    public const int RolloverDay = 9;

    /// <summary>The life month of the second REIT lot.</summary>
    public const int SecondReitLotMonth = 20;

    /// <summary>The day of that month.</summary>
    public const int SecondReitLotDay = 12;

    /// <summary>The second REIT lot's cost.</summary>
    public const long SecondReitLot = 3_000_00L;

    /// <summary>The day of the house month the brokerage raises its part of the down
    /// payment.</summary>
    public const int DownPaymentDay = 6;

    /// <summary>The percent of the total-market position sold toward the down payment.</summary>
    public const decimal DownPaymentVtiPercent = 50m;

    /// <summary>The life month of the IRA's rebalance.</summary>
    public const int IraRebalanceMonth = 38;

    /// <summary>The day of that month.</summary>
    public const int IraRebalanceDay = 15;

    /// <summary>The percent of the IRA's total-market position the rebalance sells
    /// into bonds.</summary>
    public const decimal IraRebalanceVtiPercent = 12m;

    /// <summary>The life month the brokerage trims a stock: the one stock sale, so a
    /// <c>sell</c> carries a commission (the re-homed "sell + fee" shape).</summary>
    public const int StockTrimMonth = 33;

    /// <summary>The day of that month.</summary>
    public const int StockTrimDay = 14;

    /// <summary>The stock trimmed: one that never splits, so no day is barred.</summary>
    public const string StockTrimSecurity = DemoSecurities.Msft;

    /// <summary>The percent of the position the trim sells.</summary>
    public const decimal StockTrimPercent = 25m;

    /// <summary>The life month of the brokerage buy that carries the most tags.</summary>
    public const int TaggedBuyMonth = 46;

    /// <summary>The day of that month.</summary>
    public const int TaggedBuyDay = 9;

    /// <summary>That buy's cost, before its commission.</summary>
    public const long TaggedBuy = 1_500_00L;

    /// <summary>The security that buy is in: a stock, so the commission makes it an
    /// event of several postings and its tags sit on the header.</summary>
    public const string TaggedBuySecurity = DemoSecurities.Msft;

    /// <summary>The nine tags of the IRA's rebalance buy.</summary>
    public static IReadOnlyList<string> RebalanceTags { get; } =
    [
        "Rebalance toward the long-term target allocation",
        "Contribution counted toward this year allowance",
        "Fund chosen for its low expense ratio and spread",
        "Bought on the scheduled first-Monday cadence day",
        "Lot to be held beyond one year for tax treatment",
        "Reviewed against the written investment policy",
        "Portfolio",
        "Long term",
        "Core holding",
    ];

    /// <summary>The header tags of the most-tagged buy.</summary>
    public static IReadOnlyList<string> TaggedBuyHeaderTags { get; } =
        Enumerable.Range(1, 12).Select(i => "Event tag " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    /// <summary>The tags on that buy's security split.</summary>
    public static IReadOnlyList<string> TaggedBuySecurityTags { get; } =
        Enumerable.Range(1, 10).Select(i => "Security tag " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    // --- dividends ------------------------------------------------------------------

    /// <summary>The quarterly and monthly schedules of the funds and stocks. The
    /// money-market and target-date funds have their own terms below.</summary>
    public static IReadOnlyList<DemoDividendSchedule> Dividends { get; } =
    [
        new(DemoSecurities.Vti, [3, 6, 9, 12], 24, 1.40m),
        new(DemoSecurities.Vxus, [3, 6, 9, 12], 21, 3.10m, DecemberWeight: 2m),
        new(DemoSecurities.Bnd, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], 1, 3.00m, RollForward: true),
        new(DemoSecurities.Vnq, [3, 6, 9, 12], 23, 3.90m),
        new(DemoSecurities.Nvda, [3, 6, 9, 12], 5, 0.04m),
        new(DemoSecurities.Msft, [2, 5, 8, 11], 13, 0.80m),
    ];

    /// <summary>The money-market fund's yearly yield, as a percent, by life year
    /// (index 0 is life year one); it pays on each month's last business day.</summary>
    public static IReadOnlyList<decimal> MoneyMarketYieldPercent { get; } = [0.04m, 1.60m, 4.90m, 5.10m, 4.30m];

    /// <summary>The day of December the target-date fund distributes.</summary>
    public const int TargetDateDistributionDay = 18;

    /// <summary>Its yearly income distribution, as a percent.</summary>
    public const decimal TargetDateIncomePercent = 1.90m;

    /// <summary>Its yearly long-term capital-gain distribution, as a percent.</summary>
    public const decimal TargetDateLongTermGainPercent = 1.20m;

    /// <summary>Its short-term capital-gain distribution in odd life years, as a percent.</summary>
    public const decimal TargetDateShortTermGainPercent = 0.35m;
}
