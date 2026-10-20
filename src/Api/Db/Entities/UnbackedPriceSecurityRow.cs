namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>fn_unbacked_price_securities(ledger)</c> (migration
/// 240) — one row per security holding prices that claim a trade produced them
/// while no trade currently does.
/// </summary>
/// <remarks>
/// Grouped per security, not per price, because that is the unit a person acts
/// on: the remedy is to open the security and look at its price list. Ordered
/// by what the holding is worth now, so the securities still carrying money
/// come first; a sold position sorts to the bottom rather than being hidden,
/// since a wrong historical price still shows up in a net-worth chart.
/// </remarks>
internal sealed class UnbackedPriceSecurityRow
{
    public Guid SecurityId { get; init; }
    public long UnbackedCount { get; init; }
    public DateOnly Earliest { get; init; }
    public DateOnly Latest { get; init; }
    public decimal HoldingValue { get; init; }
}
