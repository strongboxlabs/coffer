namespace Coffer.Api.Contracts;

/// <summary>
/// One account in the catalog (ADR-0063 §D5 v2 <c>list_accounts</c>). Balances
/// are Overview-consistent: investment accounts include holdings market value
/// (cash + MV). <see cref="Balance"/> is null for categories (they carry flows,
/// not a balance — use transaction_summary). <see cref="ParentId"/> gives the
/// category tree (real accounts are flat). <see cref="Class"/> is
/// asset/liability/none.
/// </summary>
public sealed record AccountInfo(
    Guid Id,
    string Name,
    string AccountType,
    string? CategoryKind,
    Guid? ParentId,
    string CurrencyCode,
    bool IsActive,
    decimal? Balance,
    string Class,
    string? TaxStatus,
    /// <summary>The account's Start Date — the as-of date of its opening balance
    /// (mig 127 / ADR-0050). Null when unknown, which is every account imported
    /// before the Moneydance importer began seeding it. Null for categories,
    /// which have no opening balance.</summary>
    DateOnly? OpenedOn);

/// <summary>One line in the net-worth breakdown.</summary>
public sealed record NetWorthLine(
    Guid AccountId, string Name, string AccountType, string Class, decimal Balance);

/// <summary>
/// Net worth (ADR-0063 §D5 v2 <c>net_worth</c>), reusing the Overview computation
/// so it matches the app's Overview screen exactly. Liabilities are negative, so
/// <see cref="NetWorth"/> = assets + liabilities. Current (as-of-now).
/// </summary>
public sealed record McpNetWorth(
    decimal NetWorth,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal InvestmentsValue,
    string CurrencyCode,
    IReadOnlyList<NetWorthLine> Breakdown);

/// <summary>
/// One point in a net-worth-over-time series: net worth AS OF <see cref="AsOf"/>.
/// <see cref="UnpricedSecurityCount"/> counts held securities that had NO price
/// at all at that date (valued at 0 — the figure is understated by those); 0
/// means every position was priced (a market close or the last trade).
/// </summary>
public sealed record NetWorthHistoryPoint(
    DateTime AsOf,
    decimal NetWorth,
    int UnpricedSecurityCount);

/// <summary>
/// Net worth over time (Track-2 historical valuations). Each point is net worth
/// as of the END of an interval period (month/quarter/year) within
/// [<see cref="FromUtc"/>, <see cref="ToUtc"/>], with the final point clamped to
/// <see cref="ToUtc"/>. Assembled from the migration-172 as-of feeder: cash
/// balance as of the instant + split-adjusted holdings market value, using the
/// same Overview-consistent classification as <c>net_worth</c> (investment
/// accounts include holdings value; holdings-sibling shadow accounts are never
/// double-counted). USD.
/// </summary>
public sealed record NetWorthHistory(
    DateTime FromUtc,
    DateTime ToUtc,
    string Interval,
    string CurrencyCode,
    IReadOnlyList<NetWorthHistoryPoint> Points);
