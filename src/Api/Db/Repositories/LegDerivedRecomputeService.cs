using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Single entry point for re-deriving the two leg-derived
/// denormalizations after any write that affects <c>txn_legs</c>,
/// <c>txn_headers</c>, <c>txn_header_overrides</c>, or
/// <c>txn_leg_overrides</c>:
/// <list type="number">
///   <item><description>the running balance on
///   <c>txn_header_account_balances</c> (mig 102, ADR-0034) via
///   <see cref="RecomputeAsync"/>;</description></item>
///   <item><description>the posting counts
///   (<c>account_postings_on_header</c> / <c>header_total_postings</c>)
///   on <c>txn_legs</c> (mig 120, ADR-0036) via
///   <see cref="RecomputePostingCountsAsync"/>.</description></item>
/// </list>
/// Both derive from the same <c>txn_legs</c> structural changes, so the
/// recompute interceptor snapshots the touched set once and drives both.
/// Every API writer reaches this at its terminal commit boundary; the
/// balance-trigger family was dropped in mig 102 per ADR-0032 /
/// ADR-0034 (recompute at call sites, not via triggers).
/// </summary>
/// <remarks>
/// <para>The point of the explicit-call-site design: every recompute
/// is visible at the writer, debuggable, and testable in isolation.
/// No batch-ordering interactions with EF's SaveChanges; no race
/// between cascade-from-header DELETEs and AFTER-statement triggers;
/// no hidden trigger-fire sequence for a reader to reason about.</para>
///
/// <para>The balance recompute dedupes by <c>account_id</c> to the
/// EARLIEST <c>from_posted_at</c> so a writer can call once per
/// (account, leg) pair without worrying about duplicates — multiple
/// legs on the same account in one transaction collapse to one
/// recompute. The posting-count recompute dedupes by header id.</para>
/// </remarks>
public sealed class LegDerivedRecomputeService
{
    private readonly AppDbContext _db;

    public LegDerivedRecomputeService(AppDbContext db) => _db = db;

    /// <summary>
    /// Re-derive balances for every (account, anchor) pair in
    /// <paramref name="affected"/>. Dedupes to <c>MIN(anchor)</c> per
    /// account; one SQL call per distinct account.
    /// </summary>
    /// <param name="affected">Pairs of (account id, anchor posted_at).
    /// Empty input is a no-op.</param>
    public async Task RecomputeAsync(
        IEnumerable<(Guid AccountId, DateTime FromPostedAt)> affected,
        CancellationToken cancellationToken = default)
    {
        // The recompute is idempotent per (account, anchor). Coalescing
        // duplicates here lets writers append affected pairs liberally
        // (e.g. once per leg) without performance cost; the dedupe
        // collapses N calls per account to one and uses the earliest
        // anchor (widest window) so nothing gets missed.
        var deduped = affected
            .GroupBy(x => x.AccountId)
            .Select(g => (AccountId: g.Key, FromPostedAt: g.Min(x => x.FromPostedAt)))
            .ToList();

        foreach (var (accountId, fromPostedAt) in deduped)
        {
            // EF's HasDbFunction binding requires us to materialise the
            // result; the row is discarded — the side effect on
            // txn_header_account_balances is the point.
            _ = await _db.RecomputeBalancesForAccount(accountId, fromPostedAt)
                .Select(r => r.AccountId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-derive the denormalized posting counts
    /// (<c>account_postings_on_header</c> / <c>header_total_postings</c>)
    /// for every leg of each header in <paramref name="headerIds"/>.
    /// Dedupes by header; one SQL call per distinct header. The
    /// recompute is idempotent, so callers can pass the same header set
    /// the balance recompute used (slightly over-broad on pure amount
    /// edits, but cheap).
    /// </summary>
    /// <param name="headerIds">Header ids whose legs changed structure.
    /// Empty input is a no-op.</param>
    public async Task RecomputePostingCountsAsync(IEnumerable<Guid> headerIds, CancellationToken ct = default)
    {
        foreach (var headerId in headerIds.Distinct())
            _ = await _db.RecomputePostingCountsForHeader(headerId).Select(r => r.HeaderId).FirstAsync(ct).ConfigureAwait(false);
    }
}
