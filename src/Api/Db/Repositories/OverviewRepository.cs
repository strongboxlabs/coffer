using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Computes the ledger overview aggregate (ADR-0056 slice 1): per-account
/// current balances grouped by type, net worth, and an investment roll-up.
/// </summary>
/// <remarks>
/// <para>Account balances come from <see cref="AccountBalancesRepository"/> — the one
/// shared definition of "current balance" (the register's latest
/// <c>balance_after</c> among the rows it does not call Scheduled, ADR-0034,
/// ADR-0056), never re-derived here. Investment accounts add holdings market value
/// over the positions held on the caller's today (qty × the latest close on or before
/// it, else the last trade's price; a position with neither carried at cost basis).
/// Liabilities are stored negative, so net worth is a straight sum. LINQ/EF
/// throughout (no raw SQL in the API).</para>
/// <para>Conversion (ADR-0114 D7, D9). Each account's balance is in its own currency; a
/// position priced in another converts into its brokerage's. Every figure that
/// combines accounts converts each account into the ledger's reporting currency and
/// sums, rounding once. Today's figures convert at the latest rate, a past day's (the
/// comparison) at that day's. A figure that needs a missing rate is null and names it.
/// Every rate comes from one <see cref="ExchangeRateLookup"/> load per computation.</para>
/// </remarks>
public sealed class OverviewRepository
{
    // Net-worth-relevant account types — the shared definition (also drives the
    // assets/liabilities subtotals). Categories and the holdings-sibling shadow
    // accounts fall outside both sets (and the sibling is excluded explicitly).
    private static readonly string[] AssetTypes = AccountClassifier.AssetTypes;
    private static readonly string[] LiabilityTypes = AccountClassifier.LiabilityTypes;

    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;
    private readonly InvestmentReportingRepository _investmentReporting;

    public OverviewRepository(
        AppDbContext db,
        AccountBalancesRepository balances,
        InvestmentReportingRepository investmentReporting)
    {
        _db = db;
        _balances = balances;
        _investmentReporting = investmentReporting;
    }

    /// <param name="today">The caller's local date, as the register takes it
    /// (<see cref="ScheduledBoundary"/>); null for the register's fallback.</param>
    public Task<LedgerOverviewDto> GetAsync(
        Guid ledgerId, DateOnly? today, CancellationToken cancellationToken = default) =>
        GetAsync(ledgerId, today, since: null, cancellationToken);

