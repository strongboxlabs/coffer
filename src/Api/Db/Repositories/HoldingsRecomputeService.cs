using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Single entry point for re-deriving holdings + lots after any write
/// that affects investment-shape <c>txn_legs</c> (i.e. legs with
/// <c>security_id IS NOT NULL</c> and <c>quantity IS NOT NULL</c>).
/// Every API writer invokes this at its terminal commit boundary via
/// <see cref="HoldingsRecomputeInterceptor"/>; the txn_legs holdings
/// trigger family was dropped in mig 104 per ADR-0032 (recompute at
/// call sites, not via triggers).
/// </summary>
/// <remarks>
/// <para>Parallels <see cref="LegDerivedRecomputeService"/> (mig 102).
/// Same rationale: explicit recompute at the writer is visible,
/// debuggable, and testable in isolation; no per-statement
/// AFTER-trigger ordering for a reader to reason about; no batched
/// SaveChanges interaction with transition tables.</para>
///
/// <para>The service dedupes <c>(account_id, security_id)</c> pairs
/// so a writer can append liberally — multi-leg events on the same
/// holding collapse to one recompute call. The underlying SQL
/// function (<c>recompute_holdings_cost_basis</c>) is idempotent per
/// (account, security).</para>
/// </remarks>
public sealed class HoldingsRecomputeService
{
    private readonly AppDbContext _db;

    public HoldingsRecomputeService(AppDbContext db) => _db = db;

    /// <summary>
    /// Re-derive holdings + lots for every (account, security) pair
    /// in <paramref name="affected"/>. Dedupes; one SQL call per
    /// distinct pair.
    /// </summary>
    /// <param name="affected">Pairs of (holdings-sibling account id,
    /// security id). Empty input is a no-op.</param>
    public async Task RecomputeAsync(
        IEnumerable<(Guid AccountId, Guid SecurityId)> affected,
        CancellationToken cancellationToken = default)
    {
        var deduped = affected.Distinct().ToList();

        foreach (var (accountId, securityId) in deduped)
        {
            // EF's HasDbFunction binding requires us to materialise the
            // result; the row is discarded — the side effect on
            // holdings + lots is the point.
            _ = await _db.RecomputeHoldingsForAccountSecurity(accountId, securityId)
                .Select(r => r.AccountId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
