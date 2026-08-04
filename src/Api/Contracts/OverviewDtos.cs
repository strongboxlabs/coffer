namespace Coffer.Api.Contracts;

/// <summary>
/// Ledger overview aggregate (ADR-0056 slice 1) — the financial summary the
/// dashboard lands on. Server-computed in a single call so the SPA never
/// re-derives money totals client-side.
/// </summary>
/// <remarks>
/// Net worth is a straight sum of every account balance: liabilities
/// (credit_card / liability / loan) are stored as negative balances, so no
/// assets-minus-liabilities subtraction is needed. <see cref="MixedCurrency"/>
/// flags a ledger whose accounts span more than one currency — v1 sums
/// naively (no FX), so the UI can warn rather than mislead.
/// </remarks>
public sealed record LedgerOverviewDto(
    decimal NetWorth,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal InvestmentsValue,
    string CurrencyCode,
    bool MixedCurrency,
    IReadOnlyList<OverviewAccountGroupDto> AccountGroups,
    PortfolioRollupDto Portfolio);

/// <summary>Accounts of one type, with the type subtotal.</summary>
public sealed record OverviewAccountGroupDto(
    string AccountType,
    decimal Subtotal,
    IReadOnlyList<OverviewAccountDto> Accounts);

/// <summary>One account row in the overview — name, type, current balance.</summary>
public sealed record OverviewAccountDto(
    Guid Id,
    string Name,
    string AccountType,
    string CurrencyCode,
    decimal Balance);

/// <summary>
/// Ledger-wide investment roll-up (holdings only, excludes brokerage cash).
/// No-price positions carry at cost basis, matching the Portfolio View.
/// </summary>
public sealed record PortfolioRollupDto(
    decimal Value,
    decimal CostBasis,
    decimal UnrealizedGain,
    decimal PercentChange);
