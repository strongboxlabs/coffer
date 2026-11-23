namespace Coffer.Api.Contracts;

/// <summary>
/// The scales reporting figures are rounded to before they leave the API.
/// </summary>
/// <remarks>
/// Money and quantities are bounded by their database columns, but anything DERIVED
/// in C# — a percentage, a per-share cost — is a <see cref="decimal"/> division, and
/// those run to the type's full 28-29 significant digits. Unrounded, a payload
/// carries a percentage that runs to 28 digits next to figures rounded to the cent,
/// in the same object.
/// <para>
/// Unlike the SQL side this is only cosmetic: a C# decimal division rounds rather
/// than throwing, so it cannot fail the way an unbounded Postgres NUMERIC read into a
/// <see cref="decimal"/> can. It is rounded because a reader cannot tell a meaningful digit from an
/// artefact of binary division, and because the inconsistency invites someone to
/// assume the extra precision means something.
/// </para>
/// <para>
/// Six decimal places on both: enough that a percentage is exact for any plausible
/// display, and it matches the per-share rounding already used for the price a
/// valuation actually applied and for investment-transaction unit costs.
/// </para>
/// </remarks>
internal static class ReportingScale
{
    /// <summary>Decimal places for a percentage figure.</summary>
    public const int PercentPlaces = 6;

    /// <summary>Decimal places for a per-share amount.</summary>
    public const int PerSharePlaces = 6;

    /// <summary>Decimal places for a position's market value.</summary>
    /// <remarks>
    /// Four, because that is what the as-of feeder already uses —
    /// <c>market_value := ROUND(v_qty * COALESCE(v_price, 0), 4)</c> in migration 172
    /// (and the batched migration 200 by construction). Matching it is the whole
    /// point; see <see cref="MarketValue"/>.
    /// </remarks>
    public const int MarketValuePlaces = 4;

    /// <summary><paramref name="part"/> of <paramref name="whole"/> as a bounded
    /// percentage, or null when the denominator is zero — a percentage of nothing
    /// is not zero, it is undefined.</summary>
    public static decimal? PercentOrNull(decimal part, decimal whole) =>
        whole == 0m ? null : decimal.Round(part / whole * 100m, PercentPlaces);

    /// <summary>As <see cref="PercentOrNull"/>, but zero when the denominator is
    /// zero — for the callers whose contract is a non-nullable percent.</summary>
    public static decimal Percent(decimal part, decimal whole) =>
        whole == 0m ? 0m : decimal.Round(part / whole * 100m, PercentPlaces);

    /// <summary>A per-share amount, bounded. Zero quantity yields zero rather than
    /// dividing.</summary>
    public static decimal PerShare(decimal total, decimal quantity) =>
        quantity == 0m ? 0m : decimal.Round(total / quantity, PerSharePlaces);

    /// <summary>
    /// A position's market value, at the same scale the SQL as-of feeder produces.
    /// </summary>
    /// <remarks>
    /// This one is NOT cosmetic, unlike the rest of this class. Net worth is computed
    /// by two independent routes — the overview values the current <c>holdings</c>
    /// projection in C#, the history series replays legs through
    /// <c>holdings_market_value_as_of_set</c> — and they must produce the SAME number
    /// for the same instant.
    /// <para>
    /// Unrounded, they do not. A C# <c>decimal</c> product keeps the sum of its operands'
    /// scales, so <c>NUMERIC(25,12)</c> quantity times <c>NUMERIC(19,4)</c> price runs
    /// to 16 decimal places, while the feeder bounds its own output to 4 (that ROUND
    /// exists because an unconstrained NUMERIC has to survive the trip into
    /// <see cref="decimal"/>, which throws rather than truncating). WHOLE-share
    /// positions cannot show the difference, because a whole-share product has no
    /// fractional part to round; a 12dp fractional position splits them by a few
    /// millionths of a unit of currency. The boundary matrix on
    /// <c>NetWorthReconciliationTests</c> pins the agreement.
    /// </para>
    /// <para>
    /// Rounding to 4 here rather than to the currency's minor unit is deliberate:
    /// rounding each position to its currency's places BEFORE summing accumulates its
    /// own error across a portfolio. Bound at 4dp internally, on REST and MCP alike;
    /// the SPA presents the figure at the currency's places (ADR-0114 D15).
    /// </para>
    /// <para>
    /// A half rounds away from zero, as Postgres <c>ROUND</c> rounds a NUMERIC.
    /// <see cref="decimal.Round(decimal, int)"/> alone takes a half to the even digit,
    /// so a product landing exactly on a midpoint (2.5 shares at a 4dp price) came out
    /// a ten-thousandth apart on the two routes.
    /// </para>
    /// </remarks>
    public static decimal MarketValue(decimal quantity, decimal price) =>
        decimal.Round(quantity * price, MarketValuePlaces, MidpointRounding.AwayFromZero);
}
