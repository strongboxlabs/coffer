using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Account-catalog + net-worth reads for the MCP tools (ADR-0063 §D5 v2).
/// Balances are Overview-consistent: cash (the shared
/// <see cref="AccountBalancesRepository"/> definition) plus holdings market value
/// for investment accounts (the shared
/// <see cref="InvestmentReportingRepository.MarketValueByAccountAsync"/>). Net
/// worth delegates to <see cref="OverviewRepository"/> so it matches the app's
/// Overview screen exactly. LINQ/EF, no raw SQL.
/// </summary>
public sealed class AccountsReportingRepository
{
    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;
    private readonly InvestmentReportingRepository _investmentReporting;
    private readonly OverviewRepository _overview;

    public AccountsReportingRepository(
        AppDbContext db,
        AccountBalancesRepository balances,
        InvestmentReportingRepository investmentReporting,
        OverviewRepository overview)
    {
        _db = db;
        _balances = balances;
        _investmentReporting = investmentReporting;
        _overview = overview;
    }

    /// <summary>
    /// The account catalog. Excludes holdings-sibling shadow accounts (their value
    /// folds into the owning brokerage). Categories are included only when asked
    /// (their <see cref="AccountInfo.Balance"/> is null). Balances are MV-aware.
    /// </summary>
    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(
        Guid ledgerId,
        bool includeCategories,
        bool includeInactive,
        string? type,
        CancellationToken cancellationToken = default)
    {
        var q = _db.Accounts.AsNoTracking().Where(a => a.LedgerId == ledgerId);
        if (!includeInactive) q = q.Where(a => a.IsActive);
        if (!includeCategories) q = q.Where(a => a.AccountType != "category");
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(a => a.AccountType == type);
        // Exclude holdings-sibling shadow accounts (same exclusion the overview
        // uses) — their value is folded into the brokerage, never standalone.
        q = q.Where(a => !_db.Accounts.Any(o =>
            o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id));

        var accounts = await q
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.AccountType,
                a.CategoryKind,
                a.ParentId,
                a.CurrencyCode,
                a.IsActive,
                a.TaxStatus,
                a.HoldingsAccountId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cashBalances = await _balances
            .GetCurrentBalancesAsync(ledgerId, activeOnly: !includeInactive, cancellationToken)
            .ConfigureAwait(false);
        var mvByAccount = await _investmentReporting
            .MarketValueByAccountAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        return accounts
            .Select(a =>
            {
                decimal? balance = null;
                if (a.AccountType != "category")
                {
                    var bal = cashBalances.GetValueOrDefault(a.Id);
                    if (a.AccountType == "investment" && a.HoldingsAccountId is { } sibling)
                        bal += mvByAccount.GetValueOrDefault(sibling);
                    balance = bal;
                }
                return new AccountInfo(
                    a.Id, a.Name, a.AccountType, a.CategoryKind, a.ParentId,
                    a.CurrencyCode, a.IsActive, balance,
                    AccountClassifier.Classify(a.AccountType), a.TaxStatus);
            })
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Net worth + per-account breakdown, delegating to the Overview
    /// computation (one definition; matches the Overview screen).</summary>
    public async Task<McpNetWorth> NetWorthAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var overview = await _overview.GetAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var breakdown = overview.AccountGroups
            .SelectMany(g => g.Accounts)
            .Select(a => new NetWorthLine(
                a.Id, a.Name, a.AccountType, AccountClassifier.Classify(a.AccountType), a.Balance))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        return new McpNetWorth(
            overview.NetWorth,
            overview.TotalAssets,
            overview.TotalLiabilities,
            overview.InvestmentsValue,
            overview.CurrencyCode,
            breakdown);
    }

    // Guards an LLM asking for e.g. a daily series over decades — 2 as-of feeder
    // calls per point, so an unbounded series is a real cost.
    private const int MaxHistoryPoints = 600;

