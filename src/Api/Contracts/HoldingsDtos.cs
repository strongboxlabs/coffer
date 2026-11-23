namespace Coffer.Api.Contracts;

/// <summary>
/// Portfolio View payload for one investment account. The "brokerage" is
/// the user-visible investment account; positions live on its
/// system-managed Holdings sibling (ADR-0019). The endpoint resolves
/// that link server-side so callers only pass the brokerage id.
/// </summary>
public sealed record HoldingsViewDto(
    Guid AccountId,
    string AccountName,
    string CurrencyCode,
    PortfolioSummaryDto Summary,
    IReadOnlyList<PositionDto> Positions);

/// <summary>
/// Aggregate totals across all positions plus the brokerage's cash side.
/// All values are in <see cref="HoldingsViewDto.CurrencyCode"/>: a position priced in
/// another currency counts at its <see cref="PositionDto.AccountValue"/>, converted at the
/// latest rate (ADR-0114 D7). A figure that needs a missing rate is null, and
/// <see cref="RateUnavailable"/> names each (ADR-0114 D9).
/// </summary>
public sealed record PortfolioSummaryDto(
    /// <summary>Sum of each position's value in the account's currency
    /// (<see cref="PositionDto.CurrentValue"/>, or <see cref="PositionDto.AccountValue"/> for
    /// one priced in another currency); treats positions with no price snapshot as
    /// <see cref="PositionDto.CostBasis"/> (so the total doesn't silently
    /// drop value when a security has no recorded price). Null when a position's rate
    /// is missing.</summary>
    decimal? PortfolioValue,
    /// <summary>Sum of <see cref="PositionDto.CostBasis"/> across all
    /// positions.</summary>
    decimal? CostBasis,
    /// <summary><see cref="PortfolioValue"/> − <see cref="CostBasis"/>; null with
    /// it.</summary>
    decimal? UnrealizedGain,
    /// <summary>100 × <see cref="UnrealizedGain"/> ÷ <see cref="CostBasis"/>;
    /// 0 when cost basis is zero; null with <see cref="PortfolioValue"/>.</summary>
    decimal? PercentChange,
    /// <summary>Running balance of the brokerage's cash side
    /// (<c>txn_legs.balance_after</c> on the latest leg). Independent of
    /// positions — securities live on the Holdings sibling.</summary>
    decimal? CashBalance,
    /// <summary><see cref="PortfolioValue"/> + <see cref="CashBalance"/>; null with
    /// <see cref="PortfolioValue"/>.</summary>
    decimal? Total,
    /// <summary>Every missing rate a figure above needed (ADR-0114 D9); absent from the
    /// JSON when none is.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// One position in the investment account. <see cref="CurrentPrice"/> +
/// derived value fields are null when no <c>security_prices</c> row
/// exists for this security yet (manual-entry / future price-feed
/// integration territory).
/// </summary>
/// <remarks>
/// <see cref="CostBasis"/> is in the account's currency, as its trades are.
/// <see cref="CurrentPrice"/> and <see cref="CurrentValue"/> are in the price's currency:
/// the account's, unless <see cref="PriceCurrencyCode"/> names another. Such a position
/// also carries <see cref="AccountValue"/>, its value converted into the account's
/// currency at the latest rate (ADR-0114 D7), and its <see cref="UnrealizedGain"/> and
/// <see cref="PercentChange"/> compare that with the cost basis. When the rate is missing
/// those three are null and <see cref="RateUnavailable"/> names it (ADR-0114 D9). The three
/// added fields are absent from the JSON for a position priced in the account's currency.
/// </remarks>
public sealed record PositionDto(
    Guid SecurityId,
    string? Ticker,
    string Name,
    string? AssetClass,
    decimal Quantity,
    decimal CostBasis,
    /// <summary><see cref="CostBasis"/> ÷ <see cref="Quantity"/>; 0 when
    /// quantity is zero (closed positions still showing because the
    /// importer retains a zero-quantity row).</summary>
    decimal CostPerShare,
    decimal? CurrentPrice,
    DateOnly? PriceAsOf,
    decimal? CurrentValue,
    decimal? UnrealizedGain,
    decimal? PercentChange,
    /// <summary>The currency <see cref="CurrentPrice"/> and <see cref="CurrentValue"/>
    /// are in, when it is not the account's; absent otherwise.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PriceCurrencyCode = null,
    /// <summary><see cref="CurrentValue"/> in the account's currency, for a position
    /// priced in another; null when its rate is missing, and absent for one priced in the
    /// account's currency.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    decimal? AccountValue = null,
    /// <summary>The missing rate <see cref="AccountValue"/> needed (ADR-0114 D9); absent
    /// when none is.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);
