namespace Coffer.Domain.Reminders;

/// <summary>
/// The next-due cursor for a recurring series (ADR-0047 §9.2), shared by the API
/// (recompute on fire / skip / edit) and the Moneydance importer (seed the
/// cursor on import). The cursor is the earliest RRULE occurrence that is BOTH
/// strictly after the consumed floor — <paramref name="consumedThrough"/>, the
/// last date treated as already handled (Moneydance's acknowledged date on
/// import, or the latest fired/skipped date) — AND not an individually consumed
/// slot (<paramref name="consumedDates"/>: specific fired/skipped occurrences
/// after the floor), clamped to the series end.
/// </summary>
/// <remarks>
/// The floor is what stops a long pre-import history from stranding the cursor:
/// an imported reminder running since 2015 with an acknowledged date of "last
/// month" lands the cursor on next month's occurrence, not 2015 — and the
/// caller's catch-up cascade likewise skips only occurrences after the floor, so
/// firing doesn't mark a decade of phantom backlog. The expansion horizon is
/// anchored to the later of the series start, the floor, and the latest
/// individually consumed slot, then extended two years so the window can't be
/// exhausted before the first open slot.
/// </remarks>
public static class NextDueCalculator
{
    /// <summary>
    /// The next-due date, or null for a blank/no-RRULE series or once no open
    /// occurrence remains on/before the series end.
    /// </summary>
    public static DateOnly? NextDue(
        RecurrenceExpander expander,
        string? rrule,
        DateOnly startDate,
        DateOnly? endDate,
        DateOnly? consumedThrough,
        IReadOnlySet<DateOnly>? consumedDates = null)
    {
        ArgumentNullException.ThrowIfNull(expander);
        if (string.IsNullOrWhiteSpace(rrule)) return null;

        var anchor = startDate;
        if (consumedThrough is { } floor && floor > anchor) anchor = floor;
        if (consumedDates is { Count: > 0 })
        {
            var latest = consumedDates.Max();
            if (latest > anchor) anchor = latest;
        }

        var horizon = anchor.AddYears(2);
        if (endDate is { } end && end < horizon) horizon = end;

        foreach (var d in expander.Expand(rrule, startDate, startDate, horizon))
        {
            if (consumedThrough is { } ct && d <= ct) continue;
            if (consumedDates is not null && consumedDates.Contains(d)) continue;
            return d;
        }
        return null;
    }
}
