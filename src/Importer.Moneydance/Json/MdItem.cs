using System.Globalization;
using System.Text.Json;

namespace Coffer.Importer.Moneydance.Json;

/// <summary>
/// Generic representation of one entry in <c>all_items</c>. Every Moneydance
/// item carries an <c>id</c> and an <c>obj_type</c> discriminator; the rest
/// of its fields are heterogeneous and dependent on the type. Typed view
/// records (<see cref="Typed.MdAcct"/>, <see cref="Typed.MdTxn"/>, etc.)
/// extract their fields from <see cref="Fields"/> via the helpers on this
/// type.
/// </summary>
/// <remarks>
/// Moneydance stores almost everything as JSON strings — including numbers
/// (<c>"samt": "-30062"</c>) and booleans (<c>"is_inactive": "y"</c>). The
/// <c>GetXxx</c> helpers normalize the most common encodings; callers stay
/// allocation-free for the success path.
/// </remarks>
public sealed record MdItem(
    string Id,
    string ObjType,
    IReadOnlyDictionary<string, JsonElement> Fields,
    /// <summary>
    /// Raw JSON text for this item exactly as it appears in the MD
    /// export — captured at parse time via `JsonElement.GetRawText()`.
    /// Mig 109 / ADR-0035 §3: persisted on `txn_headers.provider_raw_payload`
    /// for `txn` items so future classifier refinements can be pure
    /// SQL against the JSONB column instead of needing the source file.
    /// Empty string when the source element wasn't captured (test
    /// fixtures constructed by hand).
    /// </summary>
    string RawJson = "")
{
    public bool Has(string key) => Fields.ContainsKey(key);

    public JsonElement? GetElement(string key) =>
        Fields.TryGetValue(key, out var element) ? element : null;

    public string? GetString(string key) =>
        Fields.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
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
