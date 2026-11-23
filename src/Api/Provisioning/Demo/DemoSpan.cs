namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// How much of the generated window the export carries: all of it, or only its
/// trailing months.
/// </summary>
/// <remarks>
/// A short span is the only short-span mechanism. It is applied AFTER full
/// generation, so every pattern runs over the whole window and the truncated
/// document's end balances, holdings and lots equal the full one's: bank-route rows
/// before <see cref="KeepsFrom"/> are dropped and each account's opening balance is
/// raised by what they moved; investment-route rows, accounts, securities, prices,
/// splits and reminders are kept in full.
/// <para>So from the day before <see cref="KeepsFrom"/> on, every account ends each
/// day where the full document does. Before it, a bank account is moved only by the
/// kept investment-route legs, while its raised opening balance already nets out
/// every dropped bank row: one that an early investment row pays into opens low,
/// even below zero, and climbs as those legs land. A short span is a fixture for
/// its trailing months; a balance floor over the whole window holds only on the
/// full document.</para>
/// </remarks>
public abstract record DemoSpan
{
    /// <summary>The most trailing months a short span may ask for: the whole window.</summary>
    public const int MaxMonths = DemoCalendar.WindowMonths;

    private protected DemoSpan()
    {
    }

    /// <summary>The whole window.</summary>
    public static readonly DemoSpan Full = new FullSpan();

    /// <summary>
    /// The trailing <paramref name="months"/> whole months before AsOf's month,
    /// plus AsOf's own partial month.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="months"/> is
    /// outside 1 to <see cref="MaxMonths"/>.</exception>
    public static DemoSpan LastMonths(int months)
    {
        if (months < 1 || months > MaxMonths)
        {
            throw new ArgumentOutOfRangeException(
                nameof(months), months, $"a short span is 1 to {MaxMonths} months");
        }
        return new LastMonthsSpan(months);
    }

    /// <summary>
    /// The first day whose bank-route rows the export keeps, or null when it keeps
    /// every row.
    /// </summary>
    public abstract DateOnly? KeepsFrom(DemoCalendar calendar);
}

/// <summary>The whole window: nothing is dropped.</summary>
public sealed record FullSpan : DemoSpan
{
    internal FullSpan()
    {
    }

    /// <inheritdoc />
    public override DateOnly? KeepsFrom(DemoCalendar calendar) => null;
}

/// <summary>The trailing <see cref="Months"/> months of the window.</summary>
public sealed record LastMonthsSpan : DemoSpan
{
    internal LastMonthsSpan(int months) => Months = months;

    /// <summary>Whole months kept before AsOf's month.</summary>
    public int Months { get; }

    /// <inheritdoc />
    /// <remarks>Day 1 of the month <see cref="Months"/> months before AsOf's month.
    /// At the maximum that is the window start, so nothing is dropped.</remarks>
    public override DateOnly? KeepsFrom(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return calendar.MonthStart(DemoCalendar.CurrentLifeMonth - Months);
    }
}
