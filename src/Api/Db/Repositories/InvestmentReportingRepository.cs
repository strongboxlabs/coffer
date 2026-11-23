using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Investment reporting reads (ADR-0063 §D5) for the MCP tools (and future
/// Portfolio / Asset-Allocation reports). Holdings are rolled up per security
/// across the ledger; market value = qty × the latest <c>security_prices</c> row dated
/// on or before the instant valued, else the security's last trade price, and a
/// position with neither carried at cost basis (one rule for every valuation,
/// ADR-0084's 2026-10-21 amendment). LINQ/EF, no raw SQL.
/// </summary>
public sealed class InvestmentReportingRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public InvestmentReportingRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // Build identity, stamped on every reporting response so a consumer that
    // assembles one report from several calls can tell which figures are current.
    // semver+sha, not semver alone: two builds of the same release can disagree
    // about what a figure MEANS, and the sha is what separates them.
    private static string EngineVersion =>
        $"{Coffer.Api.Meta.VersionInfo.Semver}+{Coffer.Api.Meta.VersionInfo.Commit}";

    // Wall-clock at computation. Deliberately NOT the caller's as-of or window-end
    // date: those are inputs the caller chooses and can set in the past, so they
    // say nothing about whether a figure is fresh.
    private DateTime ComputedAt => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Holdings rolled up per security as of an instant — quantity, market value AND
    /// FIFO cost basis, all exact at that instant.
    /// </summary>
    /// <remarks>
    /// Valued through the same as-of feeder <c>returns</c> and <c>allocation</c> use,
    /// with basis from <c>holdings_cost_basis_as_of</c>, not the current
    /// <c>holdings</c> projection. That projection is kept in step with <c>txn_legs</c>
    /// by an EF <c>SaveChangesInterceptor</c> — so the two agree, but the projection
    /// only ever describes NOW, and no as-of report can be built on it.
    /// <para>
    /// Cost basis at a past instant needs a walk a read can borrow, which a walk keeping
    /// its state in the <c>lots</c> table is not. Since migration 202 the walk is
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
        // No instant asked for means "now", which is the register's now: the last
        // instant it does not call Scheduled (ADR-0056, 2026-10-21 amendment). Not the
        // clock's instant, which would leave out a row dated later today that the
        // register and every current balance count.
        var asOf = asOfUtc ?? ScheduledBoundary.LastCountedInstant(null, _clock);
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

        // The currency the figures are in (ADR-0114 D7): one brokerage's own snapshot is a
        // per-account figure, in its currency; the ledger's combines brokerages, in the
        // reporting currency. A past instant converts at its day's rate; now (today, or
        // later) is a current value, at the latest rate.
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var target = siblingFilter is { } only ? currencies.OfPositionsOn(only[0]) : currencies.Reporting;
        var lookup = Rates;
        var needs = lookup.NewNeeds();
        var at = RateDay.Of(asOf, needs.ServerToday);

        // Split-adjusted quantity + market value per (holdings-sibling, security) as
        // of the instant, and FIFO basis for the same positions. Both are as-of
        // functions; neither reads the current projection.
        var valued = await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOf], siblingFilter)
            .Select(r => new { r.AccountId, r.SecurityId, r.Quantity, r.MarketValue, r.PriceCurrency })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (valued.Count == 0)
            return new HoldingsSnapshot([], 0m, 0m, 0m, target, RateReasons.None, asOf, ComputedAt, EngineVersion);

        var basisRows = await _db.HoldingsCostBasisAsOf(ledgerId, asOf, siblingFilter)
            .Select(r => new { r.AccountId, r.SecurityId, r.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A position kept in another currency than the figures' has its cost basis
        // converted lot by lot, at each lot's acquisition date (ADR-0114 D7); every other
        // position keeps the basis it reports, and no lot is read for it.
        bool Foreign(Guid holdingsAccountId) => currencies.OfPositionsOn(holdingsAccountId) != target;
        var lots = await LotCosts.OpenAsync(
                _db, ledgerId, asOf, [.. basisRows.Select(b => b.AccountId).Where(Foreign).Distinct()],
                cancellationToken)
            .ConfigureAwait(false);

        // Every rate the figures read, in one load.
        foreach (var v in valued) DeclarePositionValue(needs, currencies, v.AccountId, v.PriceCurrency, target, at);
        foreach (var b in basisRows.Where(b => Foreign(b.AccountId)))
            LotCosts.Declare(needs, lots[(b.AccountId, b.SecurityId)], currencies.OfPositionsOn(b.AccountId), target);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var costBasis = basisRows
            .GroupBy(r => r.SecurityId)
            .ToDictionary(g => g.Key, g =>
            {
                var total = rates.NewTotal(target);
                foreach (var b in g)
                {
                    if (Foreign(b.AccountId))
                        LotCosts.AddTo(total, lots[(b.AccountId, b.SecurityId)], currencies.OfPositionsOn(b.AccountId));
                    else
                        total.Add(b.CostBasis, target, at);   // the identity
                }
                return total.Result;
            });

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
                    .ThenBy(s => s.AccountId)
                    .ToList());

        var rows = valued
            .GroupBy(v => v.SecurityId)
            .Select(g =>
            {
                var quantity = g.Sum(v => v.Quantity);
                // Each position in the figures' currency: from its price's into its
                // brokerage's, then into the target, both at the instant's rate.
                var valueTotal = rates.NewTotal(target);
                foreach (var v in g)
                    valueTotal.Add(PositionValueIn(rates, currencies, v.AccountId, v.MarketValue, v.PriceCurrency, target, at),
                        target, at);
                var marketValue = valueTotal.Result;
                securities.TryGetValue(g.Key, out var s);

                // The per-share price valuation ACTUALLY used, back-adjusted onto this
                // instant's split basis — not the newest security_prices row, which for
                // a past instant would be a price from the future. In the figures'
                // currency, as the market value it divides is.
                decimal? unitPrice = quantity != 0m && marketValue.Amount is { } mv ? decimal.Round(mv / quantity, 6) : null;

                var basis = costBasis.TryGetValue(g.Key, out var b) ? b : Converted.Of(0m);
                decimal? gain = marketValue.Amount is { } value && basis.Amount is { } cost ? value - cost : null;

                return new HoldingSnapshotRow(
                    g.Key,
                    s?.Ticker,
                    s?.Name ?? "(unknown security)",
                    s?.AssetClass,
                    quantity,
                    basis.Amount,
                    unitPrice,
                    marketValue.Amount,
                    gain,
                    gain is { } gn && basis.Amount is { } bs ? ReportingScale.PercentOrNull(gn, bs) : null,
                    heldInBySecurity.TryGetValue(g.Key, out var hi) ? hi : Array.Empty<HeldInSlice>(),
                    Converted.Reasons(marketValue, basis));
            })
            // A row whose value needs a missing rate sorts after every valued one.
            .OrderByDescending(r => r.MarketValue)
            .ThenBy(r => r.SecurityId)
            .ToList();

        // The totals are the rows' sums, so the rows always add up to them; one row that
        // needs a missing rate makes each total it feeds null (ADR-0114 D9).
        return new HoldingsSnapshot(
            rows,
            SumOrNull(rows.Select(r => r.MarketValue)),
            SumOrNull(rows.Select(r => r.CostBasis)),
            SumOrNull(rows.Select(r => r.UnrealizedGain)),
            target,
            RateReasons.Of([.. rows.Select(r => r.RateUnavailable)]),
            asOf,
            ComputedAt,
            EngineVersion);
    }

    /// <summary>
    /// Declares the rates one valued position reads (ADR-0114 D7): from its price's
    /// currency into its brokerage's, and from that into <paramref name="target"/>, both at
    /// <paramref name="at"/>. A position at cost basis is in its brokerage's currency.
    /// </summary>
    private static void DeclarePositionValue(
        ExchangeRateNeeds needs, LedgerCurrencies currencies, Guid holdingsAccountId, string? priceCurrency,
        string target, RateDay at)
    {
        var account = currencies.OfPositionsOn(holdingsAccountId);
        at.Declare(needs, priceCurrency ?? account, account);
        at.Declare(needs, account, target);
    }

    /// <summary>
    /// One valued position in <paramref name="target"/>: its market value from its price's
    /// currency into its brokerage's at <paramref name="at"/>, rounded as the brokerage
    /// shows it, then into the target at the same rate day (ADR-0114 D7). Null, naming the
    /// rate, when either is missing.
    /// </summary>
    private static Converted PositionValueIn(
        ExchangeRateTable rates, LedgerCurrencies currencies, Guid holdingsAccountId, decimal marketValue,
        string? priceCurrency, string target, RateDay at)
    {
        var account = currencies.OfPositionsOn(holdingsAccountId);
        var own = rates.NewTotal(account).Add(marketValue, priceCurrency ?? account, at).Result;
        return rates.NewTotal(target).Add(own, account, at).Result;
    }

    /// <summary>
    /// Current market value per holdings-sibling account (qty × the price a current
    /// value is computed at; a position with no price and no trade at one carried at
    /// cost basis — the OverviewRepository convention), over the positions held on the
    /// caller's today (<see cref="CurrentPositionsAsync"/>). The single definition of
    /// "holdings value by account", shared by the overview, net worth, and accounts
    /// reporting so they never diverge. Keyed by the holdings-sibling account id
    /// (resolve to the owning brokerage via <c>accounts.holdings_account_id</c>).
    /// </summary>
    /// <remarks>Each value is in its brokerage's currency: a position priced in another
    /// converts at the latest rate (ADR-0114 D7), and an account needing a missing rate has
    /// a null value naming it (ADR-0114 D9).</remarks>
    public async Task<IReadOnlyDictionary<Guid, Converted>> MarketValueByAccountAsync(
        Guid ledgerId, DateOnly? today, CancellationToken cancellationToken = default)
    {
        var positions = await CurrentPositionsAsync(ledgerId, null, today, cancellationToken)
            .ConfigureAwait(false);
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var at = RateDay.Latest();
        var rates = Rates;
        var needs = rates.NewNeeds();
        DeclarePositions(needs, positions, currencies, at);
        var table = await rates.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        return ValueByAccount(positions, currencies, table, at);
    }

    /// <summary>
    /// Every position held on the caller's <paramref name="today"/> (migration 259),
    /// each valued by the current-value convention: quantity × the latest close dated
    /// on or before that day, else the security's last trade price in the ledger (on
    /// the day's split basis), or the cost basis when it has neither.
    /// </summary>
    /// <remarks>
    /// Not the <c>holdings</c> projection, which counts every trade and split entered,
    /// including ones the register calls Scheduled: one future-dated buy moved today's
    /// value by its whole amount, and a future-dated price repriced every share already
    /// held. <c>holdings_as_of_now</c> still reads the projection for an account with
    /// nothing at or after the boundary, where the two are the same thing, and walks
    /// FIFO only for the accounts where they differ.
    /// <para>
    /// The boundary is the register's (<see cref="ScheduledBoundary"/>), the one the
    /// cash side is cut at too, and is computed once here from the caller's date or the
    /// register's fallback: the cash and the positions read the same date.
    /// </para>
    /// <para>
    /// The price is <c>security_prices_as_of_now</c>'s, back-adjusted onto the day's
    /// split basis, and a position it has no price for falls to the same rule the
    /// net-worth history's feeder applies (<c>fn_last_trade_price</c>, then cost basis;
    /// ADR-0084, 2026-10-21 amendment), so the two value every position alike.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<CurrentPosition>> CurrentPositionsAsync(
        Guid ledgerId, Guid[]? holdingsAccountIds, DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.HoldingsAsOfNow(
                ledgerId, ScheduledBoundary.ScheduledFrom(today, _clock), holdingsAccountIds)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Bounded to the same scale the as-of feeder produces, so the overview and the
        // net-worth history agree exactly rather than approximately.
        return rows
            .Select(r => new CurrentPosition(
                r.AccountId, r.SecurityId, r.Quantity, r.CostBasis, r.Price, r.PriceDate,
                r.Price is { } px ? ReportingScale.MarketValue(r.Quantity, px) : r.CostBasis,
                r.Price is null ? null : r.PriceCurrency))
            .ToList();
    }

    /// <summary>The rate lookup, on this repository's context and clock, for the readers
    /// that build on it.</summary>
    internal ExchangeRateLookup Rates => new(_db, _clock);

    /// <summary>The clock this repository reads "now" from.</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>
    /// Declares, for each position, the rate from its price's currency into its
    /// brokerage's (ADR-0114 D7) at <paramref name="at"/>. A position at cost basis, or
    /// priced in its brokerage's own currency, needs none.
    /// </summary>
    internal static void DeclarePositions(
        ExchangeRateNeeds needs, IEnumerable<CurrentPosition> positions, LedgerCurrencies currencies, RateDay at)
    {
        foreach (var p in positions)
        {
            var account = currencies.OfPositionsOn(p.AccountId);
            at.Declare(needs, p.ValueCurrency ?? account, account);
        }
    }

    /// <summary>
    /// Current value per holdings-sibling account, in its brokerage's currency: each
    /// position from its price's currency at <paramref name="at"/>'s rate, summed at full
    /// precision and rounded once. An account whose position needs a missing rate has a
    /// null value naming it.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, Converted> ValueByAccount(
        IEnumerable<CurrentPosition> positions, LedgerCurrencies currencies, ExchangeRateTable rates, RateDay at)
    {
        var byAccount = new Dictionary<Guid, ConvertedTotal>();
        foreach (var p in positions)
        {
            if (!byAccount.TryGetValue(p.AccountId, out var total))
                byAccount[p.AccountId] = total = rates.NewTotal(currencies.OfPositionsOn(p.AccountId));
            total.Add(p.Value, p.ValueCurrency ?? total.CurrencyCode, at);
        }
        return byAccount.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
    }

    /// <summary>
    /// One position held on the caller's today. <see cref="Price"/> and
    /// <see cref="PriceDate"/> are the latest close on or before that day or else the
    /// last trade's price and day, both null when the security has neither, in which
    /// case <see cref="Value"/> is the cost basis.
    /// </summary>
    /// <param name="ValueCurrency">The currency <see cref="Value"/> is in: the price's
    /// (migration 281); null when it is the cost basis, which is in its account's.</param>
    internal sealed record CurrentPosition(
        Guid AccountId,
        Guid SecurityId,
        decimal Quantity,
        decimal CostBasis,
        decimal? Price,
        DateOnly? PriceDate,
        decimal Value,
        string? ValueCurrency);

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
    /// asked for a past instant, and its total reconciles with a returns total
    /// valued at the same instant to the cent — securities here plus
    /// <see cref="AllocationResult.ExcludedBrokerageCash"/> equals that report's
    /// end value. A returns window with an explicit end is valued at the instant
    /// before that end, so that is the instant an allocation matches it at. Two
    /// valuation paths that agree about "now" but drift on history are how a
    /// discrepancy far too large to be rounding sat unexplained across two
    /// published reports.
    /// </remarks>
    public Task<AllocationResult> AllocationAsync(
        Guid ledgerId,
        AllocationDimension dimension = AllocationDimension.AssetClass,
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default) =>
        AllocationAsync(ledgerId, dimension, asOfUtc, today: null, cancellationToken);

    /// <inheritdoc cref="AllocationAsync(Guid, AllocationDimension, DateTime?, CancellationToken)"/>
    /// <param name="today">The caller's local date, which places "now" when no instant
    /// is asked for, as it does for returns, so the two value the same instant.</param>
    public async Task<AllocationResult> AllocationAsync(
        Guid ledgerId,
        AllocationDimension dimension,
        DateTime? asOfUtc,
        DateOnly? today,
        CancellationToken cancellationToken = default)
    {
        // "Now" is the register's now, as for HoldingsSnapshotAsync.
        var asOf = asOfUtc ?? ScheduledBoundary.LastCountedInstant(today, _clock);
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
        // Each brokerage's cash is in its own currency, summed per brokerage here and
        // converted below.
        var cashRows = brokerageIds.Count == 0
            ? []
            : await _db.AccountBalanceAsOfInstants(ledgerId, [asOf], brokerageIds.ToArray())
                .Select(b => new { b.AccountId, b.Balance })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var siblingIds = siblingToBrokerage.Keys.ToList();
        var positionRows = siblingIds.Count == 0
            ? []
            : await _db.HoldingsMarketValueAsOfSet(ledgerId, [asOf], siblingIds.ToArray())
                .Select(r => new { r.AccountId, r.SecurityId, r.MarketValue, r.PriceCurrency })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        // Every figure combines brokerages, so it is in the reporting currency (ADR-0114
        // D7): each position from its price's currency into its brokerage's, then into the
        // reporting currency; each brokerage's cash into it; at the instant's day's rate, the
        // latest for now. Only an account-dimension bucket is one brokerage's figure, and it
        // stays in that brokerage's currency. Every rate is read in one load.
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var target = currencies.Reporting;
        var lookup = Rates;
        var needs = lookup.NewNeeds();
        var at = RateDay.Of(asOf, needs.ServerToday);
        foreach (var c in cashRows) at.Declare(needs, currencies.Of(c.AccountId), target);
        foreach (var p in positionRows) DeclarePositionValue(needs, currencies, p.AccountId, p.PriceCurrency, target, at);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var cashTotal = rates.NewTotal(target);
        foreach (var c in cashRows) cashTotal.Add(c.Balance, currencies.Of(c.AccountId), at);
        var excludedCash = cashTotal.Result;

        var positions = positionRows
            .Select(p =>
            {
                var brokerageCurrency = currencies.OfPositionsOn(p.AccountId);
                return new AllocationPosition(
                    p.AccountId,
                    p.SecurityId,
                    PositionValueIn(rates, currencies, p.AccountId, p.MarketValue, p.PriceCurrency, target, at),
                    brokerageCurrency,
                    rates.NewTotal(brokerageCurrency).Add(p.MarketValue, p.PriceCurrency ?? brokerageCurrency, at).Result);
            })
            .ToList();

        if (positions.Count == 0)
            return new AllocationResult(
                [], 0m, asOf, excludedCash.Amount, [], target, excludedCash.Unavailable, ComputedAt, EngineVersion);

        var total = AllocationSum.Of(positions.Select(p => p.Value));
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

        // Each bucket keeps its figure in the bucket's own currency (the reporting one,
        // or an account bucket's brokerage's) and in the reporting currency, which its
        // percent and its order read.
        var buckets = new Dictionary<(string Key, string Currency), (AllocationSum Own, AllocationSum Reporting)>();
        void Add(string key, string currency, Converted own, Converted reporting, decimal share = 1m)
        {
            if (!buckets.TryGetValue((key, currency), out var b))
                buckets[(key, currency)] = b = (new AllocationSum(), new AllocationSum());
            b.Own.Add(own, share);
            b.Reporting.Add(reporting, share);
        }

        foreach (var g in positions.GroupBy(p => p.SecurityId))
        {
            var securityId = g.Key;
            var marketValue = AllocationSum.Of(g.Select(p => p.Value)).Result;
            secs.TryGetValue(securityId, out var s);

            if (lookThrough && s is { AssetClass: "multi_asset" } && Decomposable(securityId))
            {
                var comps = componentsBySecurity[securityId];
                var weightSum = comps.Sum(c => c.Weight);
                foreach (var c in comps)
                {
                    Add(
                        dimension == AllocationDimension.AssetClass ? c.Cls : (c.Region ?? "Unclassified"),
                        target, marketValue, marketValue, c.Weight / weightSum);
                }
                continue;
            }

            // Account dimension credits each position to the BROKERAGE holding it,
            // not to the shadow sibling it posts to (ADR-0019), in that brokerage's
            // own currency: a per-account figure stays native (ADR-0114 D7).
            if (dimension == AllocationDimension.Account)
            {
                foreach (var p in g)
                {
                    var owner = siblingToBrokerage.TryGetValue(p.AccountId, out var o) ? o : p.AccountId;
                    Add(brokerageNames.TryGetValue(owner, out var nm) ? nm : "Unclassified",
                        p.BrokerageCurrency, p.BrokerageValue, p.Value);
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
            Add(bucketKey, target, marketValue, marketValue);
        }

        decimal? PercentOfTotal(decimal? value) =>
            value is { } v && total.Amount is { } t ? ReportingScale.Percent(v, t) : null;

        var undecomposed = positions
            .GroupBy(p => p.SecurityId)
            .Where(g => multiAssetIds.Contains(g.Key) && !Decomposable(g.Key))
            .Select(g =>
            {
                var mv = AllocationSum.Of(g.Select(p => p.Value)).Amount;
                secs.TryGetValue(g.Key, out var s);
                return new UndecomposedMultiAsset(
                    g.Key,
                    s?.Ticker,
                    s?.Name ?? "Unknown",
                    mv,
                    PercentOfTotal(mv));
            })
            .OrderByDescending(u => u.MarketValue)
            .ThenBy(u => u.SecurityId)
            .ToList();

        // Ordered by the reporting-currency figure, so account buckets in different
        // currencies sort by what they are worth together; one that needs a missing rate
        // sorts last.
        var rows = buckets
            .Select(kv => (Reporting: kv.Value.Reporting.Amount, Row: new AllocationRow(
                kv.Key.Key, kv.Value.Own.Amount, PercentOfTotal(kv.Value.Reporting.Amount), kv.Key.Currency,
                RateReasons.Of(kv.Value.Own.Unavailable, kv.Value.Reporting.Unavailable))))
            .OrderByDescending(b => b.Reporting)
            .ThenBy(b => b.Row.Bucket, StringComparer.Ordinal)
            .ThenBy(b => b.Row.CurrencyCode, StringComparer.Ordinal)
            .Select(b => b.Row)
            .ToList();

        return new AllocationResult(
            rows, total.Amount, asOf, excludedCash.Amount, undecomposed, target,
            RateReasons.Of(total.Unavailable, excludedCash.Unavailable),
            ComputedAt, EngineVersion);
    }

    /// <summary>One valued position of an allocation: its value in the reporting currency,
    /// and in its brokerage's, which an account bucket shows.</summary>
    private sealed record AllocationPosition(
        Guid AccountId, Guid SecurityId, Converted Value, string BrokerageCurrency, Converted BrokerageValue);

    /// <summary>
    /// A sum of figures already in one currency, each possibly null with its missing rates,
    /// at full precision and not rounded again: an allocation's slices are shares of a
    /// figure, and a sum of identity conversions reads exactly as it did. One null figure
    /// makes the sum null (ADR-0114 D9).
    /// </summary>
    private sealed class AllocationSum
    {
        private readonly List<RateUnavailable> _unavailable = [];
        private decimal _sum;

        public static AllocationSum Of(IEnumerable<Converted> figures)
        {
            var sum = new AllocationSum();
            foreach (var f in figures) sum.Add(f);
            return sum;
        }

        public void Add(Converted figure, decimal share = 1m)
        {
            if (figure.Amount is { } a) _sum += a * share;
            foreach (var why in figure.Unavailable)
                if (!_unavailable.Contains(why)) _unavailable.Add(why);
        }

        public decimal? Amount => _unavailable.Count > 0 ? null : _sum;

        public IReadOnlyList<RateUnavailable> Unavailable => _unavailable;

        public Converted Result => new(Amount, [.. _unavailable]);
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
            select new { r, AccountName = a.Name, AccountCurrencyCode = a.CurrencyCode };

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
                // The transfer slot's other side, kept only on a transfer between real
                // accounts in two currencies, as the investment register keeps it
                // (ADR-0114 D14, "The register row"). In one currency it is this side
                // negated, and a category side mirrors, so both read null.
                var otherSideAcross = p.TransferAccountType is not (null or "category")
                    && p.TransferCurrencyCode is { } otherCurrency
                    && !string.Equals(otherCurrency, canonical.AccountCurrencyCode, StringComparison.Ordinal);
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
                    p.SettledAmount,
                    p.FeeAmount,
                    p.CategoryAccountName,
                    p.TransferAccountName,
                    otherSideAcross ? p.TransferAmount : null,
                    otherSideAcross ? p.TransferCurrencyCode : null);
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
        // The figures' currency (ADR-0114 D7): one brokerage's income is a per-account
        // figure, in its currency; the ledger's combines brokerages, in the reporting
        // currency. An income leg carries the money of the brokerage on its posting's
        // other side, in that brokerage's currency (the category's own when it faces none),
        // and converts at its transaction's date.
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var target = accountId is { } scoped ? currencies.Of(scoped) : currencies.Reporting;

        var q =
            from r in _db.ResolvedTransactions.AsNoTracking()
            join a in _db.Accounts.AsNoTracking() on r.AccountId equals a.Id
            join paying in _db.Accounts.AsNoTracking() on r.CounterpartyAccountId equals (Guid?)paying.Id into payingJoin
            from paying in payingJoin.DefaultIfEmpty()
            where a.LedgerId == ledgerId
                  && a.AccountType == "category"
                  && r.PostingRole == PostingRoles.Income
                  && !r.IsHidden
                  && r.IsMergedInto == null
            select new { r, Currency = paying != null ? paying.CurrencyCode : a.CurrencyCode };

        if (fromUtc is { } from) q = q.Where(x => x.r.PostedAt >= from);
        if (toUtc is { } to) q = q.Where(x => x.r.PostedAt < to);
        if (securityId is { } sec) q = q.Where(x => x.r.SecurityId == sec);
        // accountId filters by the brokerage (the income leg's counterparty cash).
        if (accountId is { } acct) q = q.Where(x => x.r.CounterpartyAccountId == acct);

        // Aggregate per (month, group, currency); coarser buckets rolled up in memory. A
        // leg in another currency than the figures' is grouped by its day too, the day it
        // converts at; one in the figures' currency needs no day.
        var grouped = groupBy == InvestmentIncomeGroupBy.Account
            ? await q
                .GroupBy(x => new
                {
                    x.r.PostedAt.Year, x.r.PostedAt.Month, Id = x.r.CounterpartyAccountId,
                    Name = x.r.CounterpartyAccountName, x.Currency,
                    Day = x.Currency == target ? (DateTime?)null : x.r.PostedAt.Date,
                })
                .Select(g => new IncomeGroup(g.Key.Year, g.Key.Month, g.Key.Id, null, g.Key.Name,
                    g.Key.Currency, g.Key.Day, g.Sum(x => x.r.Amount)))
                .ToListAsync(cancellationToken).ConfigureAwait(false)
            : await q
                .GroupBy(x => new
                {
                    x.r.PostedAt.Year, x.r.PostedAt.Month, Id = x.r.SecurityId,
                    Name = x.r.SecurityName, Ticker = x.r.SecurityTicker, x.Currency,
                    Day = x.Currency == target ? (DateTime?)null : x.r.PostedAt.Date,
                })
                .Select(g => new IncomeGroup(g.Key.Year, g.Key.Month, g.Key.Id, g.Key.Ticker, g.Key.Name,
                    g.Key.Currency, g.Key.Day, g.Sum(x => x.r.Amount)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var lookup = Rates;
        var needs = lookup.NewNeeds();
        var serverToday = needs.ServerToday;
        foreach (var g in grouped.Where(g => g.Day is not null))
            RateDay.Of(g.Day!.Value, serverToday).Declare(needs, g.Currency, target);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        // One group's magnitude in the figures' currency: income legs are negative, so
        // flipped, and a foreign one converted at its day (a leg dated today or later, at
        // the latest rate).
        void AddTo(ConvertedTotal total, IncomeGroup g)
        {
            if (g.Day is { } day) total.Add(-g.Sum, g.Currency, RateDay.Of(day, serverToday));
            else total.Add(-g.Sum, target, RateDay.Latest());   // the identity
        }

        // A brokerage's row is its own figure, in its own currency; a security's row
        // combines brokerages and converts. Every row is also converted for the total
        // and for its order, so rows in different currencies sort by what they are worth
        // together.
        var byAccount = groupBy == InvestmentIncomeGroupBy.Account;
        var total = rates.NewTotal(target);
        var rows = grouped
            .GroupBy(g => new
            {
                Period = PeriodLabel(timeBucket, g.Year, g.Month),
                g.Id,
                g.Ticker,
                g.Name,
                Currency = byAccount ? g.Currency : target,
            })
            .Select(g =>
            {
                var converted = rates.NewTotal(target);
                foreach (var part in g)
                {
                    AddTo(converted, part);
                    AddTo(total, part);
                }
                var amount = byAccount && g.Key.Currency != target
                    ? Converted.Of(ConvertedTotal.Round(-g.Sum(p => p.Sum)))
                    : converted.Result;
                return (SortKey: converted.Amount, Row: new InvestmentIncomeRow(
                    g.Key.Period,
                    g.Key.Id,
                    g.Key.Ticker,
                    g.Key.Name ?? "(misc income)",
                    amount.Amount,
                    g.Key.Currency,
                    amount.Unavailable));
            })
            .OrderBy(x => x.Row.Period, StringComparer.Ordinal)
            .ThenByDescending(x => x.SortKey)
            .ThenBy(x => x.Row.GroupId)
            .ThenBy(x => x.Row.GroupName, StringComparer.Ordinal)
            .ThenBy(x => x.Row.CurrencyCode, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();

        return new InvestmentIncomeResult(
            rows, total.Amount, target, total.Unavailable.ToList(), ComputedAt, EngineVersion);
    }

    /// <summary>One (month, group, currency[, day]) aggregate of income legs.</summary>
    private sealed record IncomeGroup(
        int Year, int Month, Guid? Id, string? Ticker, string? Name, string Currency, DateTime? Day, decimal Sum);

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

        // The figures' currency (ADR-0114 D7): one brokerage's gains are a per-account
        // figure, in its currency; the ledger's combine brokerages, in the reporting
        // currency.
        var currencies = await LedgerCurrencies.LoadAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var target = siblingFilter is { } scoped ? currencies.OfPositionsOn(scoped) : currencies.Reporting;
        bool Foreign(Guid holdingsAccountId) => currencies.OfPositionsOn(holdingsAccountId) != target;

        // Aggregate per (security, holdings account) in SQL, then resolve names in memory
        // (a join + GroupBy on the joined shape doesn't translate cleanly). An account in
        // the figures' currency is taken as stored.
        var agg = await q
            .GroupBy(g => new { g.SecurityId, g.AccountId })
            .Select(grp => new
            {
                grp.Key.SecurityId,
                grp.Key.AccountId,
                Proceeds = grp.Sum(x => x.Proceeds),
                CostBasisSold = grp.Sum(x => x.CostBasisSold),
                RealizedGain = grp.Sum(x => x.RealizedGain),
                RealizedGainLt = grp.Sum(x => x.RealizedGainLt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // An account in another currency converts sale by sale (ADR-0114 D7): the
        // proceeds at the sale's trade date, the cost lot by lot at each lot's acquisition
        // date, so a realized gain includes the currency move between the two. Its sales
        // and their lots are read only when there is one.
        var foreignAccounts = agg.Select(a => a.AccountId).Where(Foreign).Distinct().ToArray();
        var foreignSales = foreignAccounts.Length == 0
            ? []
            : await q.Where(g => foreignAccounts.Contains(g.AccountId))
                .Select(g => new { g.SecurityId, g.AccountId, g.SellLegId, g.SoldAt, g.Proceeds, g.ProceedsLt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var saleLegs = foreignSales.Select(s => s.SellLegId).ToHashSet();
        var lotsBySale = foreignAccounts.Length == 0
            ? Array.Empty<Entities.RealizedGainLotRow>().ToLookup(l => l.SellLegId)
            : (await _db.RealizedGainLots(ledgerId, foreignAccounts)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Where(l => saleLegs.Contains(l.SellLegId))
                .ToLookup(l => l.SellLegId);

        var lookup = Rates;
        var needs = lookup.NewNeeds();
        var serverToday = needs.ServerToday;
        foreach (var sale in foreignSales)
        {
            var saleCurrency = currencies.OfPositionsOn(sale.AccountId);
            RateDay.Of(sale.SoldAt, serverToday).Declare(needs, saleCurrency, target);
            foreach (var lot in lotsBySale[sale.SellLegId])
                needs.Add(saleCurrency, target, DateOnly.FromDateTime(lot.AcquiredAt));
        }
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var secIds = agg.Select(a => a.SecurityId).Distinct().ToList();
        var securities = (await _db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId && secIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        var salesBySecurity = foreignSales.ToLookup(s => s.SecurityId);
        var rows = agg
            .GroupBy(a => a.SecurityId)
            .Select(g =>
            {
                // The accounts in the figures' currency, as stored: their gains are exact
                // there, so they are summed rather than re-derived.
                var stored = g.Where(a => !Foreign(a.AccountId)).ToList();
                var storedProceeds = stored.Sum(a => a.Proceeds);
                var storedCost = stored.Sum(a => a.CostBasisSold);
                var storedGain = stored.Sum(a => a.RealizedGain);
                var storedGainLt = stored.Sum(a => a.RealizedGainLt);

                // Every sale of an account in another currency, converted.
                var proceeds = rates.NewTotal(target);
                var cost = rates.NewTotal(target);
                var proceedsLt = rates.NewTotal(target);
                var costLt = rates.NewTotal(target);
                foreach (var sale in salesBySecurity[g.Key])
                {
                    var saleCurrency = currencies.OfPositionsOn(sale.AccountId);
                    var soldOn = RateDay.Of(sale.SoldAt, serverToday);
                    proceeds.Add(sale.Proceeds, saleCurrency, soldOn);
                    proceedsLt.Add(sale.ProceedsLt, saleCurrency, soldOn);
                    foreach (var lot in lotsBySale[sale.SellLegId])
                    {
                        var acquired = DateOnly.FromDateTime(lot.AcquiredAt);
                        cost.Add(lot.Cost, saleCurrency, acquired);
                        // Long-term iff the sale is more than a year after acquisition, the
                        // walk's own test (migration 282).
                        if (sale.SoldAt > lot.AcquiredAt.AddYears(1)) costLt.Add(lot.Cost, saleCurrency, acquired);
                    }
                }

                decimal? proceedsTotal = proceeds.Amount is { } p ? storedProceeds + p : null;
                decimal? costTotal = cost.Amount is { } c ? storedCost + c : null;
                decimal? gain = proceeds.Amount is { } pg && cost.Amount is { } cg
                    ? storedGain + (pg - cg)
                    : null;
                decimal? gainLt = proceedsLt.Amount is { } pl && costLt.Amount is { } cl
                    ? storedGainLt + (pl - cl)
                    : null;

                securities.TryGetValue(g.Key, out var s);
                // LT stored on each row; ST is the remainder (total - LT).
                return new RealizedGainSummaryRow(
                    g.Key, s?.Ticker, s?.Name ?? "(security)",
                    proceedsTotal, costTotal, gain,
                    gain is { } gt && gainLt is { } lt ? gt - lt : null, gainLt,
                    RateReasons.Of(proceeds.Unavailable, cost.Unavailable, proceedsLt.Unavailable, costLt.Unavailable));
            })
            // A row that needs a missing rate sorts after every valued one.
            .OrderByDescending(r => r.RealizedGain)
            .ThenBy(r => r.SecurityId)
            .ToList();

        // The totals are the rows' sums (ADR-0114 D9: one null row nulls each it feeds).
        return new RealizedGainsResult(
            rows,
            SumOrNull(rows.Select(r => r.Proceeds)),
            SumOrNull(rows.Select(r => r.CostBasisSold)),
            SumOrNull(rows.Select(r => r.RealizedGain)),
            SumOrNull(rows.Select(r => r.RealizedGainShortTerm)),
            SumOrNull(rows.Select(r => r.RealizedGainLongTerm)),
            target,
            RateReasons.Of([.. rows.Select(r => r.RateUnavailable)]),
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
    // figure is lossy in a way that reliably misleads: a net outflow is equally
    // consistent with one withdrawal of that size and with a larger outflow partly
    // offset by money in, and a reader holding one salient event will bind the number
    // to it, reading a net outflow as one withdrawal when a larger withdrawal was
    // partly offset by contributions. The parts are already classified
    // when the net is summed, so the breakdown returns them instead of making the
    // reader guess.
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

        /// <summary>A brokerage's funded opening balance, arriving inside the window on
        /// its start date (or at its first posting, when that is earlier): money the
        /// brokerage held from then without any posting bringing it in. Signed as the
        /// balance is, so a negative one, a debt carried in, is money out.</summary>
        public const string OpeningBalances = "opening_balances";
    }

    // There is no boundary cap. A boundary valuation needs holdings and cash, and
    // migrations 200 and 201 batch both, so a whole-ledger time-weighted return
    // costs two queries instead of two per boundary and a cap has nothing to
    // protect.
    //
    // A boundary count cannot express a time budget in any case: per-boundary cost
    // scales with accounts in scope, so the same value is a small budget at
    // account scope and a much larger one at ledger scope. No cap value is right
    // at both.

    /// <summary>
    /// Everything a returns report needs before it starts VALUING anything: the
    /// perimeter, the classified external flows, and the resolved window.
    /// </summary>
    /// <remarks>
    /// Extracted so <see cref="ReturnsCostEstimateAsync"/> can answer "how much
    /// would this cost" from the same rules that decide the answer, rather than a
    /// second implementation of them. Scope, the perimeter test, the category
    /// posting-role qualifier and header-level in-kind detection all change the
    /// flow-instant count; a parallel copy of that logic would drift, and a
    /// classification rule applied in one place and not another is how a returns
    /// figure goes wrong.
    /// <para>
    /// This is also the CHEAP half of a returns call: its cost does not grow with
    /// the boundary count, while the valuation loop that follows it runs per boundary.
    /// </para>
    /// </remarks>
    private async Task<ReturnsScope> ResolveReturnsScopeAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool valueInKind = true)
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
                a.Id, a.Name, a.HoldingsAccountId, a.OpeningBalance, a.OpenedOn, a.CurrencyCode))
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
        // An account that is not one of this ledger's brokerages (unknown, another
        // ledger's, not an investment account, or a holdings sibling) is a bad
        // request, not an empty portfolio: a silent empty scope reads as "nothing
        // invested" and hides a mistyped or stale id from the caller.
        if (accountId is not null && brokerages.Count == 0)
            throw new ReportRequestException(
                "accountId is not an investment account of this ledger.");

        // When a funded opening balance arrives: its start date, the balance's as-of date,
        // unless the brokerage or its holdings posted earlier. The running balance starts
        // from the opening balance whatever the dates, so a posting before the start date
        // already stands on it, as the register shows; there it is held from that posting.
        // So before it arrives the brokerage holds exactly nothing, never less. One
        // statement per funded brokerage, and none for a ledger whose opening balances
        // are all zero, as most imported ones are.
        for (var i = 0; i < brokerages.Count; i++)
        {
            var b = brokerages[i];
            if (b.OpenedOn is not { } openedOn || b.OpeningBalance == 0m) continue;
            var opened = DateTime.SpecifyKind(openedOn.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var firstPosted = await NeededFxPairs.FirstLivePostingInstantOfFewAsync(
                    _db, ledgerId, b.HoldingsAccountId is { } holdings ? [b.Id, holdings] : [b.Id], cancellationToken)
                .ConfigureAwait(false);
            var arrives = firstPosted is { } posted && ToTimestampScale(AsUtc(posted)) < opened
                ? ToTimestampScale(AsUtc(posted))
                : opened;
            brokerages[i] = b with { OpeningArrives = arrives };
        }
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

        // Every bound at timestamptz's resolution before anything is compared with it
        // or valued at it. A .NET instant carries a tenth of a microsecond the column
        // cannot hold, and the instant before a bound is only exact at the column's
        // scale.
        fromUtc = fromUtc is { } rawFrom ? ToTimestampScale(rawFrom) : null;
        toUtc = toUtc is { } rawTo ? ToTimestampScale(rawTo) : null;
        nowUtc = ToTimestampScale(nowUtc);

        var endDate = toUtc ?? nowUtc;
        // The last instant the window counts, and the one its end is valued at. An
        // explicit end is exclusive, so the window stops at the instant before it; the
        // default end is already the last instant counted (the end of the caller's
        // today as the register counts it), so it counts itself. Every query below
        // takes this one bound: an instant valued into the end but not classified
        // would read a flow there as return.
        var endValuedAt = toUtc is { } explicitEnd ? LastInstantBefore(explicitEnd) : nowUtc;
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
                        && r.PostedAt <= endValuedAt);
        if (fromUtc is { } from) flowQ = flowQ.Where(r => r.PostedAt >= from);
        var cashFlows = await flowQ
            // AccountId rides along so a flow the start valuation already holds can be
            // backed out of the RIGHT account's opening value, and so the roster knows
            // which accounts money moved through.
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
                },
                Unconverted: (RateUnavailable?)null))
            .ToList();

        // IN-KIND SHARE TRANSFERS. An in-kind rollover moves securities between
        // brokerages with no cash leg anywhere, and the perimeter test above
        // cannot see it: every leg of a transfer_shares header faces an account
        // INSIDE its own brokerage.
        //
        //   source sibling    -> source brokerage      -V   (shares out)
        //   source brokerage  -> source sibling          0
        //   dest sibling      -> dest brokerage         +V   (shares in)
        //   dest brokerage    -> dest sibling            0
        //
        // where V is the MARKET value of the shares that moved at the transfer
        // instant. The legs themselves hold the lots' carried cost basis (ADR-0065
        // D2), which is not what the destination is worth after the transfer, and
        // this report values every boundary at market; a flow booked at basis would
        // leave the difference inside the sub-period that holds the transfer.
        //
        // Nothing crosses the perimeter by counterparty; the only thing joining
        // the two accounts is the shared HEADER. So value moved between
        // brokerages while every individual leg looked purely internal — the
        // destination booked the arrival as performance and the source booked the
        // departure as a loss. A single transfer could therefore inflate one
        // account's return and depress its counterpart's by the full value moved.
        //
        // Detection is therefore header-level: a transfer_shares header is an
        // external flow when its legs SPAN the perimeter. The amount is the sum
        // of its in-scope legs, each share leg valued at market, which lands
        // correctly in all three cases — a
        // contribution at the destination, a withdrawal at the source, and zero
        // at ledger scope where both sides are inside and it nets out, exactly as
        // an internal move should.
        //
        // Summing every in-scope leg rather than just the security one is
        // deliberate: the brokerage-side legs are zero, so they contribute
        // nothing, and the sum stays right if that shape ever changes.
        //
        // DISTINCT BY LEG ID matters. resolved_transactions yields a row per leg
        // per resolved counterparty, so a four-leg header returns each leg
        // several times. Summing the rows as they come counts the transfer once
        // per resolved row — the destination would read a multiple of what had
        // actually moved.
        // Find the candidate HEADERS via legs that are in scope — this is what
        // bounds the work and scopes it to the ledger. Filtering only on the
        // action would match transfer_shares across every ledger the caller can
        // see and could not use an account index: a whole-view scan to find a
        // handful of rows, and one ledger's transfers leaking into another's
        // returns.
        var inKindHeaderQ = _db.ResolvedTransactions.AsNoTracking()
            .Where(r => r.InvestmentAction == LedgerActions.TransferShares
                        && inPerimeter.Contains(r.AccountId)
                        && !r.IsHidden
                        && r.IsMergedInto == null
                        && r.PostedAt <= endValuedAt);
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
                .Select(r => new
                {
                    r.Id, r.HeaderId, r.AccountId, r.Amount, r.PostedAt, r.SecurityId, r.Quantity,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        if (inKindLegs.Count > 0)
        {
            var perimeter = inPerimeter.ToHashSet();
            var spanning = inKindLegs
                .DistinctBy(l => l.Id)
                .GroupBy(l => l.HeaderId)
                .Where(g => g.Any(l => perimeter.Contains(l.AccountId))
                            && g.Any(l => !perimeter.Contains(l.AccountId)))
                .Select(g => (Legs: g.ToList(), PostedAt: g.Min(l => l.PostedAt)))
                .ToList();

            // The flow is the shares' MARKET value at the transfer instant, the basis
            // every boundary of this report is valued on, and not the carried cost
            // basis the legs hold. A flow booked at basis leaves (market − basis)
            // inside the sub-period that contains the transfer: the destination reads
            // an unearned gain, the source reads its own appreciation as nothing.
            //
            // The share leg is the one on a HOLDINGS sibling. The resolved view also
            // shows a security and quantity on the brokerage's own zero-amount leg
            // (it borrows them from the posting it pairs with), so a test on the
            // security alone would value the one move twice.
            var shareLegs = spanning
                .SelectMany(h => h.Legs
                    .Where(l => siblingsInLedger.Contains(l.AccountId)
                                && l.SecurityId != null
                                && l.Quantity is not (null or 0m))
                    .Select(l => (Instant: ToTimestampScale(h.PostedAt), l.AccountId)))
                .ToList();
            // The cost estimate only counts flow instants and skips the valuation read:
            // its flows then carry the legs' own amounts, and the instant filter below
            // counts a transfer that moves shares or carries an amount.
            var market = valueInKind
                ? await PricedPositionsAtTransferAsync(
                    ledgerId,
                    shareLegs.Select(x => x.Instant).Distinct().ToArray(),
                    shareLegs.Select(x => x.AccountId).Distinct().ToArray(),
                    cancellationToken).ConfigureAwait(false)
                : new Dictionary<(DateTime Instant, Guid SecurityId), (decimal Quantity, decimal MarketValue, string? Currency)>();

            // The market value is in the price's currency, and the flow is the brokerage's
            // own figure, in its currency (ADR-0114 D7): a price in another converts at the
            // transfer's day. Every such rate in one load; none is read when no price is in
            // another currency.
            var currencyOf = brokerages.ToDictionary(b => b.Id, b => b.CurrencyCode);
            string LegCurrency(Guid accountInPerimeter) => currencyOf[OwningBrokerage(accountInPerimeter)];
            var lookup = Rates;
            var needs = lookup.NewNeeds();
            foreach (var h in spanning)
                foreach (var l in h.Legs.Where(l => perimeter.Contains(l.AccountId) && l.SecurityId is not null))
                    if (market.TryGetValue((ToTimestampScale(h.PostedAt), l.SecurityId!.Value), out var priced)
                        && priced.Currency is { } priceCurrency)
                        RateDay.Of(h.PostedAt, needs.ServerToday).Declare(needs, priceCurrency, LegCurrency(l.AccountId));
            var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

            // A share leg at market: the moved quantity at the unit value the position
            // at that instant is priced at, in its brokerage's currency. A leg that moves
            // no shares (the brokerage side, which is zero) keeps its amount, and so does a
            // leg whose security has neither a close nor a priced trade, which every
            // valuation carries at cost: there the market IS the basis. A leg whose price
            // needs a missing rate has no amount, only the reason. The value is a read-side
            // figure, so it keeps the 4-place bound every converted figure keeps, the scale
            // the positions it is a share of are valued at, and the screen presents it at
            // the currency's places (ADR-0114 D15).
            (decimal Amount, RateUnavailable? Why) AtMarket(
                DateTime postedAt, Guid accountId, Guid? securityId, decimal? quantity, decimal amount)
            {
                if (!siblingsInLedger.Contains(accountId)
                    || securityId is not { } security
                    || quantity is not { } moved
                    || moved == 0m
                    || !market.TryGetValue((ToTimestampScale(postedAt), security), out var held))
                    return (amount, null);
                var into = LegCurrency(accountId);
                var rate = held.Currency is { } from
                    ? RateDay.Of(postedAt, needs.ServerToday).Read(rates, from, into)
                    : ExchangeRate.Identity;
                return rate.Rate is { } r
                    ? (ConvertedTotal.Round(held.MarketValue * moved / held.Quantity * r), null)
                    : (0m, rate.Unavailable);
            }

            // One entry per (header, owning brokerage) rather than one per header.
            // The totals are identical — the amounts are the same legs, just grouped
            // one level finer — and it gives the flow an account to belong to, which
            // the per-account roster needs. A header's in-scope legs normally all
            // belong to one brokerage (its own sibling plus itself), so this is
            // usually a single entry anyway.
            //
            // A flow of zero is dropped. Valued, that is a move whose market value is
            // nil; unvalued (the estimate), a move that carries no amount AND moves no
            // shares, so the two agree on every transfer except one priced at exactly
            // zero, which the estimate still counts (an upper bound by that instant).
            flows.AddRange(spanning
                .SelectMany(h => h.Legs
                    .Where(l => perimeter.Contains(l.AccountId))
                    .GroupBy(l => OwningBrokerage(l.AccountId))
                    .Select(byAccount =>
                    {
                        var legs = byAccount
                            .Select(l => AtMarket(h.PostedAt, l.AccountId, l.SecurityId, l.Quantity, l.Amount))
                            .ToList();
                        return (
                            PostedAt: h.PostedAt,
                            Amount: legs.Sum(l => l.Amount),
                            AccountId: byAccount.Key,
                            Source: ContributionSources.InKindTransfers,
                            MovesShares: byAccount.Any(l => siblingsInLedger.Contains(l.AccountId)
                                                            && l.SecurityId != null
                                                            && l.Quantity is not (null or 0m)),
                            Unconverted: legs.Select(l => l.Why).FirstOrDefault(w => w is not null));
                    }))
                // A flow whose value needs a missing rate is kept, with no amount: the
                // figures it feeds are null, never a sum without it (ADR-0114 D9).
                .Where(x => x.Amount != 0m || x.Unconverted is not null || (!valueInKind && x.MovesShares))
                .Select(x => (x.PostedAt, x.Amount, x.AccountId, x.Source, x.Unconverted)));
        }

        // The earliest instant a scoped brokerage already HELD something, per its
        // own Start Date. opened_on is the as-of date of the opening balance, so it
        // marks an inception only when that balance is non-zero: with a zero
        // opening balance the account was empty then, and anchoring there would
        // hand TWR a zero invested base and turn a working figure into null. Most
        // Moneydance ledgers carry all history as transactions and leave every
        // opening balance at 0, so this is usually absent — but it must not be
        // ASSUMED absent, and the non-zero test is what makes it safe either way.
        //
        // It is the instant the balance arrives (above), and only one inside the window: a
        // balance that arrives after the window ends was not held in it, and anchoring
        // there would value the start after the end.
        var fundedFromOpening = Funded(brokerages)
            .Select(b => (DateTime?)OpenedInstant(b))
            .Where(o => o <= endValuedAt)
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
        //      endDate instead would collapse the window to zero length, which is
        //      not a window at all — every figure over it is undefined.
        //
        // Assets that arrive without a cash flow (an in-kind share transfer, a
        // category-funded posting) are absorbed into the start value rather than
        // read as return. That is right for a transfer, which is contributed
        // capital, and understates a dividend-only account — the open
        // investment-income-category question, not this one.
        //
        // WHERE the start is valued differs between the two kinds of window. An
        // explicit window opens at its start instant, so it is valued at the last
        // instant BEFORE it: everything dated at the start, a dividend or a
        // contribution alike, falls inside the window rather than into its opening
        // value. Since-inception is valued AT its anchor: for a scope funded in kind
        // or from a category the anchor is that first activity, and valuing before it
        // would start from nothing and read the whole opening capital as return. A
        // flow landing on the anchor is then in the start valuation, and ReturnsAsync
        // takes it back out so it counts once, as a flow.
        DateTime startDate;
        DateTime startValuedAt;
        if (fromUtc is { } windowStart)
        {
            startDate = windowStart;
            startValuedAt = LastInstantBefore(windowStart);
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
                                && r.PostedAt <= endValuedAt)
                    .Select(r => (DateTime?)r.PostedAt)
                    .MinAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Still null only when the scope has no activity at all — an untouched
            // account, correctly reported as having no measurable return.
            startDate = anchor ?? endDate;
            startValuedAt = anchor ?? endValuedAt;
        }

        // A funded opening balance is the brokerage's money from the instant it arrives
        // (above), and no posting brings it in. The valuation counts it only from then
        // (PortfolioValuation), so one that arrives inside the window is a flow there,
        // money in (or out, for a negative one): otherwise a brokerage opened partway
        // through the window would hold its balance before it existed, diluting every
        // sub-period it sat in, or, read from its start, would show the balance as
        // return. One that arrived by the time the start is valued is in the start value,
        // as it always was. An explicit window is valued before it opens, so one that
        // opens on the arrival takes the balance as its first-day flow, as it takes any
        // contribution dated at its start.
        flows.AddRange(Arrivals(brokerages)
            .Where(f => f.PostedAt > startValuedAt && f.PostedAt <= endValuedAt));
        return new ReturnsScope(
            scope, startDate, endDate, startValuedAt, endValuedAt, brokerages, brokerageIds,
            siblingIds, siblingToBrokerage, inPerimeter, flows);
    }

    /// <summary>
    /// Per (transfer instant, security), the position priced by the same rule the
    /// portfolio's boundary valuations use (<c>holdings_market_value_as_of_set</c>:
    /// the latest close on or before the instant's date, else the security's last
    /// priced trade, both restated onto the instant's split basis), in ONE batched
    /// read for every transfer. Of the positions the accounts hold at the instant it
    /// keeps the largest, so that scaling its value to a moved quantity loses the
    /// least to the value's four-decimal rounding.
    /// </summary>
    /// <remarks>
    /// Positions carried at cost basis (<c>priced_from = 'none'</c>: no close and no
    /// priced trade) are left out, so a moved quantity there falls back to the legs'
    /// own amount, which is the same carried basis. A position that is empty at the
    /// instant (everything moved on again at that instant) has no row and falls back
    /// the same way.
    /// <para>Each value carries the currency of the price it is at (migration 281), which the
    /// flow converts from.</para>
    /// </remarks>
    private async Task<Dictionary<(DateTime Instant, Guid SecurityId), (decimal Quantity, decimal MarketValue, string? Currency)>>
        PricedPositionsAtTransferAsync(
            Guid ledgerId,
            DateTime[] instants,
            Guid[] accountIds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<(DateTime, Guid), (decimal, decimal, string?)>();
        if (instants.Length == 0 || accountIds.Length == 0) return result;

        var rows = await _db.HoldingsMarketValueAsOfSet(ledgerId, instants, accountIds)
            .Where(r => r.PricedFrom != "none")
            .Select(r => new { r.AsOf, r.SecurityId, r.Quantity, r.MarketValue, r.PriceCurrency })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (row.Quantity == 0m) continue;
            var key = (ToTimestampScale(row.AsOf), row.SecurityId);
            if (!result.TryGetValue(key, out var kept) || Math.Abs(row.Quantity) > Math.Abs(kept.Item1))
                result[key] = (row.Quantity, row.MarketValue, row.PriceCurrency);
        }
        return result;
    }

    /// <summary>
    /// The earliest UTC day any returns series of the ledger can be <c>CoveredFrom</c>: the
    /// day a benchmark line can first be rebased on, so the day its growth and rates are
    /// needed from (ADR-0114 D3, D11). Null when no investment account has a posting or a
    /// funded opening balance.
    /// </summary>
    /// <remarks>
    /// <para>A series' <c>CoveredFrom</c> is the start of its first sub-period with a positive
    /// base (<see cref="ReturnsCalculator.TwrChain.CoveredFrom"/>), and every base is a
    /// valuation of the scope's brokerages: their cash, read from their postings and their
    /// opening balance, and their holdings siblings' positions, read from the siblings'
    /// postings (<see cref="PortfolioValuation"/>). Both kinds are investment accounts. So no
    /// series, of any scope or window, is invested before the first posting on an
    /// investment account or the start date of one carrying an opening balance, the same
    /// funded <c>opened_on</c> the since-inception anchor reads
    /// (<see cref="ResolveReturnsScopeAsync"/>). This is that day.</para>
    /// <para>It is not the first trade: a brokerage funded with cash weeks before it buys
    /// anything is invested from the funding, and its series is <c>CoveredFrom</c> there.
    /// Only a live posting counts, as the ledger's earliest day counts one
    /// (<see cref="NeededFxPairs.LedgerEarliestDayAsync"/>): not hidden, not merged away and
    /// not a reminder's template, whose date is the series' start, not a day any figure is
    /// on. The series read live postings alone, so none of the three can make one invested
    /// earlier, and a template dated early moves no start. <c>BenchmarkBackfillStartTests</c>
    /// holds every series' <c>CoveredFrom</c> to it.</para>
    /// <para>Two statements: the investment accounts, then the first live posting on them
    /// (<see cref="NeededFxPairs.FirstLivePostingOfFewAsync"/>), read from their legs by
    /// account and each leg's header by key, so it costs their own legs, not the ledger's
    /// history before its first investment.</para>
    /// </remarks>
    public static async Task<DateOnly?> FirstInvestedDayAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var investmentAccounts = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == InvestmentAccountType)
            .Select(a => new InvestmentAccountStart(a.Id, a.OpenedOn, a.OpeningBalance))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FirstInvestedDayOfAsync(db, ledgerId, investmentAccounts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="FirstInvestedDayAsync"/> over <paramref name="investmentAccounts"/>, every
    /// investment account of the ledger, already read: the earlier of the first live
    /// posting on any of them and the first start date of one carrying an opening balance.
    /// </summary>
    internal static async Task<DateOnly?> FirstInvestedDayOfAsync(
        AppDbContext db, Guid ledgerId, IReadOnlyCollection<InvestmentAccountStart> investmentAccounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(investmentAccounts);
        var posted = await NeededFxPairs.FirstLivePostingOfFewAsync(
                db, ledgerId, [.. investmentAccounts.Select(a => a.Id)], cancellationToken)
            .ConfigureAwait(false);
        var firstOpened = investmentAccounts
            .Where(a => a.OpenedOn is not null && a.OpeningBalance != 0m)
            .Min(a => a.OpenedOn);
        return posted is null ? firstOpened
            : firstOpened is null ? posted
            : firstOpened < posted ? firstOpened : posted;
    }

    /// <summary>A window bound at timestamptz's resolution, the microsecond: the
    /// ticks the column cannot hold are dropped, as the database drops them on the
    /// way in.</summary>
    private static DateTime ToTimestampScale(DateTime instant) =>
        new(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    /// <summary>The last instant timestamptz holds before <paramref name="bound"/>,
    /// itself at that scale, so an as-of read there (<c>posted_at &lt;= as_of</c>)
    /// admits exactly the rows dated before the bound.</summary>
    private static DateTime LastInstantBefore(DateTime bound) =>
        bound.AddTicks(-TimeSpan.TicksPerMicrosecond);

    /// <summary>The resolved scope of a returns report — see
    /// <see cref="ResolveReturnsScopeAsync"/>. <c>Brokerages</c> keeps the opening
    /// balance and opened_on the anchor rule needs; <c>Flows</c> are the external
    /// cash flows AND the in-kind headers that cross the perimeter, each already
    /// attributed to the brokerage it belongs to. <c>StartValuedAt</c> and
    /// <c>EndValuedAt</c> are the instants the two ends are valued at, which are not
    /// always <c>StartDate</c> and <c>EndDate</c>: an explicit window is valued
    /// before it opens, and an explicit end is exclusive. Each flow's amount is in its
    /// brokerage's currency; <c>Unconverted</c> is set, and the amount is not one, when an
    /// in-kind transfer's price needed a missing rate into it (ADR-0114 D9).</summary>
    private sealed record ReturnsScope(
        string Scope,
        DateTime StartDate,
        DateTime EndDate,
        DateTime StartValuedAt,
        DateTime EndValuedAt,
        IReadOnlyList<BrokerageRow> Brokerages,
        IReadOnlyList<Guid> BrokerageIds,
        IReadOnlyList<Guid> SiblingIds,
        IReadOnlyDictionary<Guid, Guid> SiblingToBrokerage,
        IReadOnlyList<Guid> InPerimeter,
        IReadOnlyList<(DateTime PostedAt, decimal Amount, Guid AccountId, string Source, RateUnavailable? Unconverted)> Flows);

    /// <summary>A scoped brokerage: identity, the two fields the since-inception anchor
    /// rule reads, and the currency its figures are in. <c>OpeningArrives</c> is set, by
    /// <see cref="ResolveReturnsScopeAsync"/>, for a funded opening balance only: the
    /// instant the brokerage holds it from.</summary>
    private sealed record BrokerageRow(
        Guid Id, string Name, Guid? HoldingsAccountId, decimal OpeningBalance, DateOnly? OpenedOn,
        string CurrencyCode)
    {
        public DateTime? OpeningArrives { get; init; }
    }

    /// <summary>
    /// How many portfolio valuations a time-weighted return would need for this
    /// window, without performing any of them. Runs the cheap half of a returns
    /// call — scope, flow classification, in-kind detection, anchor — and stops.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can size a request instead of discovering its cost by
    /// waiting for it. It reports the instant count and nothing about a ceiling,
    /// because there is no ceiling: migrations 200 and 201 batch both halves of a
    /// boundary valuation, so an instant cap has nothing to protect.
    /// <para>
    /// For the same reason there is no per-request cap override. A cap refuses TWR
    /// outright rather than approximating it, so a lower cap buys a faster null,
    /// not a coarser number.
    /// </para>
    /// </remarks>
    public Task<ReturnsCostEstimate> ReturnsCostEstimateAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateOnly? today,
        CancellationToken cancellationToken = default) =>
        ReturnsCostEstimateAsync(
            ledgerId, accountId, fromUtc, toUtc,
            ScheduledBoundary.LastCountedInstant(today, _clock), cancellationToken);

    /// <inheritdoc cref="ReturnsCostEstimateAsync(Guid, Guid?, DateTime?, DateTime?, DateOnly?, CancellationToken)"/>
    /// <param name="nowUtc">The instant a window with no end ends at.</param>
    public async Task<ReturnsCostEstimate> ReturnsCostEstimateAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var s = await ResolveReturnsScopeAsync(
            ledgerId, accountId, fromUtc, toUtc, nowUtc, cancellationToken, valueInKind: false)
            .ConfigureAwait(false);

        // Distinct INSTANTS, not flows: several flows landing together are valued
        // once.
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
    /// same amount at the same instant, so it never distorts the return (a
    /// securities-only basis reports phantom losses when contributed cash sits
    /// uninvested).
    ///
    /// Money-weighted (XIRR) runs over the external contributions/withdrawals plus
    /// the start + end portfolio value. Time-weighted (TWR) chains sub-period
    /// returns across the external-flow instants, each valued the same way, skipping
    /// the sub-periods with no invested base. Each is null with a reason when it has
    /// no answer: TWR when no sub-period held anything, when the invested stretches
    /// span no time, or when the holdings ended a sub-period worth less than nothing;
    /// XIRR when it has fewer than two dated flows, no elapsed time, flows that all
    /// run one way or offset exactly, or no rate in the range it searches.
    ///
    /// Gain is the window's change in value that contributions and withdrawals do
    /// not explain: price movement, realized or not, plus dividends and interest,
    /// less fees and investment expenses; not annualized; for the whole scope. It is
    /// end value − start value − net contributions, from the figures the report
    /// already holds.
    ///
    /// External cash flow = a brokerage leg facing a real account OUTSIDE the
    /// report's own scope. This is deliberately scope-relative: a rollover from one
    /// brokerage to another is INTERNAL at ledger scope (it nets to zero) but is a
    /// real withdrawal from the source account and a real contribution to the
    /// destination when either is reported on its own. Classifying by account type
    /// instead — treating every 'investment' counterparty as internal at both
    /// scopes — would make an account funded entirely by rollover report the whole
    /// step-up as performance. Internally-reinvested dividends and in-brokerage
    /// trades face the holdings sibling, which is inside the perimeter at every
    /// scope, so they are never external.
    ///
    /// A CATEGORY counterparty is external only when the leg's posting role is
    /// <c>transfer</c>. Roles <c>income</c> (dividends, interest, and investment
    /// expenses — ADR-0027 puts direction in the sign, not the role) and
    /// <c>fee</c> are the portfolio's own earnings and costs, so they stay inside
    /// the return on a net-of-fees basis. Without that qualifier the choice is
    /// all-or-nothing and both answers are wrong: excluding every category makes
    /// an employer retirement contribution read as investment skill, while
    /// including them reclassifies the ledger's entire dividend and interest
    /// history as contributed money.
    ///
    /// An explicit window is valued at the last instant before its start and at the
    /// last instant it counts, so an event dated on an edge lands in exactly one
    /// window, and a flow valued into the end value is always one of the window's
    /// flows (the window's edges, on <see cref="ReturnsResult"/>).
    ///
    /// Since-inception (no window) anchors on a funded opening balance's start date,
    /// else on the first external flow, else on the first activity anywhere in
    /// scope, so an account funded in-kind or from a category still gets a real
    /// window instead of a zero-length one. It is valued AT the anchor: assets that
    /// arrived without a cash flow, there or before it, land in the start value
    /// rather than reading as return, and a flow on the anchor is taken back out so
    /// it counts once. Only a scope with no activity at all yields a zero-length
    /// window, and then both figures are null with a reason.
    /// </summary>
    /// <remarks>
    /// This overload takes the caller's local date: a window with no end ends at the
    /// last instant the register does not call Scheduled, as every current value does,
    /// so its end value is the overview's investments value for the same date. Null is
    /// the register's fallback, which is what MCP gets.
    /// </remarks>
    public Task<ReturnsResult> ReturnsAsync(
        Guid ledgerId,
        Guid? accountId,
        DateTime? fromUtc,
        DateTime? toUtc,
        DateOnly? today,
        CancellationToken cancellationToken = default) =>
        ReturnsAsync(
            ledgerId, accountId, fromUtc, toUtc,
            ScheduledBoundary.LastCountedInstant(today, _clock), cancellationToken);

    /// <inheritdoc cref="ReturnsAsync(Guid, Guid?, DateTime?, DateTime?, DateOnly?, CancellationToken)"/>
    /// <param name="nowUtc">The instant a window with no end ends at.</param>
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

        // The figures' currency (ADR-0114 D7): one brokerage's return is a per-account
        // figure, in its own currency; the ledger's combines brokerages, in the reporting
        // currency. Every value and every flow converts before anything adds them: each
        // brokerage at each instant at that instant's day's rate (the latest for now), a
        // position priced in another currency into its brokerage's first; each flow at its
        // date's rate. So neither rate ever runs over a sum of currencies, and the ledger
        // line is unhedged.
        var currencyOf = brokerages.ToDictionary(b => b.Id, b => b.CurrencyCode);
        var target = accountId is { } scoped
            ? currencyOf[scoped]
            : await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);

        // Time-weighted boundaries: the window start, each external-flow instant, and
        // the window end. Interior boundaries only are valued as boundaries: the window
        // ends keep the values valued for them below — the start is value-BEFORE-flow
        // and the end is the report's own endValue less any flow at that instant, so
        // re-deriving them would risk the two drifting apart.
        var flowInstants = flows.Select(x => x.PostedAt).Distinct().ToList();
        var boundaryDates = new SortedSet<DateTime>(flowInstants) { startDate, endDate };
        var interior = boundaryDates
            .Where(d => d != startDate && d != endDate)
            .Select(AsUtc)
            .ToArray();

        // EVERY valuation in TWO queries, securities (mig 200) and cash (mig 201), and
        // every rate in one load: the window's two ends and each interior boundary. A
        // feeder that replays its whole input per instant makes a whole-portfolio
        // time-weighted return cost boundaries × history, so both halves are batched.
        // Valued per brokerage and summed here rather than in the database, so the
        // roster below and the totals are the same numbers.
        var valuation = await PortfolioValuation.LoadAsync(
                _db, Rates, ledgerId, brokerageIds, siblingToBrokerage, currencyOf, Openings(brokerages),
                [.. interior, AsUtc(s.StartValuedAt), AsUtc(s.EndValuedAt)], target, flows, null, cancellationToken)
            .ConfigureAwait(false);

        // Every missing rate any figure needed, named once (ADR-0114 D9).
        var missing = new List<RateUnavailable>();
        decimal? Track(Converted figure)
        {
            foreach (var why in figure.Unavailable)
                if (!missing.Contains(why)) missing.Add(why);
            return figure.Amount;
        }

        // Each flow in the figures' currency, at its date; null when its rate is missing.
        var flowValues = flows.Select(f => Track(valuation.Flow(f, target))).ToList();

        // Σ external flow at an instant (money into the brokerage). The value
        // BEFORE a flow subtracts it: the cash it just added is a contribution,
        // not yet return. A withdrawal (negative) is added back symmetrically.
        decimal? ContributionAt(DateTime d) =>
            SumOrNull(flows.Select((x, i) => (x, i)).Where(p => p.x.PostedAt == d).Select(p => flowValues[p.i]));

        // The window's own flows that the start valuation already holds, taken back
        // out so each counts once, as a flow. An explicit window is valued before it
        // opens and holds none of them; since-inception is valued AT its anchor and
        // holds any flow landing there.
        var heldAtStart = flows.Select((x, i) => (x, i)).Where(p => p.x.PostedAt <= s.StartValuedAt).ToList();
        var startValue = Track(valuation.In(AsUtc(s.StartValuedAt), brokerageIds, target))
                         - SumOrNull(heldAtStart.Select(p => flowValues[p.i]));
        var endValue = Track(valuation.In(AsUtc(s.EndValuedAt), brokerageIds, target));

        // Per-account roster (ledger scope only). Each row's start value is backed
        // out of that account's OWN flows the start valuation holds, matching how the
        // report's own start value is defined — otherwise the rows would miss the
        // total by exactly that flow, which is not a corner case: since-inception
        // anchors ON the first flow date, so every since-inception report would
        // have shown rows that do not add up.
        //
        // Each row is that brokerage's own figure, in its own currency (ADR-0114 D7), so
        // the rows sum to the totals once each is converted at the same instant's rate.
        IReadOnlyList<ReturnsAccountValue>? accountRows = null;
        if (accountId is null)
        {
            var startFlowByAccount = heldAtStart
                .GroupBy(p => p.x.AccountId)
                .ToDictionary(g => g.Key, g => SumOrNull(g.Select(p => p.x.Unconverted is null ? p.x.Amount : (decimal?)null)));

            // Held something at one end, or money moved through it. An account that
            // was worth nothing at both ends AND saw no flow was not covered by this
            // window in any sense a reader cares about, and every closed account a
            // ledger keeps would otherwise be a row of zeroes. Note what this rule
            // is NOT: "non-zero balance today", the rule that lost the whole opening
            // value of every drained account. Both endpoints count, so an account emptied inside the window
            // stays. The sum invariant is untouched — every row this drops
            // contributes zero to both columns.
            var accountsWithFlows = flows.Select(x => x.AccountId).ToHashSet();

            accountRows = brokerages
                .Select(b =>
                {
                    var start = valuation.Native(AsUtc(s.StartValuedAt), b.Id);
                    var end = valuation.Native(AsUtc(s.EndValuedAt), b.Id);
                    var heldFlows = startFlowByAccount.TryGetValue(b.Id, out var held) ? held : 0m;
                    var unconverted = heldAtStart
                        .Where(p => p.x.AccountId == b.Id && p.x.Unconverted is not null)
                        .Select(p => p.x.Unconverted!);
                    // Ordered by what each is worth in the figures' currency.
                    var order = valuation.In(AsUtc(s.EndValuedAt), [b.Id], target).Amount;
                    return (Order: order, Row: new ReturnsAccountValue(
                        b.Id,
                        b.Name,
                        start.Amount - heldFlows,
                        end.Amount,
                        b.CurrencyCode,
                        RateReasons.Of(start.Unavailable, end.Unavailable, unconverted)));
                })
                .Where(r => r.Row.StartValue != 0m
                            || r.Row.EndValue != 0m
                            || accountsWithFlows.Contains(r.Row.AccountId))
                .OrderByDescending(r => r.Order)
                .ThenBy(r => r.Row.AccountName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Row.AccountId)
                .Select(r => r.Row)
                .ToList();
            foreach (var row in accountRows) Track(new Converted(null, row.RateUnavailable));
        }

        var flowsConverted = flowValues.All(v => v is not null);

        // Money-weighted (investor perspective: money-in negative). The solver hands
        // back WHY it could not produce a rate, so a blank money-weighted figure is
        // explained the same way a blank time-weighted one always was. A figure that
        // needs a missing rate is not solved over a partial sum.
        double? irr = null;
        string? irrReason = ReturnsRateUnavailableReason;
        if (startValue is { } sv && endValue is { } ev && flowsConverted)
        {
            var cf = new List<ReturnsCalculator.CashFlow>();
            if (sv != 0m) cf.Add(new(startDate, -sv));
            for (var i = 0; i < flows.Count; i++) cf.Add(new(flows[i].PostedAt, -flowValues[i]!.Value));
            cf.Add(new(endDate, ev));
            var irrResult = ReturnsCalculator.Xirr(cf);
            irr = irrResult.Rate;
            irrReason = XirrReason(irrResult.Outcome);
        }

        static string? XirrReason(ReturnsCalculator.XirrOutcome outcome) => outcome switch
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

        var netContributions = SumOrNull(flowValues);

        // The same flows, un-netted. Signs stay in the netContributions convention —
        // positive is money INTO the scope — so the identity is a plain addition:
        // net == in + out, with out being zero or negative. No magnitudes, no
        // subtraction to get backwards. A rate is positive, so a converted flow keeps
        // its sign; one whose amount is unknown leaves both halves unknown.
        decimal? Half(IEnumerable<int> indexes, bool into) => SumOrNull(indexes
            .Where(i => flows[i].Unconverted is not null || (into ? flows[i].Amount > 0m : flows[i].Amount < 0m))
            .Select(i => flowValues[i]));
        var allFlows = Enumerable.Range(0, flows.Count).ToList();
        var contributionsIn = Half(allFlows, into: true);
        var contributionsOut = Half(allFlows, into: false);

        // What the flows do not explain: the change in value less the money that
        // crossed the scope's boundary. Positive netContributions is money IN, so it
        // is subtracted. Scope-relative for the same reason the flows are, which is
        // why the roster rows carry no gain.
        var gain = endValue - startValue - netContributions;
        var contributionsBySource = allFlows
            .GroupBy(i => flows[i].Source)
            .Select(g => new ContributionSourceTotals(g.Key, Half(g, into: true), Half(g, into: false)))
            // A source whose halves need a missing rate sorts last.
            .OrderByDescending(s => s.In is { } i && s.Out is { } o ? Math.Abs(i) + Math.Abs(o) : (decimal?)null)
            .ThenBy(s => s.Source, StringComparer.Ordinal)
            .ToList();

        // Time-weighted: a boundary at the window start, each external-flow instant,
        // and the window end — each valued at the same cash+securities basis. The
        // calculator chains and annualizes over the sub-periods that had money in
        // them, skipping the ones that did not; we surface the covered span and a
        // reason for each way the chain can come back empty. The number of flow
        // instants is not capped: every interior boundary was valued in the two
        // batched queries above. A boundary that needs a missing rate leaves the chain
        // unbuilt rather than chained over a partial sum.
        double? twr = null;
        string? twrReason = ReturnsRateUnavailableReason;
        DateTime? twrFrom = null;
        DateTime? twrTo = null;
        double? twrYears = null;
        int? twrDays = null;

        List<ReturnsCalculator.Boundary>? boundaries = new(boundaryDates.Count);
        foreach (var d in boundaryDates)
        {
            decimal? value;
            if (d == startDate) value = startValue;        // already value-before-flow
            // The default end counts its own instant, so a flow can land there; the
            // last sub-period's return is measured on the value before it, as every
            // interior boundary's is. An explicit end holds no flow (the window stops
            // before it), so this subtracts nothing there.
            else if (d == endDate) value = endValue - ContributionAt(d);
            else value = Track(valuation.In(AsUtc(d), brokerageIds, target)) - ContributionAt(d);
            var flow = ContributionAt(d);
            if (value is not { } v || flow is not { } fl) { boundaries = null; break; }
            boundaries.Add(new ReturnsCalculator.Boundary(d, v, -fl));
        }
        if (boundaries is not null)
        {
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
            // this from what else is returned. A report that turned a fractional year
            // into a count of weeks, and came out weeks short of the real number of
            // days, is the error this closes.
            twrDays = twrYears is { } y ? (int)Math.Round(y * 365.0) : null;
            twrReason = TwrReason(twrResult.Outcome);
        }

        static string? TwrReason(ReturnsCalculator.TwrOutcome outcome) => outcome switch
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
            scope, startDate, endDate, startValue, endValue, netContributions, gain,
            irr, irrReason, twr, twrReason, twrFrom, twrTo, twrYears, twrDays, accountRows,
            contributionsIn, contributionsOut, contributionsBySource,
            target, missing,
            ComputedAt, EngineVersion);
    }

    /// <summary>Why a returns rate is null when a figure it runs over needs a missing
    /// exchange rate (ADR-0114 D9).</summary>
    private const string ReturnsRateUnavailableReason =
        "An exchange rate this figure needs is missing (rateUnavailable names each pair and date), " +
        "and a return is never computed over a partial sum.";

    /// <summary>The sum of <paramref name="figures"/>; null when any one is, never a partial
    /// sum (ADR-0114 D9).</summary>
    private static decimal? SumOrNull(IEnumerable<decimal?> figures)
    {
        var sum = 0m;
        foreach (var f in figures)
        {
            if (f is not { } v) return null;
            sum += v;
        }
        return sum;
    }

    // The most period ends one series request values. Every period end is read from the
    // same two batched valuations, so the cap bounds the size of the response and of the
    // rows those valuations return, not a count of queries.
    private const int MaxSeriesPeriodEnds = 600;

    /// <summary>
    /// Returns over time (ADR-0104 D6): the time-weighted index, the cumulative gain and
    /// the money-weighted return of the 12 months ending there, at the end of each
    /// <paramref name="interval"/> period in [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>], the last period end clamped to the caller's
    /// <paramref name="today"/>. A null <paramref name="fromUtc"/> is since inception:
    /// the series starts at the scope's own anchor (see
    /// <see cref="ResolveReturnsScopeAsync"/>), and a null <paramref name="toUtc"/> ends
    /// at the caller's today.
    /// </summary>
    /// <param name="accountId">One brokerage's own series, or null for the ledger's.</param>
    /// <param name="includeAccounts">With no <paramref name="accountId"/>, also one series
    /// per brokerage that held something in the window, after the ledger's.</param>
    /// <param name="today">The caller's local date; the last period end is no later than
    /// the last instant the register does not call Scheduled, as for <see cref="ReturnsAsync(Guid, Guid?, DateTime?, DateTime?, DateOnly?, CancellationToken)"/>.</param>
    /// <exception cref="ReportRequestException">The interval is a day, or the range holds
    /// more than 600 period ends.</exception>
    /// <remarks>
    /// Each series is the chain <see cref="ReturnsAsync(Guid, Guid?, DateTime?, DateTime?, DateTime, CancellationToken)"/>
    /// runs for the window [from, last period end], from the same scope
    /// (<see cref="ResolveReturnsScopeAsync"/>, the one place the perimeter and the flow
    /// classification live) and the same boundaries, so the last point's index is that
    /// report's time-weighted growth before it is annualized. The chain runs on the flow
    /// instants only. A period end never becomes a boundary: it reads the cumulative
    /// factor (<see cref="ReturnsCalculator.TwrChain.ReadAt"/>), so asking for months or
    /// for years changes no number the other reads.
    /// <para>The portfolio is valued once, at the flow instants and the period ends of every
    /// series together, in the two batched feeder reads, and every series takes its values
    /// from those rows. The whole read runs in one REPEATABLE READ snapshot, so the scope
    /// resolution and the valuations describe one state of the ledger.</para>
    /// <para>The ledger series is in the reporting currency, unhedged: each brokerage's value
    /// and flows convert at each instant's rate before the chain runs (ADR-0114 D7). Each
    /// brokerage's own series is in its own currency. A point that needs a missing rate is
    /// null with the <c>rate_unavailable</c> reason and names it (ADR-0114 D9).</para>
    /// <para>With a <paramref name="benchmark"/>, every point of every series also carries that
    /// benchmark's total return, chained over the series' own invested time, rebased to 100
    /// at its first invested point and converted into its currency, unhedged (ADR-0114 D3;
    /// the rule is <see cref="BenchmarkLine"/>'s). A symbol that is not one of the ledger's
    /// benchmarks is not refused: the result names it with
    /// <c>benchmark_not_in_ledger</c> and no point has a value, so a saved report that keeps
    /// a removed pick still reads (ADR-0114 D1). The benchmark adds one statement for its row,
    /// coverage and first day, at most one for its growth, and no rate load: its rates ride
    /// the series' one load.</para>
    /// </remarks>
    /// <param name="benchmark">A benchmark symbol, or null for none.</param>
    public Task<ReturnsSeriesResult> ReturnsSeriesAsync(
        Guid ledgerId,
        Guid? accountId,
        bool includeAccounts,
        DateTime? fromUtc,
        DateTime? toUtc,
        ReportTimeBucket interval,
        DateOnly? today,
        CancellationToken cancellationToken = default,
        string? benchmark = null) =>
        ReturnsSeriesAsync(
            ledgerId, accountId, includeAccounts, fromUtc, toUtc, interval,
            ScheduledBoundary.LastCountedInstant(today, _clock), cancellationToken, benchmark);

    /// <inheritdoc cref="ReturnsSeriesAsync(Guid, Guid?, bool, DateTime?, DateTime?, ReportTimeBucket, DateOnly?, CancellationToken, string?)"/>
    /// <param name="nowUtc">The latest instant the last period end may be.</param>
    public async Task<ReturnsSeriesResult> ReturnsSeriesAsync(
        Guid ledgerId,
        Guid? accountId,
        bool includeAccounts,
        DateTime? fromUtc,
        DateTime? toUtc,
        ReportTimeBucket interval,
        DateTime nowUtc,
        CancellationToken cancellationToken = default,
        string? benchmark = null)
    {
        // None means "unspecified", and month is the documented default. Day is refused,
        // not served as months: a caller who asks for days and is handed months cannot
        // tell from the response, and a daily series is also the one shape the period
        // cap cannot carry.
        var bucket = interval == ReportTimeBucket.None ? ReportTimeBucket.Month : interval;
        if (bucket == ReportTimeBucket.Day)
            throw new ReportRequestException(
                "interval must be month, quarter or year; a daily returns series is not supported.");

        var now = ToTimestampScale(AsUtc(nowUtc));
        var requestedTo = toUtc is { } rawTo ? ToTimestampScale(AsUtc(rawTo)) : now;

        // The period ends of a window, refused past the cap. An explicit window is
        // known before any read, so it is refused before the snapshot opens or a row
        // is touched; a since-inception window is only known once its scope's anchor
        // is, which is one scope resolution (the cost of the scalar report over the
        // same window), and is refused there, before the per-account scopes and the
        // valuations that are the expensive part.
        List<DateTime> PeriodEndsWithinCap(DateTime windowFrom, DateTime windowTo)
        {
            var found = AccountsReportingRepository.PeriodEnds(windowFrom, windowTo, bucket)
                .Take(MaxSeriesPeriodEnds + 1)
                .ToList();
            if (found.Count > MaxSeriesPeriodEnds)
                throw new ReportRequestException(
                    $"That range and interval would produce more than {MaxSeriesPeriodEnds} period ends; "
                    + "widen the interval (quarter or year) or narrow the range.");
            return found;
        }

        DateTime from;
        DateTime to;
        List<DateTime>? explicitEnds = null;
        if (fromUtc is { } rawFrom)
        {
            from = ToTimestampScale(AsUtc(rawFrom));
            to = requestedTo;
            if (to < from) (from, to) = (to, from);
            if (now < to) to = now < from ? from : now;
            explicitEnds = PeriodEndsWithinCap(from, to);
        }
        else
        {
            to = now < requestedTo ? now : requestedTo;
            from = default;
        }

        // The snapshot opens before the first read so the since-inception anchor below
        // and the valuations describe one state of the ledger.
        await using var snapshot = await ReadSnapshot.BeginAsync(_db, cancellationToken, requireRepeatableRead: true)
            .ConfigureAwait(false);

        ReturnsScope? inceptionScope = null;
        if (explicitEnds is null)
        {
            // Since inception: the series starts at the scope's own anchor, the one
            // ResolveReturnsScopeAsync derives for a window with no start. The last
            // period end is the end bound itself, so that resolution already describes
            // the window and is the series' own scope, valued at the anchor.
            inceptionScope = await ResolveReturnsScopeAsync(
                ledgerId, accountId, null, null, to, cancellationToken).ConfigureAwait(false);
            from = inceptionScope.StartDate;
        }

        var ends = explicitEnds ?? PeriodEndsWithinCap(from, to);

        // Every period end at timestamptz's resolution, and keyed on those values
        // below. A period end is the tick before the next period starts, which the
        // column rounds on the way in, so a value keyed on the tick would miss the row
        // that comes back and read the whole period as an empty portfolio. It is the last
        // instant of its day, never midnight: a price is looked up by the instant's date.
        var marks = ends.Select(e => ToTimestampScale(AsUtc(e))).Distinct().ToArray();
        var lastMark = marks[^1];

        // Each point's money-weighted return is the 12 months ending at it (ADR-0104 D6,
        // amended 2026-11-21), so an explicit window reads the year of history before it:
        // its scopes are resolved from the first point's year and each keeps, as its own
        // window, only what falls in [from, lastMark]. A scope's perimeter and flow
        // classification do not depend on its start, so the window's scope derived this
        // way is the one resolved from `from` itself. Since inception, there is nothing
        // invested before the anchor, and the window's scope is its own lookback.
        var lookbackFrom = inceptionScope is null ? Earlier(from, TrailingYearStart(marks[0])) : (DateTime?)null;

        // The window is [from, lastMark]: valued before it opens, and at the last instant
        // it counts, which an end with no explicit bound counts itself.
        var primaryLookback = inceptionScope ?? await ResolveReturnsScopeAsync(
            ledgerId, accountId, lookbackFrom, null, lastMark, cancellationToken).ConfigureAwait(false);
        var primary = inceptionScope ?? Within(primaryLookback, from);

        // The ledger line combines brokerages, so it is in the reporting currency, each
        // brokerage's value and flows converted at each instant before the chain runs
        // (ADR-0114 D7: unhedged). A brokerage's own line is a per-account figure, in its
        // own currency.
        var currencyOf = primary.Brokerages.ToDictionary(b => b.Id, b => b.CurrencyCode);
        var primaryCurrency = accountId is { } scoped
            ? currencyOf[scoped]
            : await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var lines = new List<SeriesLine>
        {
            new(primary, primaryLookback, accountId is null ? "ledger" : "account", accountId,
                primary.Brokerages.SingleOrDefault(b => b.Id == accountId)?.Name,
                primaryCurrency, KeepWhenEmpty: true),
        };
        if (accountId is null && includeAccounts)
        {
            foreach (var b in primary.Brokerages
                         .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(b => b.Id))
            {
                // Since inception each brokerage anchors on its own first funding.
                var lookback = await ResolveReturnsScopeAsync(
                    ledgerId, b.Id, lookbackFrom, null, lastMark, cancellationToken)
                    .ConfigureAwait(false);
                var scope = inceptionScope is null ? Within(lookback, from) : lookback;
                lines.Add(new(scope, lookback, "account", b.Id, b.Name, b.CurrencyCode, KeepWhenEmpty: false));
            }
        }

        // The benchmark asked for, if any, read before the valuation so its conversions ride
        // the one rate load: its row, coverage and first growth day in one statement.
        BenchmarkInLedger? bench = null;
        ReturnsBenchmark? benchmarkResult = null;
        if (benchmark is { } symbol)
        {
            bench = await BenchmarkInLedger.FindAsync(_db, ledgerId, symbol, cancellationToken).ConfigureAwait(false);
            benchmarkResult = bench is null
                ? new ReturnsBenchmark(symbol, null, null, ReturnsSeriesReasons.BenchmarkNotInLedger)
                : new ReturnsBenchmark(bench.Symbol, bench.Name, bench.CurrencyCode, null);
        }

        // A benchmark in another currency converts into each series' currency at every
        // instant a sub-period of that series can start or end at (its start, its end, each
        // flow) and at every period end (ADR-0114 D3).
        void DeclareBenchmarkRates(ExchangeRateNeeds needs)
        {
            if (bench is null) return;
            foreach (var line in lines)
            {
                if (string.Equals(bench.CurrencyCode, line.CurrencyCode, StringComparison.Ordinal)) continue;
                void Declare(DateTime instant) =>
                    RateDay.Of(instant, needs.ServerToday).Declare(needs, bench.CurrencyCode, line.CurrencyCode);
                Declare(line.Scope.StartDate);
                Declare(line.Scope.EndDate);
                foreach (var flow in line.Scope.Flows) Declare(flow.PostedAt);
                foreach (var mark in marks) Declare(mark);
            }
        }

        // One valuation for every series: the flow instants of each, its start, and the
        // period ends, and every rate they convert at, in one load. The primary line's
        // flows are declared into its currency; a brokerage's own line converts nothing
        // but its positions priced in another currency.
        var instants = new SortedSet<DateTime>(marks);
        foreach (var line in lines)
        {
            instants.Add(line.Scope.StartValuedAt);
            foreach (var flow in line.Scope.Flows) instants.Add(ToTimestampScale(AsUtc(flow.PostedAt)));
        }
        // The value before each point's year opens, for its money-weighted return; the
        // year's flows convert at their own dates, declared with the lookback's flows.
        foreach (var mark in marks) instants.Add(TrailingYearValuedAt(mark));
        // A funded opening balance a since-inception scope holds at its anchor is no flow of
        // the scope, but a year that opens before the anchor takes it as money in, at its
        // date's rate (BuildSeries); declared with the flows.
        var valuation = await PortfolioValuation.LoadAsync(
                _db, Rates, ledgerId, primary.BrokerageIds, primary.SiblingToBrokerage, currencyOf,
                Openings(primary.Brokerages), instants, primaryCurrency,
                primaryLookback.Flows.Concat(Arrivals(primaryLookback.Brokerages)), DeclareBenchmarkRates,
                cancellationToken)
            .ConfigureAwait(false);

        var built = lines
            .Select(line => (Line: line, Built: BuildSeries(line, marks, valuation, cancellationToken)))
            .ToList();

        var drawn = await DrawBenchmarkAsync(ledgerId, bench, benchmarkResult, built, marks, valuation, cancellationToken)
            .ConfigureAwait(false);

        var series = new List<ReturnsSeries>(lines.Count);
        foreach (var ((line, _), points) in built.Zip(drawn))
        {
            if (line.KeepWhenEmpty || points.Any(p => p.Index is not null))
                series.Add(new ReturnsSeries(line.Name, line.AccountId, line.AccountName, line.CurrencyCode, points));
        }

        return new ReturnsSeriesResult(
            from, to, bucket.ToString().ToLowerInvariant(), series, benchmarkResult, ComputedAt, EngineVersion);
    }

    /// <summary>The ledger's benchmark symbols, in the order the Benchmark control lists
    /// them (ADR-0114 D1): what a returns series can be compared with.</summary>
    public async Task<IReadOnlyList<string>> BenchmarkSymbolsAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.LedgerBenchmarks.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.Position)
            .Select(b => b.Symbol)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Each series' points with the benchmark beside them (<see cref="BenchmarkLine"/>): as
    /// built when none was asked for, with no value and <c>benchmark_not_in_ledger</c> when
    /// the symbol is not one of the ledger's, else drawn from the benchmark's growth, read in
    /// one statement over every series' days at once.
    /// </summary>
    private async Task<List<IReadOnlyList<ReturnsSeriesPoint>>> DrawBenchmarkAsync(
        Guid ledgerId,
        BenchmarkInLedger? bench,
        ReturnsBenchmark? asked,
        IReadOnlyList<(SeriesLine Line, (IReadOnlyList<ReturnsSeriesPoint> Points, ReturnsCalculator.TwrChain Chain) Built)> built,
        IReadOnlyList<DateTime> marks,
        PortfolioValuation valuation,
        CancellationToken cancellationToken)
    {
        if (asked is null) return [.. built.Select(b => b.Built.Points)];
        if (bench is null)
            return [.. built.Select(b => BenchmarkLine.Without(b.Built.Points, ReturnsSeriesReasons.BenchmarkNotInLedger))];

        // The days every series measures between: from the earliest first invested day that
        // has a value to the last period end the fetches reach. Outside them no point draws.
        var coveredDays = built
            .Select(b => b.Built.Chain.CoveredFrom)
            .OfType<DateTime>()
            .Select(d => BenchmarkGrowthTable.CloseDay(bench, DateOnly.FromDateTime(d), valuation.ServerToday))
            .ToList();
        var loadFrom = coveredDays.Count == 0 ? (DateOnly?)null : coveredDays.Min();
        if (loadFrom is { } lf && bench.FirstGrowthDay is { } first && lf < first) loadFrom = first;
        var loadTo = DateOnly.FromDateTime(marks[^1]);
        if (bench.FetchedThrough is { } through && through < loadTo) loadTo = through;

        // No series was invested, or the benchmark has no history: an empty range reads
        // nothing, and every point that needs a day says it is missing.
        var growth = await BenchmarkGrowthTable.LoadAsync(
                _db, ledgerId, bench, loadFrom ?? loadTo, loadTo, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. built.Select(b => BenchmarkLine.Draw(
                b.Built.Points, b.Built.Chain, bench, growth, valuation.Rates, valuation.ServerToday,
                b.Line.CurrencyCode)),
        ];
    }

    /// <summary>One line of a series request: the scope it is computed from and how it
    /// is labelled. <c>Lookback</c> is the same scope from the first point's year, whose
    /// flows each point's 12-month money-weighted return reads; since inception it is
    /// <c>Scope</c> itself. <c>KeepWhenEmpty</c> is false for a brokerage listed beside
    /// the ledger, which is dropped when no period end has an index, so that a closed
    /// account is not a line of reasons.</summary>
    private sealed record SeriesLine(
        ReturnsScope Scope, ReturnsScope Lookback, string Name, Guid? AccountId, string? AccountName,
        string CurrencyCode, bool KeepWhenEmpty);

    /// <summary>A scope resolved from an earlier start, as the scope of the window that
    /// opens at <paramref name="from"/>: valued before it opens, with its own flows only.
    /// The perimeter and the flow classification do not depend on the start, and an
    /// explicit start filters flows by the instant they post at, so this is the scope
    /// <see cref="ResolveReturnsScopeAsync"/> resolves from <paramref name="from"/>.</summary>
    private static ReturnsScope Within(ReturnsScope lookback, DateTime from) => lookback with
    {
        StartDate = from,
        StartValuedAt = LastInstantBefore(from),
        Flows = [.. lookback.Flows.Where(f => f.PostedAt >= from)],
    };

    /// <summary>The instant the 12 months ending at a period end open: the same point a
    /// year earlier, just after the period end then. A period end is the last instant of
    /// its period, so this is the start of the period that followed it a year back, which
    /// keeps a February end on February's last day in a leap year and out of it.</summary>
    private static DateTime TrailingYearStart(DateTime mark) =>
        ToTimestampScale(AsUtc(mark).AddTicks(TimeSpan.TicksPerMicrosecond).AddYears(-1));

    /// <summary>Where a point's year is valued as it opens: the instant before it opens,
    /// as an explicit window is (<see cref="ResolveReturnsScopeAsync"/>).</summary>
    private static DateTime TrailingYearValuedAt(DateTime mark) => LastInstantBefore(TrailingYearStart(mark));

    private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;

    /// <summary>The instant a funded opening balance arrives: the start of its start date,
    /// or the brokerage's first posting when that is earlier
    /// (<see cref="ResolveReturnsScopeAsync"/>); the instant the since-inception anchor
    /// reads it at.</summary>
    private static DateTime OpenedInstant(BrokerageRow b) => b.OpeningArrives!.Value;

    /// <summary>The brokerages with a funded opening balance: a non-zero balance and a
    /// start date it is as of. One with no start date has no day it arrived, so it is
    /// held at every instant, as it always was.</summary>
    private static IEnumerable<BrokerageRow> Funded(IEnumerable<BrokerageRow> brokerages) =>
        brokerages.Where(b => b.OpeningArrives is not null);

    /// <summary>Each funded opening balance as the money in it is (see
    /// <see cref="ResolveReturnsScopeAsync"/>): the balance, in its brokerage's currency,
    /// on its start date.</summary>
    private static IEnumerable<(DateTime PostedAt, decimal Amount, Guid AccountId, string Source, RateUnavailable? Unconverted)>
        Arrivals(IEnumerable<BrokerageRow> brokerages) =>
        Funded(brokerages).Select(b => (
            PostedAt: OpenedInstant(b),
            Amount: b.OpeningBalance,
            AccountId: b.Id,
            Source: ContributionSources.OpeningBalances,
            Unconverted: (RateUnavailable?)null));

    /// <summary>Each funded opening balance by brokerage, with the instant it arrives,
    /// for <see cref="PortfolioValuation"/> to count only from then.</summary>
    private static IReadOnlyDictionary<Guid, (DateTime Opened, decimal Balance)> Openings(
        IEnumerable<BrokerageRow> brokerages) =>
        Funded(brokerages).ToDictionary(b => b.Id, b => (OpenedInstant(b), b.OpeningBalance));

    /// <summary>The reason a series point has no index for a chain reading state; null
    /// when the reading carries a value.</summary>
    internal static string? SeriesIndexReason(ReturnsCalculator.TwrReadState state) => state switch
    {
        ReturnsCalculator.TwrReadState.Value => null,
        ReturnsCalculator.TwrReadState.BeforeFirstFunding => ReturnsSeriesReasons.BeforeFirstFunding,
        ReturnsCalculator.TwrReadState.NoInvestedBase => ReturnsSeriesReasons.NoInvestedBase,
        _ => ReturnsSeriesReasons.NegativeCumulativeGrowth,
    };

    /// <summary>The reason a series point has no money-weighted return for a solver
    /// outcome; null when it solved.</summary>
    internal static string? SeriesMwrReason(ReturnsCalculator.XirrOutcome outcome) => outcome switch
    {
        ReturnsCalculator.XirrOutcome.Solved => null,
        ReturnsCalculator.XirrOutcome.TooFewFlows => ReturnsSeriesReasons.TooFewFlows,
        ReturnsCalculator.XirrOutcome.SingleSignedFlows => ReturnsSeriesReasons.SingleSignedFlows,
        ReturnsCalculator.XirrOutcome.ZeroLengthWindow => ReturnsSeriesReasons.ZeroLengthWindow,
        ReturnsCalculator.XirrOutcome.Indeterminate => ReturnsSeriesReasons.Indeterminate,
        _ => ReturnsSeriesReasons.NoRootInRange,
    };

    private static DateTime AsUtc(DateTime d) =>
        d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>
    /// One series from the scope's own flows and the batched valuations: the boundaries
    /// ReturnsAsync builds, chained once, and read at every period end.
    /// </summary>
    /// <remarks>
    /// Every value and flow is in the line's currency, converted at its own instant
    /// (ADR-0114 D7). A point that needs a missing rate (its own value, or any boundary or
    /// flow at or before it) is null in every figure with the <c>rate_unavailable</c>
    /// reason, and names each missing pair and day; the chain is built only over the
    /// boundaries before the first missing one, so no figure is chained or summed over a
    /// partial value (ADR-0114 D9).
    /// </remarks>
    private static (IReadOnlyList<ReturnsSeriesPoint> Points, ReturnsCalculator.TwrChain Chain) BuildSeries(
        SeriesLine line,
        IReadOnlyList<DateTime> marks,
        PortfolioValuation valuation,
        CancellationToken cancellationToken)
    {
        var s = line.Scope;
        var target = line.CurrencyCode;
        var flows = s.Flows
            .OrderBy(f => f.PostedAt)
            .Select(f => (f.PostedAt, Value: valuation.Flow(f, target)))
            .ToList();

        Converted ValueAt(DateTime instant) => valuation.In(instant, s.BrokerageIds, target);

        var flowAt = flows
            .GroupBy(f => f.PostedAt.Ticks)
            .ToDictionary(g => g.Key, g => (Sum: SumOrNull(g.Select(f => f.Value.Amount)),
                Why: Converted.Reasons([.. g.Select(f => f.Value)])));
        decimal? FlowAt(DateTime instant) =>
            flowAt.TryGetValue(instant.Ticks, out var at) ? at.Sum : 0m;

        // What the line needs that a missing rate leaves unknown, by the instant it is
        // needed from: the start value, each boundary's value and each flow.
        var unknown = new List<(DateTime From, IReadOnlyList<RateUnavailable> Why)>();
        decimal? Known(DateTime from, Converted figure)
        {
            if (figure.Amount is null) unknown.Add((from, figure.Unavailable));
            return figure.Amount;
        }
        foreach (var f in flows) Known(f.PostedAt, f.Value);

        // The scalar's own start value: the portfolio before the window opens, less any
        // of the window's flows that valuation already holds (none for an explicit start).
        var startValue = Known(s.StartDate, ValueAt(AsUtc(s.StartValuedAt)))
                         - SumOrNull(flows.Where(f => f.PostedAt <= s.StartValuedAt).Select(f => f.Value.Amount));

        // The boundaries ReturnsAsync builds: the window's start, each flow instant, the
        // window's end, each valued before the flow that lands on it. The chain stops
        // before the first boundary that needs a missing rate.
        var boundaryDates = new SortedSet<DateTime>(flows.Select(f => f.PostedAt)) { s.StartDate, s.EndDate };
        var boundaries = new List<ReturnsCalculator.Boundary>(boundaryDates.Count);
        var chained = true;
        foreach (var d in boundaryDates)
        {
            var value = d == s.StartDate ? startValue : Known(d, ValueAt(d)) - FlowAt(d);
            if (chained && value is { } v && FlowAt(d) is { } flow)
                boundaries.Add(new ReturnsCalculator.Boundary(d, v, -flow));
            else
                chained = false;
        }
        var chain = ReturnsCalculator.TwrChain.Build(boundaries);
        var firstUnknown = unknown.Count == 0 ? (DateTime?)null : unknown.Min(u => u.From);

        // The flows the 12 months ending at a point can hold, from the year before the
        // window as well as within it, each in the line's currency at its own date. A
        // funded opening balance the lookback already holds as it opens is no flow of it
        // (ResolveReturnsScopeAsync), but a year that opens before its start date values the
        // brokerage without it (PortfolioValuation), so there it comes in as money in.
        // Only a since-inception lookback can open after one that a year opens before.
        var yearFlows = line.Lookback.Flows
            .Concat(Arrivals(line.Lookback.Brokerages).Where(f => f.PostedAt <= line.Lookback.StartValuedAt))
            .OrderBy(f => f.PostedAt)
            .Select(f => (f.PostedAt, Value: valuation.Flow(f, target)))
            .ToList();

        // The money-weighted return of the 12 months ending at a point (ADR-0104 D6, amended
        // 2026-11-21): what ReturnsAsync gives for that year as its window, valued before it
        // opens and at the point, with the year's flows between, investor-signed. A rate
        // over exactly a year, so annualizing magnifies nothing; but a year that opened with
        // nothing invested holds money for less than a year, and is not shown.
        (double? Rate, string? Reason, IReadOnlyList<RateUnavailable> Why) TrailingYear(DateTime mark)
        {
            var opens = TrailingYearStart(mark);
            var opening = ValueAt(TrailingYearValuedAt(mark));
            var closing = ValueAt(mark);
            var inYear = yearFlows
                .Where(f => f.PostedAt >= opens && f.PostedAt <= mark)
                .ToList();
            if (opening.Amount is null || closing.Amount is null || inYear.Any(f => f.Value.Amount is null))
                return (null, ReturnsSeriesReasons.RateUnavailable,
                    RateReasons.Of([opening.Unavailable, closing.Unavailable, .. inYear.Select(f => f.Value.Unavailable)]));
            if (opening.Amount.Value == 0m)
                return (null, ReturnsSeriesReasons.UnderAYearCovered, RateReasons.None);
            var cashFlows = new List<ReturnsCalculator.CashFlow>(inYear.Count + 2) { new(opens, -opening.Amount.Value) };
            foreach (var f in inYear) cashFlows.Add(new(f.PostedAt, -f.Value.Amount!.Value));
            cashFlows.Add(new(mark, closing.Amount.Value));
            var solved = ReturnsCalculator.Xirr(cashFlows);
            return (solved.Rate, SeriesMwrReason(solved.Outcome), RateReasons.None);
        }

        var points = new List<ReturnsSeriesPoint>(marks.Count);
        var next = 0;
        var netContributions = 0m;
        double? lastIndex = null;
        foreach (var mark in marks)
        {
            // The money-weighted solve below runs in this process, outside any statement
            // timeout, so a request that is abandoned (or a lane that bounds the whole
            // read) stops it here, once per period end.
            cancellationToken.ThrowIfCancellationRequested();

            // A point that needs a missing rate: every figure null, with the reason and
            // each missing pair and day. Once a boundary or flow is unknown, every later
            // point needs it too.
            var markValue = ValueAt(mark);
            if (firstUnknown <= mark || markValue.Amount is null)
            {
                IReadOnlyList<RateUnavailable> why = RateReasons.Of(
                    [.. unknown.Where(u => u.From <= mark).Select(u => u.Why), markValue.Unavailable]);
                const string reason = ReturnsSeriesReasons.RateUnavailable;
                points.Add(new ReturnsSeriesPoint(
                    mark, null, reason, null, reason, 0, null, reason, null, reason, why,
                    null, null, null, RateReasons.None));
                continue;
            }

            while (next < flows.Count && flows[next].PostedAt <= mark)
            {
                netContributions += flows[next].Value.Amount!.Value;   // known: before the first unknown
                next++;
            }

            var value = markValue.Amount.Value;
            var reading = chain.ReadAt(mark, value);
            var coveredDays = (int)Math.Round(reading.CoveredDays);

            double? index = null;
            // A mark equal to the start boundary reads before_first_funding, as the scalar
            // does for a zero-length window.
            string? indexReason = SeriesIndexReason(reading.State);
            if (indexReason is null) index = reading.Factor * 100.0;

            // The return over the invested part of this period: this index over the last
            // reported one, or over the base of 100 for the first. Derived here from the
            // index so no client sums or chains it; across a stretch with no base the
            // index resumes from its last value, so that is what it is measured from.
            double? periodReturn = null;
            var periodReason = indexReason;
            if (index is { } current)
            {
                var previous = lastIndex ?? 100.0;
                if (previous == 0.0) periodReason = ReturnsSeriesReasons.PriorIndexZero;
                else periodReturn = (current / previous) - 1.0;
                lastIndex = current;
            }

            // Value less the opening value less the net money in since: nothing before the
            // first funding to be a gain on, and a figure from there on, an emptied
            // account's included (what it made stays made).
            decimal? gain = null;
            string? gainReason = null;
            if (reading.State == ReturnsCalculator.TwrReadState.BeforeFirstFunding)
                gainReason = ReturnsSeriesReasons.BeforeFirstFunding;
            else
                gain = value - startValue - netContributions;

            // A rate missing for the year before the window leaves this figure alone
            // null, and the point names it.
            var (mwr, mwrReason, mwrWhy) = TrailingYear(mark);

            points.Add(new ReturnsSeriesPoint(
                mark, index, indexReason, periodReturn, periodReason, coveredDays,
                gain, gainReason, mwr, mwrReason, mwrWhy,
                null, null, null, RateReasons.None));
        }

        return (points, chain);
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
    //
    // Broken out per brokerage rather than summed. Both feeders already return one
    // row per account (cash) and per position (securities), so this costs the same
    // two queries a sum would and differs only in where the addition happens. That
    // is why the window ends are valued here and the scalar totals derived from its
    // result: the roster is free, and start/end cannot drift from the rows that are
    // supposed to sum to them, because they are the same numbers.
    //
    // Every instant a report needs is valued at once, in two batched reads
    // (migrations 200 and 201), one for cash and one for securities, rather than two
    // queries per instant, and every rate the values and flows convert at in one load
    // (ADR-0114 D7).
    private sealed class PortfolioValuation
    {
        // Per (instant, brokerage), the amount held in each currency: the brokerage's
        // cash in its own, and its positions summed per price currency. A sum converts at
        // one rate exactly as its parts do, so the positions are not kept one by one.
        private readonly Dictionary<(long Ticks, Guid Brokerage), List<(string Currency, decimal Amount)>> _held;
        private readonly IReadOnlyDictionary<Guid, string> _currencyOf;
        private readonly ExchangeRateTable _rates;
        private readonly DateOnly _serverToday;

        private PortfolioValuation(
            Dictionary<(long, Guid), List<(string, decimal)>> held, IReadOnlyDictionary<Guid, string> currencyOf,
            ExchangeRateTable rates, DateOnly serverToday)
        {
            _held = held;
            _currencyOf = currencyOf;
            _rates = rates;
            _serverToday = serverToday;
        }

        /// <summary>
        /// Values <paramref name="brokerageIds"/> at each of <paramref name="instants"/>, and
        /// loads every rate a value or one of <paramref name="flows"/> needs into
        /// <paramref name="target"/>: each position from its price's currency into its
        /// brokerage's, each brokerage into the target at the instant, each flow at its date.
        /// </summary>
        /// <param name="openings">Each funded opening balance and the instant it arrives
        /// (<see cref="Openings"/>). The cash read carries an account's opening balance at
        /// every instant, before its start date too; a value before that instant leaves it
        /// out, so the brokerage holds only what its postings brought in.</param>
        public static async Task<PortfolioValuation> LoadAsync(
            AppDbContext db,
            ExchangeRateLookup lookup,
            Guid ledgerId,
            IReadOnlyList<Guid> brokerageIds,
            IReadOnlyDictionary<Guid, Guid> siblingToBrokerage,
            IReadOnlyDictionary<Guid, string> currencyOf,
            IReadOnlyDictionary<Guid, (DateTime Opened, decimal Balance)> openings,
            IReadOnlyCollection<DateTime> instants,
            string target,
            IEnumerable<(DateTime PostedAt, decimal Amount, Guid AccountId, string Source, RateUnavailable? Unconverted)> flows,
            Action<ExchangeRateNeeds>? declareAlso,
            CancellationToken cancellationToken)
        {
            var asOfs = instants.Distinct().ToArray();
            var sums = new Dictionary<(long Ticks, Guid Brokerage, string Currency), decimal>();
            void Hold(DateTime at, Guid brokerage, string currency, decimal amount)
            {
                var key = (at.Ticks, brokerage, currency);
                sums[key] = sums.GetValueOrDefault(key) + amount;
            }

            if (asOfs.Length > 0 && siblingToBrokerage.Count > 0)
            {
                var securities = await db.HoldingsMarketValueAsOfSet(ledgerId, asOfs, siblingToBrokerage.Keys.ToArray())
                    .Select(r => new { r.AsOf, r.AccountId, r.MarketValue, r.PriceCurrency })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                // Securities sit on the holdings sibling; they belong to its brokerage, the
                // account a reader knows by name. A position at cost basis is in its
                // brokerage's currency.
                foreach (var row in securities)
                    if (siblingToBrokerage.TryGetValue(row.AccountId, out var owner))
                        Hold(row.AsOf, owner, row.PriceCurrency ?? currencyOf[owner], row.MarketValue);
            }
            if (asOfs.Length > 0 && brokerageIds.Count > 0)
            {
                var cash = await db.AccountBalanceAsOfInstants(ledgerId, asOfs, brokerageIds.ToArray())
                    .Select(r => new { r.AsOf, r.AccountId, r.Balance })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var row in cash)
                    if (currencyOf.TryGetValue(row.AccountId, out var currency))
                        Hold(row.AsOf, row.AccountId, currency,
                            openings.TryGetValue(row.AccountId, out var opening) && AsUtc(row.AsOf) < opening.Opened
                                ? row.Balance - opening.Balance
                                : row.Balance);
            }

            var held = sums
                .GroupBy(kv => (kv.Key.Ticks, kv.Key.Brokerage))
                .ToDictionary(g => g.Key, g => g.Select(kv => (kv.Key.Currency, kv.Value)).ToList());

            var needs = lookup.NewNeeds();
            var serverToday = needs.ServerToday;
            foreach (var ((ticks, brokerage), amounts) in held)
            {
                var at = RateDay.Of(new DateTime(ticks, DateTimeKind.Utc), serverToday);
                foreach (var (currency, _) in amounts) at.Declare(needs, currency, currencyOf[brokerage]);
            }
            foreach (var instant in asOfs)
            {
                var at = RateDay.Of(instant, serverToday);
                foreach (var brokerage in brokerageIds) at.Declare(needs, currencyOf[brokerage], target);
            }
            foreach (var flow in flows)
                if (currencyOf.TryGetValue(flow.AccountId, out var currency))
                    RateDay.Of(flow.PostedAt, serverToday).Declare(needs, currency, target);
            declareAlso?.Invoke(needs);
            var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

            return new PortfolioValuation(held, currencyOf, rates, serverToday);
        }

        /// <summary>Every rate the load read, the ones <c>declareAlso</c> asked for included.</summary>
        public ExchangeRateTable Rates => _rates;

        /// <summary>The server UTC today the rates were declared for.</summary>
        public DateOnly ServerToday => _serverToday;

        /// <summary>The rate a figure at <paramref name="instant"/> converts at.</summary>
        private RateDay DayOf(DateTime instant) => RateDay.Of(instant, _serverToday);

        /// <summary>One brokerage's value at <paramref name="instant"/>, in its own
        /// currency: its cash, plus its positions converted into it, rounded once.</summary>
        public Converted Native(DateTime instant, Guid brokerage)
        {
            var currency = _currencyOf[brokerage];
            var total = _rates.NewTotal(currency);
            if (_held.TryGetValue((instant.Ticks, brokerage), out var amounts))
            {
                var at = DayOf(instant);
                foreach (var (from, amount) in amounts) total.Add(amount, from, at);
            }
            return total.Result;
        }

        /// <summary>The brokerages' value at <paramref name="instant"/> in
        /// <paramref name="target"/>: each brokerage's own value converted at the instant,
        /// summed, rounded once.</summary>
        public Converted In(DateTime instant, IEnumerable<Guid> brokerages, string target)
        {
            var total = _rates.NewTotal(target);
            var at = DayOf(instant);
            foreach (var brokerage in brokerages)
                total.Add(Native(instant, brokerage), _currencyOf[brokerage], at);
            return total.Result;
        }

        /// <summary>One flow in <paramref name="target"/>, at its date's rate; null with its
        /// reason when that rate, or an in-kind transfer's price rate, is missing.</summary>
        public Converted Flow(
            (DateTime PostedAt, decimal Amount, Guid AccountId, string Source, RateUnavailable? Unconverted) flow,
            string target)
        {
            if (flow.Unconverted is { } why) return new Converted(null, [why]);
            return _rates.NewTotal(target)
                .Add(flow.Amount, _currencyOf[flow.AccountId], DayOf(flow.PostedAt))
                .Result;
        }
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
            .ThenBy(s => s.Id)
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
        // Unbacked dates in one pass, not a predicate per row: the per-row form
        // rescans the security's legs for every price, so its cost is prices × legs
        // where the one-pass form reads the legs once. This read is NOT paginated, so the
        // per-row shape would have made a long history dear on every call.
        var unbacked = (await _db.UnbackedPriceDates(ledgerId, securityId)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(d => d.PriceDate)
            .ToHashSet();

        var rows = await q
            .OrderBy(p => p.PriceDate)
            .Select(p => new { p.PriceDate, p.Price, p.High, p.Low, p.Volume, p.Source })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(p => new PricePoint(
                p.PriceDate, p.Price, p.High, p.Low, p.Volume,
                p.Source, unbacked.Contains(p.PriceDate)))
            .ToList();
    }

    /// <summary>
    /// Detect in-kind-transfer candidates (ADR-0065 D4): disposal (sell/sellx)
    /// in one investment account paired with an acquisition (buy/buyx) in another,
    /// SAME security, SAME calendar date (UTC), EQUAL share quantity, different
    /// brokerages in ONE currency (a pair across currencies is never offered,
    /// ADR-0114 D13). Read-only — surfaces candidates for the user to review against a
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
            .Select(a => new { a.Id, a.Name, a.CurrencyCode, Sibling = a.HoldingsAccountId!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (brokerages.Count < 2) return Array.Empty<InKindTransferCandidate>();

        var bySibling = brokerages.ToDictionary(b => b.Sibling, b => (b.Id, b.Name));
        var currencyOf = brokerages.ToDictionary(b => b.Id, b => b.CurrencyCode);
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
                // A pair across currencies is not offered (ADR-0114 D13, ADR-0065 D4):
                // its two trades are each in their own currency, so equal shares on one
                // day say nothing about one position having moved.
                if (!string.Equals(currencyOf[aId], currencyOf[dId], StringComparison.Ordinal)) continue;

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
        return results.OrderBy(c => c.Date).ThenBy(c => c.SecurityName)
            .ThenBy(c => c.SellHeaderId).ThenBy(c => c.BuyHeaderId).ToList();
    }
}

/// <summary>What <see cref="InvestmentReportingRepository.FirstInvestedDayOfAsync"/> reads
/// of an investment account: its id, start date and opening balance.</summary>
internal readonly record struct InvestmentAccountStart(Guid Id, DateOnly? OpenedOn, decimal OpeningBalance);
