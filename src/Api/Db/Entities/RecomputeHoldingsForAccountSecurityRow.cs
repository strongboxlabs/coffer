namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>recompute_holdings_for_account_security</c>
/// TVF wrapper (migration 104). The wrapper PERFORMs
/// <c>recompute_holdings_cost_basis(NULL, account, security)</c> for
/// narrow recompute and returns the input account_id so EF has a
/// typed projection. Callers discard the value; the side effect on
/// <c>holdings</c> + <c>lots</c> is what matters.
/// </summary>
/// <remarks>
/// Bound via <c>HasDbFunction</c> in <see cref="AppDbContext"/> so
/// <see cref="Repositories.HoldingsRecomputeService"/> invokes the
/// recompute via LINQ. Parallels
/// <see cref="RecomputeBalancesForAccountRow"/> (mig 102). Replaces
/// the trigger-driven recompute (<c>trg_txn_legs_recompute_*</c>,
/// dropped in mig 104 per ADR-0032 "triggers as last resort").
/// </remarks>
internal sealed class RecomputeHoldingsForAccountSecurityRow
{
    public Guid AccountId { get; init; }
}
