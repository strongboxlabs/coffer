namespace Coffer.Api.Db.Entities;

/// <summary>
/// One disposal's realized gain as the pure FIFO walk computes it
/// (migration 206), rounded exactly as migration 205 rounds it on write.
/// </summary>
/// <remarks>
/// The grain matters: <c>realized_gains</c> stores one row per disposal and
/// rounds each as it is written, so a check comparing a rounded SUM against a
/// SUM of rounded rows drifts by a cent per disposal and reports drift that is
/// not there. Rounding here, at the same grain, keeps the comparison honest.
/// </remarks>
internal sealed class RealizedGainWalkRow
{
    public Guid SellLegId { get; init; }
    public DateTime SoldAt { get; init; }
    public decimal Quantity { get; init; }
    public decimal Proceeds { get; init; }
    public decimal CostBasisSold { get; init; }
    public decimal RealizedGain { get; init; }
}
