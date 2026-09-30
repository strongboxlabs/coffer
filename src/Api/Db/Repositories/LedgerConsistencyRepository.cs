using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Asks whether every derived projection still agrees with the transactions.
/// Writes nothing.
/// </summary>
/// <remarks>
/// See <see cref="LedgerConsistencyReport"/> for why this exists. The short
/// version: the projections are maintained by EF interceptors, any write that
/// bypasses the ChangeTracker skips them, and until now there was no way to find
/// out short of recomputing — which destroys the evidence.
/// <para>
/// Every comparison here derives its expectation from a PURE function that the
/// corresponding writer also uses (migrations 202 and 206), so a check can never
/// disagree with a repair about what the right answer is.
/// </para>
/// </remarks>
public sealed class LedgerConsistencyRepository
{
    private const int MaxMismatchesPerProjection = 100;

    /// <summary>
    /// "As of the end of time" — the check's horizon must match the writer's, and the
    /// writer has none. Named rather than inlined so a reader sees the intent instead
    /// of wondering why a consistency check cares about the year 9999.
    /// </summary>
    private static readonly DateTime NoHorizon =
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    private readonly AppDbContext _db;
    private readonly RegisterRepository _register;
    private readonly HoldingsRecomputeService _holdings;

    public LedgerConsistencyRepository(
        AppDbContext db,
        RegisterRepository register,
        HoldingsRecomputeService holdings)
    {
        _db = db;
        _register = register;
        _holdings = holdings;
    }

    public async Task<LedgerConsistencyReport> CheckAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        var projections = new List<ProjectionConsistency>
        {
            (await CheckBalancesAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
            (await CheckTradePricesAsync(ledgerId, cancellationToken).ConfigureAwait(false)).Projection,
        };

        // Advisory, so it takes no part in Healthy: a price nothing derives may
        // be perfectly correct, and the ledger is not broken for holding one.
        var labels = await SecurityLabelsAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var unbacked = (await _db.UnbackedPriceSecurities(ledgerId)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(u => new UnbackedPriceAdvisory(
                SecurityId: u.SecurityId,
                Security: labels.GetValueOrDefault(u.SecurityId, "(security)"),
                Count: (int)u.UnbackedCount,
                Earliest: u.Earliest,
                Latest: u.Latest,
                HoldingValue: u.HoldingValue))
            .ToList();

        return new LedgerConsistencyReport(
            Healthy: projections.All(p => p.Healthy),
            Projections: projections,
            UnbackedPrices: unbacked);
    }

    /// <summary>Running balances, via the read-only walk (mig 206).</summary>
    private async Task<CheckResult> CheckBalancesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var report = await _register.CheckBalancesAsync(ledgerId, cancellationToken)
                                    .ConfigureAwait(false);
        return BalancesFrom(report);
    }