    /// <summary>
    /// Net worth over time: net worth as of the END of each <paramref name="interval"/>
    /// period in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] (final
    /// point clamped to <paramref name="toUtc"/>), assembled from the mig-172
    /// as-of feeder — cash balance as of the instant + split-adjusted holdings
    /// market value — using the same account classification as <c>net_worth</c>
    /// (Overview-consistent; holdings-siblings folded into their brokerage, never
    /// double-counted). Computed live (ADR-0008 mview stays deferred).
    /// </summary>
    public async Task<NetWorthHistory> NetWorthHistoryAsync(
        Guid ledgerId,
        DateTime fromUtc,
        DateTime toUtc,
        ReportTimeBucket interval,
        CancellationToken cancellationToken = default)
    {
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        if (to < from) (from, to) = (to, from);
        var bucket = interval == ReportTimeBucket.None ? ReportTimeBucket.Month : interval;

        // Net-worth-relevant accounts: non-category, and NOT a holdings-sibling
        // shadow account (folded into its brokerage) — the same set the Overview
        // uses. is_active is deliberately NOT filtered (ADR-0085): a historical
        // point must value every account that was open THEN, including ones since
        // closed (e.g. a 401k rolled over mid-window) — the as-of feeder values
        // each by its state at T (nonzero while open, ~0 after liquidation).
        // Filtering current is_active would retroactively drop closed accounts
        // from every point and understate history. (Membership can't be bounded
        // by created_at either — for imported ledgers that's the import time, not
        // the real open date.)
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.AccountType != "category"
                        && !_db.Accounts.Any(o =>
                            o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id))
            .Select(a => new { a.Id, a.AccountType, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var netWorthSiblings = accounts
            .Where(a => a.AccountType == "investment" && a.HoldingsAccountId is not null)
            .Select(a => a.HoldingsAccountId!.Value)
            .ToHashSet();

        var dates = PeriodEnds(from, to, bucket).Take(MaxHistoryPoints + 1).ToList();
        if (dates.Count > MaxHistoryPoints)
            throw new ArgumentException(
                $"That range and interval would produce more than {MaxHistoryPoints} points; " +
                "widen the interval (quarter/year) or narrow the range.");

        var points = new List<NetWorthHistoryPoint>(dates.Count);
        foreach (var t in dates)
        {
            var holdings = await _db.HoldingsMarketValueAsOf(ledgerId, t, null, null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var balances = await _db.AccountBalanceAsOf(ledgerId, t, null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var balanceByAccount = balances.ToDictionary(b => b.AccountId, b => b.Balance);
            var holdingsValueBySibling = holdings
                .GroupBy(h => h.AccountId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.MarketValue));

            var netWorth = 0m;
            foreach (var a in accounts)
            {
                var bal = balanceByAccount.GetValueOrDefault(a.Id);
                if (a.AccountType == "investment" && a.HoldingsAccountId is Guid sibling)
                    bal += holdingsValueBySibling.GetValueOrDefault(sibling);
                netWorth += bal;   // liabilities are stored negative → straight sum
            }

            var unpriced = holdings
                .Count(h => h.PricedFrom == "none" && netWorthSiblings.Contains(h.AccountId));
            points.Add(new NetWorthHistoryPoint(t, netWorth, unpriced));
        }

        return new NetWorthHistory(
            from, to, bucket.ToString().ToLowerInvariant(), "USD", points);
    }

    private static DateTime Utc(DateTime d) =>
        d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    // End-of-period instants for each period from `from`'s period through `to`'s
    // period; the last (partial) period is clamped to `to`.
    private static IEnumerable<DateTime> PeriodEnds(
        DateTime from, DateTime to, ReportTimeBucket bucket)
    {
        var cursor = from;
        while (true)
        {
            var nextStart = NextPeriodStart(cursor, bucket);
            var periodEnd = nextStart.AddTicks(-1);
            yield return periodEnd <= to ? periodEnd : to;
            if (nextStart > to) yield break;
            cursor = nextStart;
        }
    }

    private static DateTime NextPeriodStart(DateTime d, ReportTimeBucket bucket) => bucket switch
    {
        ReportTimeBucket.Year    => FirstOfMonthUtc(d.Year + 1, 1),
        ReportTimeBucket.Quarter => FirstOfMonthUtc(d.Year, ((d.Month - 1) / 3 + 1) * 3 + 1),
        _                        => FirstOfMonthUtc(d.Year, d.Month + 1),   // Month
    };

    // First-of-month at UTC midnight; a month index > 12 rolls into later years.
    private static DateTime FirstOfMonthUtc(int year, int monthIndex)
    {
        year += (monthIndex - 1) / 12;
        var month = (monthIndex - 1) % 12 + 1;
        return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
