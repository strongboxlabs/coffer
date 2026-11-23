namespace Coffer.Api.Contracts;

/// <summary>
/// Ledger overview aggregate (ADR-0056 slice 1) — the financial summary the
/// dashboard lands on. Server-computed in a single call so the SPA never
/// re-derives money totals client-side.
/// </summary>
/// <remarks>
/// <para>Net worth is a straight sum of every account balance: liabilities
/// (credit_card / liability / loan) are stored as negative balances, so no
/// assets-minus-liabilities subtraction is needed.</para>
/// <para>Every figure here that combines accounts is in the ledger's reporting
/// currency, <see cref="CurrencyCode"/> (ADR-0114 D7): each account's balance converted
/// at the latest rate, summed, rounded once. A figure that needs a missing rate is null,
/// and <see cref="RateUnavailable"/> names every missing pair and day (ADR-0114 D9).
/// Each account row stays in its own currency.</para>
/// <para><see cref="Comparison"/> is present only when the request named a
/// <c>since</c> month-end.</para>
/// </remarks>
public sealed record LedgerOverviewDto(
    decimal? NetWorth,
    decimal? TotalAssets,
    decimal? TotalLiabilities,
    decimal? InvestmentsValue,
    string CurrencyCode,
    IReadOnlyList<OverviewAccountGroupDto> AccountGroups,
    PortfolioRollupDto Portfolio,
    IReadOnlyList<RateUnavailable> RateUnavailable,
    OverviewComparisonDto? Comparison = null);

/// <summary>
/// Net worth at the end of <see cref="Since"/>, beside today's.
/// </summary>
/// <remarks>
/// <para><see cref="NoHistory"/> is set, with both figures null, when
/// <see cref="Since"/> precedes the ledger's first dated fact: there was no
/// ledger to value then, which is not the same as a net worth of zero.</para>
/// <para>Net worth then converts at <see cref="Since"/>'s rate, as a balance on that day
/// does (ADR-0114 D7). When it needs a missing rate it is null, and so is the change;
/// <see cref="RateUnavailable"/> names what is missing.</para>
/// </remarks>
public sealed record OverviewComparisonDto(
    DateOnly Since,
    OverviewChangeDto NetWorth,
    bool NoHistory,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>A figure then, and today's less it.</summary>
public sealed record OverviewChangeDto(decimal? Then, decimal? Change);

/// <summary>Accounts of one type, with the type subtotal in the reporting currency,
/// converted; null with <see cref="RateUnavailable"/> when it needs a missing
/// rate.</summary>
public sealed record OverviewAccountGroupDto(
    string AccountType,
    decimal? Subtotal,
    IReadOnlyList<OverviewAccountDto> Accounts,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>One account row in the overview — name, type, current balance, and
/// whether it is open. A closed account still counts in net worth (ADR-0085);
/// the flag is for display.</summary>
/// <remarks>The balance is in the account's own <see cref="CurrencyCode"/>. A position
/// priced in another currency converts into it at the latest rate (ADR-0114 D7); when
/// that rate is missing the balance is null, and <see cref="RateUnavailable"/> says
/// why.</remarks>
public sealed record OverviewAccountDto(
    Guid Id,
    string Name,
    string AccountType,
    string CurrencyCode,
    decimal? Balance,
    bool IsActive,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// Ledger-wide investment roll-up. <see cref="Value"/> is holdings only;
/// <see cref="CashBalance"/> is the brokerages' cash beside it, so the two sum to
/// the overview's investments value. No-price positions carry at cost basis,
/// matching the Portfolio View.
/// </summary>
/// <remarks>
/// <para>Every figure is in the ledger's reporting currency, <see cref="CurrencyCode"/>
/// (ADR-0114 D7). The value and the cash convert at the latest rate. The cost basis
/// converts lot by lot at each lot's acquisition date, as a trade converts at its trade
/// date, so the unrealized gain includes the currency move since each purchase. A
/// figure that needs a missing rate is null, and so is every figure derived from it;
/// <see cref="RateUnavailable"/> names what is missing.</para>
/// </remarks>
public sealed record PortfolioRollupDto(
    decimal? Value,
    decimal? CostBasis,
    decimal? UnrealizedGain,
    decimal? PercentChange,
    decimal? CashBalance,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable);
