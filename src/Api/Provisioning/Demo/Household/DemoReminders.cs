using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>One Moneydance reminder the export carries, as its item fields.</summary>
/// <param name="Id">The reminder's MD id.</param>
/// <param name="Start">Its first occurrence (<c>sdt</c>).</param>
/// <param name="Acknowledged">The occurrence of its last bill in the history (<c>ackdt</c>).</param>
/// <param name="Fields">Every field after <c>obj_type</c> and <c>id</c>, in order.</param>
public sealed record DemoReminder(
    string Id, DateOnly Start, DateOnly Acknowledged, IReadOnlyList<KeyValuePair<string, string>> Fields);

/// <summary>
/// The two Moneydance reminders of the export: the monthly Internet
/// bill, which auto-commits three days ahead, and the yearly vehicle registration,
/// which is manual and ends ten years after it starts.
/// </summary>
/// <remarks>
/// <para>Each starts on its first occurrence in the window and is acknowledged
/// through the occurrence of its last bill in the history, so every acknowledged
/// occurrence has its row (<see cref="BillsPattern"/>) and the importer's next due
/// date is the first occurrence with no bill. The Internet bill posts on its own
/// day of the month, so for it that is always the first occurrence after AsOf. The
/// registration's bill moves to the next business day, so on the few days when its
/// occurrence has come but that bill has not, that one occurrence is due, which is
/// what a reminder shows on such a day.</para>
///
/// <para>Moneydance reads a reminder's cadence in the order daily, monthly, weekly,
/// yearly, so each clears the fields before its own. There is no Moneydance reminder
/// for the mortgage or for Electric: the seeder creates those.</para>
/// </remarks>
public static class DemoReminders
{
    /// <summary>The monthly Internet reminder's id.</summary>
    public static string InternetId(DemoIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Id("reminder", "internet", 0);
    }

    /// <summary>The vehicle registration reminder's id.</summary>
    public static string RegistrationId(DemoIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Id("reminder", "registration", 0);
    }

    /// <summary>The reminders, in export order.</summary>
    public static IReadOnlyList<DemoReminder> For(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var calendar = book.Calendar;
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;

        // Each is acknowledged through the occurrence of its last bill in the
        // history. The Internet bill posts on its occurrence, so for it this is the
        // last occurrence on or before AsOf. The registration's bill posts on the
        // occurrence or the next business day, so while that bill is still in flight
        // (its business day falls after AsOf) the occurrence before it is the last
        // acknowledged one.
        var internetStart = calendar.DayInMonth(0, BillsPattern.InternetDay);
        var internetAck = calendar.DayInMonth(
            calendar.LifeMonthOf(LastBill(book, BillsPattern.InternetMark).Date), BillsPattern.InternetDay);
        var internetPrice = BillsPattern.InternetIn(DemoCalendar.CurrentLifeMonth);
        var internetId = InternetId(book.Ids);

        var first = new DateOnly(calendar.WindowStart.Year, BillsPattern.RegistrationMonth, BillsPattern.RegistrationDay);
        if (first < calendar.WindowStart) first = first.AddYears(1);
        var last = new DateOnly(
            LastBill(book, BillsPattern.RegistrationMark).Date.Year, BillsPattern.RegistrationMonth, BillsPattern.RegistrationDay);
        var registrationPrice = BillsPattern.RegistrationIn(calendar.LifeMonthOf(last));
        var registrationId = RegistrationId(book.Ids);

        return
        [
            new DemoReminder(internetId, internetStart, internetAck,
            [
                new("desc", BillsPattern.InternetPayee),
                new("memo", BillsPattern.InternetMemo),
                new("type", "monthly"),
                new("sdt", Date(internetStart)),
                new("ackdt", Date(internetAck)),
                new("acdays", "3"),
                new("daily", "0"),
                new("monthlydays", BillsPattern.InternetDay.ToString(CultureInfo.InvariantCulture)),
                new("monthlymod", "1"),
                new("weeklydays", string.Empty),
                new("yearly", "0"),
                new("is_loan_reminder", "0"),
                .. Template(book, internetId, checking, BillsPattern.InternetPayee, BillsPattern.InternetMemo, internetStart,
                    book.Base.Category(MdCategoryType.Expense, "Bills", "Internet"), internetPrice),
            ]),
            new DemoReminder(registrationId, first, last,
            [
                new("desc", BillsPattern.RegistrationPayee),
                new("memo", BillsPattern.RegistrationMemo),
                new("type", "yearly"),
                new("sdt", Date(first)),
                new("ackdt", Date(last)),
                new("acdays", "-1"),
                new("ldt", Date(first.AddYears(10))),
                new("daily", "0"),
                new("monthlydays", string.Empty),
                new("monthlymod", "0"),
                new("weeklydays", string.Empty),
                new("yearly", "1"),
                new("is_loan_reminder", "0"),
                .. Template(book, registrationId, checking, BillsPattern.RegistrationPayee, BillsPattern.RegistrationMemo, first,
                    book.Base.Category(MdCategoryType.Expense, "Automotive", "Misc"), registrationPrice),
            ]),
        ];
    }

    private static DemoTxn LastBill(DemoBook book, string mark)
    {
        var bills = book.Marked(mark);
        if (bills.Count == 0) throw new InvalidOperationException($"no bill is marked {mark}, so its reminder has no history");
        return bills.MaxBy(t => t.Date)!;
    }

    private static IEnumerable<KeyValuePair<string, string>> Template(
        DemoBook book, string reminderId, string accountId, string payee, string memo, DateOnly start, string categoryId, long amount)
    {
        yield return new("txn.acctid", accountId);
        yield return new("txn.desc", payee);
        yield return new("txn.memo", memo);
        yield return new("txn.dt", Date(start));
        yield return new("txn.0.id", book.Ids.Id("reminder-split", reminderId, 0));
        yield return new("txn.0.acctid", categoryId);
        yield return new("txn.0.samt", amount.ToString(CultureInfo.InvariantCulture));
        yield return new("txn.0.pamt", (-amount).ToString(CultureInfo.InvariantCulture));
        yield return new("txn.0.desc", memo);
    }

    private static string Date(DateOnly date) => DemoCalendar.MdDate(date).ToString(CultureInfo.InvariantCulture);
}
