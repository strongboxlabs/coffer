using System.Text.Json;
using Coffer.Api.Provisioning.Demo;
using Coffer.Domain.Reminders;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The monthly Internet reminder is never in flight: on every AsOf, its imported next
/// due date is its first occurrence strictly after AsOf. A skip cascades onto every
/// earlier occurrence not yet acted on, so an occurrence due on or before AsOf whose
/// bill is still to post would be skipped along with the seeded skip's target.
/// </summary>
/// <remarks>
/// <para>The sweep runs the one pattern that posts the reminder's bills
/// (<see cref="BillsPattern"/>) over a fresh book per day and reads the reminder
/// <see cref="DemoReminders"/> writes into the export through the importer's own
/// reader, rrule builder and next-due calculator, as the import computes the next due
/// date. Those bills depend on the calendar alone, and the first fact pins the
/// shortcut: at the tests' date, the reminder it reads has exactly the fields of the
/// full export's.</para>
/// </remarks>
public sealed class DemoInternetReminderSweepTests
{
    /// <summary>Two full years of consecutive days, starting at the tests' date.</summary>
    private const int SweepDays = 731;

    [Fact]
    public void The_sweeps_reminder_is_the_full_exports()
    {
        using var export = DemoTestExports.FullExport();
        var full = export.AllItems.Single(i => i.ObjType == "reminder" && i.GetInt("monthlydays") == BillsPattern.InternetDay);

        var (_, reminder) = BillsOnly(DemoTestExports.AsOf);
        Assert.Equal(full.Id, reminder.Id);
        Assert.Equal(
            full.FieldNames.Where(f => f is not ("id" or "obj_type")).Select(f => $"{f}={full.GetString(f)}"),
            reminder.Fields.Select(f => $"{f.Key}={f.Value}"));
    }

    [Fact]
    public void On_every_day_of_two_years_the_imported_next_due_is_the_first_occurrence_after_AsOf()
    {
        var expander = new RecurrenceExpander();
        var problems = new List<string>();
        var shutBillDays = 0;
        for (var i = 0; i < SweepDays; i++)
        {
            var asOf = DemoTestExports.AsOf.AddDays(i);
            var (book, demoReminder) = BillsOnly(asOf);
            var reminder = Read(demoReminder);
            var rrule = MdReminderRrule.Build(reminder)!;
            var start = MdItem.ParseMdDate(reminder.StartDate)!.Value;
            var acknowledged = MdItem.ParseMdDate(reminder.AcknowledgedDate)!.Value;
            var nextDue = NextDueCalculator.NextDue(expander, rrule, start, null, consumedThrough: acknowledged);
            var firstAfter = expander.Expand(rrule, start, asOf.AddDays(1), asOf.AddDays(62))[0];

            // A day on which a bill moved to the next business day would still be in flight.
            var slot = expander.Expand(rrule, start, asOf.AddDays(-31), asOf)[^1];
            if (DemoCalendar.OnOrNextBusinessDay(slot) > asOf) shutBillDays++;

            var lastBill = book.Marked(BillsPattern.InternetMark).Max(t => t.Date);
            if (nextDue != firstAfter)
                problems.Add($"{asOf:yyyy-MM-dd}: next due {nextDue:yyyy-MM-dd}, not {firstAfter:yyyy-MM-dd}");
            if (lastBill != acknowledged)
                problems.Add($"{asOf:yyyy-MM-dd}: the last bill is dated {lastBill:yyyy-MM-dd}, acknowledged {acknowledged:yyyy-MM-dd}");
        }

        Assert.Empty(problems);
        Assert.True(shutBillDays > 0, "the sweep has no day on which a bill moved to a business day would be in flight");
    }

    // A fresh book with only the bills, and the Internet reminder the export writes for it.
    private static (DemoBook Book, DemoReminder Reminder) BillsOnly(DateOnly asOf)
    {
        var (book, _) = DemoExportGenerator.RunUnverified(
            new DemoSpec(DemoSpec.DefaultSeed, asOf, DemoSpan.LastMonths(2)), _ => [new BillsPattern()]);
        return (book, DemoReminders.For(book).Single(r => r.Id == DemoReminders.InternetId(book.Ids)));
    }

    // The reminder as the importer reads its export item.
    private static MdReminder Read(DemoReminder reminder)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in reminder.Fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var fields = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return MdReminder.From(new MdItem(reminder.Id, "reminder", fields));
    }
}
