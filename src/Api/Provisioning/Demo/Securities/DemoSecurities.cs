using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>How a security's price moves through the window.</summary>
public enum DemoPriceModel
{
    /// <summary>A daily step from the security's own stream, with a drift and a
    /// spread per asset class and one drawdown regime.</summary>
    Walk,

    /// <summary>A constant-weight blend of the daily returns of other walks.</summary>
    Blend,

    /// <summary>A fixed unit price.</summary>
    Fixed,

    /// <summary>A unit value credited daily at a rate per life year; no price rows.</summary>
    StableValue,
}

/// <summary>
/// One Demo security: its generic name, its real ticker (the ticker carries the
/// reality, the name never a brand), its share precision, Moneydance's type codes
/// for its holding accounts, and the terms of its price path.
/// </summary>
public sealed class DemoSecurity
{
    /// <summary>The stable key the generator names the security by: the ticker, or a
    /// slug for the one fund without one.</summary>
    public required string Key { get; init; }

    /// <summary>The real ticker, or null for a fund only its plan prices.</summary>
    public string? Ticker { get; init; }

    /// <summary>The generic display name.</summary>
    public required string Name { get; init; }

    /// <summary>The <c>curr</c> item's <c>currid</c>.</summary>
    public required string Symbol { get; init; }

    /// <summary>Share decimals (<c>dec</c>): a share count is in units of
    /// ten to the minus this.</summary>
    public required int Decimals { get; init; }

    /// <summary>Moneydance's <c>sec_type</c> code (<see cref="MdSecurityType"/>).</summary>
    public required string SecType { get; init; }

    /// <summary>Moneydance's <c>sec_subtype</c>, one of its own <c>secsubtypes</c>
    /// names, or null.</summary>
    public string? SecSubtype { get; init; }

    /// <summary><c>sec_exchange</c>, or null.</summary>
    public string? Exchange { get; init; }

    /// <summary>How the price moves.</summary>
    public required DemoPriceModel Model { get; init; }

    /// <summary>The price on the window's first business day. For the splitting
    /// stock it is in post-split units: the walk tracks the split-adjusted price,
    /// and a date before the split trades at that times the ratio.</summary>
    public decimal InitialPrice { get; init; }

    /// <summary>The walk's daily drift outside the drawdown regime.</summary>
    public decimal DailyDrift { get; init; }

    /// <summary>The walk's daily spread outside the drawdown regime.</summary>
    public decimal DailyVolatility { get; init; }

    /// <summary>The walk's daily drift in the drawdown regime.</summary>
    public decimal DrawdownDrift { get; init; }

    /// <summary>The walk's daily spread in the drawdown regime.</summary>
    public decimal DrawdownVolatility { get; init; }

    /// <summary>True for an individual stock, whose trades carry a commission.</summary>
    public bool IsStock => SecType == MdSecurityType.Stock && SecSubtype != MdSecurityType.ExchangeTradedFundSubtype;

    /// <summary>The number of share units in one share.</summary>
    public long ShareUnit
    {
        get
        {
            var unit = 1L;
            for (var i = 0; i < Decimals; i++) unit *= 10;
            return unit;
        }
    }
}

/// <summary>
/// The Demo's nine securities: five index funds and a money-market fund
/// in the brokerage, two individual stocks (one of which splits), one target-date
/// fund both earners' plans hold, and one stable-value fund with no ticker.
/// </summary>
/// <remarks>
/// <para>The type codes come from the importer's own table, <see cref="MdSecurityType"/>,
/// so the codes the generator writes and the codes the importer reads cannot drift.
/// The subtype strings are Moneydance's own <c>secsubtypes</c> names in base.json.</para>
///
/// <para>Every price figure here is an invented build parameter. Drift and spread are
/// daily; the drawdown regime runs through life year two.</para>
/// </remarks>
public static class DemoSecurities
{
    /// <summary>The total-market index fund.</summary>
    public const string Vti = "VTI";

    /// <summary>The international index fund.</summary>
    public const string Vxus = "VXUS";

    /// <summary>The bond index fund.</summary>
    public const string Bnd = "BND";

    /// <summary>The real-estate index fund, sold out for the down payment.</summary>
    public const string Vnq = "VNQ";

    /// <summary>The target-date fund.</summary>
    public const string TargetDate = "VFFVX";

    /// <summary>The money-market fund.</summary>
    public const string MoneyMarket = "VMFXX";

    /// <summary>The stock that splits.</summary>
    public const string Nvda = "NVDA";

    /// <summary>The other stock.</summary>
    public const string Msft = "MSFT";

    /// <summary>The stable-value fund, which has no ticker.</summary>
    public const string StableValue = "stable-value";

    /// <summary>The life year of the drawdown regime.</summary>
    public const int DrawdownLifeYear = 2;

    /// <summary>The splitting stock's first split-adjusted trading day.</summary>
    public static readonly DateOnly SplitDate = new(2024, 6, 10);

    /// <summary>Shares after the split for <see cref="SplitOldShares"/> before it.</summary>
    public const long SplitNewShares = 10;

    /// <summary>Shares before the split.</summary>
    public const long SplitOldShares = 1;

