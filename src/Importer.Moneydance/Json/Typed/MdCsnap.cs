namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>csnap</c> item — a price snapshot for a
/// security or currency. The <see cref="CurrId"/> points at a <see cref="MdCurr"/>;
/// the importer filters to security-typed currencies during the price-history
/// mapper (PR 2.7).
/// </summary>
public sealed record MdCsnap(
    string Id,
    string CurrId,
    int Date,
    long? PriceDateMillis,
    decimal? Rate,
    decimal? RelativeRate,
    decimal? High,
    decimal? Low,
    decimal? RelativeHigh,
    decimal? RelativeLow,
    long? Volume)
{
    public static MdCsnap From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "csnap")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'csnap'.", nameof(item));

        // Real Moneydance exports write csnap rates under `urt` (unadjusted
        // rate) and `relrt` (relative rate); some older / synthetic formats
        // use the `rate` / `rrate` names that mirror the parent `curr` row.
        // Read whichever is present.
        return new MdCsnap(
            Id: item.Id,
            CurrId: item.GetString("curr") ?? throw new InvalidDataException(
                $"csnap {item.Id}: missing required 'curr' field"),
            Date: item.GetInt("dt") ?? 0,
            PriceDateMillis: item.GetLong("price_date"),
            Rate:           item.GetDecimal("urt")   ?? item.GetDecimal("rate"),
            RelativeRate:   item.GetDecimal("relrt") ?? item.GetDecimal("rrate"),
            High:           item.GetDecimal("hi"),
            Low:            item.GetDecimal("lo"),
            RelativeHigh:   item.GetDecimal("rhi"),
            RelativeLow:    item.GetDecimal("rlo"),
            Volume:         item.GetLong("vol"));
    }
}