    /// <summary>
    /// Holdings quantity and cost basis, against the same FIFO walk the recompute
    /// persists (mig 202).
    /// </summary>
    private async Task<CheckResult> CheckHoldingsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var stored = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new { h.AccountId, h.SecurityId, h.Quantity, h.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // NO upper bound, because the WRITER has none. recompute_holdings_cost_basis
        // calls holdings_fifo_walk(account, security, NULL) — every event, whenever
        // posted — so stored holdings include future-dated ones. Asking here for
        // "as of now" compared stored-including-future against expected-excluding-
        // future, and a scheduled transaction is a first-class state (the register's
        // whole "scheduled" tab is headers posted after now). One future-dated trade
        // therefore reported drift the repair could not fix: the repair walks
        // unbounded, re-stores the same values, and the next check reports the same
        // mismatch, forever.
        //
        // A check must expect exactly what the writer produces. The bound is a
        // non-nullable parameter on the EF binding, so "no bound" is expressed as the
        // maximum representable instant; the SQL's own comparisons are
        // `posted_at <= p_as_of`, which that satisfies for every real event.
        var walked = (await _db.HoldingsCostBasisAsOf(ledgerId, NoHorizon, null)
            .Select(r => new { r.AccountId, r.SecurityId, r.Quantity, r.CostBasis })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(r => (r.AccountId, r.SecurityId));

        var names = await AccountNamesAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var securities = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var h in stored)
        {
            if (!walked.TryGetValue((h.AccountId, h.SecurityId), out var w)) continue;
            var scope = names.GetValueOrDefault(h.AccountId, "(account)")
                        + " / " + securities.GetValueOrDefault(h.SecurityId, "(security)");
            if (w.Quantity != h.Quantity)
                mismatches.Add(new ConsistencyMismatch(scope, "quantity", h.Quantity, w.Quantity,
                    AccountId: h.AccountId, SecurityId: h.SecurityId));
            if (w.CostBasis != h.CostBasis)
                mismatches.Add(new ConsistencyMismatch(scope, "cost_basis", h.CostBasis, w.CostBasis,
                    AccountId: h.AccountId, SecurityId: h.SecurityId));
        }

        return Build(ConsistencyProjections.Holdings, stored.Count, mismatches);
    }

