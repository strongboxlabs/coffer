namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_cost_basis_as_of</c> (migration 202): FIFO cost basis and
/// quantity for a (holdings-account, security) as of an instant.
/// <para>
/// Produced by the SAME <c>holdings_fifo_walk</c> that
/// <c>recompute_holdings_cost_basis</c> persists, so the read and the write cannot
/// disagree about FIFO. Before mig 202 the walk kept its state in the <c>lots</c>
/// table, which no read could borrow — so basis at a past instant was unavailable.
/// </para>
/// </summary>
internal sealed class HoldingsCostBasisAsOfRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal CostBasis { get; init; }
}
