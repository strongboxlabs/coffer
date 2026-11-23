namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Medical costs paid outside the HSA: copays and
/// pharmacy, dental twice a year and vision yearly, the prenatal visits, the
/// delivery bill's remainder on the hospital payment plan and its six transfers,
/// and the insurer's reimbursement.
/// </summary>
/// <remarks>
/// <para><b>The payment plan.</b> On the delivery bill's date the plan's origin row
/// carries the bill less what the HSA paid
/// (<see cref="HsaMedicalPattern.HsaShareOfDeliveryBill"/>, so this pattern posts
/// after <see cref="HsaMedicalPattern"/>) on Healthcare &gt; Hospital. Six equal
/// transfers from Joint Checking pay it off, the last taking the rounding remainder,
/// and the plan closes at zero.</para>
///
/// <para><b>The reimbursement</b> is a deposit into Joint Checking, negative on
/// Healthcare &gt; Hospital.</para>
/// </remarks>
public sealed class MedicalPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "medical";

    /// <summary>The first and last life months of the prenatal visits.</summary>
    public const int PrenatalFrom = 33, PrenatalThrough = 39;

    /// <summary>The life month of the insurer's reimbursement.</summary>
    public const int ReimbursementMonth = 44;

    /// <summary>The reimbursement.</summary>
    public const long Reimbursement = 1_150_00L;

    /// <summary>Copay and pharmacy visits a month before the HSA, and after it (when
    /// the HSA pays some of them).</summary>
    public const decimal VisitsPerMonth = 3m, VisitsPerMonthWithHsa = 2m;

    /// <summary>The day of the month each payment-plan transfer is sent.</summary>
    public const int PlanTransferDay = 15;

    /// <summary>The number of payment-plan transfers.</summary>
    public const int PlanTransfers = 6;

    private static readonly (string Payee, string Bank, string Category, long Min, long Max)[] Visits =
    [
        ("Larkfield Family Clinic", "LARKFIELD FAMILY CLINIC", "Physician", 25_00L, 45_00L),
        ("Quarry Lane Pharmacy", "QUARRY LANE PHARMACY 22", "Prescriptions", 8_00L, 64_00L),
        ("Quarry Lane Pharmacy", "QUARRY LANE PHARMACY 22", "Prescriptions", 5_00L, 28_00L),
    ];

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 72;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        string Category(string leaf) => book.Base.Category(MdCategoryType.Expense, "Healthcare", leaf);

        DemoTxn FromChecking(DateOnly date, string payee, string bank, string category, long amount, string? memo) =>
            book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = payee,
                Memo = memo,
                BankPayee = bank,
                Splits = [DemoSplit.Bank(category, amount)],
            });

        DemoTxn OnCard(DateOnly date, string payee, string bank, string category, long amount, string? memo) =>
            DemoCards.Purchase(this, book, DemoCards.CardFor(calendar, date, everyday: false), date,
                payee, bank, category, amount, memo);

        for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var start = calendar.MonthStart(month);
            var stream = rng.Stream(Name, start);
            // Days are drawn over the whole calendar month, and the partial current
            // month drops the ones after AsOf, so it keeps the monthly daily rate.
            var last = calendar.MonthEnd(month).Day;

            var visits = stream.NextCount(month >= HouseholdTerms.HsaOpens ? VisitsPerMonthWithHsa : VisitsPerMonth);
            for (var i = 0; i < visits; i++)
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(1, last));
                var (payee, bank, category, min, max) = stream.Choice(Visits);
                var amount = DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(min, max));
                var fromChecking = stream.Chance(0.5m);
                if (!calendar.Contains(date)) continue;
                if (fromChecking) FromChecking(date, payee, "POS DEBIT " + bank, Category(category), amount, null);
                else OnCard(date, payee, bank, Category(category), amount, null);
            }

            if (start.Month is 3 or 9)
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(5, 25));
                if (calendar.Contains(date))
                {
                    OnCard(date, "Mirrowfield Dental", "MIRROWFIELD DENTAL GROUP", Category("Dental"),
                        DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(90_00L, 260_00L)), "Cleaning");
                }
            }

            if (start.Month == 10)
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(3, 20));
                if (calendar.Contains(date))
                {
                    OnCard(date, "Clearholt Eye Care", "CLEARHOLT EYE CARE", Category("Vision"),
                        DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(150_00L, 320_00L)), "Eye exam and lenses");
                }
            }

            if (month is >= PrenatalFrom and <= PrenatalThrough)
            {
                var count = month >= 37 ? 2 : 1;
                for (var i = 0; i < count; i++)
                {
                    var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, 6 + i * 14));
                    var txn = FromChecking(date, "Larkfield Family Clinic", "POS DEBIT LARKFIELD FAMILY CLINIC",
                        Category("Physician"), stream.NextMinor(30_00L, 60_00L), "Prenatal visit");
                    if (month == PrenatalFrom && i == 0) book.Mark(DemoEvents.PrenatalFirst, txn);
                    if (month == PrenatalThrough && i == count - 1) book.Mark(DemoEvents.PrenatalLast, txn);
                }
            }
        }

        PaymentPlan(calendar, book, checking, Category("Hospital"));

        var reimbursed = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(ReimbursementMonth, 12));
        book.Mark(DemoEvents.Reimbursement, FromChecking(reimbursed, "Northfield Health Plan", "NORTHFIELD HEALTH CLAIM PMT",
            Category("Hospital"), -Reimbursement, "Claim reimbursement"));
    }

    private void PaymentPlan(DemoCalendar calendar, DemoBook book, string checking, string hospital)
    {
        var plan = book.Account(DemoAccountRoles.PaymentPlan);
        var remainder = HouseholdTerms.DeliveryBillTotal - HsaMedicalPattern.HsaShareOfDeliveryBill(book);
        var billDate = HsaMedicalPattern.DeliveryBillDate(calendar);
        book.Mark(DemoEvents.PaymentPlanOrigin, book.Post(this, new DemoTxn
        {
            AccountId = plan.Id,
            Date = billDate,
            Description = HsaMedicalPattern.Hospital,
            Memo = "Delivery balance on a payment plan",
            Splits = [DemoSplit.Bank(hospital, remainder)],
        }));

        var each = remainder / PlanTransfers;
        for (var i = 0; i < PlanTransfers; i++)
        {
            var month = HouseholdTerms.PaymentPlanOpens + 1 + i;
            var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, PlanTransferDay));
            var amount = i == PlanTransfers - 1 ? remainder - each * (PlanTransfers - 1) : each;
            var txn = book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = HsaMedicalPattern.Hospital,
                Memo = "Payment plan " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of 6",
                BankPayee = "FERNHILL MEDICAL CTR PMT",
                Splits = [DemoSplit.Bank(plan.Id, amount)],
            });
            if (i == 0) book.Mark(DemoEvents.PlanTransferFirst, txn);
            if (i == PlanTransfers - 1)
            {
                book.Mark(DemoEvents.PlanTransferLast, txn);
                plan.ActiveThrough = calendar.MonthEnd(month);
            }
        }

        book.AddCheck(PatternName, b =>
            b.BalanceAtEndOf(plan.Id, calendar.AsOf) == 0 ? [] : ["the payment plan does not end at zero"]);
    }
}
