namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Medical spending paid from the Family HSA: from the
/// HSA's month, copays and prescriptions as investment-route <c>exp</c> rows (a zero
/// <c>sec</c> split on the HSA's holding plus the <c>exp</c> split, which maps to
/// <c>misc</c>), and the HSA's share of the delivery bill.
/// </summary>
/// <remarks>
/// <para><b>Never overdrawn.</b> Rows are posted in date order, each only when it
/// fits under the lowest the HSA's cash stands at from its date through AsOf; the
/// payroll contributions are already in the book, so every later day's balance is
/// known. A claim that does not fit is not made.</para>
///
/// <para><b>The delivery bill.</b> On <see cref="DeliveryBillDate"/> the HSA pays its
/// whole cash balance in whole dollars toward the bill, on Healthcare &gt; Hospital.
/// The rest of the bill (<see cref="HouseholdTerms.DeliveryBillTotal"/> less
/// <see cref="HsaShareOfDeliveryBill"/>) is the payment plan's, posted by the
/// pattern that owns that account.</para>
///
/// <para>This pattern posts before <see cref="InvestmentsPattern"/>, whose HSA buys
/// then never spend cash a later bill needs.</para>
/// </remarks>
public sealed class HsaMedicalPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "hsa-medical";

    /// <summary>The delivery bill row's memo.</summary>
    public const string DeliveryMemo = "Delivery, paid from the HSA";

    /// <summary>The hospital the delivery bill is from.</summary>
    public const string Hospital = "Fernhill Medical Center";

    /// <summary>Claims paid from the HSA in a month, on average.</summary>
    public const decimal ClaimsPerMonth = 1.2m;

    private static readonly (string Payee, string Category, long Min, long Max)[] Claims =
    [
        ("Larkfield Family Clinic", "Physician", 25_00L, 60_00L),
        ("Quarry Lane Pharmacy", "Prescriptions", 8_00L, 85_00L),
    ];

    private readonly DemoMarket _market;

    /// <summary>The pattern over the Demo's market (an <c>exp</c> row names a holding).</summary>
    public HsaMedicalPattern(DemoMarket market) => _market = market ?? throw new ArgumentNullException(nameof(market));

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 30;

    /// <summary>The date the delivery bill is paid.</summary>
    public static DateOnly DeliveryBillDate(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return DemoCalendar.OnOrNextBusinessDay(
            calendar.DayInMonth(HouseholdTerms.PaymentPlanOpens, HouseholdTerms.DeliveryBillDay));
    }

    /// <summary>What the HSA paid toward the delivery bill, in cents; zero when it
    /// had nothing to pay.</summary>
    public static long HsaShareOfDeliveryBill(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var row = book.Transactions.FirstOrDefault(t => t.Pattern == PatternName && t.Memo == DeliveryMemo);
        return row is null ? 0 : -row.Splits.Sum(s => s.ParentAmount);
    }

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        var desk = new DemoInvestmentDesk(book, _market);
        var hsa = book.Account(DemoAccountRoles.FamilyHsa);
        var billDate = DeliveryBillDate(calendar);
        var billPaid = false;

        long Available(DateOnly date) => book.LowestDayEnd(hsa.Id, date, calendar.AsOf).Balance;

        void PayBill()
        {
            billPaid = true;
            if (!calendar.Contains(billDate)) return;
            var whole = Available(billDate) / 100 * 100;
            if (whole <= 0) return;
            if (whole > HouseholdTerms.DeliveryBillTotal) whole = HouseholdTerms.DeliveryBillTotal;
            book.Post(this, desk.Exp(DemoAccountRoles.FamilyHsa, DemoSecurities.Vti, billDate, whole,
                book.Base.Category(MdCategoryType.Expense, "Healthcare", "Hospital"), Hospital, DeliveryMemo));
        }

        for (var month = HouseholdTerms.HsaOpens; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var stream = rng.Stream(Name, calendar.MonthStart(month));
            var count = stream.NextCount(ClaimsPerMonth);
            var claims = new List<(DateOnly Date, int Kind, long Amount)>();
            for (var i = 0; i < count; i++)
            {
                var date = calendar.DayInMonth(month, (int)stream.NextLong(1, 28));
                var kind = stream.NextIndex(Claims.Length);
                claims.Add((date, kind, stream.NextMinor(Claims[kind].Min, Claims[kind].Max)));
            }
            claims.Sort((a, b) => a.Date.CompareTo(b.Date));

            foreach (var (date, kind, amount) in claims)
            {
                if (!billPaid && date > billDate) PayBill();
                if (!calendar.Contains(date) || !hsa.IsActiveOn(date) || amount > Available(date)) continue;
                var (payee, category, _, _) = Claims[kind];
                book.Post(this, desk.Exp(DemoAccountRoles.FamilyHsa, DemoSecurities.Vti, date, amount,
                    book.Base.Category(MdCategoryType.Expense, "Healthcare", category), payee, "Paid from the HSA"));
            }
            if (!billPaid && month == calendar.LifeMonthOf(billDate)) PayBill();
        }
    }
}
