namespace Coffer.Importer.Moneydance.Json.Typed;

/// <summary>
/// Typed view over a Moneydance <c>csplit</c> item — a stock split / reverse
/// split event for a security. csplits live outside the txn stream;
/// <see cref="CurrId"/> points at the security (<see cref="MdCurr"/>) the
/// split applies to.
/// </summary>
/// <remarks>
/// Schema observed in real-world exports:
/// <code>
/// { "obj_type": "csplit",
///   "id":       "&lt;uuid&gt;",
///   "curr":     "&lt;security md-id&gt;",
///   "dt":       20260519,        // yyyymmdd, effective date
///   "ratio":    "2.0",           // post-split qty multiplier
///   "oldshrs":  "2",             // audit
///   "newshrs":  "1",             // audit
///   "ts":       1779249083314 }  // event millis, sub-day ordering
/// </code>
/// Coffer persists ratio as the load-bearing field; <see cref="OldShares"/>
/// and <see cref="NewShares"/> are kept for audit / round-trip.
/// </remarks>
public sealed record MdCsplit(
    string Id,
    string CurrId,
    int Date,
    long? TimestampMillis,
    decimal Ratio,
    decimal? OldShares,
    decimal? NewShares)
{
    public static MdCsplit From(MdItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ObjType != "csplit")
            throw new ArgumentException(
                $"MdItem.obj_type is '{item.ObjType}', expected 'csplit'.", nameof(item));

        var ratio = item.GetDecimal("ratio")
            ?? throw new InvalidDataException(
                $"csplit {item.Id}: missing required 'ratio' field");

        return new MdCsplit(
            Id: item.Id,
            CurrId: item.GetString("curr") ?? throw new InvalidDataException(
                $"csplit {item.Id}: missing required 'curr' field"),
            Date: item.GetInt("dt") ?? 0,
            TimestampMillis: item.GetLong("ts"),
            Ratio: ratio,
            OldShares: item.GetDecimal("oldshrs"),
            NewShares: item.GetDecimal("newshrs"));
    }
}
