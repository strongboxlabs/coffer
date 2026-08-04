namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>recompute_holdings_for_brokerage</c>
/// scalar wrapper (migration 088). The wrapper calls
/// <c>recompute_holdings_cost_basis</c> (void) for the specified
/// brokerage and returns the count of holdings rows under it for
/// diagnostic / test purposes; callers typically discard the value.
/// Bound via <c>HasDbFunction</c> in <see cref="AppDbContext"/> so
/// the repository invokes the recompute via LINQ rather than raw SQL.
/// Replaces the trigger-driven recompute removed by migration 088
/// (ADR-0032).
/// </summary>
internal sealed class RecomputeHoldingsForBrokerageRow
{
    public int RecomputedCount { get; init; }
}
