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
/// All values are in <see cref="HoldingsViewDto.CurrencyCode"/>.
/// </summary>
public sealed record PortfolioSummaryDto(
    /// <summary>Sum of <see cref="PositionDto.CurrentValue"/> across all
    /// positions; treats positions with no price snapshot as
    /// <see cref="PositionDto.CostBasis"/> (so the total doesn't silently
    /// drop value when a security has no recorded price).</summary>
    decimal PortfolioValue,
    /// <summary>Sum of <see cref="PositionDto.CostBasis"/> across all
    /// positions.</summary>
    decimal CostBasis,
    /// <summary><see cref="PortfolioValue"/> − <see cref="CostBasis"/>.</summary>
    decimal UnrealizedGain,
    /// <summary>100 × <see cref="UnrealizedGain"/> ÷ <see cref="CostBasis"/>;
    /// 0 when cost basis is zero.</summary>
    decimal PercentChange,
    /// <summary>Running balance of the brokerage's cash side
    /// (<c>txn_legs.balance_after</c> on the latest leg). Independent of
    /// positions — securities live on the Holdings sibling.</summary>
    decimal CashBalance,
    /// <summary><see cref="PortfolioValue"/> + <see cref="CashBalance"/>.</summary>
    decimal Total);

/// <summary>
/// One position in the investment account. <see cref="CurrentPrice"/> +
/// derived value fields are null when no <c>security_prices</c> row
/// exists for this security yet (manual-entry / future price-feed
/// integration territory).
/// </summary>
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
    decimal? PercentChange);
