using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Unit.Db;

/// <summary>
/// <see cref="BudgetProgressRepository.MonthsAveragedByDay"/>: how many of the window's
/// months each day of the month averages over.
/// </summary>
/// <remarks>
/// <para>A day a window month does not have (the 31st of a 30-day month, the 29th to
/// 31st of February) is read as that month's last day. That is what keeps a ledger older
/// than the window dividing every day by the window's length, and what decides whether a
/// month in which the ledger began on one of those last days counts for them. The
/// integration suites write every window row on an early day of the month, so these pin
/// the late days directly.</para>
/// <para>The window here is February, March and April: a month shorter than every day
/// past its 28th, a 31-day month, and a 30-day month.</para>
/// </remarks>
public sealed class BudgetMonthsAveragedByDayTests
{
    private const int Window = 3;
    private const int LastDayOfAnyMonth = 31;
    private static readonly DateOnly WindowFirstDay = new(2026, 2, 1);
    private static readonly DateOnly FebruaryLast = new(2026, 2, 28);
    private static readonly DateOnly MarchLast = new(2026, 3, 31);

    private static int[] Months(DateOnly? firstTransaction) =>
        BudgetProgressRepository.MonthsAveragedByDay(firstTransaction, WindowFirstDay, Window);

    private static IEnumerable<int> EveryDay() => Enumerable.Range(1, LastDayOfAnyMonth);

    [Fact]
    public void A_ledger_with_no_transactions_divides_every_day_by_the_whole_window()
    {
        var months = Months(null);
        Assert.All(EveryDay(), day => Assert.Equal(Window, months[day]));
    }

    [Fact]
    public void A_ledger_older_than_the_window_divides_every_day_by_the_whole_window_the_late_days_included()
    {
        var months = Months(WindowFirstDay.AddDays(-1));
        Assert.All(EveryDay(), day => Assert.Equal(Window, months[day]));
    }

    [Fact]
    public void A_ledger_that_began_on_the_31st_counts_that_month_for_the_31st_and_a_shorter_month_after_it_too()
    {
        var months = Months(MarchLast);

        // The 31st: March, which has it, and April, read on its last day. Not February,
        // whose last day is before the ledger's first.
        const int MonthsForThe31st = 2;
        Assert.Equal(MonthsForThe31st, months[MarchLast.Day]);
        // The 30th: April only, since March's 30th is before the ledger's first.
        const int MonthsForThe30th = 1;
        Assert.Equal(MonthsForThe30th, months[MarchLast.Day - 1]);
    }

    [Fact]
    public void A_ledger_that_began_on_Februarys_last_day_counts_February_for_the_days_February_lacks()
    {
        var months = Months(FebruaryLast);

        // Every day from February's last on reads February at its last day, which is the
        // ledger's first: the whole window.
        Assert.All(Enumerable.Range(FebruaryLast.Day, LastDayOfAnyMonth - FebruaryLast.Day + 1),
            day => Assert.Equal(Window, months[day]));
        // A day before it misses February.
        Assert.Equal(Window - 1, months[FebruaryLast.Day - 1]);
    }

    [Fact]
    public void A_ledger_that_began_after_the_window_still_divides_by_one()
    {
        var months = Months(WindowFirstDay.AddMonths(Window));
        Assert.All(EveryDay(), day => Assert.Equal(1, months[day]));
    }
}
