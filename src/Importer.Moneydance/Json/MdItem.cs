using System.Globalization;
using System.Text.Json;

namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// Generic representation of one entry in <c>all_items</c>. Every Moneydance
/// item carries an <c>id</c> and an <c>obj_type</c> discriminator; the rest
/// of its fields are heterogeneous and dependent on the type. Typed view
/// records (<see cref="Typed.MdAcct"/>, <see cref="Typed.MdTxn"/>, etc.)
/// extract their fields via the <c>GetXxx</c> helpers on this type.
/// </summary>
/// <remarks>
/// <para>
/// Moneydance stores almost everything as JSON strings — including numbers
/// (<c>"samt": "-30062"</c>) and booleans (<c>"is_inactive": "y"</c>). The
/// <c>GetXxx</c> helpers normalize the most common encodings; callers stay
/// allocation-free for the success path.
/// </para>
/// <para>
/// <see cref="Fields"/> holds JsonElements that are VIEWS into the export's single
/// parsed <see cref="JsonDocument"/> — never <c>Clone()</c>s of it. Clone is what
/// caused the OOM this design replaced: it allocates an independent
/// <c>JsonDocument</c> (object + byte buffer + metadata db) per field, and a real
/// 80.5 MB export has 1,737,198 fields. Not cloning is the whole fix; the
/// dictionary itself was never the problem.
/// </para>
/// <para>
/// The dictionary IS load-bearing for speed, and this was measured the hard way.
/// An earlier attempt dropped it and read fields straight off the item's element
/// via <c>TryGetProperty</c>. That is a LINEAR SCAN of the item's properties, and
/// a transaction with many splits carries a hundred-plus fields which
/// <c>MdTxn.ExtractSplits</c> then probes one at a time — quadratic per item. It
/// cut memory to 286 MB but took the same real import from 29 seconds to 442.
/// O(1) lookup is worth its ~80 MB.
/// </para>
/// <para>
/// CONSEQUENCE: an item is only readable while the owning <see cref="MdExport"/>
/// is undisposed. Reading after disposal throws <see cref="ObjectDisposedException"/>.
/// The export therefore owns the document and callers must keep it alive for the
/// whole import — see <see cref="MdExport"/>.
/// </para>
/// </remarks>
public sealed record MdItem(
    string Id,
    string ObjType,
    /// <summary>
    /// This item's fields, as views into the export's document. Valid only while
    /// the owning <see cref="MdExport"/> is alive.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement> Fields,
    /// <summary>
    /// Raw JSON text for this item exactly as it appears in the MD export.
    /// Mig 109 / ADR-0035 §3: persisted on <c>provider_raw_payload</c> so future
    /// classifier refinements can be pure SQL against the JSONB column instead of
    /// needing the source file. Empty for the obj_types that never persist it, and
    /// for items constructed by hand in tests.
    /// </summary>
    string RawJson = "")
{
    /// <summary>Field names present on this item, in document order.</summary>
    public IEnumerable<string> FieldNames => Fields.Keys;

    public bool Has(string key) => Fields.ContainsKey(key);

    /// <summary>
    /// The field's string value, or <c>null</c> if the key is absent or its
    /// value was not a JSON string.
    /// </summary>
    public string? GetString(string key) =>
        Fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Parse a long from a string-encoded number. Returns <c>null</c> if the
    /// key is missing, empty, or non-numeric. Moneydance amounts are always
    /// in minor units (cents), so this returns the raw integer.
    /// </summary>
    public long? GetLong(string key)
    {
        var text = GetString(key);
        if (string.IsNullOrEmpty(text)) return null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }

    public int? GetInt(string key)
    {
        var v = GetLong(key);
        if (v is null) return null;
        if (v.Value < int.MinValue || v.Value > int.MaxValue) return null;
        return (int)v.Value;
    }

    /// <summary>
    /// Parse a decimal from a string-encoded number. Used by Moneydance for
    /// rates (<c>rrate</c>, <c>rate</c>, <c>relrt</c>) where precision matters.
    /// </summary>
    public decimal? GetDecimal(string key)
    {
        var text = GetString(key);
        if (string.IsNullOrEmpty(text)) return null;
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }

    /// <summary>
    /// Moneydance encodes calendar dates as a <c>yyyyMMdd</c> integer
    /// (<c>20260101</c> = 2026-01-01) — the shape used by <c>date_created</c> on
    /// accounts and <c>sdt</c> on reminders. Returns <c>null</c> when the key is
    /// missing, zero (MD's "unset"), or not a real date, rather than guessing.
    /// </summary>
    /// <remarks>
    /// MD may carry the same instant twice — <c>date_created</c> as this integer
    /// and <c>creation_date</c> as epoch milliseconds (see
    /// <see cref="GetMdEpochDate"/>). Prefer this one where present: it is
    /// already a calendar date and needs no conversion at all. It is NOT always
    /// present, so callers should fall back.
    /// </remarks>
    public DateOnly? GetMdDate(string key) => ParseMdDate(GetInt(key));

    /// <summary>
    /// A Moneydance epoch-milliseconds timestamp read as a calendar date — the
    /// shape of <c>creation_date</c> on accounts.
    /// </summary>
    /// <remarks>
    /// Taking the UTC date is safe rather than a guess: MD stamps these at local
    /// NOON (they land on 16:00/17:00Z for a US-Eastern file), which is exactly
    /// the convention that keeps the calendar day stable under conversion. On a
    /// real 781-account export, all 64 accounts carrying BOTH fields agree
    /// between this UTC date and <c>date_created</c> — and they still agree
    /// across every offset from UTC-12 to UTC+2, so no local timezone is needed
    /// to land on the right day.
    /// </remarks>
    public DateOnly? GetMdEpochDate(string key)
    {
        var millis = GetLong(key);
        if (millis is null or 0) return null;
        try
        {
            return DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeMilliseconds(millis.Value).UtcDateTime);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// The shared <c>yyyyMMdd</c> rule behind <see cref="GetMdDate"/>, for
    /// callers that already hold the raw integer.
    /// </summary>
    public static DateOnly? ParseMdDate(int? yyyymmdd)
    {
        if (yyyymmdd is null or 0) return null;
        var v = yyyymmdd.Value;
        var year = v / 10000;
        var month = (v / 100) % 100;
        var day = v % 100;
        if (year < 1900 || year > 9999) return null;
        if (month is < 1 or > 12) return null;
        if (day is < 1 or > 31) return null;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Moneydance encodes booleans as one of: "y"/"n", "yes"/"no", "1"/"0",
    /// "true"/"false". A missing key returns <c>null</c>; an unrecognized
    /// value returns <c>null</c> rather than guessing.
    /// </summary>
    public bool? GetBool(string key)
    {
        var text = GetString(key);
        if (text is null) return null;
        return text switch
        {
            "y" or "yes" or "Y" or "Yes" or "YES" or "1" or "true" or "True" or "TRUE" => true,
            "n" or "no" or "N" or "No" or "NO" or "0" or "false" or "False" or "FALSE" => false,
            _ => null,
        };
    }
}
