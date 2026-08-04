using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Coffer.Domain.Reminders;

/// <summary>
/// Expands an RFC 5545 <c>RRULE</c> (ADR-0047) into the occurrence dates that
/// fall in a window, using Ical.Net for the date math (month-end clamping,
/// last-day / nth-weekday, interval stepping, UNTIL/COUNT) rather than a
/// hand-rolled walk. Pure + deterministic — no DB, no clock.
/// </summary>
/// <remarks>
/// Reminders are date-based (no time-of-day), so everything is
/// <see cref="DateOnly"/>; the rule's DTSTART is the series start date. Shared
/// (ADR-0051) by the API and the Moneydance importer so occurrence math
/// has a single implementation.
/// </remarks>
public sealed class RecurrenceExpander
{
    /// <summary>
    /// Occurrence dates of <paramref name="rrule"/> anchored at
    /// <paramref name="seriesStart"/> that fall within
    /// <c>[windowStart, windowEnd]</c> (both inclusive), ascending and
    /// de-duplicated. Empty when the rule is blank or the window is inverted.
    /// The caller clips by a series end date by passing
    /// <paramref name="windowEnd"/> = min(window, end) — the expander itself
    /// only honors an UNTIL/COUNT carried in the rule.
    /// </summary>
    public IReadOnlyList<DateOnly> Expand(
        string? rrule, DateOnly seriesStart, DateOnly windowStart, DateOnly windowEnd)
    {
        if (string.IsNullOrWhiteSpace(rrule) || windowEnd < windowStart)
            return Array.Empty<DateOnly>();

        var ev = new CalendarEvent
        {
            Start = new CalDateTime(seriesStart.Year, seriesStart.Month, seriesStart.Day),
            RecurrenceRule = new RecurrencePattern(rrule),
        };

        // Ical.Net 5.x GetOccurrences(start) yields an ORDERED, lazy (possibly
        // infinite) stream from `start` forward — so we take while we're still
        // inside the window and stop at the first occurrence past windowEnd.
        var from = new CalDateTime(windowStart.Year, windowStart.Month, windowStart.Day);

        var dates = new SortedSet<DateOnly>();
        foreach (var occurrence in ev.GetOccurrences(from))
        {
            var d = DateOnly.FromDateTime(occurrence.Period.StartTime.Value);
            if (d > windowEnd) break;
            if (d >= windowStart) dates.Add(d);
        }

        return dates.ToList();
    }

    /// <summary>
    /// True when <paramref name="rrule"/> is a non-empty RFC 5545 rule Ical.Net
    /// can parse. Drives the <c>reminder-rrule-invalid</c> 422 on create/edit —
    /// validation lives here (next to the parser) so the endpoint doesn't
    /// duplicate Ical.Net knowledge. A blank rule is invalid for a manual
    /// reminder (the recurrence picker always produces a concrete rule).
    /// </summary>
    public bool IsValidRrule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return false;
        try
        {
            _ = new RecurrencePattern(rrule);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
