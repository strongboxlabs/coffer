namespace Coffer.Api.Quotes.Yahoo;

/// <summary>
/// The currency a Yahoo chart response names for its symbol (<c>chart.result[0].meta.currency</c>),
/// read into the major unit every stored price and rate is in (ADR-0114 D5).
/// </summary>
/// <remarks>
/// <para>Yahoo quotes some exchanges in a minor unit and marks it by case: <c>GBp</c> is pence,
/// not pounds. No rate source quotes a minor unit, so a price in one is divided by its
/// factor and stored under the major code. Every minor unit lives in
/// <see cref="MinorUnits"/>, the one table; nothing else divides a Yahoo price.</para>
///
/// <para>Fails closed. A missing currency is <see cref="MissingCode"/>. A code that is in
/// neither the table nor the shape of a major code (three upper-case ASCII letters) is
/// <see cref="UnknownCode"/>: an unlisted lower-case-suffixed code is a minor unit nobody
/// has put in the table, and storing it as-is would read a pence figure as pounds.</para>
/// </remarks>
public static class YahooCurrency
{
    /// <summary>Per-symbol error code: the response names no currency.</summary>
    public const string MissingCode = CurrencyCodes.MissingErrorCode;

    /// <summary>Per-symbol error code: the response names a currency this table does not
    /// recognise.</summary>
    public const string UnknownCode = CurrencyCodes.UnknownErrorCode;

    /// <summary>
    /// The minor units Yahoo quotes in: its code (case-sensitive), the ISO 4217 major
    /// currency, and how many minor units make one major unit.
    /// </summary>
    /// <remarks>
    /// The three ADR-0114 D5 and its build brief name. Not confirmed against a live
    /// response here; another minor unit Yahoo uses fails closed as
    /// <see cref="UnknownCode"/> until a row is added for it.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, (string MajorCode, int Factor)> MinorUnits =
        new Dictionary<string, (string MajorCode, int Factor)>(StringComparer.Ordinal)
        {
            // Pence sterling (London Stock Exchange).
            ["GBp"] = ("GBP", 100),
            // South African cents (Johannesburg Stock Exchange).
            ["ZAc"] = ("ZAR", 100),
            // Israeli agorot (Tel Aviv Stock Exchange).
            ["ILA"] = ("ILS", 100),
        };

    /// <summary>Resolve <paramref name="metaCurrency"/> to its major code and factor.</summary>
    public static YahooCurrencyResolution Resolve(string? metaCurrency)
    {
        if (string.IsNullOrEmpty(metaCurrency))
            return new YahooCurrencyResolution(null, 0, MissingCode);
        if (MinorUnits.TryGetValue(metaCurrency, out var minor))
            return new YahooCurrencyResolution(minor.MajorCode, minor.Factor, null);
        if (IsMajorShape(metaCurrency))
            return new YahooCurrencyResolution(metaCurrency, 1, null);
        return new YahooCurrencyResolution(null, 0, UnknownCode);
    }

    private static bool IsMajorShape(string code) =>
        code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');
}

/// <summary>
/// A resolved Yahoo currency: the major code and the factor a quoted price is divided by,
/// or the per-symbol error code when it does not resolve.
/// </summary>
public readonly record struct YahooCurrencyResolution(string? CurrencyCode, int Factor, string? ErrorCode)
{
    public bool Resolved => ErrorCode is null;

    /// <summary><paramref name="quoted"/> in the major unit. Only for a resolved currency.</summary>
    public decimal ToMajor(decimal quoted) =>
        Resolved ? quoted / Factor : throw new InvalidOperationException("Currency did not resolve.");
}
