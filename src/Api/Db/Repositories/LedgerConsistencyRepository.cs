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
            await CheckBalancesAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
        };

        return new LedgerConsistencyReport(
            Healthy: projections.All(p => p.Healthy),
            Projections: projections);
    }

    /// <summary>Running balances, via the read-only walk (mig 206).</summary>
    private async Task<ProjectionConsistency> CheckBalancesAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var report = await _register.CheckBalancesAsync(ledgerId, cancellationToken)
                                    .ConfigureAwait(false);
        var mismatches = report.Drifted
            .Take(MaxMismatchesPerProjection)
            .Select(d => new ConsistencyMismatch(
                Scope: d.AccountName + " @ " + d.PostedAt.ToString("yyyy-MM-dd"),
                Field: "balance_after",
                Stored: d.StoredBefore,
                Expected: d.RecomputedAfter,
                AccountId: d.AccountId,
                HeaderId: d.HeaderId))
            .ToList();

        return new ProjectionConsistency(
            ConsistencyProjections.Balances, report.Healthy, report.RowsChecked, report.DriftedCount, mismatches);
    }

    /// <summary>
    /// Holdings quantity and cost basis, against the same FIFO walk the recompute
    /// persists (mig 202).
    /// </summary>
    private async Task<ProjectionConsistency> CheckHoldingsAsync(
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
        var mismatches = new List<ConsistencyMismatch>();
        foreach (var h in stored)
        {
            if (!walked.TryGetValue((h.AccountId, h.SecurityId), out var w)) continue;
            var scope = names.GetValueOrDefault(h.AccountId, "(account)") + " / " + h.SecurityId;
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
    private async Task<ProjectionConsistency> CheckRealizedGainsAsync(
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
    private async Task<ProjectionConsistency> CheckPostingCountsAsync(
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

        var mismatches = actual
            .Where(a => a.Stored != a.Total)
            .Take(MaxMismatchesPerProjection)
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
            return await CheckBalancesFromAsync(healed).ConfigureAwait(false);
        }

        var before = projection switch
        {
            ConsistencyProjections.Holdings =>
                await CheckHoldingsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.RealizedGains =>
                await CheckRealizedGainsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            ConsistencyProjections.PostingCounts =>
                await CheckPostingCountsAsync(ledgerId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(projection), projection,
                     "Unknown projection."),
        };
        if (before.Healthy) return before;

        if (projection == ConsistencyProjections.PostingCounts)
        {
            foreach (var headerId in before.Mismatches
                         .Select(m => m.HeaderId).Where(id => id is not null)
                         .Select(id => id!.Value).Distinct())
            {
                _ = await _db.RecomputePostingCountsForHeader(headerId)
                    .Select(r => r.HeaderId)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return before;
        }

        // Holdings and realized gains are the SAME projection from the writer's point
        // of view — recompute_holdings_cost_basis rebuilds quantity, cost basis and
        // realized_gains together — so both repair through one call over the
        // disagreeing pairs.
        var pairs = before.Mismatches
            .Where(m => m.AccountId is not null && m.SecurityId is not null)
            .Select(m => (m.AccountId!.Value, m.SecurityId!.Value))
            .Distinct()
            .ToList();
        await _holdings.RecomputeAsync(pairs, cancellationToken).ConfigureAwait(false);
        return before;
    }

    /// <summary>Shape a balance-repair result as a projection report.</summary>
    private Task<ProjectionConsistency> CheckBalancesFromAsync(BalanceHealthReport report) =>
        Task.FromResult(new ProjectionConsistency(
            ConsistencyProjections.Balances,
            report.Healthy,
            report.RowsChecked,
            report.DriftedCount,
            report.Drifted
                .Take(MaxMismatchesPerProjection)
                .Select(d => new ConsistencyMismatch(
                    Scope: d.AccountName + " @ " + d.PostedAt.ToString("yyyy-MM-dd"),
                    Field: "balance_after",
                    Stored: d.StoredBefore,
                    Expected: d.RecomputedAfter,
                    AccountId: d.AccountId,
                    HeaderId: d.HeaderId))
                .ToList()));

    private async Task<Dictionary<Guid, string>> AccountNamesAsync(
        Guid ledgerId, CancellationToken cancellationToken) =>
        (await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(a => a.Id, a => a.Name);

    private static ProjectionConsistency Build(
        string name, int checkedCount, List<ConsistencyMismatch> mismatches) =>
        new(name,
            Healthy: mismatches.Count == 0,
            Checked: checkedCount,
            MismatchedCount: mismatches.Count,
            Mismatches: mismatches.Take(MaxMismatchesPerProjection).ToList());
}
