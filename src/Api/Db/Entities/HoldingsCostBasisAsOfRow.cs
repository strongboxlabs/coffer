namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>holdings_cost_basis_as_of</c> (migration 202): FIFO cost basis and
/// quantity for a (holdings-account, security) as of an instant.
/// <para>
/// Produced by the SAME <c>holdings_fifo_walk</c> that
/// <c>recompute_holdings_cost_basis</c> persists, so the read and the write cannot
/// disagree about FIFO. A walk that keeps its state in the <c>lots</c> table cannot be
/// borrowed by a read, so basis at a past instant would be unavailable (mig 202).
/// </para>
/// </summary>
internal sealed class HoldingsCostBasisAsOfRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Quantity { get; init; }
    public decimal CostBasis { get; init; }
}
