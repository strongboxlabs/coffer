namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The balance simulator: Savings' monthly
/// interest, the sweeps of Joint Checking's surplus to Savings, the top-ups back,
/// and Savings' part of the down payment.
/// </summary>
/// <remarks>
/// <para><b>Runs last.</b> Every other pattern has posted, so each Checking day-end is
/// known except for what this pattern moves; it walks the window day by day and
/// decides from the balances as they stand.</para>
///
/// <para><b>Interest</b> on each month's last day is the prior month-end balance
/// times the life year's APY over twelve, to the cent, on Investment &gt; Interest
/// Received.</para>
///
/// <para><b>Sweeps.</b> The business day after each card payment (or a fixed day
/// in a month with none), Checking's surplus over the buffer is swept to Savings in
/// whole hundreds: the lowest Checking stands at over the next
/// <see cref="HorizonDays"/> days, less <see cref="Buffer"/>. Chained, the sweeps
/// keep Checking at or above the buffer. <b>Top-ups:</b> on a day Checking would
/// still end under the buffer, Savings sends what the coming weeks need, in whole
/// five hundreds.</para>
///
/// <para><b>The down payment.</b> On the brokerage's down-payment day in the house
/// month, Savings sends enough, in whole thousands, to keep Checking at the buffer
/// through the closing.</para>
/// </remarks>
public sealed class SavingsPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "savings";

    /// <summary>The balance Checking is kept at or above.</summary>
    public const long Buffer = 4_000_00L;

    /// <summary>Days ahead a sweep or a top-up looks.</summary>
    public const int HorizonDays = 40;

    /// <summary>The smallest sweep.</summary>
    public const long MinimumSweep = 100_00L;

    /// <summary>The nominal sweep day of a month with no card payment.</summary>
    public const int FallbackSweepDay = 27;

    /// <summary>Savings' APY by life year (index 0 is life year one), as a percent.</summary>
    public static IReadOnlyList<decimal> ApyPercent { get; } = [0.5m, 2.0m, 4.2m, 4.4m, 3.8m];

    /// <summary>The mark of every sweep.</summary>
    public const string SweepMark = "sweep";

    /// <summary>The mark of every top-up.</summary>
    public const string TopUpMark = "top-up";

    /// <summary>The mark of every interest payment.</summary>
    public const string InterestMark = "savings-interest";

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 90;

    /// <summary>The APY in life month <paramref name="lifeMonth"/>, as a percent.</summary>
    public static decimal ApyIn(int lifeMonth)
    {
        var year = DemoCalendar.LifeYearOf(lifeMonth) - 1;
        return ApyPercent[year < ApyPercent.Count ? year : ApyPercent.Count - 1];
    }

    /// <summary>The brokerage's down-payment day, on which Savings sends its part.</summary>
    public static DateOnly DownPaymentDate(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(HouseholdTerms.HouseOpens, InvestmentTerms.DownPaymentDay));
    }

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var savings = book.Account(DemoAccountRoles.Savings).Id;
        var interest = book.Base.Category(MdCategoryType.Income, "Investment", "Interest Received");

        var sweepDays = new HashSet<DateOnly>();
        var paidMonths = new HashSet<int>();
        foreach (var payment in book.Marked(DemoCards.PaymentMark))
        {
            sweepDays.Add(DemoCalendar.NextBusinessDayAfter(payment.Date));
            paidMonths.Add(calendar.LifeMonthOf(payment.Date));
        }
        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            if (!paidMonths.Contains(month))
                sweepDays.Add(DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, FallbackSweepDay)));
        }

        var downPayment = DownPaymentDate(calendar);
        var closing = HousingPattern.ClosingDate(calendar);

        DemoTxn Move(DateOnly date, string from, string to, long amount, string description, string memo) =>
            book.Post(this, new DemoTxn
            {
                AccountId = from,
                Date = date,
                Description = description,
                Memo = memo,
                BankPayee = from == checking ? "ONLINE TRANSFER TO SAV ****4836" : "ONLINE TRANSFER TO CHK ****4821",
                Splits = [DemoSplit.Bank(to, amount)],
            });

        long LowestAhead(DateOnly from, DateOnly through) =>
            book.LowestDayEnd(checking, from, through > calendar.AsOf ? calendar.AsOf : through).Balance;

        for (var day = calendar.WindowStart; day <= calendar.AsOf; day = day.AddDays(1))
        {
            if (day == downPayment)
            {
                var shortfall = Buffer - LowestAhead(day, closing.AddDays(7));
                var amount = shortfall <= 0 ? 10_000_00L : (shortfall + 999_99L) / 1_000_00L * 1_000_00L;
                book.Mark(DemoEvents.DownPayment, Move(day, savings, checking, amount,
                    "Transfer to Joint Checking", "Down payment"));
            }

            if (sweepDays.Contains(day))
            {
                var surplus = LowestAhead(day, day.AddDays(HorizonDays)) - Buffer;
                var sweep = surplus / 100_00L * 100_00L;
                if (sweep >= MinimumSweep)
                    book.Mark(SweepMark, Move(day, checking, savings, sweep, "Transfer to Savings", "Monthly sweep"));
            }

            if (book.BalanceAtEndOf(checking, day) < Buffer)
            {
                var need = Buffer - LowestAhead(day, day.AddDays(HorizonDays));
                var amount = (need + 499_99L) / 500_00L * 500_00L;
                book.Mark(TopUpMark, Move(day, savings, checking, amount, "Transfer to Joint Checking", "Top-up"));
            }

            if (day == calendar.MonthEnd(calendar.LifeMonthOf(day)))
            {
                var month = calendar.LifeMonthOf(day);
                var prior = book.BalanceAtEndOf(savings, calendar.MonthStart(month).AddDays(-1));
                var paid = DemoAmortization.RoundCents(prior * ApyIn(month) / 100m / 12m);
                if (paid > 0)
                {
                    book.Mark(InterestMark, book.Post(this, new DemoTxn
                    {
                        AccountId = savings,
                        Date = day,
                        Description = "Interest paid",
                        BankPayee = "INTEREST PAYMENT",
                        Splits = [DemoSplit.Bank(interest, -paid)],
                    }));
                }
            }
        }
    }
}
