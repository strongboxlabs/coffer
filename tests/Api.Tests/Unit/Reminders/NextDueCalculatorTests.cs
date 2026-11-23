using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Unit.Reminders;

/// <summary>
/// The shared next-due cursor math (ADR-0051) used by both the API
/// (recompute on fire/skip/edit) and the Moneydance importer (seed on import).
/// The defining behavior is the acknowledged FLOOR: an imported series running
/// for years lands its cursor on the first occurrence after the ack date, not
/// on its long-ago first occurrence. Pure (real expander, no DB/clock).
/// </summary>
public sealed class NextDueCalculatorTests
{
    private static readonly RecurrenceExpander Expander = new();
    private const string Monthly10th = "FREQ=MONTHLY;BYMONTHDAY=10";

    [Fact]
    public void No_floor_returns_first_occurrence_from_start()
    {
        var next = NextDueCalculator.NextDue(
            Expander, Monthly10th, new DateOnly(2026, 1, 10), endDate: null, consumedThrough: null);
        Assert.Equal(new DateOnly(2026, 1, 10), next);
    }

    [Fact]
    public void Floor_lands_on_the_first_occurrence_after_the_acknowledged_date()
    {
        // An invented series started years ago (2012-03-04), acknowledged through
        // February 2025: the cursor must be March 2025, NOT the 2012 first occurrence.
        var next = NextDueCalculator.NextDue(
            Expander, Monthly10th, new DateOnly(2012, 3, 4), endDate: null,
            consumedThrough: new DateOnly(2025, 2, 10));
        Assert.Equal(new DateOnly(2025, 3, 10), next);
    }

    [Fact]
    public void Floor_on_a_non_occurrence_date_still_advances_past_it()
    {
        // Ack date isn't itself an occurrence (the 27th); next is the following 10th.
        var next = NextDueCalculator.NextDue(
            Expander, Monthly10th, new DateOnly(2012, 3, 4), endDate: null,
            consumedThrough: new DateOnly(2025, 1, 27));
        Assert.Equal(new DateOnly(2025, 2, 10), next);
    }

    [Fact]
    public void Individually_consumed_slots_after_the_floor_are_skipped()
    {
        var consumed = new HashSet<DateOnly> { new(2025, 3, 10) };
        var next = NextDueCalculator.NextDue(
            Expander, Monthly10th, new DateOnly(2012, 3, 4), endDate: null,
            consumedThrough: new DateOnly(2025, 2, 10), consumedDates: consumed);
        Assert.Equal(new DateOnly(2025, 4, 10), next);
    }

    [Fact]
    public void End_date_before_the_next_open_occurrence_yields_null()
    {
        var next = NextDueCalculator.NextDue(
            Expander, Monthly10th, new DateOnly(2012, 3, 4),
            endDate: new DateOnly(2025, 2, 28),
            consumedThrough: new DateOnly(2025, 2, 10));
        Assert.Null(next);   // next would be 2025-03-10, past the end
    }

    [Fact]
    public void Blank_rrule_is_null()
    {
        Assert.Null(NextDueCalculator.NextDue(
            Expander, "", new DateOnly(2026, 1, 1), endDate: null, consumedThrough: null));
        Assert.Null(NextDueCalculator.NextDue(
            Expander, null, new DateOnly(2026, 1, 1), endDate: null, consumedThrough: null));
    }
}
