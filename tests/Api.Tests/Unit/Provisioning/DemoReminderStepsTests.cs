using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Unit.Provisioning;

/// <summary>
/// The dates the reminders step computes its writes by: the rule slot each left-out bill is
/// posted as, the managed loan payment's first due date after the Demo's last day, and the
/// occurrence the skip example skips.
/// </summary>
public sealed class DemoReminderStepsTests
{
    private static readonly RecurrenceExpander Expander = new();

    [Fact]
    public void Each_bill_is_its_own_month_s_slot_even_when_moved_to_a_later_business_day()
    {
        // The second bill's day fell on a weekend and posted two days later; the input is
        // out of order.
        var bills = new[]
        {
            new DemoDatedAmount(new DateOnly(2026, 11, 12), 300),
            new DemoDatedAmount(new DateOnly(2026, 9, 12), 100),
            new DemoDatedAmount(new DateOnly(2026, 10, 14), 200),
        };

        var slots = DemoReminderSteps.ElectricSlots(bills, 12, Expander);

        Assert.Equal(
            [
                (new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 12)),
                (new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14)),
                (new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12)),
            ],
            slots.Select(s => (s.Slot, s.Bill.Date)));
        Assert.Equal([100L, 200L, 300L], slots.Select(s => s.Bill.Amount));
    }

    [Fact]
    public void A_bill_before_its_month_s_slot_has_no_slot_and_fails()
    {
        var bills = new[]
        {
            new DemoDatedAmount(new DateOnly(2026, 9, 12), 100),
            new DemoDatedAmount(new DateOnly(2026, 10, 9), 200),
        };

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReminderSteps.ElectricSlots(bills, 12, Expander));
        Assert.Contains("2026-10-09", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_bills_in_one_month_share_its_slot_and_fail()
    {
        var bills = new[]
        {
            new DemoDatedAmount(new DateOnly(2026, 10, 12), 100),
            new DemoDatedAmount(new DateOnly(2026, 10, 20), 200),
        };

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReminderSteps.ElectricSlots(bills, 12, Expander));
        Assert.Contains("2026-10-12", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-11-18", "2026-12-01")]
    [InlineData("2026-12-01", "2027-01-01")]
    [InlineData("2026-11-30", "2026-12-01")]
    [InlineData("2023-01-31", "2023-06-01")]
    public void The_first_due_date_after_AsOf_is_strictly_after_it(string asOf, string expected)
    {
        var first = new DateOnly(2023, 6, 1);

        Assert.Equal(
            DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            DemoReminderSteps.FirstMonthlyDueAfter(first, DateOnly.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void The_skip_takes_the_first_occurrence_after_AsOf_when_it_is_the_one_due_next()
    {
        var asOf = new DateOnly(2026, 11, 18);
        var after = Expander.Expand(DemoReminderSteps.MonthlyRule(15), new DateOnly(2026, 1, 15), asOf.AddDays(1), asOf.AddDays(366));

        Assert.Equal(new DateOnly(2026, 12, 15), DemoReminderSteps.SkipTarget(after, new DateOnly(2026, 12, 15)));
    }

    [Fact]
    public void An_occurrence_still_due_on_AsOf_whose_bill_posts_later_is_never_skipped_past()
    {
        // The 15th fell on a Saturday, so its bill posts on the Monday, after a Sunday
        // AsOf: that occurrence is still due, and a skip after it would skip it too.
        var asOf = new DateOnly(2026, 8, 16);
        var after = Expander.Expand(DemoReminderSteps.MonthlyRule(15), new DateOnly(2026, 1, 15), asOf.AddDays(1), asOf.AddDays(366));
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2026, 8, 15).DayOfWeek);

        var failure = Assert.Throws<DemoSeedStepException>(() => DemoReminderSteps.SkipTarget(after, new DateOnly(2026, 8, 15)));
        Assert.Equal(
            "the imported Internet reminder is next due on 2026-08-15, not on 2026-09-15, its first occurrence after "
            + "the Demo's last day, and a skip there would also skip the one due",
            failure.Message);
    }

    [Fact]
    public void A_reminder_with_no_occurrence_after_AsOf_or_none_due_has_nothing_to_skip()
    {
        Assert.Throws<DemoSeedStepException>(() => DemoReminderSteps.SkipTarget([], new DateOnly(2026, 12, 15)));
        Assert.Throws<DemoSeedStepException>(() => DemoReminderSteps.SkipTarget([new DateOnly(2026, 12, 15)], null));
    }

    [Fact]
    public void The_monthly_rule_is_one_the_reminders_endpoint_accepts()
    {
        var rule = DemoReminderSteps.MonthlyRule(12);

        Assert.True(Expander.IsValidRrule(rule));
        Assert.Equal(
            [new DateOnly(2026, 1, 12), new DateOnly(2026, 2, 12)],
            Expander.Expand(rule, new DateOnly(2026, 1, 12), new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28)));
    }
}
