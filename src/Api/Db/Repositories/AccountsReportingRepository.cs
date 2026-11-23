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
    /// (their <see cref="AccountInfo.Balance"/> is null). Balances are MV-aware, and
    /// current on <paramref name="today"/>, the caller's local date as the register
    /// takes it (null for the register's fallback, which is what MCP gets).
    /// </summary>
    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(
        Guid ledgerId,
        bool includeCategories,
        bool includeInactive,
        string? type,
        DateOnly? today,
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
                a.OpenedOn,
                a.HoldingsAccountId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cashBalances = await _balances
            .GetCurrentBalancesAsync(ledgerId, activeOnly: !includeInactive, today, cancellationToken)
            .ConfigureAwait(false);
        var mvByAccount = await _investmentReporting
            .MarketValueByAccountAsync(ledgerId, today, cancellationToken)
            .ConfigureAwait(false);
        var folderByAccount = await _db.AccountFolderMembers.AsNoTracking()
            .Where(m => m.LedgerId == ledgerId)
            .Join(_db.AccountFolders, m => m.FolderId, f => f.Id,
                (m, f) => new { m.AccountId, FolderId = f.Id, f.Name })
            .ToDictionaryAsync(x => x.AccountId, cancellationToken)
            .ConfigureAwait(false);

        return accounts
            .Select(a =>
            {
                decimal? balance = null;
                IReadOnlyList<RateUnavailable> missing = RateReasons.None;
                if (a.AccountType != "category")
                {
                    balance = cashBalances.GetValueOrDefault(a.Id);
                    // The holdings value is already in the brokerage's currency: a
                    // position priced in another converted at the latest rate, and null
                    // when that rate is missing (ADR-0114 D7, D9).
                    if (a.AccountType == "investment" && a.HoldingsAccountId is { } sibling
                        && mvByAccount.TryGetValue(sibling, out var held))
                    {
                        balance = held.Amount is { } mv ? balance + mv : null;
                        missing = held.Unavailable;
                    }
                }
                var folder = folderByAccount.GetValueOrDefault(a.Id);
                return new AccountInfo(
                    a.Id, a.Name, a.AccountType, a.CategoryKind, a.ParentId,
                    a.CurrencyCode, a.IsActive, balance,
                    AccountClassifier.Classify(a.AccountType), a.TaxStatus, a.OpenedOn,
                    folder?.FolderId, folder?.Name, missing);
            })
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .ToList();
    }

    /// <summary>Net worth + per-account breakdown, delegating to the Overview
    /// computation (one definition; matches the Overview screen). MCP passes no date,
    /// so it gets the register's fallback.</summary>
    public async Task<McpNetWorth> NetWorthAsync(
        Guid ledgerId, DateOnly? today, CancellationToken cancellationToken = default)
    {
        var overview = await _overview.GetAsync(ledgerId, today, cancellationToken).ConfigureAwait(false);
        var breakdown = overview.AccountGroups
            .SelectMany(g => g.Accounts)
            .Select(a => new NetWorthLine(
                a.Id, a.Name, a.AccountType, AccountClassifier.Classify(a.AccountType), a.Balance,
                a.CurrencyCode, a.RateUnavailable))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.AccountId)
            .ToList();
        return new McpNetWorth(
            overview.NetWorth,
            overview.TotalAssets,
            overview.TotalLiabilities,
            overview.InvestmentsValue,
            overview.CurrencyCode,
            breakdown,
            overview.RateUnavailable);
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
    public Task<NetWorthHistory> NetWorthHistoryAsync(
        Guid ledgerId,
        DateTime fromUtc,
        DateTime toUtc,
        ReportTimeBucket interval,
        CancellationToken cancellationToken = default) =>
        NetWorthHistoryAsync(ledgerId, fromUtc, toUtc, interval, today: null, cancellationToken);

    /// <inheritdoc cref="NetWorthHistoryAsync(Guid, DateTime, DateTime, ReportTimeBucket, CancellationToken)"/>
    /// <param name="today">The caller's local date. When sent, the series ends no
    /// later than the last instant the register does not call Scheduled
    /// (<see cref="ScheduledBoundary.LastCountedInstant"/>), so its last point is
    /// the overview's net worth for the same date. Null leaves the window as asked,
    /// which is what MCP gets.</param>
    public async Task<NetWorthHistory> NetWorthHistoryAsync(
        Guid ledgerId,
        DateTime fromUtc,
        DateTime toUtc,
        ReportTimeBucket interval,
        DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        if (to < from) (from, to) = (to, from);
        if (today is { } local)
        {
            var now = ScheduledBoundary.LastCountedInstant(local);
            if (now < to) to = now < from ? from : now;
        }
        // None means "unspecified", and month is the documented default.
        var bucket = interval == ReportTimeBucket.None ? ReportTimeBucket.Month : interval;

        // Day is REFUSED rather than honoured, because `NextPeriodStart` has no
        // Day arm: it falls to the month default, so a daily request came back
        // as MONTHLY points presented as a daily series — wrong, silently, and
        // reachable today through the `net_worth_history` MCP tool, whose
        // description says month/quarter/year while its parser accepts the whole
        // enum. Same reasoning as SummarizeAsync refusing a daily bucket off the
        // Category dimension: a caller who asks for days and is handed months
        // cannot tell from the response.
        //
        // Refused rather than implemented because a daily series is also the one
        // shape MaxHistoryPoints cannot carry — 600 points is under two years of
        // days, and each point costs two as-of feeder reads.
        if (bucket == ReportTimeBucket.Day)
            throw new ReportRequestException(
                "interval must be month, quarter or year; a daily net-worth series is not "
                + "supported (each point is a full as-of valuation).");

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
            .Select(a => new { a.Id, a.AccountType, a.HoldingsAccountId, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var currencyCode = currencies.Reporting;

        // A point before the ledger's first dated fact values nothing: flagged, so a
        // reader never counts the ledger's arrival as growth.
        var earliestFact = await LedgerHistoryStart.EarliestFactAsync(_db, ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var netWorthSiblings = accounts
            .Where(a => a.AccountType == "investment" && a.HoldingsAccountId is not null)
            .Select(a => a.HoldingsAccountId!.Value)
            .ToHashSet();

        var dates = PeriodEnds(from, to, bucket).Take(MaxHistoryPoints + 1).ToList();
        if (dates.Count > MaxHistoryPoints)
            throw new ReportRequestException(
                $"That range and interval would produce more than {MaxHistoryPoints} points; " +
                "widen the interval (quarter/year) or narrow the range.");

        // Both feeders answer for EVERY period end in one call, not once per point
        // inside the loop below — per-point calls are O(instants) round trips, the
        // cost migrations 200/201 remove from a whole-ledger TWR, and the reason
        // MaxHistoryPoints exists at all.
        // TRUNCATED TO MICROSECONDS, and the same truncated values are used as the
        // dictionary keys below. PeriodEnds yields nextStart.AddTicks(-1), i.e. one
        // tick (a ten-millionth of a second) before midnight — but timestamptz is
        // microsecond-precision, so that value
        // is rounded on the way into Postgres and the as_of coming back is NOT the
        // DateTime that was sent. Keying on a round-tripped timestamp silently misses
        // every lookup and reports a net worth of zero for every point.
        var instants = dates
            .Select(d =>
            {
                var utc = d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
                return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
            })
            .ToArray();

        var holdingsRows = await _db.HoldingsMarketValueAsOfSet(ledgerId, instants, null)
            .Select(r => new { r.AsOf, r.AccountId, r.MarketValue, r.PricedFrom, r.PriceCurrency })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Every rate the series reads, in one load (ADR-0114 D7): each position from its
        // price's currency into its brokerage's, and each account into the reporting
        // currency, both at the point's own day. A point's day is its instant's UTC date,
        // the day every price and balance it values is dated by; a point dated today or
        // later (the series' last) converts at the latest rate, by the one rule
        // (RateDay.Of).
        static DateOnly DayOf(DateTime instant) => DateOnly.FromDateTime(instant);
        var lookup = _investmentReporting.Rates;
        var needs = lookup.NewNeeds();
        foreach (var r in holdingsRows)
        {
            var into = currencies.OfPositionsOn(r.AccountId);
            needs.Add(r.PriceCurrency ?? into, into, DayOf(r.AsOf));
        }
        foreach (var instant in instants)
            foreach (var a in accounts)
                needs.Add(a.CurrencyCode, currencyCode, DayOf(instant));
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        // Per instant and holdings account, the positions' value in the brokerage's
        // currency, converted at full precision and rounded once.
        var holdingsByInstant = holdingsRows
            .GroupBy(r => r.AsOf)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.AccountId)
                      .ToDictionary(a => a.Key, a =>
                      {
                          var into = currencies.OfPositionsOn(a.Key);
                          var total = rates.NewTotal(into);
                          foreach (var x in a) total.Add(x.MarketValue, x.PriceCurrency ?? into, DayOf(x.AsOf));
                          return total.Result;
                      }));

        // Unpriced positions per instant — the same count the per-instant loop
        // produced, now derived from the one batched read.
        var unpricedByInstant = holdingsRows
            .Where(r => r.PricedFrom == "none" && netWorthSiblings.Contains(r.AccountId))
            .GroupBy(r => r.AsOf)
            .ToDictionary(g => g.Key, g => g.Count());

        var balancesByInstant = (await _db.AccountBalanceAsOfInstants(ledgerId, instants, null)
                .Select(r => new { r.AsOf, r.AccountId, r.Balance })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.AsOf)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.AccountId, x => x.Balance));

        var points = new List<NetWorthHistoryPoint>(dates.Count);
        for (var i = 0; i < dates.Count; i++)
        {
            var t = dates[i];
            var key = instants[i];
            var balanceByAccount = balancesByInstant.GetValueOrDefault(key)
                                   ?? new Dictionary<Guid, decimal>();
            var holdingsValueBySibling = holdingsByInstant.GetValueOrDefault(key)
                                         ?? new Dictionary<Guid, Converted>();
            var day = DayOf(key);

            // Each account in its own currency (cash, plus its positions converted into
            // it), then converted into the reporting currency at the point's day and
            // summed, rounded once. Liabilities are stored negative, so a straight sum.
            var netWorth = rates.NewTotal(currencyCode);
            foreach (var a in accounts)
            {
                var cash = balanceByAccount.GetValueOrDefault(a.Id);
                if (a.AccountType == "investment" && a.HoldingsAccountId is Guid sibling
                    && holdingsValueBySibling.TryGetValue(sibling, out var held))
                {
                    var own = rates.NewTotal(a.CurrencyCode)
                        .Add(cash, a.CurrencyCode, day)
                        .Add(held, currencies.OfPositionsOn(sibling), day)
                        .Result;
                    netWorth.Add(own, a.CurrencyCode, day);
                }
                else
                {
                    netWorth.Add(cash, a.CurrencyCode, day);
                }
            }

            var unpriced = unpricedByInstant.GetValueOrDefault(key);
            var value = netWorth.Result;
            points.Add(new NetWorthHistoryPoint(
                t, value.Amount, unpriced, LedgerHistoryStart.Precedes(DateOnly.FromDateTime(t), earliestFact),
                value.Unavailable));
        }

        return new NetWorthHistory(
            from, to, bucket.ToString().ToLowerInvariant(), currencyCode, points);
    }

    private static DateTime Utc(DateTime d) =>
        d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    // End-of-period instants for each period from `from`'s period through `to`'s
    // period; the last (partial) period is clamped to `to`. Shared with the returns
    // series, so the two reports end a month, a quarter and a year at the same instant.
    internal static IEnumerable<DateTime> PeriodEnds(
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
