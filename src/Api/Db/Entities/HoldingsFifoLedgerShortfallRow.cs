namespace Coffer.Api.Db.Entities;

/// <summary>
/// One short position of a ledger: a (holdings account, security) whose disposals took
/// more shares than were held at the time (<c>holdings_fifo_shortfalls</c>, migration
/// 256).
/// </summary>
/// <remarks>
/// A short position overstates its realized gain by the unmatched shares' proceeds,
/// since the walk books them at zero cost. The consistency report lists these and
/// nothing repairs them: the purchase they are missing is not in the data.
/// </remarks>
internal sealed class HoldingsFifoLedgerShortfallRow
{
    public Guid AccountId { get; init; }
    public Guid SecurityId { get; init; }
    public decimal Shortfall { get; init; }
}
