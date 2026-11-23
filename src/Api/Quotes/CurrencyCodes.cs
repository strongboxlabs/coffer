namespace Coffer.Api.Quotes;

/// <summary>
/// The one currency-code SHAPE rule for accounts and prices: trimmed, upper-cased, three
/// characters. <c>AccountsRepository</c> applies it to an account's currency on create and
/// update, and the price paths to a typed or feed-reported price's, so a price and an
/// account write their codes in the same stored form.
/// </summary>
/// <remarks>
/// <para>This is the shape only. Which codes an account or a ledger can take, and each
/// one's places, live in <see cref="Coffer.Domain.Money.CurrencyMinorUnits"/>, which reads
/// the stored form this produces (ADR-0114 D15). A price or quote currency passes the
/// shape rule and is not checked against that table.</para>
/// <para>Yahoo's <c>meta.currency</c> is NOT read through this: its case carries meaning (a
/// lower-case suffix names a minor unit), so <see cref="Yahoo.YahooCurrency"/> reads it.</para>
/// </remarks>
public static class CurrencyCodes
{
    /// <summary>Quote error code: the source named no currency for a price.</summary>
    public const string MissingErrorCode = "currency-missing";

    /// <summary>Quote error code: the source named a currency Coffer does not recognise.</summary>
    public const string UnknownErrorCode = "currency-unknown";

    /// <summary>Quote error code: a provider handed the write path a currency code no stored
    /// price may carry (a provider bug, caught where every provider converges).</summary>
    public const string InvalidErrorCode = "currency-invalid";

    /// <summary>Normalize <paramref name="raw"/> to the stored form. False for a blank
    /// value or one that is not three characters once trimmed.</summary>
    public static bool TryNormalize(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var c = raw.Trim().ToUpperInvariant();
        if (c.Length != 3) return false;
        code = c;
        return true;
    }
}