    /// <summary>
    /// The overview, and, when <paramref name="since"/> is given, net worth at the end
    /// of that day beside it.
    /// </summary>
    /// <remarks>
    /// <para>Net worth then is this same computation with <paramref name="since"/> as
    /// its date: migration 259 defines a current value on a past date as the
    /// history's valuation at the end of that day, so the figure equals the
    /// net-worth history's point for <paramref name="since"/>, converted at that day's
    /// rate as the history's point is. Assets and liabilities are not compared; nothing
    /// shows their change.</para>
    /// <para>Before the ledger's first dated fact there is no history, and the change
    /// is null rather than the whole net worth (<see cref="LedgerHistoryStart"/>).</para>
    /// </remarks>
    /// <param name="since">A month-end before <paramref name="today"/>; the endpoint
    /// refuses any other date.</param>
    public async Task<LedgerOverviewDto> GetAsync(
        Guid ledgerId, DateOnly? today, DateOnly? since, CancellationToken cancellationToken = default)
    {
        var (now, _) = await ComputeAsync(ledgerId, today, null, [], cancellationToken).ConfigureAwait(false);
        if (since is not { } day) return now;

        var earliest = await LedgerHistoryStart.EarliestFactAsync(_db, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (LedgerHistoryStart.Precedes(day, earliest))
        {
            return now with
            {
                Comparison = new OverviewComparisonDto(
                    day, new OverviewChangeDto(null, null), NoHistory: true, RateReasons.None),
            };
        }

        var (then, _) = await ComputeAsync(ledgerId, day, day, [], cancellationToken).ConfigureAwait(false);
        decimal? change = now.NetWorth is { } n && then.NetWorth is { } t ? n - t : null;
        return now with
        {
            Comparison = new OverviewComparisonDto(
                day, new OverviewChangeDto(then.NetWorth, change), NoHistory: false,
                then.NetWorth is null ? RateReasons.Of(then.RateUnavailable) : RateReasons.None),
        };
    }

    /// <param name="today">The day valued: the caller's today, or the comparison's.</param>
    /// <param name="ratedOn">The day whose rate it converts at, by the one rule
    /// (<see cref="RateDay.Of(DateOnly, DateOnly)"/>): the comparison's past day; null
    /// for the latest rate, as today's figures are.</param>
    /// <param name="sets">Further sets of accounts to combine, each by the same rule as a
    /// type's subtotal; their figures come back in the same order. An id that is not one
    /// of the overview's accounts adds nothing.</param>
    private async Task<(LedgerOverviewDto Overview, IReadOnlyList<Converted> Sets)> ComputeAsync(
        Guid ledgerId, DateOnly? today, DateOnly? ratedOn, IReadOnlyList<IReadOnlyCollection<Guid>> sets,
        CancellationToken cancellationToken)
    {
        // 1) Net-worth-relevant accounts. The type filter excludes categories;
        //    the NOT EXISTS excludes holdings-sibling shadow accounts (same
        //    exclusion AccountsRepository uses) — their value folds into the
        //    owning brokerage below, never counted standalone. is_active is
        //    deliberately NOT filtered (ADR-0085): net worth reflects real value,
        //    so a closed account still holding value must count. is_active is a
        //    UI-surfacing flag, not a valuation gate; a closed-and-zeroed account
        //    contributes 0 regardless.
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && (AssetTypes.Contains(a.AccountType)
                            || LiabilityTypes.Contains(a.AccountType))
                        && !_db.Accounts.Any(o =>
                            o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id))
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.AccountType,
                a.CurrencyCode,
                a.HoldingsAccountId,
                a.IsActive,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2) Current cash balance per account, from the shared balance source.
        //    activeOnly: false — net worth includes every real account regardless
        //    of is_active (ADR-0085); a closed account's residual value is real.
        var balanceByAccountId = await _balances
            .GetCurrentBalancesAsync(ledgerId, activeOnly: false, today, cancellationToken)
            .ConfigureAwait(false);

        // 3) The positions held on the same day, each valued in its price's currency,
        //    by the single shared definition (net worth and accounts reporting read it).
        var positions = await _investmentReporting
            .CurrentPositionsAsync(ledgerId, null, today, cancellationToken)
            .ConfigureAwait(false);
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var reporting = currencies.Reporting;

