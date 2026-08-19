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

        var walked = (await _db.HoldingsCostBasisAsOf(ledgerId, DateTime.UtcNow, null)
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
            var storedSum = await _db.RealizedGains.AsNoTracking()
                .Where(g => g.AccountId == p.AccountId && g.SecurityId == p.SecurityId)
                .SumAsync(g => g.RealizedGain, cancellationToken)
                .ConfigureAwait(false);

            var walkedSum = await _db.RealizedGainsWalk(p.AccountId, p.SecurityId)
                .SumAsync(g => g.RealizedGain, cancellationToken)
                .ConfigureAwait(false);

            if (storedSum != walkedSum)
            {
                mismatches.Add(new ConsistencyMismatch(
                    Scope: names.GetValueOrDefault(p.AccountId, "(account)") + " / " + p.SecurityId,
                    Field: "realized_gain",
                    Stored: storedSum,
                    Expected: walkedSum,
                    AccountId: p.AccountId,
                    SecurityId: p.SecurityId));
            }
        }

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
                    .FirstAsync(cancellationToken)
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
