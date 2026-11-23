namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Where the household lives: rent by cheque until the purchase, the
/// purchase itself, the mortgage and its escrow, and the house's revaluation.
/// </summary>
/// <remarks>
/// <para><b>Rent.</b> A hand-written cheque dated the 1st (its tax date) that the
/// bank posts one to three business days later, with sequential cheque numbers; the
/// rent steps up at the lease renewal. The last cheque is in the house month, and
/// the lease deposit comes back the month after the move. Renter's insurance is
/// yearly, on the card in use, until the closing.</para>
///
/// <para><b>Buying.</b> An inspection and an appraisal a few months ahead; the
/// earnest money wired to the house account (with its wire fee) in the house month;
/// the closing as ONE multi-split on Joint Checking whose origin is the cash to
/// close: the house (price and capitalized costs, less the earnest money already
/// held), the mortgage principal, the prepaid per-diem interest, the seller's
/// prorated property-tax credit, the initial escrow deposit and the first year's
/// home insurance. The closing row carries the long header and leg tags of the
/// re-homed heavy-tag shape. Movers follow it.</para>
///
/// <para><b>The initial escrow deposit</b> is the smallest whole-dollar amount that
/// keeps the escrow account at zero or more at every day-end in the window, given
/// the escrow each payment adds and every disbursement, plus a cushion of
/// <see cref="HouseholdTerms.EscrowCushionMonths"/> months' escrow.</para>
///
/// <para><b>The mortgage.</b> Each payment from its first is one row on Joint
/// Checking with three lines: interest (rounded per payment on the iterated
/// balance), principal (the rest of the level payment) and the escrow transfer.
/// Property tax is paid from escrow in halves each April and October, and the home
/// insurance each closing anniversary from the first.</para>
///
/// <para>Savings' part of the down payment is moved by
/// <see cref="SavingsPattern"/>, which sees every other Checking flow.</para>
/// </remarks>
public sealed class HousingPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "housing";

    /// <summary>The life month of the inspection and the appraisal.</summary>
    public const int InspectionMonth = 25;

    /// <summary>The landlord.</summary>
    public const string Landlord = "Corvane Property Group";

    /// <summary>The mortgage servicer.</summary>
    public const string Servicer = "Ashby Mutual Mortgage";

    /// <summary>The settlement agent the closing is paid to.</summary>
    public const string SettlementAgent = "Harwick Title & Escrow";

    /// <summary>The insurer of the rental and the house.</summary>
    public const string Insurer = "Brackwell Mutual";

    /// <summary>The county the property tax is paid to.</summary>
    public const string County = "County Treasurer";

    /// <summary>The rent cheque's memo.</summary>
    public const string RentMemo = "Monthly rent";

    /// <summary>The mortgage payment's memo.</summary>
    public const string MortgageMemo = "Monthly payment";

    /// <summary>The closing row's memo.</summary>
    public const string ClosingMemo = "Closing on the house";

    /// <summary>The months property tax is paid from escrow.</summary>
    public static IReadOnlyList<int> PropertyTaxMonths { get; } = [4, 10];

    /// <summary>The day of those months it is paid.</summary>
    public const int PropertyTaxDay = 20;

    /// <summary>The calendar month renter's insurance renews.</summary>
    public const int RentersInsuranceMonth = 5;

    /// <summary>The yearly renter's insurance premium.</summary>
    public const long RentersInsurance = 186_00L;

    /// <summary>The header tags of the closing row: nine, several of them long.</summary>
    public static IReadOnlyList<string> ClosingHeaderTags { get; } =
    [
        "Closing statement filed with the house documents",
        "Settlement figures checked against the final disclosure",
        "Deductible points and interest noted for the tax year",
        "Escrow deposit sized by the servicer analysis",
        "Proration of the property tax credited by the seller",
        "Wire confirmed by phone with the settlement agent",
        "House purchase",
        "Closing",
        "Home",
    ];

    /// <summary>The tags on the closing row's house line: eight, several long.</summary>
    public static IReadOnlyList<string> HouseLineTags { get; } =
    [
        "Purchase price less the earnest money already held",
        "Capitalized settlement costs added to the cost basis",
        "Title insurance and recording fees included in basis",
        "Survey and inspection charges folded into the basis",
        "Basis document kept for the eventual sale of the house",
        "Improvements tracked separately from this purchase line",
        "Basis",
        "Property",
    ];

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 60;

    /// <summary>The closing date.</summary>
    public static DateOnly ClosingDate(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(HouseholdTerms.ClosingMonth, HouseholdTerms.ClosingDay));
    }

    /// <summary>The rent in life month <paramref name="lifeMonth"/>.</summary>
    public static long RentIn(int lifeMonth) =>
        lifeMonth < HouseholdTerms.LeaseRenewalMonth
            ? HouseholdTerms.Rent
            : DemoAmortization.RoundCents(HouseholdTerms.Rent * HouseholdTerms.RentStep);

    /// <summary>The mortgage payment dates in the window, first payment first.</summary>
    public static IReadOnlyList<DateOnly> MortgagePaymentDates(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var dates = new List<DateOnly>();
        for (var month = HouseholdTerms.FirstMortgagePayment; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, HouseholdTerms.MortgagePaymentDay));
            if (calendar.Contains(date)) dates.Add(date);
        }
        return dates;
    }

    /// <summary>Every escrow disbursement in the window after the closing: the
    /// property-tax halves and the insurance renewals, in date order.</summary>
    public static IReadOnlyList<(DateOnly Date, bool IsTax, long Amount)> EscrowDisbursements(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var closing = ClosingDate(calendar);
        var list = new List<(DateOnly, bool, long)>();
        var firstHalf = HouseholdTerms.PropertyTaxAnnual / 2;
        for (var month = HouseholdTerms.ClosingMonth; month <= DemoCalendar.CurrentLifeMonth; month++)
        {
            var start = calendar.MonthStart(month);
            var taxIndex = -1;
            for (var i = 0; i < PropertyTaxMonths.Count; i++)
            {
                if (PropertyTaxMonths[i] == start.Month) taxIndex = i;
            }
            if (taxIndex >= 0)
            {
                var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, PropertyTaxDay));
                if (date > closing && calendar.Contains(date))
                    list.Add((date, true, taxIndex == 0 ? firstHalf : HouseholdTerms.PropertyTaxAnnual - firstHalf));
            }
            if (month > HouseholdTerms.ClosingMonth && (month - HouseholdTerms.ClosingMonth) % 12 == 0)
            {
                var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, HouseholdTerms.ClosingDay));
                if (calendar.Contains(date)) list.Add((date, false, HouseholdTerms.HomeInsuranceAnnual));
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>
    /// The initial escrow deposit: the smallest whole-dollar amount that keeps the
    /// escrow account at zero or more at every day-end in the window, plus the
    /// cushion.
    /// </summary>
    public static long InitialEscrowDeposit(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var flows = new SortedDictionary<DateOnly, long>();
        foreach (var date in MortgagePaymentDates(calendar))
            flows[date] = flows.GetValueOrDefault(date) + HouseholdTerms.MonthlyEscrow;
        foreach (var (date, _, amount) in EscrowDisbursements(calendar))
            flows[date] = flows.GetValueOrDefault(date) - amount;

        var running = 0L;
        var lowest = 0L;
        foreach (var (_, net) in flows)
        {
            running += net;
            if (running < lowest) lowest = running;
        }
        var needed = (-lowest + 99) / 100 * 100;
        return needed + HouseholdTerms.EscrowCushionMonths * HouseholdTerms.MonthlyEscrow;
    }

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        new Run(this, calendar, rng, book).Post();
    }

    private sealed class Run(HousingPattern pattern, DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        private readonly string _checking = book.Account(DemoAccountRoles.JointChecking).Id;
        private readonly string _house = book.Account(DemoAccountRoles.House).Id;
        private readonly string _escrow = book.Account(DemoAccountRoles.MortgageEscrow).Id;
        private readonly string _mortgage = book.Account(DemoAccountRoles.Mortgage).Id;
        private readonly string _rent = book.Base.Category(MdCategoryType.Expense, "Bills", "Rent");
        private readonly string _interest = book.Base.Category(MdCategoryType.Expense, "Bills", "Mortgage Interest");
        private readonly string _propertyTax = book.Base.Category(MdCategoryType.Expense, "Tax", "Property Tax");
        private readonly string _homeInsurance = book.Base.Category(MdCategoryType.Expense, "Insurance", "Home");
        private readonly string _homeMisc = book.Base.Category(MdCategoryType.Expense, "Home", "Misc");
        private readonly string _serviceCharges = book.Base.Category(MdCategoryType.Expense, "Bank Charge", "Service Charges");
        private readonly string _valuation = book.Base.Category(MdCategoryType.Expense, "Valuation Adjustment");

        public void Post()
        {
            RentCheques();
            RentersInsurance();
            Purchase();
            MortgagePayments();
            EscrowDisbursements();
            Revaluation();
        }

        private DemoTxn Row(DateOnly date, string payee, string? memo, IReadOnlyList<DemoSplit> splits,
            string? account = null, string? bankPayee = null, string? check = null, DateOnly? taxDate = null,
            DemoEntry entry = DemoEntry.Auto, string? tags = null) =>
            book.Post(pattern, new DemoTxn
            {
                AccountId = account ?? _checking,
                Date = date,
                TaxDate = taxDate,
                Description = payee,
                Memo = memo,
                BankPayee = bankPayee,
                CheckNumber = check,
                Entry = entry,
                Tags = tags,
                Splits = splits,
            });

        // A cheque each month through the house month, dated the 1st, posted one to
        // three business days later; the deposit comes back after the move.
        private void RentCheques()
        {
            for (var month = 0; month <= HouseholdTerms.HouseOpens; month++)
            {
                var written = calendar.MonthStart(month);
                var stream = rng.Stream(pattern.Name + ":rent", written);
                var posted = written;
                var lag = stream.NextLong(1, 3);
                for (var i = 0; i < lag; i++) posted = DemoCalendar.NextBusinessDayAfter(posted);
                if (!calendar.Contains(posted)) continue;

                var cheque = (HouseholdTerms.FirstRentCheque + month).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var txn = Row(posted, Landlord, RentMemo, [DemoSplit.Bank(_rent, RentIn(month))],
                    check: cheque, taxDate: written, entry: DemoEntry.Manual);
                if (month == HouseholdTerms.LeaseRenewalMonth) book.Mark(DemoEvents.RentStep, txn);
                if (month == HouseholdTerms.HouseOpens) book.Mark(DemoEvents.LastRent, txn);
            }

            var back = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(HouseholdTerms.LeaseDepositReturnMonth, 10));
            book.Mark(DemoEvents.LeaseDeposit, Row(back, Landlord, "Lease deposit returned",
                [DemoSplit.Bank(_rent, -HouseholdTerms.LeaseDeposit)], bankPayee: "DEPOSIT CORVANE PROPERTY GRP"));
        }

        // Yearly on the card in use while renting.
        private void RentersInsurance()
        {
            for (var month = 0; month < HouseholdTerms.ClosingMonth; month++)
            {
                if (calendar.MonthStart(month).Month != RentersInsuranceMonth) continue;
                var date = calendar.DayInMonth(month, 9);
                if (!calendar.Contains(date)) continue;
                DemoCards.Purchase(pattern, book, DemoCards.CardFor(calendar, date, everyday: false), date,
                    Insurer, "BRACKWELL MUTUAL INS", _homeInsurance,
                    DemoInflation.Apply(DemoInflation.Kind.General, month, HousingPattern.RentersInsurance), "Renter's insurance");
            }
        }

        private void Purchase()
        {
            var stream = rng.Stream(pattern.Name + ":purchase", calendar.MonthStart(InspectionMonth));

            var inspection = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(InspectionMonth, 9));
            book.Mark(DemoEvents.Inspection, Row(inspection, "Tallowmere Home Inspection", "Home inspection",
                [DemoSplit.Bank(_homeMisc, stream.NextMinor(480_00L, 620_00L))]));
            var appraisal = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(InspectionMonth, 21));
            book.Mark(DemoEvents.Appraisal, Row(appraisal, "Granitefield Appraisal", "Appraisal",
                [DemoSplit.Bank(_homeMisc, stream.NextMinor(550_00L, 700_00L))]));

            var earnestDay = DemoCalendar.OnOrNextBusinessDay(
                calendar.DayInMonth(HouseholdTerms.HouseOpens, HouseholdTerms.EarnestMoneyDay + 7));
            book.Mark(DemoEvents.EarnestMoney, Row(earnestDay, "Earnest money deposit", "Wired to the settlement agent",
                [DemoSplit.Bank(_house, HouseholdTerms.EarnestMoney)], bankPayee: "OUTGOING WIRE HARWICK TITLE"));
            book.Mark(DemoEvents.WireFee, Row(earnestDay, "Wire transfer fee", "Outgoing wire",
                [DemoSplit.Bank(_serviceCharges, HouseholdTerms.WireFee)], bankPayee: "OUTGOING WIRE FEE"));

            Closing();

            var moved = DemoCalendar.OnOrNextBusinessDay(ClosingDate(calendar).AddDays(4));
            book.Mark(DemoEvents.Movers, Row(moved, "Stillwater Movers", "Moving day",
                [DemoSplit.Bank(_homeMisc, stream.NextMinor(1_150_00L, 1_650_00L))]));
        }

        private void Closing()
        {
            var closing = ClosingDate(calendar);
            var principal = HouseholdTerms.MortgagePrincipal;
            var monthEnd = calendar.MonthEnd(HouseholdTerms.ClosingMonth);
            var days = monthEnd.DayNumber - closing.DayNumber + 1;
            var perDiem = DemoAmortization.RoundCents(principal * HouseholdTerms.MortgageRatePercent / 100m / 365m * days);

            // The seller's share of the tax for the half-year the closing falls in.
            var halfStart = new DateOnly(closing.Year, closing.Month <= 6 ? 1 : 7, 1);
            var credit = DemoAmortization.RoundCents(
                HouseholdTerms.PropertyTaxAnnual * (closing.DayNumber - halfStart.DayNumber) / 365m);

            var splits = new List<DemoSplit>
            {
                DemoSplit.Bank(_house, HouseholdTerms.HousePrice + HouseholdTerms.SettlementCosts - HouseholdTerms.EarnestMoney,
                    "Purchase price and settlement costs", string.Join(',', HouseLineTags)),
                DemoSplit.Bank(_mortgage, -principal, "Mortgage principal"),
                DemoSplit.Bank(_interest, perDiem, "Prepaid interest"),
                DemoSplit.Bank(_propertyTax, -credit, "Seller's property tax credit"),
                DemoSplit.Bank(_escrow, InitialEscrowDeposit(calendar), "Initial escrow deposit"),
                DemoSplit.Bank(_homeInsurance, HouseholdTerms.HomeInsuranceAnnual, "First year's home insurance"),
            };
            book.Mark(DemoEvents.Closing, Row(closing, SettlementAgent, ClosingMemo, splits,
                bankPayee: "OUTGOING WIRE HARWICK TITLE", tags: string.Join(',', ClosingHeaderTags)));
        }

        // Interest on the iterated balance, the rest of the level payment to principal,
        // and the escrow.
        private void MortgagePayments()
        {
            var terms = HouseholdTerms.Mortgage;
            var dates = MortgagePaymentDates(calendar);
            for (var k = 0; k < dates.Count; k++)
            {
                var owed = terms.BalanceAfter(k);
                if (owed <= 0) break;
                var interest = terms.InterestOn(owed);
                var principalPart = terms.Payment - interest;
                if (principalPart > owed) principalPart = owed;
                var txn = Row(dates[k], Servicer, MortgageMemo,
                [
                    DemoSplit.Bank(_interest, interest, "Interest"),
                    DemoSplit.Bank(_mortgage, principalPart, "Principal"),
                    DemoSplit.Bank(_escrow, HouseholdTerms.MonthlyEscrow, "Escrow"),
                ], bankPayee: "ASHBY MUTUAL MTG ACH");
                if (k == 0) book.Mark(DemoEvents.FirstMortgagePayment, txn);
            }
        }

        private void EscrowDisbursements()
        {
            foreach (var (date, isTax, amount) in HousingPattern.EscrowDisbursements(calendar))
            {
                if (isTax)
                {
                    var half = date.Month == PropertyTaxMonths[0] ? "Property tax, first half" : "Property tax, second half";
                    Row(date, County, half, [DemoSplit.Bank(_propertyTax, amount)], account: _escrow);
                }
                else
                {
                    Row(date, Insurer, "Homeowners insurance renewal", [DemoSplit.Bank(_homeInsurance, amount)], account: _escrow);
                }
            }
        }

        // A markup on the house's value, posted to the adjustment-kind category.
        private void Revaluation()
        {
            var date = calendar.DayInMonth(HouseholdTerms.RevaluationMonth, 15);
            var markup = DemoAmortization.RoundCents(HouseholdTerms.HousePrice * HouseholdTerms.RevaluationPercent / 100m);
            book.Mark(DemoEvents.Revaluation, Row(date, "Market value update", "Revaluation",
                [DemoSplit.Bank(_valuation, -markup)], account: _house));
        }
    }
}
