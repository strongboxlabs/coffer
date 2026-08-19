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

    // Build identity, stamped on every reporting response so a consumer that
    // assembles one report from several calls can tell which figures are current.
    // semver+sha, not semver alone: two builds of the same release can disagree
    // about what a figure MEANS, and the sha is what separates them.
    private static string EngineVersion =>
        $"{Coffer.Api.Meta.VersionInfo.Semver}+{Coffer.Api.Meta.VersionInfo.Commit}";

    // Wall-clock at computation. Deliberately NOT the caller's as-of or window-end
    // date: those are inputs the caller chooses and can set in the past, so they
    // say nothing about whether a figure is fresh.
    private static DateTime ComputedAt => DateTime.UtcNow;

    /// <summary>
    /// Holdings rolled up per security as of an instant — quantity, market value AND
    /// FIFO cost basis, all exact at that instant.
    /// </summary>
    /// <remarks>
    /// Valued through the same as-of feeder <c>returns</c> and <c>allocation</c> use,
    /// with basis from <c>holdings_cost_basis_as_of</c>. This read the current
    /// <c>holdings</c> projection instead, which is kept in step with <c>txn_legs</c>
    /// by an EF <c>SaveChangesInterceptor</c> — so the two agreed, but the projection
    /// only ever describes NOW, and no as-of report can be built on it.
    /// <para>
    /// Cost basis reaching a past instant needed migration 202: the FIFO walk kept its
    /// state in the <c>lots</c> table, so a read could not borrow it. The walk is now
    /// pure and as-of-bounded, and <c>recompute_holdings_cost_basis</c> is a thin
    /// persist over the same function — one algorithm, so the stored basis and an
    /// as-of basis cannot drift.
    /// </para>
    /// </remarks>
    public async Task<HoldingsSnapshot> HoldingsSnapshotAsync(
        Guid ledgerId,
        Guid? accountId = null,
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var asOf = asOfUtc ?? DateTime.UtcNow;
        if (asOf.Kind != DateTimeKind.Utc) asOf = DateTime.SpecifyKind(asOf, DateTimeKind.Utc);

        // Optional brokerage filter → its holdings-sibling account. Fall back to
        // the id itself if the caller passed a sibling (or an account with none).
        Guid[]? siblingFilter = null;
        if (accountId is { } acct)
        {
            var sibling = await _db.Accounts.AsNoTracking()
                .Where(a => a.Id == acct && a.LedgerId == ledgerId)
                .Select(a => a.HoldingsAccountId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            siblingFilter = [sibling ?? acct];
        }

        // Split-adjusted quantity + market value per (holdings-sibling, security) as
        // of the instant, and FIFO basis for the same positions. Both are as-of
        // functions; neither reads the current projection.
        var valued = await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOf], siblingFilter)
            .Select(r => new { r.AccountId, r.SecurityId, r.Quantity, r.MarketValue })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (valued.Count == 0)
            return new HoldingsSnapshot([], 0m, 0m, 0m, asOf, ComputedAt, EngineVersion);

        var basisRows = await _db.HoldingsCostBasisAsOf(ledgerId, asOf, siblingFilter)
            .Select(r => new { r.SecurityId, r.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var costBasis = basisRows
            .GroupBy(r => r.SecurityId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CostBasis));

        var securityIds = valued.Select(v => v.SecurityId).Distinct().ToList();
        var securities = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && securityIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name, s.AssetClass })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        // Held-in breakdown, mapped from the holdings sibling up to the brokerage a
        // reader recognises (ADR-0019).
        var brokerageBySibling = (await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId != null)
            .Select(a => new { Sibling = a.HoldingsAccountId!.Value, BrokerageId = a.Id, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(a => a.Sibling, a => (a.BrokerageId, a.Name));

        var heldInBySecurity = valued
            .GroupBy(v => v.SecurityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<HeldInSlice>)g
                    .GroupBy(v => v.AccountId)
                    .Select(byAccount => brokerageBySibling.TryGetValue(byAccount.Key, out var bk)
                        ? new HeldInSlice(bk.BrokerageId, bk.Name, byAccount.Sum(v => v.Quantity))
                        : new HeldInSlice(byAccount.Key, "(account)", byAccount.Sum(v => v.Quantity)))
                    .OrderByDescending(s => s.Quantity)
                    .ToList());

        var rows = valued
            .GroupBy(v => v.SecurityId)
            .Select(g =>
            {
                var quantity = g.Sum(v => v.Quantity);
                var marketValue = g.Sum(v => v.MarketValue);
                securities.TryGetValue(g.Key, out var s);

                // The per-share price valuation ACTUALLY used, back-adjusted onto this
                // instant's split basis — not the newest security_prices row, which for
                // a past instant would be a price from the future.
                decimal? unitPrice = quantity != 0m ? decimal.Round(marketValue / quantity, 6) : null;

                var basis = costBasis.GetValueOrDefault(g.Key);
                var gain = marketValue - basis;

                return new HoldingSnapshotRow(
                    g.Key,
                    s?.Ticker,
                    s?.Name ?? "(unknown security)",
                    s?.AssetClass,
                    quantity,
                    basis,
                    unitPrice,
                    marketValue,
                    gain,
                    ReportingScale.PercentOrNull(gain, basis),
                    heldInBySecurity.TryGetValue(g.Key, out var hi) ? hi : Array.Empty<HeldInSlice>());
            })
            .OrderByDescending(r => r.MarketValue)
            .ToList();

        return new HoldingsSnapshot(
            rows,
            rows.Sum(r => r.MarketValue),
            rows.Sum(r => r.CostBasis),
            rows.Sum(r => r.UnrealizedGain),
            asOf,
            ComputedAt,
            EngineVersion);
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
            // Bounded to the same scale the as-of feeder produces, so the overview
            // and the net-worth history agree exactly rather than approximately.
            var value = latestPrice.TryGetValue(h.SecurityId, out var px)
                ? ReportingScale.MarketValue(h.Quantity, px)
                : h.CostBasis;
            byAccount[h.AccountId] = byAccount.GetValueOrDefault(h.AccountId) + value;
        }
        return byAccount;
    }

    /// <summary>
    /// Portfolio allocation (ADR-0063/0067) bucketed by a chosen dimension —
    /// asset class, region, vehicle, security or account — as of an instant.
    /// For asset_class/region, a <c>multi_asset</c> security with
    /// <c>security_components</c> sleeves is decomposed across them by weight
    /// rather than counting 100% in one bucket; one WITHOUT sleeves cannot be
    /// decomposed and is reported in
    /// <see cref="AllocationResult.UndecomposedMultiAssets"/> as well as bucketed
    /// wholesale, because that case makes the whole chart quietly wrong.
    /// Unclassified values bucket as "Unclassified".
    /// </summary>
    /// <remarks>
    /// Valued through the SAME as-of feeder <c>returns</c> uses, not the current
    /// Holdings table. Two consequences, both deliberate: an allocation can be
    /// asked for a past instant, and its total reconciles with a returns total at
    /// the same instant to the cent — securities here plus
    /// <see cref="AllocationResult.ExcludedBrokerageCash"/> equals that report's
    /// portfolio value. Two valuation paths that agree about "now" but drift on
    /// history are how a $5,339 discrepancy sat unexplained across two published
    /// reports.
    /// </remarks>
    public async Task<AllocationResult> AllocationAsync(
        Guid ledgerId,
        AllocationDimension dimension = AllocationDimension.AssetClass,
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var asOf = asOfUtc ?? DateTime.UtcNow;
        if (asOf.Kind != DateTimeKind.Utc) asOf = DateTime.SpecifyKind(asOf, DateTimeKind.Utc);

        // Scope: every brokerage in the ledger and its holdings sibling. Same
        // identification rule as ReturnsAsync — a sibling is any account that is
        // some other account's holdings_account_id, NOT "has no sibling of its
        // own", which would drop a brokerage that never held a position.
        var investmentAccounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "investment")
            .Select(a => new { a.Id, a.Name, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var siblingsInLedger = investmentAccounts
            .Where(a => a.HoldingsAccountId != null)
            .Select(a => a.HoldingsAccountId!.Value)
            .ToHashSet();
        var brokerages = investmentAccounts.Where(a => !siblingsInLedger.Contains(a.Id)).ToList();
        var brokerageIds = brokerages.Select(b => b.Id).ToList();
        var siblingToBrokerage = brokerages
            .Where(b => b.HoldingsAccountId != null)
            .ToDictionary(b => b.HoldingsAccountId!.Value, b => b.Id);
        var brokerageNames = brokerages.ToDictionary(b => b.Id, b => b.Name);

        // Cash that cannot be bucketed. A money-market FUND is a holding and lands
        // in the buckets below; a cash BALANCE has no asset class at all. Stating
        // it is the whole point — silently dropping it is what made an allocation
        // total disagree with a returns total for no visible reason.
        var excludedCash = brokerageIds.Count == 0
            ? 0m
            : await _db.AccountBalanceAsOfInstants(ledgerId, [asOf], brokerageIds.ToArray())
                .SumAsync(b => (decimal?)b.Balance, cancellationToken)
                .ConfigureAwait(false) ?? 0m;

        var siblingIds = siblingToBrokerage.Keys.ToList();
        var positions = siblingIds.Count == 0
            ? []
            : await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOf], siblingIds.ToArray())
                .Select(r => new { r.AccountId, r.SecurityId, r.MarketValue })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        if (positions.Count == 0)
            return new AllocationResult(
                [], 0m, asOf, excludedCash, [], ComputedAt, EngineVersion);

        var total = positions.Sum(p => p.MarketValue);
        var secIds = positions.Select(p => p.SecurityId).Distinct().ToList();
        var secs = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && secIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name, s.AssetClass, s.Region, s.VehicleType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        // Look-through applies only to the asset_class / region dimensions, and
        // only to multi_asset securities (the single look-through signal).
        var lookThrough = dimension is AllocationDimension.AssetClass or AllocationDimension.Region;
        var componentsBySecurity = new Dictionary<Guid, List<(string Cls, string? Region, decimal Weight)>>();
        var multiAssetIds = secs.Values
            .Where(s => s.AssetClass == "multi_asset")
            .Select(s => s.Id)
            .ToHashSet();
        if (multiAssetIds.Count > 0)
        {
            componentsBySecurity = (await _db.SecurityComponents.AsNoTracking()
                .Where(c => multiAssetIds.Contains(c.SecurityId))
                .Select(c => new { c.SecurityId, c.ComponentAssetClass, c.ComponentRegion, c.Weight })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .GroupBy(c => c.SecurityId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => (c.ComponentAssetClass, c.ComponentRegion, c.Weight)).ToList());
        }

        // Decomposable only with sleeves carrying positive total weight. Anything
        // else is opaque, and reported as such REGARDLESS of dimension: the
        // security dimension does not look through, but the security is just as
        // undecomposed, and a caller switching dimensions should not see the
        // warning appear and vanish.
        bool Decomposable(Guid securityId) =>
            componentsBySecurity.TryGetValue(securityId, out var c)
            && c.Count > 0
            && c.Sum(x => x.Weight) > 0m;

        var buckets = new Dictionary<string, decimal>();
        void Add(string key, decimal mv) => buckets[key] = buckets.GetValueOrDefault(key) + mv;

        foreach (var g in positions.GroupBy(p => p.SecurityId))
        {
            var securityId = g.Key;
            var marketValue = g.Sum(p => p.MarketValue);
            secs.TryGetValue(securityId, out var s);

            if (lookThrough && s is { AssetClass: "multi_asset" } && Decomposable(securityId))
            {
                var comps = componentsBySecurity[securityId];
                var weightSum = comps.Sum(c => c.Weight);
                foreach (var c in comps)
                {
                    var slice = marketValue * (c.Weight / weightSum);
                    Add(
                        dimension == AllocationDimension.AssetClass ? c.Cls : (c.Region ?? "Unclassified"),
                        slice);
                }
                continue;
            }

            // Account dimension credits each position to the BROKERAGE holding it,
            // not to the shadow sibling it posts to (ADR-0019).
            if (dimension == AllocationDimension.Account)
            {
                foreach (var p in g)
                {
                    var owner = siblingToBrokerage.TryGetValue(p.AccountId, out var o) ? o : p.AccountId;
                    Add(brokerageNames.TryGetValue(owner, out var nm) ? nm : "Unclassified", p.MarketValue);
                }
                continue;
            }

            var bucketKey = dimension switch
            {
                AllocationDimension.Region => s?.Region ?? "Unclassified",
                AllocationDimension.VehicleType => s?.VehicleType ?? "Unclassified",
                AllocationDimension.Security => s?.Ticker ?? s?.Name ?? "Unclassified",
                _ => s?.AssetClass ?? "Unclassified",
            };
            Add(bucketKey, marketValue);
        }

        var undecomposed = positions
            .GroupBy(p => p.SecurityId)
            .Where(g => multiAssetIds.Contains(g.Key) && !Decomposable(g.Key))
            .Select(g =>
            {
                var mv = g.Sum(p => p.MarketValue);
                secs.TryGetValue(g.Key, out var s);
                return new UndecomposedMultiAsset(
                    g.Key,
                    s?.Ticker,
                    s?.Name ?? "Unknown",
                    mv,
                    ReportingScale.Percent(mv, total));
            })
            .OrderByDescending(u => u.MarketValue)
            .ToList();

        var rows = buckets
            .Select(kv => new AllocationRow(kv.Key, kv.Value, ReportingScale.Percent(kv.Value, total)))
            .OrderByDescending(b => b.MarketValue)
            .ToList();

        return new AllocationResult(
            rows, total, asOf, excludedCash, undecomposed, ComputedAt, EngineVersion);
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

        return new InvestmentIncomeResult(
            rows, rows.Sum(r => r.Amount), ComputedAt, EngineVersion);
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
            rows.Sum(r => r.RealizedGainLongTerm),
            ComputedAt,
            EngineVersion);
    }

    // Counterparty account types whose legs are CANDIDATE cash flows. Whether a
    // candidate is actually external is decided by scope, not by type (see
    // ReturnsAsync) — 'investment' belongs here because a rollover to another
    // brokerage is a genuine withdrawal from the source account's return even
    // though it nets to zero across the ledger.
    //
    // 'category' belongs here too, but only in combination with the posting-role
    // qualifier below: money can both ARRIVE from a category (an employer
    // retirement contribution) and be GENERATED by one (a dividend), and those are
    // opposite things.
    private static readonly string[] FlowCounterpartyTypes =
        { "bank", "cash", "credit_card", "asset", "liability", "loan", "investment", "category" };

    // The counterparty type that needs the posting-role qualifier in ReturnsAsync.
    private const string CategoryAccountType = "category";

    // Another brokerage — internal at ledger scope, a rollover at account scope.
    private const string InvestmentAccountType = "investment";

    // Where a flow came from or went, for the net-contribution breakdown. A NET
    // figure is lossy in a way that reliably misleads: -653,611 is equally
    // consistent with one withdrawal of that size and with 688,759 out against
    // 35,148 in, and a reader holding one salient event will bind the number to it.
    // A real report did exactly that, describing a net as "the rollover" when the
    // rollover was 678,803 and continued employer contributions offset it. The
    // parts are already classified when the net is summed — throwing them away is
    // what forces the guess.
    internal static class ContributionSources
    {
        /// <summary>Banks, cash, credit cards, assets, liabilities, loans — money
        /// entering or leaving the investment world entirely.</summary>
        public const string ExternalAccounts = "external_accounts";

        /// <summary>Another brokerage outside this report's perimeter: a rollover.
        /// Only ever appears at account scope, where the other side is external.</summary>
        public const string OtherInvestmentAccounts = "other_investment_accounts";

        /// <summary>A category counterparty on a TRANSFER posting: an employer
        /// retirement contribution arriving, a withdrawal leaving. Dividends,
        /// interest, expenses and fees are NOT here — they are the portfolio's own
        /// earnings and costs and stay inside the return (ADR-0027).</summary>
        public const string CategoryTransfers = "category_transfers";

        /// <summary>Securities moved between brokerages with no cash leg anywhere
        /// (ADR-0065). Invisible to any counterparty test; detected per header.</summary>
        public const string InKindTransfers = "in_kind_transfers";
    }

    // There is no boundary cap. There WAS one — MaxReturnsBoundaries, 400 — because
    // a boundary valuation replayed the ledger twice: once for holdings and once for
    // cash. Migrations 200 and 201 made both of them batched, so a whole-ledger
    // time-weighted return now costs two queries instead of two per boundary, and
    // the cap had nothing left to protect.
    //
    // It is worth recording why it is GONE rather than raised. It was set from a bad
    // measurement three times — 2000 from a near-empty synthetic ledger (~60x
    // optimistic), 700 from a realistic one budgeted against a human watching a
    // screen rather than a tool call with a timeout, then back to 400 because the
    // numbers that would have justified 700 varied more between runs than the change
    // being measured. A boundary count cannot express a time budget in any case:
    // per-boundary cost scales with accounts in scope, so one value means seconds at
    // account scope and a minute at ledger scope. The answer was never a better
    // number.

    /// <summary>
    /// Everything a returns report needs before it starts VALUING anything: the
    /// perimeter, the classified external flows, and the resolved window.
    /// </summary>
    /// <remarks>
    /// Extracted so <see cref="ReturnsCostEstimateAsync"/> can answer "how much
    /// would this cost" from the same rules that decide the answer, rather than a
    /// second implementation of them. Scope, the perimeter test, the category
    /// posting-role qualifier and header-level in-kind detection all change the
    /// flow-instant count; a parallel copy of that logic would drift, and every
    /// returns defect this engine has had came from a classification rule being
    /// applied in one place and not another.
    /// <para>
    /// This is also the CHEAP half of a returns call — measured at 473 ms on a
    /// ledger where the valuation loop that follows it ran to tens of seconds.
    /// </para>
    /// </remarks>
    private async Task<ReturnsScope> ResolveReturnsScopeAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // A BROKERAGE is any of the ledger's investment accounts that is not some
        // other account's holdings sibling. The sibling shadow accounts are also
        // AccountType 'investment' (ADR-0019), so filtering on the type alone would
        // fold the sibling into brokerageIds — and its balance is the sum of the
        // security legs' cost basis, which would then be double-counted as phantom
        // cash on top of the market value. Identify siblings by the set of
        // holdings_account_id values across the whole ledger rather than by
        // "holdings_account_id IS NULL", which also drops a brokerage that has
        // never held a position — such an account still holds cash and still takes
        // transfers, so it is in scope and its transfers must be classified.
        var investmentAccounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "investment")
            .Select(a => new BrokerageRow(
                a.Id, a.Name, a.HoldingsAccountId, a.OpeningBalance, a.OpenedOn))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var siblingsInLedger = investmentAccounts
            .Where(a => a.HoldingsAccountId != null)
            .Select(a => a.HoldingsAccountId!.Value)
            .ToHashSet();

        var brokerages = investmentAccounts
            .Where(a => !siblingsInLedger.Contains(a.Id)
                        && (accountId == null || a.Id == accountId))
            .ToList();
        var brokerageIds = brokerages.Select(b => b.Id).ToList();
        var siblingIds = brokerages
            .Where(b => b.HoldingsAccountId != null)
            .Select(b => b.HoldingsAccountId!.Value)
            .ToList();

        // Sibling → the brokerage that owns it. Securities and in-kind share legs
        // post to the sibling, which is a shadow account with no name a reader would
        // recognise (ADR-0019); everything reported per account is credited to the
        // brokerage instead.
        var siblingToBrokerage = brokerages
            .Where(b => b.HoldingsAccountId != null)
            .ToDictionary(b => b.HoldingsAccountId!.Value, b => b.Id);

        // Any in-perimeter account mapped to the brokerage it belongs to — the
        // identity for a brokerage, the owner for a sibling.
        Guid OwningBrokerage(Guid accountInPerimeter) =>
            siblingToBrokerage.TryGetValue(accountInPerimeter, out var owner)
                ? owner
                : accountInPerimeter;

        // Everything this report treats as INSIDE its own perimeter: the scoped
        // brokerages and their holdings siblings. A leg facing one of these is an
        // internal movement (a trade's cash leg faces the sibling; a ledger-scope
        // rollover faces another in-scope brokerage). A leg facing anything else is
        // money crossing the boundary. Materialized as a List so EF translates
        // Contains to `= ANY(@ids)`.
        var inPerimeter = brokerageIds.Concat(siblingIds).ToList();

        var endDate = toUtc ?? nowUtc;
        var scope = accountId is null ? "ledger" : "account";

        // External cash flows: brokerage legs facing a real account OUTSIDE this
        // report's perimeter, within the window. Scope decides, not account type —
        // the same brokerage-to-brokerage rollover is internal at ledger scope and
        // external at account scope, and only the perimeter test gets both right.
        var flowQ = _db.ResolvedTransactions.AsNoTracking()
            .Where(r => brokerageIds.Contains(r.AccountId)
                        && r.CounterpartyAccountId != null
                        && r.CounterpartyAccountType != null
                        && FlowCounterpartyTypes.Contains(r.CounterpartyAccountType)
                        && !inPerimeter.Contains(r.CounterpartyAccountId.Value)
                        // A CATEGORY counterparty is a flow only when the leg is a
                        // TRANSFER. ADR-0027 makes posting_role the marker and the
                        // truth, and it already separates the two shapes that look
                        // identical from outside:
                        //
                        //   'income'   — dividends and interest, and (per ADR-0027)
                        //                investment expenses too, direction living
                        //                in the sign. The portfolio generated or
                        //                consumed this; it is return.
                        //   'fee'      — the dedicated fee leg. Netted against
                        //                return, the standard net-of-fees basis.
                        //   'transfer' — money moved across the account's boundary
                        //                that merely happens to face a category: an
                        //                employer retirement contribution arriving,
                        //                a withdrawal leaving. A real flow.
                        //
                        // Non-category counterparties keep no role requirement:
                        // plain cash transfers carry a null posting_role (the
                        // trigger gates it on header.action, which is null for
                        // cash-shape headers), so requiring one would silently drop
                        // every bank transfer.
                        && (r.CounterpartyAccountType != CategoryAccountType
                            || r.PostingRole == PostingRoles.Transfer)
                        && !r.IsHidden
                        && r.IsMergedInto == null
                        && r.PostedAt < endDate);
        if (fromUtc is { } from) flowQ = flowQ.Where(r => r.PostedAt >= from);
        var cashFlows = await flowQ
            // AccountId rides along so a flow landing exactly on the window's start
            // instant can be backed out of the RIGHT account's opening value below.
            .Select(r => new { r.PostedAt, r.Amount, r.AccountId, r.CounterpartyAccountType })
            .ToListAsync(cancellationToken)                          // +amount = money into the brokerage
            .ConfigureAwait(false);
        var flows = cashFlows
            .Select(x => (
                PostedAt: x.PostedAt,
                Amount: x.Amount,
                AccountId: x.AccountId,
                Source: x.CounterpartyAccountType switch
                {
                    CategoryAccountType => ContributionSources.CategoryTransfers,
                    InvestmentAccountType => ContributionSources.OtherInvestmentAccounts,
                    _ => ContributionSources.ExternalAccounts,
                }))
            .ToList();

        // IN-KIND SHARE TRANSFERS. An in-kind rollover moves securities between
        // brokerages with no cash leg anywhere, and the perimeter test above
        // cannot see it: every leg of a transfer_shares header faces an account
        // INSIDE its own brokerage.
        //
        //   source sibling    -> source brokerage      -1,720.58   (shares out)
        //   source brokerage  -> source sibling             0.00
        //   dest sibling      -> dest brokerage         +1,720.58   (shares in)
        //   dest brokerage    -> dest sibling                0.00
        //
        // Nothing crosses the perimeter by counterparty; the only thing joining
        // the two accounts is the shared HEADER. So value moved between
        // brokerages while every individual leg looked purely internal — the
        // destination booked the arrival as performance and the source booked the
        // departure as a loss. One real transfer put an account at +258%/yr and
        // its counterpart at -10.9%/yr simultaneously.
        //
        // Detection is therefore header-level: a transfer_shares header is an
        // external flow when its legs SPAN the perimeter. The amount is the sum
        // of its in-scope legs, which lands correctly in all three cases — a
        // contribution at the destination, a withdrawal at the source, and zero
        // at ledger scope where both sides are inside and it nets out, exactly as
        // an internal move should.
        //
        // Summing every in-scope leg rather than just the security one is
        // deliberate: the brokerage-side legs are 0.00, so they contribute
        // nothing, and the sum stays right if that shape ever changes.
        //
        // DISTINCT BY LEG ID matters. resolved_transactions yields a row per leg
        // per resolved counterparty, so a four-leg header returns each leg
        // several times. Summing the rows as they come counted this transfer at
        // 3x its value — the destination read 30,000 against a real 10,000.
        // Find the candidate HEADERS via legs that are in scope — this is what
        // bounds the work and scopes it to the ledger. Filtering only on the
        // action, as the first version did, matched transfer_shares across every
        // ledger the caller can see and could not use an account index: a whole-
        // view scan to find a handful of rows, and one ledger's transfers leaking
        // into another's returns.
        var inKindHeaderQ = _db.ResolvedTransactions.AsNoTracking()
            .Where(r => r.InvestmentAction == LedgerActions.TransferShares
                        && inPerimeter.Contains(r.AccountId)
                        && !r.IsHidden
                        && r.IsMergedInto == null
                        && r.PostedAt < endDate);
        if (fromUtc is { } inKindFrom) inKindHeaderQ = inKindHeaderQ.Where(r => r.PostedAt >= inKindFrom);
        var inKindHeaderIds = await inKindHeaderQ
            .Select(r => r.HeaderId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Then read every leg of those headers — including the ones OUTSIDE the
        // perimeter, which is what the span test needs.
        var inKindLegs = inKindHeaderIds.Count == 0
            ? []
            : await _db.ResolvedTransactions.AsNoTracking()
                .Where(r => inKindHeaderIds.Contains(r.HeaderId)
                            && !r.IsHidden
                            && r.IsMergedInto == null)
                .Select(r => new { r.Id, r.HeaderId, r.AccountId, r.Amount, r.PostedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        if (inKindLegs.Count > 0)
        {
            var perimeter = inPerimeter.ToHashSet();
            // One entry per (header, owning brokerage) rather than one per header.
            // The totals are identical — the amounts are the same legs, just grouped
            // one level finer — and it gives the flow an account to belong to, which
            // the per-account roster needs. A header's in-scope legs normally all
            // belong to one brokerage (its own sibling plus itself), so this is
            // usually a single entry anyway.
            flows.AddRange(inKindLegs
                .DistinctBy(l => l.Id)
                .GroupBy(l => l.HeaderId)
                .Where(g => g.Any(l => perimeter.Contains(l.AccountId))
                            && g.Any(l => !perimeter.Contains(l.AccountId)))
                .SelectMany(g => g
                    .Where(l => perimeter.Contains(l.AccountId))
                    .GroupBy(l => OwningBrokerage(l.AccountId))
                    .Select(byAccount => (
                        PostedAt: g.Min(l => l.PostedAt),
                        Amount: byAccount.Sum(l => l.Amount),
                        AccountId: byAccount.Key,
                        Source: ContributionSources.InKindTransfers)))
                .Where(x => x.Amount != 0m));
        }

        // The earliest instant a scoped brokerage already HELD something, per its
        // own Start Date. opened_on is the as-of date of the opening balance, so it
        // marks an inception only when that balance is non-zero: with a zero
        // opening balance the account was empty then, and anchoring there would
        // hand TWR a zero invested base and turn a working figure into null. Most
        // Moneydance ledgers carry all history as transactions and leave every
        // opening balance at 0, so this is usually absent — but it must not be
        // ASSUMED absent, and the non-zero test is what makes it safe either way.
        var fundedFromOpening = brokerages
            .Where(b => b.OpenedOn is not null && b.OpeningBalance != 0m)
            .Select(b => (DateTime?)DateTime.SpecifyKind(
                b.OpenedOn!.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc))
            .Min();

        // Since-inception anchors on the EARLIEST honest inception point. It may
        // only ever move earlier, never later: moving it forward would reclassify
        // contributed money as opening capital, and shortening the window
        // over-annualizes whatever gain sits inside it.
        //
        //   1. a funded opened_on — the account demonstrably held value from then;
        //   2. the first external flow — before it the portfolio is empty, so a
        //      value of 0 there is exact;
        //   3. the first activity anywhere in scope, for an account funded in-kind
        //      or from a category, which has no flow to anchor to. Falling back to
        //      endDate instead collapsed the window to zero length, which is not a
        //      window at all — every figure over it is undefined, and the IRR
        //      solver used to answer one anyway.
        //
        // Assets that arrive without a cash flow (an in-kind share transfer, a
        // category-funded posting) are absorbed into the start value rather than
        // read as return. That is right for a transfer, which is contributed
        // capital, and understates a dividend-only account — the open
        // investment-income-category question, not this one.
        DateTime startDate;
        if (fromUtc is { } windowStart)
        {
            startDate = windowStart;
        }
        else
        {
            DateTime? anchor = flows.Count > 0 ? flows.Min(x => x.PostedAt) : null;
            if (fundedFromOpening is { } opened && (anchor is null || opened < anchor))
                anchor = opened;

            if (anchor is null)
            {
                anchor = await _db.ResolvedTransactions.AsNoTracking()
                    .Where(r => inPerimeter.Contains(r.AccountId)
                                && !r.IsHidden
                                && r.IsMergedInto == null
                                && r.PostedAt < endDate)
                    .Select(r => (DateTime?)r.PostedAt)
                    .MinAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Still null only when the scope has no activity at all — an untouched
            // account, correctly reported as having no measurable return.
            startDate = anchor ?? endDate;
        }
        return new ReturnsScope(
            scope, startDate, endDate, brokerages, brokerageIds, siblingIds,
            siblingToBrokerage, inPerimeter, flows);
    }

    /// <summary>The resolved scope of a returns report — see
    /// <see cref="ResolveReturnsScopeAsync"/>. <c>Brokerages</c> keeps the opening
    /// balance and opened_on the anchor rule needs; <c>Flows</c> are the external
    /// cash flows AND the in-kind headers that cross the perimeter, each already
    /// attributed to the brokerage it belongs to.</summary>
    private sealed record ReturnsScope(
        string Scope,
        DateTime StartDate,
        DateTime EndDate,
        IReadOnlyList<BrokerageRow> Brokerages,
        IReadOnlyList<Guid> BrokerageIds,
        IReadOnlyList<Guid> SiblingIds,
        IReadOnlyDictionary<Guid, Guid> SiblingToBrokerage,
        IReadOnlyList<Guid> InPerimeter,
        IReadOnlyList<(DateTime PostedAt, decimal Amount, Guid AccountId, string Source)> Flows);

    /// <summary>A scoped brokerage: identity plus the two fields the
    /// since-inception anchor rule reads.</summary>
    private sealed record BrokerageRow(
        Guid Id, string Name, Guid? HoldingsAccountId, decimal OpeningBalance, DateOnly? OpenedOn);

    /// <summary>
    /// How many portfolio valuations a time-weighted return would need for this
    /// window, without performing any of them. Runs the cheap half of a returns
    /// call — scope, flow classification, in-kind detection, anchor — and stops.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can size a request instead of discovering its cost by
    /// waiting for it. It reports the instant count and nothing about a ceiling,
    /// because there is no ceiling left to report: migrations 200 and 201 made both
    /// halves of a boundary valuation batched, so the cap that used to refuse a
    /// time-weighted figure past 400 instants was deleted rather than raised.
    /// <para>
    /// This is also why the per-request cap override that was asked for was
    /// declined rather than shipped. It would have promoted an internal constant
    /// into public API that outlived it, and it never bought what it appeared to:
    /// over the ceiling the engine refused TWR outright rather than approximating
    /// it, so a lower cap bought a faster null, not a coarser number.
    /// </para>
    /// </remarks>
    public async Task<ReturnsCostEstimate> ReturnsCostEstimateAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var s = await ResolveReturnsScopeAsync(
            ledgerId, accountId, fromUtc, toUtc, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        // Distinct INSTANTS, not flows: several flows landing together are valued
        // once. This is the same count ReturnsAsync tests against the ceiling.
        var instants = s.Flows.Select(x => x.PostedAt).Distinct().Count();

        return new ReturnsCostEstimate(
            s.Scope,
            s.StartDate,
            s.EndDate,
            instants,
            ComputedAt,
            EngineVersion);
    }

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
    ///
    /// External cash flow = a brokerage leg facing a real account OUTSIDE the
    /// report's own scope. This is deliberately scope-relative: a rollover from one
    /// brokerage to another is INTERNAL at ledger scope (it nets to zero) but is a
    /// real withdrawal from the source account and a real contribution to the
    /// destination when either is reported on its own. Classifying by account type
    /// instead — treating every 'investment' counterparty as internal at both
    /// scopes — made an account funded entirely by rollover report the whole
    /// step-up as performance. Internally-reinvested dividends and in-brokerage
    /// trades face the holdings sibling, which is inside the perimeter at every
    /// scope, so they are never external.
    ///
    /// A CATEGORY counterparty is external only when the leg's posting role is
    /// <c>transfer</c>. Roles <c>income</c> (dividends, interest, and investment
    /// expenses — ADR-0027 puts direction in the sign, not the role) and
    /// <c>fee</c> are the portfolio's own earnings and costs, so they stay inside
    /// the return on a net-of-fees basis. Without that qualifier the choice was
    /// all-or-nothing and both answers were wrong: excluding every category made
    /// an employer retirement contribution read as investment skill, while
    /// including them would have reclassified the ledger's entire dividend and
    /// interest history as contributed money.
    ///
    /// Since-inception (no window) anchors on the first external flow, or — when
    /// there is none — on the first activity anywhere in scope, so an account
    /// funded in-kind or from a category still gets a real window instead of a
    /// zero-length one. Assets arriving without a cash flow land in the start
    /// value rather than reading as return. Only a scope with no activity at all
    /// yields a zero-length window, and then both figures are null with a reason.
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

        var s = await ResolveReturnsScopeAsync(
            ledgerId, accountId, fromUtc, toUtc, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var scope = s.Scope;
        var startDate = s.StartDate;
        var endDate = s.EndDate;
        var brokerages = s.Brokerages;
        var brokerageIds = s.BrokerageIds;
        var siblingIds = s.SiblingIds;
        var siblingToBrokerage = s.SiblingToBrokerage;
        var flows = s.Flows;

        // Σ external flow at an instant (money into the brokerage). The value
        // BEFORE a flow subtracts it: the cash it just added is a contribution,
        // not yet return. A withdrawal (negative) is added back symmetrically.
        decimal ContributionAt(DateTime d) =>
            flows.Where(x => x.PostedAt == d).Sum(x => x.Amount);

        // Boundary values (cash + securities as of the instant). Start = value just
        // BEFORE any flow at the window start; end = the full value at the window
        // end (no flow lands there — flows are strictly before endDate).
        //
        // Valued per brokerage and summed here rather than summed in the database:
        // same two queries either way, and it means the roster below and these
        // totals are literally the same numbers, so the rows cannot fail to add up
        // to the report they sit under.
        var startByAccount = await PortfolioValueByBrokerageAsOfAsync(
            ledgerId, brokerageIds, siblingToBrokerage, startDate, cancellationToken)
            .ConfigureAwait(false);
        var endByAccount = await PortfolioValueByBrokerageAsOfAsync(
            ledgerId, brokerageIds, siblingToBrokerage, endDate, cancellationToken)
            .ConfigureAwait(false);

        var startValue = startByAccount.Values.Sum() - ContributionAt(startDate);
        var endValue = endByAccount.Values.Sum();

        // Per-account roster (ledger scope only). Each row's start value is backed
        // out of that account's OWN flow at the start instant, matching how the
        // report's own start value is defined — otherwise the rows would miss the
        // total by exactly that flow, which is not a corner case: since-inception
        // anchors ON the first flow date, so every since-inception report would
        // have shown rows that do not add up.
        IReadOnlyList<ReturnsAccountValue>? accountRows = null;
        if (accountId is null)
        {
            var startFlowByAccount = flows
                .Where(x => x.PostedAt == startDate)
                .GroupBy(x => x.AccountId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

            // Held something at one end, or money moved through it. An account that
            // was worth nothing at both ends AND saw no flow was not covered by this
            // window in any sense a reader cares about, and on a real ledger the
            // dead ones outnumber the live ones several to one. Note what this rule
            // is NOT: "non-zero balance today", the rule that lost $2.18M of opening
            // value. Both endpoints count, so an account emptied inside the window
            // stays. The sum invariant is untouched — every row this drops
            // contributes zero to both columns.
            var accountsWithFlows = flows.Select(x => x.AccountId).ToHashSet();

            accountRows = brokerages
                .Select(b => new ReturnsAccountValue(
                    b.Id,
                    b.Name,
                    startByAccount.GetValueOrDefault(b.Id)
                        - startFlowByAccount.GetValueOrDefault(b.Id),
                    endByAccount.GetValueOrDefault(b.Id)))
                .Where(r => r.StartValue != 0m
                            || r.EndValue != 0m
                            || accountsWithFlows.Contains(r.AccountId))
                .OrderByDescending(r => r.EndValue)
                .ThenBy(r => r.AccountName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Money-weighted (investor perspective: money-in negative). The solver hands
        // back WHY it could not produce a rate, so a blank money-weighted figure is
        // explained the same way a blank time-weighted one always was.
        var cf = new List<ReturnsCalculator.CashFlow>();
        if (startValue != 0m) cf.Add(new(startDate, -startValue));
        foreach (var x in flows) cf.Add(new(x.PostedAt, -x.Amount));
        cf.Add(new(endDate, endValue));
        var irrResult = ReturnsCalculator.Xirr(cf);
        var irr = irrResult.Rate;
        var irrReason = irrResult.Outcome switch
        {
            ReturnsCalculator.XirrOutcome.Solved => null,
            ReturnsCalculator.XirrOutcome.TooFewFlows =>
                "Money-weighted return needs at least two dated cash flows (the opening and " +
                "closing valuations count); this window has fewer.",
            ReturnsCalculator.XirrOutcome.SingleSignedFlows =>
                "Money-weighted return needs both money in and money out; every cash flow in " +
                "this window runs the same direction, so no rate can reconcile them.",
            ReturnsCalculator.XirrOutcome.ZeroLengthWindow =>
                "Money-weighted return needs a window with elapsed time; every cash flow — " +
                "including the opening and closing valuations — falls on a single instant, so " +
                "there is nothing to annualize over. An account with no external cash flows at " +
                "all lands here, because since-inception then has no first flow to start from.",
            ReturnsCalculator.XirrOutcome.Indeterminate =>
                "Money-weighted return is indeterminate for this window: the cash flows offset " +
                "exactly, so every rate satisfies them equally and none is more correct.",
            ReturnsCalculator.XirrOutcome.NoRootInRange =>
                "Money-weighted return has no solution between -99.99%/yr and 10000%/yr for " +
                "these cash flows.",
            _ => null,
        };

        var netContributions = flows.Sum(x => x.Amount);

        // The same flows, un-netted. Signs stay in the netContributions convention —
        // positive is money INTO the scope — so the identity is a plain addition:
        // net == in + out, with out being zero or negative. No magnitudes, no
        // subtraction to get backwards.
        var contributionsIn = flows.Where(x => x.Amount > 0m).Sum(x => x.Amount);
        var contributionsOut = flows.Where(x => x.Amount < 0m).Sum(x => x.Amount);
        var contributionsBySource = flows
            .GroupBy(x => x.Source)
            .Select(g => new ContributionSourceTotals(
                g.Key,
                g.Where(x => x.Amount > 0m).Sum(x => x.Amount),
                g.Where(x => x.Amount < 0m).Sum(x => x.Amount)))
            .OrderByDescending(s => Math.Abs(s.In) + Math.Abs(s.Out))
            .ToList();

        // Time-weighted: a boundary at the window start, each external-flow instant,
        // and the window end — each valued at the same cash+securities basis. The
        // calculator chains and annualizes over the sub-periods that had money in
        // them, skipping the ones that did not; we surface the covered span and a
        // reason for each way the chain can come back empty, plus the flow-count cap.
        double? twr = null;
        string? twrReason = null;
        DateTime? twrFrom = null;
        DateTime? twrTo = null;
        double? twrYears = null;
        int? twrDays = null;
        var flowInstants = flows.Select(x => x.PostedAt).Distinct().ToList();
        var boundaryDates = new SortedSet<DateTime>(flowInstants) { startDate, endDate };

        // Interior boundaries only: the window ends are already valued above and
        // must keep those values — the start is value-BEFORE-flow and the end is
        // the report's own endValue, so re-deriving them here would risk the two
        // drifting apart.
        var interior = boundaryDates
            .Where(d => d != startDate && d != endDate)
            .Select(d => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc))
            .ToArray();

        // EVERY interior boundary valued in TWO queries — securities (mig 200) and
        // cash (mig 201) — instead of two per boundary. Both feeders used to replay
        // their whole input per instant, which is what made a whole-portfolio
        // time-weighted return cost about a minute and is the entire reason a
        // boundary cap existed. Batching the holdings half alone was not enough: it
        // took that feeder from 17.6 ms to 1.7 ms per instant, and the end-to-end
        // figure still sat at 27.9 ms per boundary because the cash call had become
        // the dominant term.
        var securitiesByInstant = interior.Length == 0 || siblingIds.Count == 0
            ? new Dictionary<DateTime, decimal>()
            : (await _db.HoldingsMarketValueAsOfSet(ledgerId, interior, siblingIds.ToArray())
                    .Select(r => new { r.AsOf, r.MarketValue })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(r => r.AsOf)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.MarketValue));

        var cashByInstant = interior.Length == 0 || brokerageIds.Count == 0
            ? new Dictionary<DateTime, decimal>()
            : (await _db.AccountBalanceAsOfInstants(ledgerId, interior, brokerageIds.ToArray())
                    .Select(r => new { r.AsOf, r.Balance })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(r => r.AsOf)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Balance));

        var boundaries = new List<ReturnsCalculator.Boundary>(boundaryDates.Count);
        foreach (var d in boundaryDates)
        {
            decimal value;
            if (d == startDate) value = startValue;        // already value-before-flow
            else if (d == endDate) value = endValue;       // flow-free window end
            else
            {
                var asOfUtc = d.Kind == DateTimeKind.Utc
                    ? d
                    : DateTime.SpecifyKind(d, DateTimeKind.Utc);
                value = cashByInstant.GetValueOrDefault(asOfUtc)
                    + securitiesByInstant.GetValueOrDefault(asOfUtc)
                    - ContributionAt(d);
            }
            boundaries.Add(new ReturnsCalculator.Boundary(d, value, -ContributionAt(d)));
        }
        var twrResult = ReturnsCalculator.Twr(boundaries);
        twr = twrResult.Rate;
        twrFrom = twrResult.CoveredFrom;
        twrTo = twrResult.CoveredTo;
        twrYears = twrResult.Outcome == ReturnsCalculator.TwrOutcome.Solved
            ? twrResult.CoveredYears
            : null;
        // Days as well as years, and NOT because callers cannot divide. With an
        // interior gap the covered span is the SUM of the invested stretches, so
        // CoveredTo − CoveredFrom does not equal it and no caller can derive
        // this from what else is returned. A report reading 0.28 years as "ten
        // weeks" when it was 101 days is the error this closes.
        twrDays = twrYears is { } y ? (int)Math.Round(y * 365.0) : null;
        twrReason = twrResult.Outcome switch
        {
            ReturnsCalculator.TwrOutcome.Solved => null,
            ReturnsCalculator.TwrOutcome.TooFewBoundaries =>
                "Time-weighted return needs at least two portfolio valuations to chain a " +
                "sub-period between; this window has fewer.",
            ReturnsCalculator.TwrOutcome.NoInvestedSubPeriod =>
                "Time-weighted return measures how the holdings performed, and this account " +
                "held nothing at any point in the window — there is no performance to " +
                "measure. Money that merely passed through appears in net contributions.",
            ReturnsCalculator.TwrOutcome.ZeroLengthCoverage =>
                "Time-weighted return needs elapsed time to annualize over; the only " +
                "sub-periods with money in them span no time at all.",
            ReturnsCalculator.TwrOutcome.NegativeCumulativeGrowth =>
                "Time-weighted return is undefined here: the holdings ended a sub-period " +
                "worth less than nothing (borrowed against the position), and no annualized " +
                "rate compounds to a negative value. Money-weighted (IRR) is provided.",
            _ => null,
        };
    

        return new ReturnsResult(
            scope, startDate, endDate, startValue, endValue, netContributions,
            irr, irrReason, twr, twrReason, twrFrom, twrTo, twrYears, twrDays, accountRows,
            contributionsIn, contributionsOut, contributionsBySource,
            ComputedAt, EngineVersion);
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

        // Ask for the brokerages, not for every account in the ledger. The
        // 3-argument overload computes a balance for ALL of them — on a real
        // ledger that is ~663 accounts, 413 of them categories, to use 50 — and
        // this runs once per TWR boundary. Measured at 62.6 ms against 0.8 ms for
        // the same sum with the account set pushed into the scan, on a ledger of
        // only 41 accounts. It was the dominant cost of a whole-ledger report.
        var cash = brokerageIds.Count == 0
            ? 0m
            : await _db.AccountBalanceAsOfInstants(ledgerId, [asOfUtc], brokerageIds.ToArray())
                .SumAsync(b => (decimal?)b.Balance, cancellationToken)
                .ConfigureAwait(false) ?? 0m;

        var securities = siblingIds.Count == 0
            ? 0m
            : await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOfUtc], siblingIds.ToArray())
                .SumAsync(r => (decimal?)r.MarketValue, cancellationToken)
                .ConfigureAwait(false) ?? 0m;

        return cash + securities;
    }

    // The same valuation, broken out per brokerage instead of summed. Both feeders
    // already return one row per account (cash) and per position (securities) — the
    // scalar form above just sums them server-side — so this costs the identical two
    // queries and differs only in where the addition happens. That is why the window
    // endpoints are valued through THIS method and the scalar totals derived from
    // its result: the roster is free, and start/end cannot drift from the rows that
    // are supposed to sum to them, because they are the same numbers.
    //
    // Not used for TWR boundaries. Those run hundreds of times and need the sum
    // only, so they keep the server-side aggregate.
    private async Task<Dictionary<Guid, decimal>> PortfolioValueByBrokerageAsOfAsync(
        Guid ledgerId,
        IReadOnlyList<Guid> brokerageIds,
        IReadOnlyDictionary<Guid, Guid> siblingToBrokerage,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        var asOfUtc = asOf.Kind == DateTimeKind.Utc
            ? asOf
            : DateTime.SpecifyKind(asOf, DateTimeKind.Utc);

        var byAccount = brokerageIds.ToDictionary(id => id, _ => 0m);
        if (brokerageIds.Count == 0) return byAccount;

        var cashRows = await _db.AccountBalanceAsOfInstants(ledgerId, [asOfUtc], brokerageIds.ToArray())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in cashRows)
            if (byAccount.ContainsKey(row.AccountId))
                byAccount[row.AccountId] += row.Balance;

        var siblingIds = siblingToBrokerage.Keys.ToList();
        if (siblingIds.Count > 0)
        {
            var securityRows = await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOfUtc], siblingIds.ToArray())
                .Select(r => new { r.AccountId, r.MarketValue })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            // Securities sit on the holdings sibling; credit them to the brokerage
            // that owns it, which is the account a reader knows by name.
            foreach (var row in securityRows)
                if (siblingToBrokerage.TryGetValue(row.AccountId, out var owner))
                    byAccount[owner] += row.MarketValue;
        }

        return byAccount;
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
