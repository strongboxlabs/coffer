namespace Coffer.Api.Db.Entities;

/// <summary>
/// Shares the FIFO walk's disposals could not match, across one (holdings
/// account, security) — <c>holdings_fifo_shortfall</c>, migration 256.
/// </summary>
/// <remarks>
/// Zero for a sound position. Non-zero means some sale took shares the account
/// did not hold at the time; the walk then books those shares at zero cost and
/// takes the holding negative. Imported data can carry small ones, so the write
/// paths compare it before and after a save rather than refusing any non-zero.
/// </remarks>
internal sealed class HoldingsFifoShortfallRow
{
    public decimal Shortfall { get; init; }
}
