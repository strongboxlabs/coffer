using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The household's recurring bills: utilities by ACH from Joint Checking,
/// subscriptions on the card in use, car insurance and registration, daycare,
/// December giving, and the yearly rows that carry a coverage shape.
/// </summary>
/// <remarks>
/// <para><b>Utilities.</b> Each bill is its base amount times a seasonal factor for
/// its calendar month, times the general price level, with a small wobble. While
/// renting the household pays Electric, Internet and Phone; from the closing it
/// pays the owned home's set: Electric, Natural Gas, Water/Sewer, quarterly
/// Garbage, Internet and Phone. An ACH debit on a shut day moves to the next
/// business day, except Internet's: an autopay that posts on its own day of the
/// month, shut or not, so the reminder it backs has no occurrence on or before AsOf
/// whose bill is still to post (the seeded skip relies on it). Water and Phone are clean payees (<see cref="DemoProvenance"/>):
/// the bank's text equals the payee, so the import keeps no original for them.</para>
///
/// <para><b>Electric's last three bills</b> on or before AsOf are simulated but not
/// exported: the seeder posts them through the reminder it creates, so the
/// estimate has linked history. They are marked <see cref="ElectricOmittedMark"/>.</para>
///
/// <para><b>Subscriptions</b> step their price at fixed life months: video
/// streaming twice, and the news subscription runs for a span of the window only.</para>
///
/// <para><b>Coverage shapes.</b> Each October a zero-amount service charge, waived;
/// each January a card purchase whose tax date is the last day of December; each
/// May a hardware purchase whose tax date is late April.</para>
/// </remarks>
public sealed class BillsPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "bills";

    /// <summary>The mark of Electric's omitted bills.</summary>
    public const string ElectricOmittedMark = "electric-omitted";

    /// <summary>The mark of every Internet bill.</summary>
    public const string InternetMark = "internet";

    /// <summary>The mark of every registration renewal.</summary>
    public const string RegistrationMark = "registration";

    /// <summary>Electric's last bills on or before AsOf left for the seeder.</summary>
    public const int ElectricOmittedCount = 3;

    /// <summary>The nominal day Electric is debited.</summary>
    public const int ElectricDay = 12;

    /// <summary>The nominal day Natural Gas is debited.</summary>
    public const int GasDay = 9;

    /// <summary>The nominal day Water/Sewer is debited.</summary>
    public const int WaterDay = 16;

    /// <summary>The nominal day the quarterly Garbage bill is debited.</summary>
    public const int GarbageDay = 5;

    /// <summary>The day Internet is debited, weekends and holidays included: the
    /// reminder's monthly day.</summary>
    public const int InternetDay = 18;

    /// <summary>The nominal day Phone is debited.</summary>
    public const int PhoneDay = 22;

    /// <summary>The Internet provider.</summary>
    public const string InternetPayee = "Lattice Broadband";

    /// <summary>The Internet bill's memo.</summary>
    public const string InternetMemo = "Monthly internet";

    /// <summary>The Electric utility.</summary>
    public const string ElectricPayee = "Ridgeline Power";

    /// <summary>The water utility. Its bank text is already clean (the bank sends
    /// this payee as is), so its feed rows carry no original to keep.</summary>
    public const string WaterPayee = "Harbour Water District";

    /// <summary>The phone carrier. Its bank text is already clean, as for water.</summary>
    public const string PhonePayee = "Quillfen Mobile";

    /// <summary>The registration office.</summary>
    public const string RegistrationPayee = "Motor Vehicle Registration";

    /// <summary>The registration's memo.</summary>
    public const string RegistrationMemo = "Vehicle registration";

    /// <summary>The calendar month and day the registration renews.</summary>
    public const int RegistrationMonth = 6, RegistrationDay = 10;

    /// <summary>The yearly registration fee.</summary>
    public const long Registration = 186_00L;

    /// <summary>The life month the video price first rises.</summary>
    public const int StreamingRise1Month = 18;

    /// <summary>The life month the video price rises again.</summary>
    public const int StreamingRise2Month = 42;

    /// <summary>The first and last life months of the news subscription.</summary>
    public const int NewsStartMonth = 20, NewsLastMonth = 44;

    /// <summary>The life month daycare starts.</summary>
    public const int DaycareFrom = 43;

    /// <summary>Daycare's monthly fee, and the life month it rises.</summary>
    public const long Daycare = 1_150_00L;

    /// <summary>The life month daycare's fee rises.</summary>
    public const int DaycareRiseMonth = 55;

    /// <summary>Daycare's fee multiplier at the rise.</summary>
    public const decimal DaycareRise = 1.04m;

    /// <summary>Internet's price while renting, then for the owned home.</summary>
    public const long InternetRenting = 59_99L, InternetOwned = 74_99L;

    // Seasonal factors by calendar month (January first).
    private static readonly decimal[] ElectricSeason = [1.15m, 1.00m, 0.90m, 0.85m, 0.95m, 1.20m, 1.45m, 1.50m, 1.15m, 0.90m, 0.95m, 1.10m];
    private static readonly decimal[] GasSeason = [2.00m, 1.80m, 1.30m, 0.90m, 0.60m, 0.40m, 0.35m, 0.35m, 0.45m, 0.80m, 1.40m, 1.90m];
    private static readonly decimal[] WaterSeason = [0.80m, 0.80m, 0.85m, 0.95m, 1.10m, 1.30m, 1.45m, 1.40m, 1.15m, 0.95m, 0.85m, 0.80m];

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 64;

    /// <summary>Internet's price in life month <paramref name="lifeMonth"/>.</summary>
    public static long InternetIn(int lifeMonth) =>
        lifeMonth > HouseholdTerms.ClosingMonth ? InternetOwned : InternetRenting;

    /// <summary>The registration fee in life month <paramref name="lifeMonth"/>.</summary>
    public static long RegistrationIn(int lifeMonth) =>
        DemoInflation.Apply(DemoInflation.Kind.General, lifeMonth / 12 * 12, Registration);

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        new Run(this, calendar, rng, book).Post();
    }

    private sealed class Run(BillsPattern pattern, DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        private readonly string _checking = book.Account(DemoAccountRoles.JointChecking).Id;
        private readonly DateOnly _closing = HousingPattern.ClosingDate(calendar);

        private string Category(params string[] segments) => book.Base.Category(MdCategoryType.Expense, segments);

        public void Post()
        {
            Utilities();
            Subscriptions();
            Yearly();
            Daycare();
        }

        private DemoTxn Ach(DateOnly date, string payee, string bankPayee, string category, long amount,
            string? memo = null, bool exported = true, DateOnly? taxDate = null) =>
            book.Post(pattern, new DemoTxn
            {
                AccountId = _checking,
                Date = date,
                TaxDate = taxDate,
                Description = payee,
                Memo = memo,
                BankPayee = bankPayee,
                Exported = exported,
                Splits = [DemoSplit.Bank(category, amount)],
            });

        private void Utilities()
        {
            var electric = new List<(DateOnly Date, long Amount)>();
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var stream = rng.Stream(pattern.Name + ":utilities", calendar.MonthStart(month));
                var calendarMonth = calendar.MonthStart(month).Month;
                long Seasonal(long baseAmount, decimal[] season) => DemoAmortization.RoundCents(
                    DemoInflation.Apply(DemoInflation.Kind.General, month, baseAmount) * season[calendarMonth - 1]
                    * (0.95m + stream.NextDecimal(0m, 0.10m)));
                DateOnly Due(int day) => DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, day));

                var electricDate = Due(ElectricDay);
                var owned = electricDate > _closing;
                var electricAmount = Seasonal(owned ? 142_00L : 86_00L, ElectricSeason);
                if (calendar.Contains(electricDate)) electric.Add((electricDate, electricAmount));

                var gasDate = Due(GasDay);
                var gasAmount = Seasonal(64_00L, GasSeason);
                if (gasDate > _closing && calendar.Contains(gasDate))
                    Ach(gasDate, "Cedar Gas Co", "CEDAR GAS CO ACH DEBIT", Category("Bills", "Natural Gas"), gasAmount);

                var waterDate = Due(WaterDay);
                var waterAmount = Seasonal(52_00L, WaterSeason);
                if (waterDate > _closing && calendar.Contains(waterDate))
                    Ach(waterDate, WaterPayee, WaterPayee, Category("Bills", "Water/Sewer"), waterAmount);

                var garbageDate = Due(GarbageDay);
                if (calendarMonth % 3 == 1 && garbageDate > _closing && calendar.Contains(garbageDate))
                {
                    Ach(garbageDate, "Hollins Waste Services", "HOLLINS WASTE SVCS", Category("Bills", "Garbage"),
                        DemoInflation.Apply(DemoInflation.Kind.General, month, 87_00L), "Quarterly service");
                }

                // Internet posts on its own day, shut or not, so its reminder never has an
                // occurrence on or before AsOf whose bill is still to post.
                var internetDate = calendar.DayInMonth(month, InternetDay);
                if (calendar.Contains(internetDate))
                {
                    book.Mark(InternetMark, Ach(internetDate, InternetPayee, "LATTICE BROADBAND MONTHLY",
                        Category("Bills", "Internet"), InternetIn(month), InternetMemo));
                }

                var phoneDate = Due(PhoneDay);
                if (calendar.Contains(phoneDate))
                {
                    var phone = month >= HouseholdTerms.HsaOpens ? 92_00L : 85_00L;
                    Ach(phoneDate, PhonePayee, PhonePayee, Category("Bills", "Phone"), phone);
                }
            }

            // Electric: every bill simulated, the last few left for the seeder.
            electric.Sort((a, b) => a.Date.CompareTo(b.Date));
            for (var i = 0; i < electric.Count; i++)
            {
                var omitted = i >= electric.Count - ElectricOmittedCount;
                var txn = Ach(electric[i].Date, ElectricPayee, "RIDGELINE POWER AUTOPAY", Category("Bills", "Electric"),
                    electric[i].Amount, exported: !omitted);
                if (omitted) book.Mark(ElectricOmittedMark, txn);
            }
        }

        // On the card in use: video, music and cloud storage throughout, news for a
        // span; video's price steps twice.
        private void Subscriptions()
        {
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                void Charge(int day, string payee, string bankPayee, string category, long amount, string? mark = null)
                {
                    var date = calendar.DayInMonth(month, day);
                    if (!calendar.Contains(date)) return;
                    var txn = DemoCards.Purchase(pattern, book, DemoCards.CardFor(calendar, date, everyday: false), date,
                        payee, bankPayee, Category("Personal", category), amount, "Subscription");
                    if (mark is not null) book.Mark(mark, txn);
                }

                var video = month < StreamingRise1Month ? 15_49L : month < StreamingRise2Month ? 17_99L : 22_99L;
                Charge(7, "Streamwell", "STREAMWELL.TV", "Entertainment", video,
                    month == StreamingRise1Month ? DemoEvents.StreamingRise1
                    : month == StreamingRise2Month ? DemoEvents.StreamingRise2 : null);
                Charge(11, "Tunebarrow Music", "TUNEBARROW MUSIC", "Music", 10_99L);
                Charge(19, "Skyvault Storage", "SKYVAULT STORAGE", "Computer", month < 40 ? 2_99L : 9_99L);
                if (month is >= NewsStartMonth and <= NewsLastMonth)
                {
                    Charge(25, "The Morning Courier", "MORNING COURIER DIGITAL", "Books/Magazines", 12_00L,
                        month == NewsStartMonth ? DemoEvents.NewsStarts : month == NewsLastMonth ? DemoEvents.NewsLast : null);
                }
            }
        }

        // Car insurance twice a year, the registration yearly, December giving, and
        // the yearly coverage shapes.
        private void Yearly()
        {
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var start = calendar.MonthStart(month);
                var stream = rng.Stream(pattern.Name + ":yearly", start);

                if (start.Month is 3 or 9)
                {
                    var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, 2));
                    if (calendar.Contains(date))
                    {
                        Ach(date, HousingPattern.Insurer, "BRACKWELL MUTUAL INS PREM", Category("Insurance", "Automobile"),
                            DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(540_00L, 600_00L)),
                            "Car insurance");
                    }
                }

                if (start.Month == RegistrationMonth)
                {
                    var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, RegistrationDay));
                    if (calendar.Contains(date))
                    {
                        book.Mark(RegistrationMark, Ach(date, RegistrationPayee, "MOTOR VEHICLE REG ONLINE",
                            Category("Automotive", "Misc"), RegistrationIn(month), RegistrationMemo));
                    }
                }

                if (start.Month == 12)
                {
                    var gifts = 1 + (int)stream.NextLong(0, 2);
                    for (var i = 0; i < gifts; i++)
                    {
                        var date = calendar.DayInMonth(month, (int)stream.NextLong(14, 30));
                        var charity = stream.Choice(Charities);
                        if (calendar.Contains(date))
                        {
                            Ach(date, charity.Payee, charity.Bank, Category("Personal", "Donations"),
                                stream.NextLong(5, 30) * 10_00L, "Year-end gift");
                        }
                    }
                }

                // A zero-amount row that records a waived fee.
                if (start.Month == 10)
                {
                    var date = calendar.DayInMonth(month, 27);
                    if (calendar.Contains(date))
                    {
                        Ach(date, "Account service charge", "MONTHLY SERVICE CHARGE WAIVED",
                            Category("Bank Charge", "Service Charges"), 0, "Waived, balance above minimum");
                    }
                }

                // Bought on the last day of December, posted by the card on 2 January.
                if (start.Month == 1)
                {
                    var date = calendar.DayInMonth(month, 2);
                    if (calendar.Contains(date))
                    {
                        DemoCards.Purchase(pattern, book, DemoCards.CardFor(calendar, date, everyday: false), date,
                            "Pellowick Books", "PELLOWICK BOOKS 2210", Category("Education", "Books"),
                            DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(42_00L, 98_00L)),
                            "Ordered 31 Dec, posted 2 Jan", taxDate: new DateOnly(date.Year - 1, 12, 31));
                    }
                }

                // Ordered in late April, charged in May.
                if (start.Month == 5)
                {
                    var date = calendar.DayInMonth(month, 6);
                    if (calendar.Contains(date))
                    {
                        Ach(date, "Thistlewood Hardware", "THISTLEWOOD HARDWARE", Category("Home", "Maintenance"),
                            DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(31_00L, 124_00L)),
                            "Ordered 28 Apr, charged 6 May", taxDate: new DateOnly(date.Year, 4, 28));
                    }
                }
            }
        }

        // A monthly ACH booked on the Childcare root.
        private void Daycare()
        {
            for (var month = DaycareFrom; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var date = DemoCalendar.OnOrNextBusinessDay(calendar.DayInMonth(month, 1));
                if (!calendar.Contains(date)) continue;
                var fee = month >= DaycareRiseMonth
                    ? DemoAmortization.RoundCents(BillsPattern.Daycare * DaycareRise)
                    : BillsPattern.Daycare;
                var txn = Ach(date, "Little Acorns Daycare", "LITTLE ACORNS DAYCARE ACH", Category("Childcare"), fee,
                    "Monthly tuition " + date.ToString("MMMM", CultureInfo.InvariantCulture));
                if (month == DaycareFrom) book.Mark(DemoEvents.DaycareFirst, txn);
            }
        }

        private static readonly (string Payee, string Bank)[] Charities =
        [
            ("Riverbend Food Bank", "RIVERBEND FOOD BANK DONATION"),
            ("Open Shelf Library Fund", "OPEN SHELF LIBRARY FUND"),
            ("Hearthside Animal Rescue", "HEARTHSIDE ANIMAL RESCUE"),
        ];
    }
}