    /// <summary>The target-date fund's look-through weights by ticker; they sum to one
    /// and are the weights its price blends.</summary>
    public static IReadOnlyDictionary<string, decimal> TargetDateWeights { get; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [Vti] = 0.54m,
            [Vxus] = 0.36m,
            [Bnd] = 0.10m,
        };

    /// <summary>The stable-value fund's unit value on the window's first day.</summary>
    public const decimal StableValueInitial = 10.0000m;

    /// <summary>The stable-value fund's crediting rate, as a yearly percent, by life
    /// year (index 0 is life year one).</summary>
    public static IReadOnlyList<decimal> StableValueRatePercent { get; } = [1.90m, 2.10m, 2.60m, 3.00m, 3.20m];

    private const string Etf = MdSecurityType.ExchangeTradedFundSubtype;
    private const string Nyse = "NYSE";
    private const string Nasdaq = "NASDAQ";

    /// <summary>Every security, in export order.</summary>
    public static IReadOnlyList<DemoSecurity> All { get; } =
    [
        new()
        {
            Key = Vti, Ticker = Vti, Name = "US Total Market Index ETF", Symbol = "^" + Vti, Decimals = 4,
            SecType = MdSecurityType.MutualFund, SecSubtype = Etf, Exchange = Nyse, Model = DemoPriceModel.Walk,
            InitialPrice = 236.40m, DailyDrift = 0.00055m, DailyVolatility = 0.0105m,
            DrawdownDrift = -0.00085m, DrawdownVolatility = 0.0150m,
        },
        new()
        {
            Key = Vxus, Ticker = Vxus, Name = "International Index ETF", Symbol = "^" + Vxus, Decimals = 4,
            SecType = MdSecurityType.MutualFund, SecSubtype = Etf, Exchange = Nasdaq, Model = DemoPriceModel.Walk,
            InitialPrice = 64.85m, DailyDrift = 0.00040m, DailyVolatility = 0.0095m,
            DrawdownDrift = -0.00090m, DrawdownVolatility = 0.0135m,
        },
        new()
        {
            Key = Bnd, Ticker = Bnd, Name = "US Bond Index ETF", Symbol = "^" + Bnd, Decimals = 4,
            SecType = MdSecurityType.MutualFund, SecSubtype = Etf, Exchange = Nasdaq, Model = DemoPriceModel.Walk,
            InitialPrice = 85.70m, DailyDrift = 0.00012m, DailyVolatility = 0.0030m,
            DrawdownDrift = -0.00055m, DrawdownVolatility = 0.0045m,
        },
        new()
        {
            Key = Vnq, Ticker = Vnq, Name = "REIT Index ETF", Symbol = "^" + Vnq, Decimals = 4,
            SecType = MdSecurityType.MutualFund, SecSubtype = Etf, Exchange = Nyse, Model = DemoPriceModel.Walk,
            InitialPrice = 112.30m, DailyDrift = 0.00045m, DailyVolatility = 0.0125m,
            DrawdownDrift = -0.00110m, DrawdownVolatility = 0.0170m,
        },
        new()
        {
            Key = TargetDate, Ticker = TargetDate, Name = "Target Date 2055 Fund", Symbol = "^" + TargetDate,
            Decimals = 3, SecType = MdSecurityType.MutualFund, SecSubtype = "Balanced / Targeted", Exchange = Nasdaq,
            Model = DemoPriceModel.Blend, InitialPrice = 58.20m,
        },
        new()
        {
            Key = MoneyMarket, Ticker = MoneyMarket, Name = "Federal Money Market Fund", Symbol = "^" + MoneyMarket,
            Decimals = 2, SecType = MdSecurityType.MutualFund, SecSubtype = MdSecurityType.MoneyMarketSubtype,
            Exchange = Nasdaq, Model = DemoPriceModel.Fixed, InitialPrice = 1.0000m,
        },
        new()
        {
            Key = Nvda, Ticker = Nvda, Name = "Large-Cap Stock A", Symbol = "^" + Nvda, Decimals = 4,
            SecType = MdSecurityType.Stock, Exchange = Nasdaq, Model = DemoPriceModel.Walk,
            InitialPrice = 30.55m, DailyDrift = 0.00200m, DailyVolatility = 0.0290m,
            DrawdownDrift = -0.00220m, DrawdownVolatility = 0.0380m,
        },
        new()
        {
            Key = Msft, Ticker = Msft, Name = "Large-Cap Stock B", Symbol = "^" + Msft, Decimals = 4,
            SecType = MdSecurityType.Stock, Exchange = Nasdaq, Model = DemoPriceModel.Walk,
            InitialPrice = 331.20m, DailyDrift = 0.00075m, DailyVolatility = 0.0160m,
            DrawdownDrift = -0.00110m, DrawdownVolatility = 0.0210m,
        },
        new()
        {
            Key = StableValue, Name = "Stable Value Fund", Symbol = "^SVF", Decimals = 4,
            SecType = MdSecurityType.MutualFund, SecSubtype = "Bond", Model = DemoPriceModel.StableValue,
            InitialPrice = StableValueInitial,
        },
    ];

    /// <summary>The security with the given key.</summary>
    /// <exception cref="KeyNotFoundException">No Demo security has that key.</exception>
    public static DemoSecurity Get(string key)
    {
        foreach (var security in All)
        {
            if (security.Key == key) return security;
        }
        throw new KeyNotFoundException($"no Demo security has the key {key}");
    }
}
