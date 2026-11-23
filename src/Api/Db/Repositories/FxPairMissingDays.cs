namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The days a needed pair has no rate for (ADR-0114 D9), as the rates view lists them: the
/// spans of days from the first day a figure needs the pair through the server's UTC today
/// on which a conversion reads "rate unavailable".
/// </summary>
/// <remarks>
/// <para>It is <see cref="ExchangeRateTable"/>'s rule read the other way round, not a
/// second rule: a day before today is missing when it is before the pair's first stored
/// rate or after its coverage (<see cref="ExchangeRateTable.RateOn"/>), and today, which a
/// current value converts at (<see cref="RateDay.Of(DateOnly, DateOnly)"/>), when the pair
/// has no rate on or before it or its coverage ends before yesterday
/// (<see cref="ExchangeRateTable.Latest"/>). A day between two stored rates reads the
/// earlier one, so no gap opens inside the stored days; what is missing is at most the
/// stretch before the first rate and the stretch after coverage ends.
/// <c>FxPairMissingDaysTests</c> holds the two against each other day by day.</para>
/// <para>A later day than today converts at today's rate, so today is the last day listed.
/// A pair needed only for its latest rate (no first day) is asked about today alone, as is
/// one whose first needed day is still ahead.</para>
/// </remarks>
public static class FxPairMissingDays
{
    /// <summary>The missing spans, earliest first, each from its first missing day through
    /// its last; empty when every needed day has a rate.</summary>
    /// <param name="neededFrom">The first day a figure needs the pair
    /// (<see cref="NeededFxPair.NeededFrom"/>); null when only the latest rate is needed.</param>
    /// <param name="firstRate">The pair's first stored rate's day; null when none is stored.</param>
    /// <param name="coveredThrough">The pair's coverage: the later of its fetched-through day
    /// and its latest hand-entered day; null when it has neither.</param>
    /// <param name="serverToday">The server clock's UTC date.</param>
    public static IReadOnlyList<(DateOnly From, DateOnly Through)> Of(
        DateOnly? neededFrom, DateOnly? firstRate, DateOnly? coveredThrough, DateOnly serverToday)
    {
        var today = serverToday;
        var start = neededFrom is { } n && n < today ? n : today;

        // Nothing stored, nothing covered, or the first rate not yet reached: every day.
        if (firstRate is not { } first || coveredThrough is not { } covered || first > today)
            return [(start, today)];

        var spans = new List<(DateOnly From, DateOnly Through)>(2);
        // Before the first rate: no rate on or before the day (today is not here: first <= today).
        if (start < first) spans.Add((start, first.AddDays(-1)));
        // After coverage: a day before today past it is missing, and so is today once
        // coverage ends before yesterday; covered through yesterday leaves both readable.
        if (covered < today.AddDays(-1))
        {
            var from = covered.AddDays(1) > start ? covered.AddDays(1) : start;
            if (spans.Count > 0 && from <= spans[0].Through.AddDays(1))
                spans[0] = (spans[0].From, today);
            else
                spans.Add((from, today));
        }
        return spans;
    }
}
