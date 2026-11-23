namespace Coffer.Api.Contracts;

/// <summary>
/// One account in the catalog (ADR-0063 §D5 v2 <c>list_accounts</c>). Balances
/// are Overview-consistent: investment accounts include holdings market value
/// (cash + MV). <see cref="Balance"/> is null for categories (they carry flows,
/// not a balance — use transaction_summary). <see cref="ParentId"/> gives the
/// category tree; real accounts are grouped by sidebar folder instead
/// (<see cref="FolderId"/>). <see cref="Class"/> is asset/liability/none.
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
    DateOnly? OpenedOn,
    /// <summary>The sidebar folder the account sits in directly (mig 258), or null
    /// when it is in none. Always null for categories.</summary>
    Guid? FolderId,
    /// <summary>That folder's own name (not its path), or null.</summary>
    string? FolderName,
    /// <summary>Why <see cref="Balance"/> is null for an account that has one: a
    /// position priced in another currency needs a missing rate (ADR-0114 D9). The
    /// balance is in the account's own <see cref="CurrencyCode"/>.</summary>
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>One line in the net-worth breakdown, in the account's own
/// <see cref="CurrencyCode"/> (ADR-0114 D7). Null, with <see cref="RateUnavailable"/>,
/// when a position priced in another currency needs a missing rate.</summary>
public sealed record NetWorthLine(
    Guid AccountId, string Name, string AccountType, string Class, decimal? Balance,
    string CurrencyCode, IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// Net worth (ADR-0063 §D5 v2 <c>net_worth</c>), reusing the Overview computation
/// so it matches the app's Overview screen exactly. Liabilities are negative, so
/// <see cref="NetWorth"/> = assets + liabilities. Current (as-of-now).
/// </summary>
/// <remarks>The totals are in the ledger's reporting currency, <see cref="CurrencyCode"/>:
/// each account converted at the latest rate, summed, rounded once (ADR-0114 D7). A total
/// that needs a missing rate is null and <see cref="RateUnavailable"/> names it
/// (ADR-0114 D9).</remarks>
public sealed record McpNetWorth(
    decimal? NetWorth,
    decimal? TotalAssets,
    decimal? TotalLiabilities,
    decimal? InvestmentsValue,
    string CurrencyCode,
    IReadOnlyList<NetWorthLine> Breakdown,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// One point in a net-worth-over-time series: net worth AS OF <see cref="AsOf"/>.
/// <see cref="UnpricedSecurityCount"/> counts held positions that had NO price at
/// all at that date, neither a close nor a trade at one (carried at their cost basis
/// since migration 259, as every current value carries them; before it, at 0); 0
/// means every position was priced (a market close or the security's last trade).
/// <see cref="NoHistory"/> is set when <see cref="AsOf"/> precedes the ledger's
/// first dated fact: the point values a ledger that did not exist yet, so its zero
/// is not a net worth.
/// <see cref="NetWorth"/> is in the series' reporting currency, each account converted
/// at the point's day's rate (ADR-0114 D7); null when one is missing, named in
/// <see cref="RateUnavailable"/> (ADR-0114 D9).
/// </summary>
public sealed record NetWorthHistoryPoint(
    DateTime AsOf,
    decimal? NetWorth,
    int UnpricedSecurityCount,
    bool NoHistory,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>
/// Net worth over time (Track-2 historical valuations). Each point is net worth
/// as of the END of an interval period (month/quarter/year) within
/// [<see cref="FromUtc"/>, <see cref="ToUtc"/>], with the final point clamped to
/// <see cref="ToUtc"/>. Assembled from the migration-172 as-of feeder: cash
/// balance as of the instant + split-adjusted holdings market value, using the
/// same Overview-consistent classification as <c>net_worth</c> (investment
/// accounts include holdings value; holdings-sibling shadow accounts are never
/// double-counted). In the ledger's reporting currency (<see cref="CurrencyCode"/>),
/// each point converted at its own day's rate (ADR-0114 D7).
/// </summary>
public sealed record NetWorthHistory(
    DateTime FromUtc,
    DateTime ToUtc,
    string Interval,
    string CurrencyCode,
    IReadOnlyList<NetWorthHistoryPoint> Points);
