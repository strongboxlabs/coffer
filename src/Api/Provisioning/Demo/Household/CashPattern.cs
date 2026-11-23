namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The cash wallet: small purchases typed in by hand, and ATM
/// withdrawals from Joint Checking that keep the wallet from going below zero.
/// </summary>
/// <remarks>
/// <para><b>Spending.</b> About <see cref="SpendsPerMonth"/> hand-entered purchases a
/// month on dining, groceries, personal and car sundries, and from daycare's month
/// a sitter now and then.</para>
///
/// <para><b>ATM.</b> Twice a month, once in each half, a withdrawal in multiples of
/// <see cref="AtmUnit"/> that tops the wallet up to a few notes above the lowest it
/// reaches before the next one; every <see cref="FeeOneIn"/>th carries an
/// out-of-network fee as its own row. Then one pass over the window: on any day the
/// wallet would still end below zero, an extra withdrawal comes first.</para>
/// </remarks>
public sealed class CashPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "cash";

    /// <summary>Hand-entered cash purchases a month, on average.</summary>
    public const decimal SpendsPerMonth = 14m;

    /// <summary>The note every withdrawal is a multiple of.</summary>
    public const long AtmUnit = 20_00L;

    /// <summary>One withdrawal in this many carries a fee.</summary>
    public const int FeeOneIn = 6;

    /// <summary>The out-of-network fee.</summary>
    public const long AtmFee = 3_50L;

    private static readonly (string Payee, string[] Category, long Min, long Max)[] Spends =
    [
        ("Copperleaf Coffee", ["Personal", "Dining"], 3_50L, 12_00L),
        ("Street food cart", ["Personal", "Dining"], 6_00L, 18_00L),
        ("Farmers market", ["Personal", "Groceries"], 8_00L, 40_00L),
        ("Corner store", ["Personal", "Misc"], 2_00L, 20_00L),
        ("Laundromat", ["Personal", "Misc"], 4_00L, 14_00L),
        ("Meter parking", ["Automotive", "Misc"], 2_00L, 12_00L),
    ];

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 85;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        var wallet = book.Account(DemoAccountRoles.CashWallet);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        var fees = book.Base.Category(MdCategoryType.Expense, "Bank Charge", "Service Charges");
        var atmCount = 0;
        var withdrawalDays = new List<DateOnly>();
        var cushions = new List<long>();

        void Atm(DateOnly date, long amount)
        {
            book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = "ATM withdrawal",
                BankPayee = "ATM WITHDRAWAL 4471 HIGH ST",
                Splits = [DemoSplit.Bank(wallet.Id, amount)],
            });
            atmCount++;
            if (atmCount % FeeOneIn == 0)
            {
                book.Post(this, new DemoTxn
                {
                    AccountId = checking,
                    Date = date,
                    Description = "ATM fee",
                    Memo = "Out-of-network withdrawal",
                    BankPayee = "NON-NETWORK ATM FEE",
                    Splits = [DemoSplit.Bank(fees, AtmFee)],
                });
            }
        }

        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var stream = rng.Stream(Name, calendar.MonthStart(month));
            // Days are drawn over the whole calendar month, and the partial current
            // month drops the ones after AsOf, so it keeps the monthly daily rate.
            var last = calendar.MonthEnd(month).Day;

            var count = stream.NextCount(SpendsPerMonth);
            for (var i = 0; i < count; i++)
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(1, last));
                var (payee, category, min, max) = stream.Choice(Spends);
                var amount = DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(min, max));
                if (calendar.Contains(date)) Spend(book, wallet, date, payee, category, amount);
            }
            if (month >= BillsPattern.DaycareFrom && stream.Chance(0.6m))
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(1, last));
                var amount = stream.NextLong(3, 6) * 20_00L;
                if (calendar.Contains(date)) Spend(book, wallet, date, "Babysitter", ["Childcare"], amount);
            }

            // Two withdrawal days, one in each half of the month.
            withdrawalDays.Add(calendar.DayInMonth(month, (int)stream.NextLong(1, 14)));
            var second = calendar.DayInMonth(month, (int)stream.NextLong(15, 31));
            withdrawalDays.Add(second);
            cushions.Add(stream.NextLong(2, 5));
            cushions.Add(stream.NextLong(2, 5));
        }

        // Each withdrawal tops the wallet up to a few notes above the lowest it
        // reaches before the next one.
        for (var i = 0; i < withdrawalDays.Count; i++)
        {
            var day = withdrawalDays[i];
            if (!calendar.Contains(day)) continue;
            var until = i + 1 < withdrawalDays.Count ? withdrawalDays[i + 1].AddDays(-1) : calendar.AsOf;
            if (until > calendar.AsOf) until = calendar.AsOf;
            if (until < day) until = day;
            var (lowest, _) = book.LowestDayEnd(wallet.Id, day, until);
            var units = cushions[i] - (lowest >= 0 ? lowest / AtmUnit : -((-lowest + AtmUnit - 1) / AtmUnit));
            if (units < 2) units = 2;
            Atm(day, units * AtmUnit);
        }

        // Never below zero: an extra withdrawal on the first day it would be.
        for (var day = calendar.WindowStart; day <= calendar.AsOf; day = day.AddDays(1))
        {
            if (book.BalanceAtEndOf(wallet.Id, day) >= 0) continue;
            var horizon = day.AddDays(21) > calendar.AsOf ? calendar.AsOf : day.AddDays(21);
            var (lowest, _) = book.LowestDayEnd(wallet.Id, day, horizon);
            var units = (-lowest + AtmUnit - 1) / AtmUnit + 2;
            Atm(day, units * AtmUnit);
        }
    }

    private void Spend(DemoBook book, DemoAccount wallet, DateOnly date, string payee, string[] category, long amount) =>
        book.Post(this, new DemoTxn
        {
            AccountId = wallet.Id,
            Date = date,
            Description = payee,
            Entry = DemoEntry.Manual,
            Splits = [DemoSplit.Bank(book.Base.Category(MdCategoryType.Expense, category), amount)],
        });
}
