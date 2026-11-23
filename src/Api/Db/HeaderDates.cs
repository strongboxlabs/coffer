namespace Coffer.Api.Db;

/// <summary>
/// The one rule every save path applies to a header date a request carries
/// (<c>posted_at</c>, <c>transacted_at</c>): a value on the stored row's own
/// calendar day is not an edit.
/// </summary>
/// <remarks>
/// <para>The editors work in days. They show a date picker and send the chosen
/// day back as midnight UTC, so a row whose stored timestamp carries a time of
/// day (every feed row does) comes back on the same day at a different instant
/// even when nobody touched the date. Assigning that value would rewrite the
/// timestamp, move the row among its same-day neighbours (the register and the
/// FIFO walk both order by it), and, on a row never edited before, capture
/// originals for a change the user did not make.</para>
///
/// <para>So a requested date on the stored day keeps the stored timestamp, to
/// the microsecond, and a requested date on another day is applied as sent.
/// The day is the UTC calendar day, the convention the server already uses for
/// <c>posted_at</c> (ADR-0084 D3, <see cref="TradePriceFromLegInterceptor"/>):
/// an <see cref="DateTimeKind.Unspecified"/> value is already UTC wall-clock,
/// so it is stamped UTC rather than shifted by the local offset.</para>
///
/// <para>Comparing days rather than instants is what makes the rule
/// scale-proof: a stored value read back at <c>timestamptz</c>'s microsecond
/// scale and a request value at tick scale can differ in their last digits,
/// but never in their day.</para>
/// </remarks>
internal static class HeaderDates
{
    /// <summary>The UTC calendar day of <paramref name="value"/>.</summary>
    public static DateOnly UtcDay(DateTime value) =>
        DateOnly.FromDateTime(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());

    /// <summary>True when <paramref name="requested"/> names a different day
    /// than <paramref name="stored"/>, i.e. when applying it is an edit. A
    /// missing stored value (the entity's <c>transacted_at</c> is nullable in
    /// C# though NOT NULL in the schema) is always changed.</summary>
    public static bool Changes(DateTime? stored, DateTime requested) =>
        stored is not { } s || UtcDay(s) != UtcDay(requested);

    /// <summary>The value to store: <paramref name="stored"/> unchanged when
    /// <paramref name="requested"/> is on its day, otherwise
    /// <paramref name="requested"/>.</summary>
    public static DateTime Resolve(DateTime? stored, DateTime requested) =>
        Changes(stored, requested) ? requested : stored!.Value;
}
