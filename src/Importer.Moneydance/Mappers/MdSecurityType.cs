namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Moneydance's security-type codes and how each becomes Coffer's orthogonal
/// (<c>asset_class</c>, <c>vehicle_type</c>) pair (ADR-0016, amendment of
/// 2026-11-18; ADR-0067). Moneydance stores the type as a numeric code in the
/// <c>sec_type</c> parameter of the type-<c>s</c> security ACCOUNT, beside
/// <c>sec_subtype</c> (one of its own <c>secsubtypes</c> names) and
/// <c>sec_exchange</c>; the <c>curr</c> item carries none of them.
/// </summary>
/// <remarks>
/// The table is the importer's, and anything that writes a Moneydance export
/// (a generator) uses these constants so the two cannot drift. Moneydance files
/// ETFs under either Mutual Fund or Stock, told apart only by the subtype. The
/// code for Moneydance's OTHER type is undocumented, so every code outside the
/// table maps to vehicle <c>other</c> and is reported as unrecognised; an absent
/// or empty code leaves the security unclassified.
/// </remarks>
public static class MdSecurityType
{
    /// <summary>Certificate of deposit.</summary>
    public const string Cd = "0";

    /// <summary>Bond.</summary>
    public const string Bond = "1";

    /// <summary>Mutual fund, and an ETF or money-market fund by its subtype.</summary>
    public const string MutualFund = "2";

    /// <summary>Stock, and an ETF by its subtype.</summary>
    public const string Stock = "3";

    /// <summary>Option.</summary>
    public const string Option = "4";

    /// <summary>Moneydance's subtype name for an exchange-traded fund.</summary>
    public const string ExchangeTradedFundSubtype = "Exchange Traded Fund";

    /// <summary>Moneydance's subtype name for a money-market fund.</summary>
    public const string MoneyMarketSubtype = "Money Market";

    /// <summary>One security's classification from its code and subtype.</summary>
    /// <param name="AssetClass">The economic class, when the vehicle implies one; a
    /// fund's class is unknown at import, so null.</param>
    /// <param name="VehicleType">The legal wrapper; null when the code is absent.</param>
    /// <param name="Recognised">False when the code is present but not in the table,
    /// so the security was given vehicle <c>other</c>.</param>
    public readonly record struct Classification(string? AssetClass, string? VehicleType, bool Recognised)
    {
        /// <summary>True when the code was present, recognised or not.</summary>
        public bool Classified => VehicleType is not null;
    }

    /// <summary>Classify a security from its account's <c>sec_type</c> and
    /// <c>sec_subtype</c>.</summary>
    public static Classification Translate(string? secType, string? secSubtype) => secType switch
    {
        null or "" => new(null, null, Recognised: true),
        Cd => new("cash", "cd", true),
        Bond => new("fixed_income", "bond", true),
        MutualFund => secSubtype switch
        {
            ExchangeTradedFundSubtype => new(null, "etf", true),
            MoneyMarketSubtype => new("cash", "money_market", true),
            _ => new(null, "mutual_fund", true),
        },
        Stock => secSubtype == ExchangeTradedFundSubtype
            ? new(null, "etf", true)
            : new("equity", "stock", true),
        Option => new("alternative", "option", true),
        _ => new(null, "other", Recognised: false),
    };
}
