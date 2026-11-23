using System.Collections.Frozen;
using System.Text.Json;

namespace Coffer.Domain.Money;

/// <summary>
/// Each currency's ISO 4217 minor units, and which codes an account or a ledger can take
/// (ADR-0114 D15).
/// </summary>
/// <remarks>
/// <para>One checked-in table holds the exponent of every code on ISO 4217 List One,
/// <c>src/Web/src/lib/currencyMinorUnits.json</c>, derived from SIX's published list and
/// recording that list's publication date. The SPA's <c>lib/minorUnits</c> imports it and
/// this assembly embeds it, so the API and the SPA read the same places. The SQL
/// minor-units function is generated from the same file.</para>
/// <para>A code is accepted as an account or ledger currency when List One gives it an
/// exponent of at most <see cref="MaxPlaces"/>. Refused: a code List One does not hold, an
/// entry whose exponent is "N.A." (precious metals, the bond-market units, the testing and
/// no-currency codes), and the two fund units with more places than the ceiling, CLF and
/// UYW. A code already stored that would now be refused keeps working at
/// <see cref="DefaultPlaces"/>, as every code did before ADR-0114 D15, so
/// <see cref="PlacesOf"/> answers the default for it rather than the table's
/// exponent.</para>
/// <para>Codes are compared in their stored form, three upper-case letters, as
/// <c>CurrencyCodes.TryNormalize</c> produces it. A price or quote currency is not
/// checked against this table: prices are not money.</para>
/// </remarks>
public static class CurrencyMinorUnits
{
    /// <summary>The places a code the table does not accept keeps, as every code had
    /// before ADR-0114 D15.</summary>
    public const int DefaultPlaces = 2;

    /// <summary>The most places an accepted currency can have, and the ceiling the
    /// database's <c>txn_legs</c> CHECK holds.</summary>
    public const int MaxPlaces = 3;

    private const string ResourceName = "Coffer.Domain.Money.currency-minor-units.json";

    private static readonly Table Loaded = Load();

    /// <summary>The publication date of the List One the table is derived from, as SIX
    /// prints it (yyyy-MM-dd).</summary>
    public static string Published => Loaded.Published;

    /// <summary>Every code on List One and its exponent, null for an "N.A." entry, as the
    /// table holds it. Not the places a code is used at: see <see cref="PlacesOf"/>.</summary>
    public static IReadOnlyDictionary<string, int?> ListOne => Loaded.Codes;

    /// <summary>Every code an account or a ledger can take, in ordinal order.</summary>
    public static IReadOnlyList<string> AcceptedCodes => Loaded.Accepted;

    /// <summary>True, with its places, when <paramref name="code"/> is a currency an
    /// account or a ledger can take.</summary>
    public static bool TryGetAccepted(string? code, out int places)
    {
        places = DefaultPlaces;
        if (code is null || !Loaded.Codes.TryGetValue(code, out var exponent)) return false;
        if (exponent is not { } e || e > MaxPlaces) return false;
        places = e;
        return true;
    }

    /// <summary>True when <paramref name="code"/> is a currency an account or a ledger
    /// can take.</summary>
    public static bool IsAcceptedAccountCurrency(string? code) => TryGetAccepted(code, out _);

    /// <summary>The places money in <paramref name="code"/> is shown, typed and stored at:
    /// the table's exponent for an accepted code, <see cref="DefaultPlaces"/> for any
    /// other.</summary>
    public static int PlacesOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return TryGetAccepted(code, out var places) ? places : DefaultPlaces;
    }

    /// <summary>True when <paramref name="value"/> carries no more places than
    /// <paramref name="code"/> allows. Compares the value, not its scale, so trailing
    /// zeros fit.</summary>
    public static bool Fits(decimal value, string code) =>
        value == Math.Round(value, PlacesOf(code));

    /// <summary><paramref name="value"/> at <paramref name="code"/>'s places, half away
    /// from zero: the one rounding of a figure the API computes (ADR-0114 D15).</summary>
    public static decimal RoundAwayFromZero(decimal value, string code) =>
        Math.Round(value, PlacesOf(code), MidpointRounding.AwayFromZero);

    /// <summary><paramref name="value"/> raised to the next whole minor unit of
    /// <paramref name="code"/> (toward positive infinity), the servicer convention for a
    /// loan's periodic payment (ADR-0078). A value that already fits is unchanged.</summary>
    public static decimal RoundUpToMinorUnit(decimal value, string code) =>
        Math.Round(value, PlacesOf(code), MidpointRounding.ToPositiveInfinity);

    private sealed record Table(
        string Published,
        FrozenDictionary<string, int?> Codes,
        IReadOnlyList<string> Accepted);

    private static Table Load()
    {
        using var stream = typeof(CurrencyMinorUnits).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var (published, codes) = Parse(buffer.ToArray());
        var accepted = codes
            .Where(c => c.Value is { } e && e <= MaxPlaces)
            .Select(c => c.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new Table(published, codes.ToFrozenDictionary(StringComparer.Ordinal), accepted);
    }

    /// <summary>
    /// Reads the table, failing on anything but its one shape: a <c>published</c> date and
    /// a <c>minorUnits</c> object of three-letter upper-case codes, each a non-negative
    /// integer or null, no code twice. Read token by token because a dictionary
    /// deserialize keeps the last of two duplicate keys silently.
    /// </summary>
    private static (string Published, Dictionary<string, int?> Codes) Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        string? published = null;
        Dictionary<string, int?>? codes = null;

        Expect(ref reader, JsonTokenType.StartObject);
        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Bad("a property name");
            var name = reader.GetString();
            switch (name)
            {
                case "published":
                    Expect(ref reader, JsonTokenType.String);
                    published = reader.GetString();
                    break;
                case "minorUnits":
                    if (codes is not null) throw Bad("one minorUnits object");
                    codes = ReadCodes(ref reader);
                    break;
                default:
                    Next(ref reader);
                    reader.Skip();
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(published)) throw Bad("a published date");
        if (codes is null || codes.Count == 0) throw Bad("a non-empty minorUnits object");
        return (published, codes);
    }

    private static Dictionary<string, int?> ReadCodes(ref Utf8JsonReader reader)
    {
        var codes = new Dictionary<string, int?>(StringComparer.Ordinal);
        Expect(ref reader, JsonTokenType.StartObject);
        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            var code = reader.GetString() ?? string.Empty;
            if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
                throw Bad($"a three-letter upper-case code, not '{code}'");
            int? exponent = Next(ref reader) switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.Number when reader.TryGetInt32(out var e) && e >= 0 => e,
                _ => throw Bad($"a non-negative integer or null for '{code}'"),
            };
            if (!codes.TryAdd(code, exponent)) throw Bad($"'{code}' once");
        }
        return codes;
    }

    private static JsonTokenType Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read()) throw Bad("more of the document");
        return reader.TokenType;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (Next(ref reader) != type) throw Bad(type.ToString());
    }

    private static InvalidDataException Bad(string expected) =>
        new($"The ISO 4217 minor-units table '{ResourceName}' is malformed: expected {expected}.");
}