        // A position kept in another currency than the reporting one has its cost basis
        // converted lot by lot, at each lot's acquisition date (ADR-0114 D7); every other
        // position keeps the cost basis it reports, and no lot is read for it.
        bool Foreign(Guid holdingsAccountId) => currencies.OfPositionsOn(holdingsAccountId) != reporting;
        var lots = await LotCosts.OpenAsync(
                _db, ledgerId, ScheduledBoundary.LastCountedInstant(today, _investmentReporting.Clock),
                [.. positions.Select(p => p.AccountId).Where(Foreign).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        // 4) Every rate the figures below read, in one load: each position into its
        //    brokerage's currency, each account into the reporting currency, and each
        //    foreign lot at its acquisition day.
        var lookup = _investmentReporting.Rates;
        var needs = lookup.NewNeeds();
        var at = ratedOn is { } pastDay ? RateDay.Of(pastDay, needs.ServerToday) : RateDay.Latest();
        InvestmentReportingRepository.DeclarePositions(needs, positions, currencies, at);
        foreach (var a in accounts) at.Declare(needs, a.CurrencyCode, reporting);
        foreach (var p in positions.Where(p => Foreign(p.AccountId)))
            LotCosts.Declare(needs, lots[(p.AccountId, p.SecurityId)], currencies.OfPositionsOn(p.AccountId), reporting);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var holdingsValueByAccount = InvestmentReportingRepository.ValueByAccount(positions, currencies, rates, at);

        // 5) Per-account balance = cash (+ holdings value for investments), in the
        //    account's own currency.
        var enriched = accounts
            .Select(a =>
            {
                var cash = balanceByAccountId.GetValueOrDefault(a.Id);
                var balance = Converted.Of(cash);
                if (a.AccountType == "investment" && a.HoldingsAccountId is Guid sibling
                    && holdingsValueByAccount.TryGetValue(sibling, out var held))
                {
                    balance = rates.NewTotal(a.CurrencyCode)
                        .Add(cash, a.CurrencyCode, at)
                        .Add(held, currencies.OfPositionsOn(sibling), at)
                        .Result;
                }
                return new OverviewRow(a.Id, a.Name, a.AccountType, a.CurrencyCode, a.IsActive, cash, balance);
            })
            .ToList();

        // A combined figure: each account's figure converted into the reporting currency
        // at the day's rate, summed, rounded once (ADR-0114 D7).
        Converted Combined(IEnumerable<OverviewRow> rows, Func<OverviewRow, Converted> figure)
        {
            var total = rates.NewTotal(reporting);
            foreach (var r in rows) total.Add(figure(r), r.CurrencyCode, at);
            return total.Result;
        }

        // 6) Group by type with subtotals; totals; net worth (a straight sum,
        //    liabilities already negative).
        var groups = enriched
            .GroupBy(a => a.AccountType)
            .Select(g =>
            {
                var subtotal = Combined(g, a => a.Balance);
                return new OverviewAccountGroupDto(
                    g.Key,
                    subtotal.Amount,
                    g.OrderBy(a => a.Name, StringComparer.Ordinal)
                        .ThenBy(a => a.Id)
                        .Select(a => new OverviewAccountDto(
                            a.Id, a.Name, a.AccountType, a.CurrencyCode, a.Balance.Amount, a.IsActive,
                            a.Balance.Unavailable))
                        .ToList(),
                    subtotal.Unavailable);
            })
            .OrderBy(g => TypeOrder(g.AccountType))
            .ToList();

        var totalAssets = Combined(enriched.Where(a => AssetTypes.Contains(a.AccountType)), a => a.Balance);
        var totalLiabilities = Combined(enriched.Where(a => LiabilityTypes.Contains(a.AccountType)), a => a.Balance);
        var netWorth = Combined(enriched, a => a.Balance);
        var investments = enriched.Where(a => a.AccountType == "investment").ToList();
        var investmentsValue = Combined(investments, a => a.Balance);

        // 7) The portfolio, in the reporting currency. The value is the positions'; the
        //    cash is the brokerages' own, beside it, so the two make investmentsValue.
        var valueTotal = rates.NewTotal(reporting);
        foreach (var (sibling, held) in holdingsValueByAccount)
            valueTotal.Add(held, currencies.OfPositionsOn(sibling), at);
        var portfolioValue = valueTotal.Result;
        var portfolioCash = Combined(investments, a => Converted.Of(a.Cash));
        var costTotal = rates.NewTotal(reporting);
        foreach (var p in positions)
        {
            if (Foreign(p.AccountId))
                LotCosts.AddTo(costTotal, lots[(p.AccountId, p.SecurityId)], currencies.OfPositionsOn(p.AccountId));
            else
                costTotal.Add(p.CostBasis, reporting, at);
        }
        var portfolioCost = costTotal.Result;
        decimal? gain = portfolioValue.Amount is { } value && portfolioCost.Amount is { } cost ? value - cost : null;
        decimal? percent = gain is { } g && portfolioCost.Amount is { } basis
            ? (basis != 0m ? ReportingScale.Percent(g, basis) : 0m)
            : null;
        var portfolio = new PortfolioRollupDto(
            Value: portfolioValue.Amount,
            CostBasis: portfolioCost.Amount,
            UnrealizedGain: gain,
            PercentChange: percent,
            CashBalance: portfolioCash.Amount,
            CurrencyCode: reporting,
            RateUnavailable: RateReasons.Of(
                portfolioValue.Unavailable, portfolioCost.Unavailable, portfolioCash.Unavailable));

        var overview = new LedgerOverviewDto(
            NetWorth: netWorth.Amount,
            TotalAssets: totalAssets.Amount,
            TotalLiabilities: totalLiabilities.Amount,
            InvestmentsValue: investmentsValue.Amount,
            CurrencyCode: reporting,
            AccountGroups: groups,
            Portfolio: portfolio,
            RateUnavailable: RateReasons.Of(
                netWorth.Unavailable, totalAssets.Unavailable, totalLiabilities.Unavailable,
                investmentsValue.Unavailable));
        var combinedSets = sets
            .Select(set => Combined(enriched.Where(a => set.Contains(a.Id)), a => a.Balance))
            .ToList();
        return (overview, combinedSets);
    }

    /// <summary>
    /// The sidebar rail's totals (ADR-0106 D6): each account type's, and each folder's over
    /// every account beneath it at any depth, inactive ones included (ADR-0085). Each is a
    /// combined figure (ADR-0114 D7): the accounts' current balances converted into the
    /// reporting currency at the latest rate, summed, rounded once, by the same computation
    /// as the overview's type subtotals, so a type's figure is the overview's. A total that
    /// needs a missing rate is null and names it (ADR-0114 D9); no account is left out.
    /// </summary>
    /// <param name="today">The caller's local date, as the register takes it; null for the
    /// register's fallback.</param>
    public async Task<AccountSubtotalsDto> SubtotalsAsync(
        Guid ledgerId, DateOnly? today, CancellationToken cancellationToken = default)
    {
        var folders = await _db.AccountFolders.AsNoTracking()
            .Where(f => f.LedgerId == ledgerId)
            .Select(f => new { f.Id, f.ParentId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var members = await _db.AccountFolderMembers.AsNoTracking()
            .Where(m => m.LedgerId == ledgerId)
            .Select(m => new { m.FolderId, m.AccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every account beneath each folder, at any depth. A cycle cannot be stored (the
        // folder writes refuse one); the walk is guarded all the same.
        var childrenOf = folders.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value, f => f.Id);
        var accountsOf = members.ToLookup(m => m.FolderId, m => m.AccountId);
        List<Guid> Beneath(Guid folderId)
        {
            var seen = new HashSet<Guid>();
            var pending = new Stack<Guid>([folderId]);
            var ids = new List<Guid>();
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (!seen.Add(next)) continue;
                ids.AddRange(accountsOf[next]);
                foreach (var child in childrenOf[next]) pending.Push(child);
            }
            return ids;
        }

        var beneath = folders
            .OrderBy(f => f.Id)
            .Select(f => (f.Id, Accounts: Beneath(f.Id).Order().ToList()))
            .ToList();
        var (overview, sums) = await ComputeAsync(
                ledgerId, today, null, [.. beneath.Select(b => (IReadOnlyCollection<Guid>)b.Accounts.ToHashSet())],
                cancellationToken)
            .ConfigureAwait(false);

        return new AccountSubtotalsDto(
            overview.CurrencyCode,
            [.. overview.AccountGroups.Select(g => new AccountTypeSubtotalDto(g.AccountType, g.Subtotal, g.RateUnavailable))],
            [.. beneath.Select((b, i) => new AccountFolderSubtotalDto(b.Id, b.Accounts, sums[i].Amount, sums[i].Unavailable))]);
    }

    /// <summary>One account of the overview: its cash, and its balance in its own
    /// currency (cash plus its positions, converted into it).</summary>
    private sealed record OverviewRow(
        Guid Id, string Name, string AccountType, string CurrencyCode, bool IsActive,
        decimal Cash, Converted Balance);

    // Display order for the account-type groups (mirrors the old Hub order).
    private static int TypeOrder(string type) => type switch
    {
        "bank" => 0,
        "cash" => 1,
        "credit_card" => 2,
        "investment" => 3,
        "asset" => 4,
        "liability" => 5,
        "loan" => 6,
        _ => 99,
    };
}
