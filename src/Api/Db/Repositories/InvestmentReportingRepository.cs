using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Investment reporting reads (ADR-0063 §D5) for the MCP tools (and future
/// Portfolio / Asset-Allocation reports). Holdings are rolled up per security
/// across the ledger; market value = qty × latest <c>security_prices</c>, no-price
/// positions carried at cost basis (the <see cref="OverviewRepository"/>
/// convention, reused here). LINQ/EF, no raw SQL.
/// </summary>
public sealed class InvestmentReportingRepository
{
    private readonly AppDbContext _db;

    public InvestmentReportingRepository(AppDbContext db) => _db = db;

    public async Task<HoldingsSnapshot> HoldingsSnapshotAsync(
        Guid ledgerId, Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        // Optional brokerage filter → its holdings-sibling account. Fall back to
        // the id itself if the caller passed a sibling (or an account with none).
        Guid? siblingFilter = null;
        if (accountId is { } acct)
        {
            siblingFilter = await _db.Accounts.AsNoTracking()
                .Where(a => a.Id == acct && a.LedgerId == ledgerId)
                .Select(a => a.HoldingsAccountId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            siblingFilter ??= acct;
        }

        var holdingsQ = _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.Quantity != 0m);
        if (siblingFilter is { } sib)
            holdingsQ = holdingsQ.Where(h => h.AccountId == sib);

        // Roll up holdings per security (a security may sit in more than one
        // brokerage's Holdings sibling).
        var positions = await holdingsQ
            .GroupBy(h => h.SecurityId)
            .Select(g => new
            {
                SecurityId = g.Key,
                Quantity = g.Sum(x => x.Quantity),
                CostBasis = g.Sum(x => x.CostBasis),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (positions.Count == 0)
            return new HoldingsSnapshot([], 0m, 0m, 0m);

        var securityIds = positions.Select(p => p.SecurityId).ToList();

        var securities = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && securityIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name, s.AssetClass })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        // Latest price per security (reduce in C#; EF GroupBy-First is awkward —
        // same approach as OverviewRepository).
        var latestPrice = (await _db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && securityIds.Contains(p.SecurityId))
            .Select(p => new { p.SecurityId, p.Price, p.PriceDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .GroupBy(p => p.SecurityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PriceDate).First().Price);

        // Per-(security, account) quantities → the "held in which account(s)"
        // breakdown, mapped from the holdings-sibling up to the owning brokerage.
        var perAccount = await holdingsQ
            .GroupBy(h => new { h.SecurityId, h.AccountId })
            .Select(g => new { g.Key.SecurityId, g.Key.AccountId, Quantity = g.Sum(x => x.Quantity) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var brokerageBySibling = (await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId != null)
            .Select(a => new { Sibling = a.HoldingsAccountId!.Value, BrokerageId = a.Id, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(a => a.Sibling, a => (a.BrokerageId, a.Name));
        var heldInBySecurity = perAccount
            .GroupBy(x => x.SecurityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<HeldInSlice>)g
                    .Select(x => brokerageBySibling.TryGetValue(x.AccountId, out var bk)
                        ? new HeldInSlice(bk.BrokerageId, bk.Name, x.Quantity)
                        : new HeldInSlice(x.AccountId, "(account)", x.Quantity))
                    .OrderByDescending(s => s.Quantity)
                    .ToList());

        var rows = positions
            .Select(p =>
            {
                var hasPrice = latestPrice.TryGetValue(p.SecurityId, out var px);
                var marketValue = hasPrice ? p.Quantity * px : p.CostBasis;
                var gain = marketValue - p.CostBasis;
                securities.TryGetValue(p.SecurityId, out var s);
                return new HoldingSnapshotRow(
                    p.SecurityId,
                    s?.Ticker,
                    s?.Name ?? "(unknown security)",
                    s?.AssetClass,
                    p.Quantity,
                    p.CostBasis,
                    hasPrice ? px : null,
                    marketValue,
                    gain,
                    p.CostBasis != 0m ? gain / p.CostBasis * 100m : null,
                    heldInBySecurity.TryGetValue(p.SecurityId, out var hi)
                        ? hi : Array.Empty<HeldInSlice>());
            })
            .OrderByDescending(r => r.MarketValue)
            .ToList();

        return new HoldingsSnapshot(
            rows,
            rows.Sum(r => r.MarketValue),
            rows.Sum(r => r.CostBasis),
            rows.Sum(r => r.UnrealizedGain));
    }

    /// <summary>
    /// Current market value per holdings-sibling account (qty × latest
    /// <c>security_prices</c>; no-price positions carried at cost basis — the
    /// OverviewRepository convention). The single definition of "holdings value
    /// by account", shared by the overview, net worth, and accounts reporting so
    /// they never diverge. Keyed by the holdings-sibling account id (resolve to
    /// the owning brokerage via <c>accounts.holdings_account_id</c>).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> MarketValueByAccountAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var holdings = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.Quantity != 0m)
            .Select(h => new { h.AccountId, h.SecurityId, h.Quantity, h.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (holdings.Count == 0)
            return new Dictionary<Guid, decimal>();

        var securityIds = holdings.Select(h => h.SecurityId).Distinct().ToList();
        var latestPrice = (await _db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && securityIds.Contains(p.SecurityId))
            .Select(p => new { p.SecurityId, p.Price, p.PriceDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .GroupBy(p => p.SecurityId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PriceDate).First().Price);

        var byAccount = new Dictionary<Guid, decimal>();
        foreach (var h in holdings)
        {
            var value = latestPrice.TryGetValue(h.SecurityId, out var px)
                ? h.Quantity * px
                : h.CostBasis;
            byAccount[h.AccountId] = byAccount.GetValueOrDefault(h.AccountId) + value;
        }
        return byAccount;
    }

    /// <summary>
    /// Portfolio allocation (ADR-0063/0067) bucketed by a chosen dimension —
    /// asset class, region, or vehicle. For asset_class/region, a
    /// <c>multi_asset</c> security with <c>security_components</c> sleeves is
    /// decomposed across them (by weight) rather than counting 100% in one bucket;
    /// a multi_asset security with no sleeves buckets wholesale as "multi_asset".
    /// Vehicle is always a leaf attribute. Unclassified values bucket as
    /// "Unclassified".
    /// </summary>
    public async Task<AllocationResult> AllocationAsync(
        Guid ledgerId,
        AllocationDimension dimension = AllocationDimension.AssetClass,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await HoldingsSnapshotAsync(ledgerId, accountId: null, cancellationToken)
            .ConfigureAwait(false);
        var total = snapshot.TotalMarketValue;
        if (snapshot.Holdings.Count == 0)
            return new AllocationResult([], 0m);

        var secIds = snapshot.Holdings.Select(h => h.SecurityId).Distinct().ToList();
        var secs = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && secIds.Contains(s.Id))
            .Select(s => new { s.Id, s.AssetClass, s.Region, s.VehicleType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        // Look-through applies only to the asset_class / region dimensions, and
        // only to multi_asset securities (the single look-through signal).
        var lookThrough = dimension is AllocationDimension.AssetClass or AllocationDimension.Region;
        var componentsBySecurity = new Dictionary<Guid, List<(string Cls, string? Region, decimal Weight)>>();
        if (lookThrough)
        {
            var ltIds = secs.Values.Where(s => s.AssetClass == "multi_asset").Select(s => s.Id).ToList();
            if (ltIds.Count > 0)
            {
                componentsBySecurity = (await _db.SecurityComponents.AsNoTracking()
                    .Where(c => ltIds.Contains(c.SecurityId))
                    .Select(c => new { c.SecurityId, c.ComponentAssetClass, c.ComponentRegion, c.Weight })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                    .GroupBy(c => c.SecurityId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(c => (c.ComponentAssetClass, c.ComponentRegion, c.Weight)).ToList());
            }
        }

        var buckets = new Dictionary<string, decimal>();
        void Add(string key, decimal mv) => buckets[key] = buckets.GetValueOrDefault(key) + mv;

        foreach (var h in snapshot.Holdings)
        {
            secs.TryGetValue(h.SecurityId, out var s);

            if (lookThrough && s is { AssetClass: "multi_asset" }
                && componentsBySecurity.TryGetValue(h.SecurityId, out var comps) && comps.Count > 0)
            {
                var weightSum = comps.Sum(c => c.Weight);
                if (weightSum > 0m)
                {
                    foreach (var c in comps)
                    {
                        var slice = h.MarketValue * (c.Weight / weightSum);
                        var key = dimension == AllocationDimension.AssetClass
                            ? c.Cls
                            : (c.Region ?? "Unclassified");
                        Add(key, slice);
                    }
                    continue;
                }
            }

            // Account dimension decomposes each position across the brokerage(s)
            // holding it, apportioning market value by quantity share (robust to
            // null prices — MarketValue is already carried at cost there).
            // Multiply BEFORE dividing so an exactly-divisible split stays exact
            // (2250 * 10 / 15 = 1500, not 2250 * 0.666… rounded).
            if (dimension == AllocationDimension.Account)
            {
                if (h.Quantity != 0m)
                    foreach (var slice in h.HeldIn)
                        Add(slice.AccountName, h.MarketValue * slice.Quantity / h.Quantity);
                continue;
            }

            var bucketKey = dimension switch
            {
                AllocationDimension.Region => s?.Region ?? "Unclassified",
                AllocationDimension.VehicleType => s?.VehicleType ?? "Unclassified",
                AllocationDimension.Security => h.Ticker ?? h.Name,
                _ => s?.AssetClass ?? h.AssetClass ?? "Unclassified",
            };
            Add(bucketKey, h.MarketValue);
        }

        var rows = buckets
            .Select(kv => new AllocationRow(kv.Key, kv.Value, total != 0m ? kv.Value / total * 100m : 0m))
            .OrderByDescending(b => b.MarketValue)
            .ToList();

        return new AllocationResult(rows, total);
    }

    /// <summary>
    /// Investment activity feed (ADR-0080): each investment header collapsed into
    /// ONE event via <see cref="InvestmentEventProjector"/> — the same aggregation
    /// the register renders, reused server-side for MCP. Scoped to investment
    /// accounts; filters by brokerage / security / date window; newest first, capped
    /// at <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// Fetches only the brokerage-account legs of each event — their counterparty
    /// fields carry everything the projector needs for the category / transfer / fee
    /// slots, so the off-account legs aren't needed. The result set is bounded by the
    /// filters + limit; pass a date window for a large ledger. The security filter is
    /// applied to the PROJECTED event's security, so an event's fee leg is never
    /// dropped mid-aggregation.
    /// </remarks>
    public async Task<InvestmentActivityResult> ActivityAsync(
        Guid ledgerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        Guid? accountId,
        Guid? securityId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var q =
            from r in _db.ResolvedTransactions.AsNoTracking()
            join a in _db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == ledgerId
                  && a.AccountType == "investment"
                  // Real brokerages only (holdings_account_id set) — excludes the
                  // holdings-sibling sub-accounts, which are also type 'investment'
                  // but are structural counterparties (ADR-0019), not user-facing
                  // activity. Same brokerage-detection HoldingsSnapshot uses.
                  && a.HoldingsAccountId != null
                  && !r.IsHidden
                  && r.IsMergedInto == null
            select new { r, AccountName = a.Name };

        if (accountId is { } acct) q = q.Where(x => x.r.AccountId == acct);
        if (fromUtc is { } from) q = q.Where(x => x.r.PostedAt >= from);
        if (toUtc is { } to) q = q.Where(x => x.r.PostedAt < to);

        var rows = await q.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return new InvestmentActivityResult([]);

        // Holdings-sibling per brokerage — the projector strips it as noise.
        var accountIds = rows.Select(x => x.r.AccountId).Distinct().ToArray();
        var holdings = (await _db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.HoldingsAccountId })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(a => a.Id, a => a.HoldingsAccountId);

        var events = rows
            .GroupBy(x => new { x.r.HeaderId, x.r.AccountId })
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.r.LegIndex).ToList();
                var canonical = ordered[0];
                holdings.TryGetValue(g.Key.AccountId, out var sibling);
                var p = InvestmentEventProjector.ProjectEvent(
                    ordered.Select(x => InvestmentEventLegMapping.ToEventLeg(x.r)).ToList(),
                    sibling);
                return new InvestmentActivityRow(
                    g.Key.HeaderId,
                    canonical.r.PostedAt,
                    g.Key.AccountId,
                    canonical.AccountName,
                    canonical.r.InvestmentAction,
                    p.SecurityId,
                    p.SecurityTicker,
                    p.SecurityName,
                    p.Quantity,
                    p.UnitPrice,
                    p.Amount,
                    p.FeeAmount,
                    p.CategoryAccountName,
                    p.TransferAccountName);
            })
            .Where(e => securityId is null || e.SecurityId == securityId)
            .OrderByDescending(e => e.PostedAt)
            .ThenByDescending(e => e.HeaderId)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();

        return new InvestmentActivityResult(events);
    }

    /// <summary>
    /// Investment income (ADR-0063 v2): dividend / interest / misc income from
    /// <c>posting_role='income'</c> legs over the window, grouped by security or
    /// brokerage. Income legs carry the negative side of the symmetric posting, so
    /// magnitude = −Σ(amount). Optionally bucketed by period.
    /// </summary>
    public async Task<InvestmentIncomeResult> IncomeAsync(
        Guid ledgerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        Guid? accountId,
        Guid? securityId,
        InvestmentIncomeGroupBy groupBy,
        ReportTimeBucket timeBucket,
        CancellationToken cancellationToken = default)
    {
        // Income postings stamp posting_role='income' on BOTH legs (the cash side
        // and the category side); count only the CATEGORY-side leg so the pair
        // doesn't cancel. That leg carries the negative magnitude (flipped below)
        // and the security via the view's counterparty/security projection.
        var q =
            from r in _db.ResolvedTransactions.AsNoTracking()
            join a in _db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            where a.LedgerId == ledgerId
                  && a.AccountType == "category"
                  && r.PostingRole == PostingRoles.Income
                  && !r.IsHidden
                  && r.IsMergedInto == null
            select r;

        if (fromUtc is { } from) q = q.Where(r => r.PostedAt >= from);
        if (toUtc is { } to) q = q.Where(r => r.PostedAt < to);
        if (securityId is { } sec) q = q.Where(r => r.SecurityId == sec);
        // accountId filters by the brokerage (the income leg's counterparty cash).
        if (accountId is { } acct) q = q.Where(r => r.CounterpartyAccountId == acct);

        // Aggregate per (month, group); coarser buckets rolled up in memory.
        var grouped = groupBy == InvestmentIncomeGroupBy.Account
            ? await q
                .GroupBy(r => new { r.PostedAt.Year, r.PostedAt.Month, Id = r.CounterpartyAccountId, Name = r.CounterpartyAccountName })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Id, Ticker = (string?)null, Name = g.Key.Name, Sum = g.Sum(x => x.Amount) })
                .ToListAsync(cancellationToken).ConfigureAwait(false)
            : await q
                .GroupBy(r => new { r.PostedAt.Year, r.PostedAt.Month, Id = r.SecurityId, r.SecurityTicker, r.SecurityName })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Id, Ticker = g.Key.SecurityTicker, Name = g.Key.SecurityName, Sum = g.Sum(x => x.Amount) })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rows = grouped
            .GroupBy(g => new
            {
                Period = PeriodLabel(timeBucket, g.Year, g.Month),
                g.Id,
                g.Ticker,
                g.Name,
            })
            .Select(g => new InvestmentIncomeRow(
                g.Key.Period,
                g.Key.Id,
                g.Key.Ticker,
                g.Key.Name ?? "(misc income)",
                -g.Sum(x => x.Sum)))   // income legs are negative; flip to magnitude
            .OrderBy(r => r.Period, StringComparer.Ordinal)
            .ThenByDescending(r => r.Amount)
            .ToList();

        return new InvestmentIncomeResult(rows, rows.Sum(r => r.Amount));
    }

    /// <summary>
    /// Realized gains (ADR-0064 FIFO) over the window, grouped by security: from
    /// the recompute-owned <c>realized_gains</c> table. <paramref name="accountId"/>
    /// filters by the brokerage (resolved to its holdings sibling).
    /// </summary>
    public async Task<RealizedGainsResult> RealizedGainsAsync(
        Guid ledgerId,
        DateTime? fromUtc,
        DateTime? toUtc,
        Guid? accountId,
        Guid? securityId,
        CancellationToken cancellationToken = default)
    {
        Guid? siblingFilter = null;
        if (accountId is { } acct)
        {
            siblingFilter = await _db.Accounts.AsNoTracking()
                .Where(a => a.Id == acct && a.LedgerId == ledgerId)
                .Select(a => a.HoldingsAccountId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            siblingFilter ??= acct;
        }

        var q = _db.RealizedGains.AsNoTracking().Where(g => g.LedgerId == ledgerId);
        if (fromUtc is { } from) q = q.Where(g => g.SoldAt >= from);
        if (toUtc is { } to) q = q.Where(g => g.SoldAt < to);
        if (securityId is { } sec) q = q.Where(g => g.SecurityId == sec);
        if (siblingFilter is { } sib) q = q.Where(g => g.AccountId == sib);

        // Aggregate per security in SQL, then resolve names in memory (a join +
        // GroupBy on the joined shape doesn't translate cleanly).
        var agg = await q
            .GroupBy(g => g.SecurityId)
            .Select(grp => new
            {
                SecurityId = grp.Key,
                Proceeds = grp.Sum(x => x.Proceeds),
                CostBasisSold = grp.Sum(x => x.CostBasisSold),
                RealizedGain = grp.Sum(x => x.RealizedGain),
                RealizedGainLt = grp.Sum(x => x.RealizedGainLt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var secIds = agg.Select(a => a.SecurityId).ToList();
        var securities = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && secIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        var rows = agg
            .Select(a =>
            {
                securities.TryGetValue(a.SecurityId, out var s);
                // LT stored on each row; ST is the remainder (total - LT).
                return new RealizedGainSummaryRow(
                    a.SecurityId, s?.Ticker, s?.Name ?? "(security)",
                    a.Proceeds, a.CostBasisSold, a.RealizedGain,
                    a.RealizedGain - a.RealizedGainLt, a.RealizedGainLt);
            })
            .OrderByDescending(r => r.RealizedGain)
            .ToList();

        return new RealizedGainsResult(
            rows,
            rows.Sum(r => r.Proceeds),
            rows.Sum(r => r.CostBasisSold),
            rows.Sum(r => r.RealizedGain),
            rows.Sum(r => r.RealizedGainShortTerm),
            rows.Sum(r => r.RealizedGainLongTerm));
    }

    // Real (non-investment, non-category) account types: a transfer between a
    // brokerage and one of these is an external contribution/withdrawal.
    private static readonly string[] ExternalFlowTypes =
        { "bank", "cash", "credit_card", "asset", "liability", "loan" };

    // TWR values the portfolio once per distinct external-flow instant (two feeder
    // reads each), so an account with a huge number of flow dates is bounded: past
    // this, TWR is skipped with a reason and only IRR (two valuations) is returned.
    private const int MaxReturnsBoundaries = 400;

    /// <summary>
    /// Investment returns (ADR-0063 v2). Both figures value the portfolio the same
    /// way at every boundary: brokerage cash (<c>account_balance_as_of</c>) +
    /// split-adjusted holdings market value (the migration-172 feeder). Valuing
    /// cash + securities together means a contribution raises value and flow by the
    /// same amount at the same instant, so it never distorts the return (the
    /// securities-only basis reported phantom losses when contributed cash sat
    /// uninvested).
    ///
    /// Money-weighted (XIRR) runs over the external contributions/withdrawals plus
    /// the start + end portfolio value. Time-weighted (TWR) chains sub-period
    /// returns across the external-flow instants, each valued the same way, and is
    /// null-with-reason when a sub-period's invested base is non-positive or there
    /// are more flow dates than <see cref="MaxReturnsBoundaries"/>. External cash
    /// flow = a transfer between a brokerage and a real outside account;
    /// internally-reinvested dividends and in-brokerage trades are not external.
    /// Since-inception (no window) starts from value 0 at the first external flow;
    /// an account funded in-kind before its first cash flow is valued best-effort.
    /// </summary>
    public async Task<ReturnsResult> ReturnsAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        // Scope REAL brokerages (holdings_account_id set) + their holdings siblings.
        // The sibling shadow accounts are also AccountType 'investment' (ADR-0019),
        // so filtering on the type alone would fold the sibling into brokerageIds —
        // and its balance is the sum of the security legs' cost basis, which would
        // then be double-counted as phantom cash on top of the market value.
        var brokerages = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.AccountType == "investment"
                        && a.HoldingsAccountId != null
                        && (accountId == null || a.Id == accountId))
            .Select(a => new { a.Id, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var brokerageIds = brokerages.Select(b => b.Id).ToList();
        var siblingIds = brokerages
            .Select(b => b.HoldingsAccountId!.Value)
            .ToList();

        var endDate = toUtc ?? nowUtc;
        var scope = accountId is null ? "ledger" : "account";

        // External cash flows: brokerage legs whose counterparty is a real
        // outside account, within the window.
        var flowQ = _db.ResolvedTransactions.AsNoTracking()
            .Where(r => brokerageIds.Contains(r.AccountId)
                        && r.CounterpartyAccountType != null
                        && ExternalFlowTypes.Contains(r.CounterpartyAccountType)
                        && !r.IsHidden
                        && r.IsMergedInto == null
                        && r.PostedAt < endDate);
        if (fromUtc is { } from) flowQ = flowQ.Where(r => r.PostedAt >= from);
        var flows = await flowQ
            .Select(r => new { r.PostedAt, r.Amount })   // +amount = money into the brokerage
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var startDate = fromUtc
            ?? (flows.Count > 0 ? flows.Min(x => x.PostedAt) : endDate);

        // Σ external flow at an instant (money into the brokerage). The value
        // BEFORE a flow subtracts it: the cash it just added is a contribution,
        // not yet return. A withdrawal (negative) is added back symmetrically.
        decimal ContributionAt(DateTime d) =>
            flows.Where(x => x.PostedAt == d).Sum(x => x.Amount);

        // Boundary values (cash + securities as of the instant). Start = value just
        // BEFORE any flow at the window start; end = the full value at the window
        // end (no flow lands there — flows are strictly before endDate).
        var startValue =
            await PortfolioValueAsOfAsync(ledgerId, brokerageIds, siblingIds, startDate, cancellationToken)
                .ConfigureAwait(false)
            - ContributionAt(startDate);
        var endValue =
            await PortfolioValueAsOfAsync(ledgerId, brokerageIds, siblingIds, endDate, cancellationToken)
                .ConfigureAwait(false);

        // Money-weighted (investor perspective: money-in negative).
        var cf = new List<ReturnsCalculator.CashFlow>();
        if (startValue != 0m) cf.Add(new(startDate, -startValue));
        foreach (var x in flows) cf.Add(new(x.PostedAt, -x.Amount));
        cf.Add(new(endDate, endValue));
        var irr = ReturnsCalculator.Xirr(cf);

        var netContributions = flows.Sum(x => x.Amount);

        // Time-weighted: a boundary at the window start, each external-flow instant,
        // and the window end — each valued at the same cash+securities basis. The
        // calculator chains and annualizes, returning null when any base is
        // non-positive; we add a reason for that and for the flow-count cap.
        double? twr = null;
        string? twrReason = null;
        var flowInstants = flows.Select(x => x.PostedAt).Distinct().ToList();
        if (flowInstants.Count > MaxReturnsBoundaries)
        {
            twrReason =
                $"Time-weighted return spans {flowInstants.Count} cash-flow dates, more than the " +
                $"{MaxReturnsBoundaries} that can be valued precisely; money-weighted (IRR) is provided.";
        }
        else
        {
            var boundaryDates = new SortedSet<DateTime>(flowInstants) { startDate, endDate };
            var boundaries = new List<ReturnsCalculator.Boundary>(boundaryDates.Count);
            foreach (var d in boundaryDates)
            {
                decimal value;
                if (d == startDate) value = startValue;        // already value-before-flow
                else if (d == endDate) value = endValue;       // flow-free window end
                else value =
                    await PortfolioValueAsOfAsync(ledgerId, brokerageIds, siblingIds, d, cancellationToken)
                        .ConfigureAwait(false)
                    - ContributionAt(d);
                boundaries.Add(new ReturnsCalculator.Boundary(d, value, -ContributionAt(d)));
            }
            twr = ReturnsCalculator.Twr(boundaries);
            if (twr is null)
                twrReason =
                    "Time-weighted return needs a positive invested base in every sub-period and a " +
                    "non-zero window; one sub-period could not be valued, so only money-weighted " +
                    "(IRR) is provided.";
        }

        return new ReturnsResult(
            scope, startDate, endDate, startValue, endValue, netContributions,
            irr, twr, twrReason);
    }

    // Portfolio value as of an instant = brokerage CASH (account_balance_as_of, the
    // mig-172 date-bounded twin of the mig-133 running balance) + split-adjusted
    // holdings MARKET VALUE (the mig-172 feeder: feed close ≤ T, else the latest
    // trade price ≤ T, split-aware quantity replay). Cash lives on the brokerage
    // account, securities on its holdings sibling — the two sum with no double-count
    // (brokerageIds is real brokerages only; see the scope above, or a sibling's
    // security-leg cost basis would be summed as phantom cash). account_balance_as_of
    // reads the balance recompute's OUTPUT table, so cash inherits the recompute's
    // merged/hidden/template filter rather than re-deriving it (single source).
    private async Task<decimal> PortfolioValueAsOfAsync(
        Guid ledgerId,
        IReadOnlyList<Guid> brokerageIds,
        IReadOnlyList<Guid> siblingIds,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        // timestamptz binding requires a UTC kind; the as-of inputs are UTC.
        var asOfUtc = asOf.Kind == DateTimeKind.Utc
            ? asOf
            : DateTime.SpecifyKind(asOf, DateTimeKind.Utc);

        var cash = brokerageIds.Count == 0
            ? 0m
            : await _db.AccountBalanceAsOf(ledgerId, asOfUtc, null)
                .Where(b => brokerageIds.Contains(b.AccountId))
                .SumAsync(b => (decimal?)b.Balance, cancellationToken)
                .ConfigureAwait(false) ?? 0m;

        var securities = siblingIds.Count == 0
            ? 0m
            : await _db.HoldingsMarketValueAsOf(ledgerId, asOfUtc, null, null)
                .Where(r => siblingIds.Contains(r.AccountId))
                .SumAsync(r => (decimal?)r.MarketValue, cancellationToken)
                .ConfigureAwait(false) ?? 0m;

        return cash + securities;
    }

    // ISO-ish period label shared with the income series.
    private static string? PeriodLabel(ReportTimeBucket bucket, int year, int month) => bucket switch
    {
        ReportTimeBucket.None => null,
        ReportTimeBucket.Year => year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
        ReportTimeBucket.Quarter => $"{year:D4}-Q{(month - 1) / 3 + 1}",
        _ => $"{year:D4}-{month:D2}",
    };

    public async Task<IReadOnlyList<SecurityInfo>> SecuritiesAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .OrderBy(s => s.Name)
            .Select(s => new SecurityInfo(
                s.Id, s.Ticker, s.Name, s.AssetClass, s.VehicleType, s.Region,
                s.EquitySize, s.EquityStyle, s.FiDuration, s.FiCredit,
                s.TaxCharacter, s.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PricePoint>> PriceHistoryAsync(
        Guid ledgerId, Guid securityId, DateTime? fromUtc, DateTime? toUtc,
        CancellationToken cancellationToken = default)
    {
        var q = _db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.SecurityId == securityId);
        // Bounds arrive as instants (MCP contract); collapse to calendar days
        // explicitly — price_date is a DATE (ADR-0070). from incl, to excl.
        if (fromUtc is { } f) { var fd = DateOnly.FromDateTime(f); q = q.Where(p => p.PriceDate >= fd); }
        if (toUtc is { } t) { var td = DateOnly.FromDateTime(t); q = q.Where(p => p.PriceDate < td); }
        return await q
            .OrderBy(p => p.PriceDate)
            .Select(p => new PricePoint(p.PriceDate, p.Price, p.High, p.Low, p.Volume))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Detect in-kind-transfer candidates (ADR-0065 D4): disposal (sell/sellx)
    /// in one investment account paired with an acquisition (buy/buyx) in another,
    /// SAME security, SAME calendar date (UTC), EQUAL share quantity, different
    /// brokerages. Read-only — surfaces candidates for the user to review against a
    /// statement and convert via the apply endpoint. Overlapping matches (several
    /// same-(security,date,qty) trades) are all listed; converting one removes its
    /// two headers from the next detection.
    /// </summary>
    public async Task<IReadOnlyList<InKindTransferCandidate>> FindInKindTransferCandidatesAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        // Investment accounts (with a holdings sibling); need ≥2 to transfer between.
        var brokerages = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.AccountType == "investment"
                        && a.HoldingsAccountId != null)
            .Select(a => new { a.Id, a.Name, Sibling = a.HoldingsAccountId!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (brokerages.Count < 2) return Array.Empty<InKindTransferCandidate>();

        var bySibling = brokerages.ToDictionary(b => b.Sibling, b => (b.Id, b.Name));
        var siblingIds = brokerages.Select(b => b.Sibling).ToList();

        // Holdings-side security legs of buy/buyx/sell/sellx (not hidden/merged).
        var legs = await _db.ResolvedTransactions.AsNoTracking()
            .Where(r => siblingIds.Contains(r.AccountId)
                        && r.PostingRole == PostingRoles.Security
                        && r.SecurityId != null && r.Quantity != null && r.Quantity != 0m
                        && (r.InvestmentAction == LedgerActions.Buy
                            || r.InvestmentAction == LedgerActions.BuyXfr
                            || r.InvestmentAction == LedgerActions.Sell
                            || r.InvestmentAction == LedgerActions.SellXfr)
                        && !r.IsHidden && r.IsMergedInto == null)
            .Select(r => new
            {
                r.HeaderId,
                r.AccountId,
                SecurityId = r.SecurityId!.Value,
                r.SecurityTicker,
                r.SecurityName,
                Quantity = r.Quantity!.Value,
                r.PostedAt,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (legs.Count == 0) return Array.Empty<InKindTransferCandidate>();

        // Headers carrying a fee leg — the in-kind transfer drops it, so warn.
        var headerIds = legs.Select(l => l.HeaderId).Distinct().ToList();
        var feeHeaderIds = (await _db.ResolvedTransactions.AsNoTracking()
            .Where(r => headerIds.Contains(r.HeaderId) && r.PostingRole == PostingRoles.Fee)
            .Select(r => r.HeaderId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var disposals = legs.Where(l => l.Quantity < 0m).ToList();
        var acquisitions = legs.Where(l => l.Quantity > 0m).ToList();

        var results = new List<InKindTransferCandidate>();
        foreach (var d in disposals)
        {
            var (dId, dName) = bySibling[d.AccountId];
            foreach (var a in acquisitions)
            {
                if (a.SecurityId != d.SecurityId) continue;
                if (a.PostedAt.Date != d.PostedAt.Date) continue;
                if (a.Quantity != -d.Quantity) continue;     // equal share count (net-zero)
                var (aId, aName) = bySibling[a.AccountId];
                if (aId == dId) continue;                     // must cross accounts

                results.Add(new InKindTransferCandidate(
                    SellHeaderId: d.HeaderId,
                    BuyHeaderId: a.HeaderId,
                    SourceAccountId: dId,
                    SourceAccountName: dName,
                    DestAccountId: aId,
                    DestAccountName: aName,
                    SecurityId: d.SecurityId,
                    SecurityTicker: d.SecurityTicker,
                    SecurityName: d.SecurityName,
                    Quantity: -d.Quantity,
                    Date: d.PostedAt,
                    SourceHadFee: feeHeaderIds.Contains(d.HeaderId),
                    DestHadFee: feeHeaderIds.Contains(a.HeaderId)));
            }
        }
        return results.OrderBy(c => c.Date).ThenBy(c => c.SecurityName).ToList();
    }
}
