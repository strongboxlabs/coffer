namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>fn_unbacked_price_dates(ledger, security)</c>
/// (migration 241) — the dates on which this security has a trade-source price
/// no trade currently derives.
/// </summary>
/// <remarks>
/// Dates rather than a per-row boolean so a caller joins once. The per-row
/// predicate rescans the security's legs for every price: measured at 728ms
/// against 14ms over a 1,229-price history.
/// </remarks>
internal sealed class UnbackedPriceDateRow
{
    public DateOnly PriceDate { get; init; }
}