    /// <summary>
    /// Realized gains per (account, security), against the pure walk (mig 206).
    /// </summary>
    /// <remarks>
    /// Compared at the grain the table STORES — one rounded row per disposal,
    /// summed — rather than by rounding a total. Rounding a sum and summing
    /// rounded rows differ by up to a cent per disposal, and an ad-hoc query that
    /// got this wrong reported thirty rows of drift that did not exist.
    /// </remarks>
    /// <summary>
    /// Per-disposal realized gains, against the pure walk (mig 206, widened by 217).
    /// </summary>
    /// <remarks>
    /// <para>
    /// PER ROW, and across every money column, because the two obvious shortcuts each
    /// hide the drift they exist to find. This compared <c>SUM(realized_gain)</c> per
    /// (account, security), which is blind twice over: two disposals drifting +0.01 and
    /// -0.01 in the same position sum to zero and report healthy, and the three
    /// long-term columns were not compared at all — the walk did not return them until
    /// mig 217, so a ledger whose entire short/long tax split had been zeroed would have
    /// passed. Those columns are NOT NULL DEFAULT 0, so that is a silent write rather
    /// than an error, which is exactly how it would have happened.
    /// </para>
    /// <para>
    /// The comparison is over the INTERSECTION on <c>sell_leg_id</c> — rows that exist
    /// on both sides — and deliberately does not report rows present in one and not the
    /// other. The walk legitimately omits disposals on hidden headers, on merged headers
    /// (mig 163) and on <c>transfer_shares</c> actions (ADR-0065 D1), so an install
    /// carrying legacy rows of those shapes would light up with drift that no repair can
    /// resolve. This file already records what that costs: cry wolf once and the check
    /// gets ignored forever. Row-set divergence is a real question and wants its own
    /// answer, not a false positive bolted onto this one.
    /// </para>
    /// </remarks>
    private async Task<CheckResult> CheckRealizedGainsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var positions = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new { h.AccountId, h.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var names = await AccountNamesAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var p in positions)
        {
            var stored = await _db.RealizedGains.AsNoTracking()
                .Where(g => g.AccountId == p.AccountId && g.SecurityId == p.SecurityId)
                .Select(g => new
                {
                    g.SellLegId,
                    g.Proceeds,
                    g.CostBasisSold,
                    g.RealizedGain,
                    g.ProceedsLt,
                    g.CostBasisSoldLt,
                    g.RealizedGainLt,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var walked = await _db.RealizedGainsWalk(p.AccountId, p.SecurityId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var walkedByLeg = walked
                .GroupBy(w => w.SellLegId)
                .ToDictionary(g => g.Key, g => g.First());

            var accountName = names.GetValueOrDefault(p.AccountId, "(account)");
            foreach (var row in stored)
            {
                if (!walkedByLeg.TryGetValue(row.SellLegId, out var w)) continue;

                // One mismatch per COLUMN, not one per row: a reader fixing this needs
                // to know that the long-term split drifted rather than that "the row"
                // did, and the two have different causes.
                Compare("proceeds", row.Proceeds, w.Proceeds);
                Compare("cost_basis_sold", row.CostBasisSold, w.CostBasisSold);
                Compare("realized_gain", row.RealizedGain, w.RealizedGain);
                Compare("proceeds_lt", row.ProceedsLt, w.ProceedsLt);
                Compare("cost_basis_sold_lt", row.CostBasisSoldLt, w.CostBasisSoldLt);
                Compare("realized_gain_lt", row.RealizedGainLt, w.RealizedGainLt);

                // No cap here even though only MaxMismatchesPerProjection are
                // displayed: Build reports mismatches.Count as the TOTAL, so
                // stopping early would make a badly drifted ledger report exactly
                // 100 and understate itself at the moment it most needs not to.
                void Compare(string field, decimal storedValue, decimal expected)
                {
                    if (storedValue == expected) return;
                    mismatches.Add(new ConsistencyMismatch(
                        Scope: accountName + " / " + p.SecurityId + " / " + row.SellLegId,
                        Field: field,
                        Stored: storedValue,
                        Expected: expected,
                        AccountId: p.AccountId,
                        SecurityId: p.SecurityId));
                }
            }
        }

        // POSITIONS examined, not disposals compared. Briefly changed to the latter on
        // the reasoning that the unit of comparison had moved to the row — wrong, and an
        // existing vacuity guard said so: a position holding shares it has never sold is
        // still a position this check looked at, and reporting 0 for a healthy ledger
        // that simply has not sold anything reads as "this projection did nothing".
        return Build(ConsistencyProjections.RealizedGains, positions.Count, mismatches);
    }

    /// <summary>
    /// The denormalised posting counts on <c>txn_legs</c> (mig 120), against the
    /// definition the recompute uses.
    /// </summary>
    /// <remarks>
    /// <c>header_total_postings</c> is <c>COUNT(DISTINCT posting_index)</c>, NOT a
    /// count of legs — a two-leg transfer shares one posting index and counts as
    /// one. A first version of this check counted legs and flagged every
    /// multi-leg header in a perfectly consistent ledger, which is the failure
    /// mode a consistency check can least afford: cry wolf once and it gets
    /// ignored forever.
    /// </remarks>
    private async Task<CheckResult> CheckPostingCountsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var actual = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId)
            .GroupBy(l => l.HeaderId)
            .Select(g => new
            {
                HeaderId = g.Key,
                Total = g.Select(l => l.PostingIndex).Distinct().Count(),
                Stored = g.Max(l => l.HeaderTotalPostings),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // No cap here, for the reason spelled out on the realized-gains compare:
        // Build reports mismatches.Count as the TOTAL, so stopping at the display
        // cap made a badly drifted ledger report exactly 100 — understating itself
        // at the moment it most needs not to, and capping the repair with it.
        var mismatches = actual
            .Where(a => a.Stored != a.Total)
            .Select(a => new ConsistencyMismatch(
                Scope: "header " + a.HeaderId,
                Field: "header_total_postings",
                Stored: a.Stored,
                Expected: a.Total,
                HeaderId: a.HeaderId))
            .ToList();

        return Build(ConsistencyProjections.PostingCounts, actual.Count, mismatches);
    }

    /// <summary>
    /// Trade-derived <c>security_prices</c>, via <c>fn_trade_price_check</c>
    /// (migration 232).
    /// </summary>
    /// <remarks>
    /// The rule lives in the function, not here, and that is the point: it is a
    /// reader of migration 177's rank-gated writer, and a re-statement of the
    /// writer's logic in LINQ would be a second copy free to drift from the
    /// thing it is checking. The function already treats the days a
    /// <c>fetch</c> or <c>manual</c> price legitimately outranks as consistent,
    /// which is why this projection could not simply ask "is there a row?".
    ///
    /// <para>The function returns the days it EXAMINED, not only the ones that
    /// disagree, so <c>Checked</c> means here what it means everywhere else in
    /// this report — and a healthy ledger does not report "0 examined", which
    /// would be indistinguishable from a query that silently returns nothing.
    /// No cap on the scan for the same reason the realized-gains check has
    /// none: <see cref="Build"/> reports the true total and truncates only what
    /// is displayed.</para>
    /// </remarks>
    private async Task<CheckResult> CheckTradePricesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var examined = await _db.TradePriceCheck(ledgerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var labels = await SecurityLabelsAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var accounts = await TradingAccountsAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var mismatches = examined
            .Where(d => d.Drifted)
            .Select(d => new ConsistencyMismatch(
                // Security, account, day — the three things needed to go find the
                // trade behind the expected figure and judge it. The header id
                // travels separately so a link can target it exactly.
                Scope: labels.GetValueOrDefault(d.SecurityId, "(security)")
                       + (d.AccountId is not Guid acct
                            ? ""
                            : " · " + accounts.GetValueOrDefault(
                                  acct, (Id: acct, Name: "(account)")).Name)
                       + " · " + d.PriceDate.ToString("yyyy-MM-dd"),
                // Said the way a reader can act on. "missing" means the day's trade
                // seeded no price at all; the other means a row is sitting on that
                // day claiming a price the day's last trade does not imply — which
                // is what a since-corrected trade leaves behind (ADR-0084 D4).
                Field: d.Reason == "missing"
                    ? "no price for the day's trade"
                    : "price disagrees with the day's last trade",
                // A missing row stores nothing; 0 is the honest reading of
                // "there is no stored value", and Diff then equals the whole
                // expected price rather than a difference against a fiction.
                Stored: d.StoredPrice ?? 0m,
                Expected: d.ExpectedPrice,
                AccountId: d.AccountId is Guid a
                    ? accounts.GetValueOrDefault(a, (Id: a, Name: "")).Id
                    : null,
                SecurityId: d.SecurityId,
                HeaderId: d.HeaderId,
                PriceDate: d.PriceDate,
                Reason: d.Reason))
            .ToList();

        return Build(ConsistencyProjections.TradePrices, examined.Count, mismatches);
    }

    /// <summary>
    /// Rebuild one projection, touching only what the check reported.
    /// </summary>
    /// <remarks>
    /// Every projection the report names is repairable, so a reader is never told
    /// about a problem the product cannot fix — which was the state that led to a
    /// scrub's damage sitting unrepaired for months while three separate ad-hoc
    /// queries were written to look at it.
    /// <para>
    /// Repairs are TARGETED: the disagreeing (account, security) pairs or headers,
    /// not the whole ledger. Fixing 17 headers should not rewrite 42,000 rows, and a
    /// full-ledger FIFO recompute is heavy enough that doing it needlessly is its own
    /// hazard.
    /// </para>
    /// </remarks>
    public async Task<ProjectionConsistency> RepairAsync(
        Guid ledgerId,
        string projection,
        CancellationToken cancellationToken = default)
    {
        // Balances have their own whole-ledger repair, which already reports what it
        // changed; the others compare first so the repair knows what to touch.
        if (projection == ConsistencyProjections.Balances)
        {
            var healed = await _register.VerifyAndHealBalancesAsync(ledgerId, cancellationToken)
                                        .ConfigureAwait(false);
            return (await CheckBalancesFromAsync(healed).ConfigureAwait(false)).Projection;
        }

        var before = projection switch
        {
            ConsistencyProjections.Holdings =>
                await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.RealizedGains =>
                await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.PostingCounts =>
                await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.TradePrices =>
                await CheckTradePricesAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(projection), projection,
                     "Unknown projection."),
        };
        if (before.Projection.Healthy) return before.Projection;

        // EVERY branch below targets before.AllMismatches, never
        // before.Projection.Mismatches. The latter is capped at
        // MaxMismatchesPerProjection for display; repairing from it fixes the first
        // hundred disagreements and reports the repair it just performed, so a
        // ledger with five hundred needs five clicks and says so nowhere.
        if (projection == ConsistencyProjections.TradePrices)
        {
            // Repaired through migration 177's OWN upsert primitive, one drifted
            // day at a time, rather than by writing security_prices from here.
            // The eligibility rule is already restated in four places (see
            // database-schema.md under security_price_upsert_from_trade); a fifth
            // copy living in the repair would mean a ledger could be "repaired"
            // into a state the writer would never produce.
            //
            // The primitive is rank-gated, so a day a fetch/manual price has
            // taken ownership of since the check ran is left alone rather than
            // clobbered — the repair cannot undo a truer price.
            //
            // missing / value only, read from the function rather than filtered
            // out of AllMismatches. An ORPHAN also carries a SecurityId and a
            // PriceDate, so a shape-based filter would sweep it in and re-upsert
            // its own price over itself — a write that changes nothing, restamps
            // the row, and leaves the re-check still reporting it, which reads as
            // a repair that failed. The reason is the thing that matters, so the
            // reason is what this selects on. Uncapped, like AllMismatches.
            var repairable = await _db.TradePriceCheck(ledgerId)
                .AsNoTracking()
                .Where(d => d.Reason == "missing" || d.Reason == "value")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var d in repairable)
            {
                _ = await _db.SecurityPriceUpsertFromTrade(
                        ledgerId, d.SecurityId, d.PriceDate, d.ExpectedPrice)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return (await CheckTradePricesAsync(ledgerId, cancellationToken)
                .ConfigureAwait(false)).Projection;
        }

        if (projection == ConsistencyProjections.PostingCounts)
        {
            foreach (var headerId in before.AllMismatches
                         .Select(m => m.HeaderId).Where(id => id is not null)
                         .Select(id => id!.Value).Distinct())
            {
                _ = await _db.RecomputePostingCountsForHeader(headerId)
                    .Select(r => r.HeaderId)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return before.Projection;
        }

        // Holdings and realized gains are the SAME projection from the writer's point
        // of view — recompute_holdings_cost_basis rebuilds quantity, cost basis and
        // realized_gains together — so both repair through one call over the
        // disagreeing pairs.
        var pairs = before.AllMismatches
            .Where(m => m.AccountId is not null && m.SecurityId is not null)
            .Select(m => (m.AccountId!.Value, m.SecurityId!.Value))
            .Distinct()
            .ToList();
        await _holdings.RecomputeAsync(pairs, cancellationToken).ConfigureAwait(false);
        return before.Projection;
    }

    /// <summary>Shape a balance-repair result as a projection report.</summary>
    private Task<CheckResult> CheckBalancesFromAsync(BalanceHealthReport report) =>
        Task.FromResult(BalancesFrom(report));

    /// <summary>
    /// Shape a <see cref="BalanceHealthReport"/> as this class's check result.
    /// </summary>
    /// <remarks>
    /// One builder for both entry points — the check and the post-repair report —
    /// because they had drifted into two copies of the same projection with the
    /// same six field mappings, which is how a repair ends up describing a row
    /// differently from the check that found it.
    /// <para>
    /// Uncapped on the way in: <c>Drifted</c> is the full set (<c>DriftedCount</c>
    /// is its own <c>Count</c>), and the cap belongs on the way out.
    /// </para>
    /// </remarks>
    private static CheckResult BalancesFrom(BalanceHealthReport report)
    {
        var mismatches = report.Drifted
            .Select(d => new ConsistencyMismatch(
                Scope: d.AccountName + " @ " + d.PostedAt.ToString("yyyy-MM-dd"),
                Field: "balance_after",
                Stored: d.StoredBefore,
                Expected: d.RecomputedAfter,
                AccountId: d.AccountId,
                HeaderId: d.HeaderId))
            .ToList();

        return new CheckResult(
            new ProjectionConsistency(
                ConsistencyProjections.Balances,
                report.Healthy,
                report.RowsChecked,
                report.DriftedCount,
                mismatches.Take(MaxMismatchesPerProjection).ToList()),
            mismatches);
    }

    private async Task<Dictionary<Guid, string>> AccountNamesAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(a => a.Id, a => a.Name);

    /// <summary>
    /// Display labels for this ledger's securities — ticker where there is one,
    /// otherwise the name, and both where they differ.
    /// </summary>
    /// <remarks>
    /// A report a reader cannot act on is not a report. This projection shipped
    /// naming rows by bare GUID (`security 9705c7fa-… on 2006-01-06`), which tells
    /// someone deciding whether to repair absolutely nothing about which holding
    /// is affected — they would have to go look up the id by hand, six times, to
    /// form a view. The holdings check had the same hole on its security half.
    /// </remarks>
    private async Task<Dictionary<Guid, string>> SecurityLabelsAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.Securities.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId)
            .Select(x => new { x.Id, x.Ticker, x.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(
            x => x.Id,
            x => string.IsNullOrWhiteSpace(x.Ticker) ? x.Name
                 : string.Equals(x.Ticker, x.Name, StringComparison.OrdinalIgnoreCase) ? x.Ticker!
                 : $"{x.Ticker} — {x.Name}");

    /// <summary>
    /// Account names for display, with a holdings sibling resolved to the brokerage
    /// it belongs to.
    /// </summary>
    /// <remarks>
    /// A security leg lives on the holdings sibling, which is a system account the
    /// reader never navigates to and may not recognise — and whose register is not
    /// where the trade is read. Both the name and the id resolve to the BROKERAGE,
    /// so the row names, and can link to, the account someone would actually open.
    /// <para>
    /// Safe to redirect the id because nothing repairs by it: the trade-price repair
    /// targets (security, day). It is carried for navigation only.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, (Guid Id, string Name)>> TradingAccountsAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var rows = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.Name, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var map = rows.ToDictionary(a => a.Id, a => (a.Id, a.Name));
        foreach (var brokerage in rows.Where(a => a.HoldingsAccountId is not null))
            map[brokerage.HoldingsAccountId!.Value] = (brokerage.Id, brokerage.Name);
        return map;
    }

    private static CheckResult Build(
        string name, int checkedCount, List<ConsistencyMismatch> mismatches) =>
        new(new ProjectionConsistency(
                name,
                Healthy: mismatches.Count == 0,
                Checked: checkedCount,
                MismatchedCount: mismatches.Count,
                Mismatches: mismatches.Take(MaxMismatchesPerProjection).ToList()),
            mismatches);

    /// <summary>
    /// One projection's check result, carrying the UNTRUNCATED mismatch list beside
    /// the client-facing <see cref="ProjectionConsistency"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectionConsistency.Mismatches"/> is capped at
    /// <see cref="MaxMismatchesPerProjection"/> because it is what the client
    /// renders, and a badly drifted ledger should not ship forty thousand rows to
    /// draw a list of a hundred. The REPAIR needs all of them.
    /// <para>
    /// Until this type existed the repairs iterated that capped list, so a ledger
    /// with five hundred disagreeing headers was repaired a hundred at a time —
    /// and each pass reported the repair it had just performed, so nothing on
    /// screen said four more were needed. A repair that silently fixes what fits
    /// in the display is the same class of defect as a check that disagrees with
    /// its own repair: it trains the reader to believe a problem is handled.
    /// </para>
    /// </remarks>
    private sealed record CheckResult(
        ProjectionConsistency Projection,
        IReadOnlyList<ConsistencyMismatch> AllMismatches);
}
