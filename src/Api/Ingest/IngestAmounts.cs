using Coffer.Domain.Money;

namespace Coffer.Api.Ingest;

/// <summary>
/// The one rounding rule for an amount a provider hands the orchestrator (ADR-0114 D15,
/// "An ingested amount is rounded and the row is held for review"; ADR-0031 §2).
/// </summary>
/// <remarks>
/// <para>SimpleFIN, OFX or QFX, QIF, CSV, the brokerage CSV shim and a promote-on-clear
/// all write their amounts through here, so every ingest path rounds the same way: to the
/// account currency's places from the shared ISO 4217 table
/// (<see cref="CurrencyMinorUnits"/>, which the SPA reads too), half away from
/// zero.</para>
/// <para><see cref="Fitted.Rounded"/> compares values, never decimal scale: a figure that
/// carries trailing zeros past the currency's places fits as it is. Reading the scale
/// would seed the originals of, and hold for review, every row a provider writes with a
/// trailing zero.</para>
/// </remarks>
internal static class IngestAmounts
{
    /// <summary>An ingested amount fitted to its account currency's places.</summary>
    /// <param name="Amount">The amount the legs take: <c>value</c> at the currency's
    /// places, half away from zero. A value that already fits is returned as it
    /// is, its scale included.</param>
    /// <param name="Rounded">True only when rounding changed the value, so the bank's
    /// figure differs from <paramref name="Amount"/>.</param>
    internal readonly record struct Fitted(decimal Amount, bool Rounded);

    /// <summary>
    /// <paramref name="value"/> fitted to <paramref name="currencyCode"/>'s places. A code
    /// the table does not accept but which is stored keeps the default places, as
    /// <see cref="CurrencyMinorUnits.PlacesOf"/> answers.
    /// </summary>
    public static Fitted Fit(decimal value, string currencyCode)
    {
        ArgumentNullException.ThrowIfNull(currencyCode);
        var rounded = CurrencyMinorUnits.RoundAwayFromZero(value, currencyCode);
        return rounded == value ? new Fitted(value, false) : new Fitted(rounded, true);
    }
}
