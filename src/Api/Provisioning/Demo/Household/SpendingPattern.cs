namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Everyday spending: groceries, dining, household, personal,
/// fuel and car costs, baby supplies, the November-December holidays, the summer
/// trip and the occasional return.
/// </summary>
/// <remarks>
/// <para><b>Counts.</b> Each stream draws a Poisson count per month around its mean
/// (<see cref="Streams"/>), from its own stream keyed by month, so a change to one
/// stream or one month moves nothing else. Each purchase falls on a uniform day of
/// the calendar month and costs a uniform amount in its range at that month's price
/// level; in the partial current month the ones after AsOf are dropped.</para>
///
/// <para><b>Where it is paid.</b> Most purchases go on the card in use
/// (<see cref="DemoCards.CardFor"/>); a share of groceries, household and pharmacy
/// spending is paid by debit card from Joint Checking instead.</para>
///
/// <para><b>The summer trip.</b> Each summer in the window: the flights booked in
/// April or May on Vacation &gt; Travel &gt; Airfare, then in July or August a car
/// rental, lodging, rail and parking on Vacation &gt; Travel itself (so the
/// depth-two parent has direct postings beside its depth-three children) and
/// dining. Every trip row carries the header tag <see cref="TripTag"/>, spelled in
/// lower case in life year three, so one tag arrives in two spellings. Life year
/// four's trip is shorter.</para>
///
/// <para><b>Returns.</b> Every two or three months a purchase of clothing or
/// furnishing is returned in part, later the same month, as a negative amount on
/// the purchase's own category and card.</para>
/// </remarks>
public sealed class SpendingPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "spending";

    /// <summary>The summer trip's header tag.</summary>
    public const string TripTag = "Summer trip";

    /// <summary>The life year whose trip spells the tag in lower case.</summary>
    public const int LowerCaseTripYear = 3;

    /// <summary>The life year whose trip is shorter.</summary>
    public const int ShortTripYear = 4;

    /// <summary>The mark of every trip row.</summary>
    public const string TripMark = "summer-trip";

    /// <summary>The mark of every return.</summary>
    public const string ReturnMark = "return";

    /// <summary>A spending stream: its name, monthly mean count, the life months it
    /// runs in, its share paid by debit card, its price kind and its purchases.</summary>
    public sealed record SpendingStream(
        string Name, decimal MonthlyMean, int FromMonth, decimal DebitShare, bool Everyday,
        DemoInflation.Kind PriceKind, IReadOnlyList<Purchase> Purchases);

    /// <summary>One kind of purchase: payee, the bank's raw text, category segments
    /// and an amount range in cents at the window-start price level.</summary>
    public sealed record Purchase(string Payee, string BankPayee, string[] Category, long Min, long Max);

    /// <summary>The everyday streams, by monthly mean.</summary>
    public static IReadOnlyList<SpendingStream> Streams { get; } =
    [
        new("groceries", 15m, 0, 0.25m, true, DemoInflation.Kind.Groceries,
        [
            new("Quellmont Market", "QUELLMONT MARKET #0412", ["Personal", "Groceries"], 25_00L, 115_00L),
            new("Pinewood Grocery", "PINEWOOD GROCERY 88", ["Personal", "Groceries"], 15_00L, 85_00L),
            new("Valebrook Foods", "VALEBROOK FOODS 1120", ["Personal", "Groceries"], 10_00L, 60_00L),
        ]),
        new("dining", 34m, 0, 0.10m, true, DemoInflation.Kind.Dining,
        [
            new("Copperleaf Coffee", "COPPERLEAF COFFEE 0217", ["Personal", "Dining"], 3_75L, 9_50L),
            new("Copperleaf Coffee", "COPPERLEAF COFFEE 0217", ["Personal", "Dining"], 3_75L, 9_50L),
            new("Copperleaf Coffee", "COPPERLEAF COFFEE 0217", ["Personal", "Dining"], 3_75L, 9_50L),
            new("Tidewell Bakery", "TIDEWELL BAKERY", ["Personal", "Dining"], 5_00L, 16_00L),
            new("Tidewell Bakery", "TIDEWELL BAKERY", ["Personal", "Dining"], 5_00L, 16_00L),
            new("Brasswick Kitchen", "BRASSWICK KITCHEN 7781", ["Personal", "Dining"], 22_00L, 60_00L),
            new("Nine Lanterns", "NINE LANTERNS LLC", ["Personal", "Dining"], 16_00L, 48_00L),
            new("The Wenlow Tap", "WENLOW TAP HOUSE 0099", ["Personal", "Dining"], 14_00L, 40_00L),
            new("Marrow & Vine", "MARROW AND VINE 4410", ["Personal", "Dining"], 32_00L, 90_00L),
        ]),
        new("household", 18m, 0, 0.20m, false, DemoInflation.Kind.General,
        [
            new("Ferncastle Home Goods", "FERNCASTLE HOME GOODS", ["Home", "Misc"], 6_00L, 45_00L),
            new("Thistlewood Hardware", "THISTLEWOOD HARDWARE", ["Home", "Maintenance"], 6_00L, 70_00L),
            new("Dunmere Supply", "DUNMERE SUPPLY CO", ["Home", "Misc"], 4_00L, 30_00L),
            new("Dunmere Supply", "DUNMERE SUPPLY CO", ["Home", "Misc"], 4_00L, 30_00L),
            new("Larkspur Furnishings", "LARKSPUR FURNISHINGS", ["Home", "Furnishing"], 25_00L, 150_00L),
            new("Brightmoor Salon", "BRIGHTMOOR SALON", ["Personal", "Personal Care"], 18_00L, 60_00L),
        ]),
        new("personal", 12m, 0, 0m, false, DemoInflation.Kind.General,
        [
            new("Tamsett & Co", "TAMSETT AND CO RETAIL", ["Personal", "Clothing"], 18_00L, 110_00L),
            new("Orrindale Cinema", "ORRINDALE CINEMA 14", ["Personal", "Entertainment"], 14_00L, 44_00L),
            new("Kestrel Outfitters", "KESTREL OUTFITTERS", ["Personal", "Sporting Goods"], 16_00L, 90_00L),
            new("Pellowick Books", "PELLOWICK BOOKS 2210", ["Personal", "Books/Magazines"], 8_00L, 35_00L),
            new("Gantry Electronics", "GANTRY ELECTRONICS", ["Personal", "Computer"], 12_00L, 120_00L),
            new("Corner Gift Shop", "CORNER GIFT SHOP", ["Personal", "Gifts"], 10_00L, 50_00L),
            new("Hillcrest Pharmacy Goods", "HILLCREST PHARMACY", ["Personal", "Misc"], 4_00L, 28_00L),
        ]),
        new("fuel", 5m, 0, 0m, true, DemoInflation.Kind.General,
        [
            new("Westgate Fuel", "WESTGATE FUEL 221", ["Automotive", "Fuel"], 28_00L, 62_00L),
            new("Oakline Petrol", "OAKLINE PETROL STN 7", ["Automotive", "Fuel"], 25_00L, 58_00L),
        ]),
        new("automotive", 1.5m, 0, 0.30m, false, DemoInflation.Kind.General,
        [
            new("Ridgeway Auto Service", "RIDGEWAY AUTO SVC", ["Automotive", "Service"], 45_00L, 420_00L),
            new("Clearwash Car Care", "CLEARWASH CAR CARE", ["Automotive", "Misc"], 12_00L, 30_00L),
        ]),
        new("baby", 5m, HouseholdTerms.PaymentPlanOpens, 0m, false, DemoInflation.Kind.General,
        [
            new("Little Sprout Baby Shop", "LITTLE SPROUT BABY", ["Childcare", "Baby Supplies"], 12_00L, 95_00L),
            new("Quellmont Market", "QUELLMONT MARKET #0412", ["Childcare", "Baby Supplies"], 18_00L, 60_00L),
        ]),
    ];

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 70;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(book);
        new Run(this, calendar, rng, book).Post();
    }

    private sealed class Run(SpendingPattern pattern, DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        private string Category(params string[] segments) => book.Base.Category(MdCategoryType.Expense, segments);

        public void Post()
        {
            foreach (var stream in Streams) Everyday(stream);
            Crib();
            Holidays();
            SummerTrips();
            Returns();
        }

        private DemoTxn Buy(DateOnly date, bool everyday, decimal debitShare, DemoRng stream, Purchase purchase, long amount,
            string? memo = null, string? tags = null)
        {
            var debit = debitShare > 0 && stream.Chance(debitShare);
            if (debit)
            {
                return book.Post(pattern, new DemoTxn
                {
                    AccountId = book.Account(DemoAccountRoles.JointChecking).Id,
                    Date = date,
                    Description = purchase.Payee,
                    Memo = memo,
                    Tags = tags,
                    BankPayee = "POS DEBIT " + purchase.BankPayee,
                    Splits = [DemoSplit.Bank(Category(purchase.Category), amount)],
                });
            }
            return DemoCards.Purchase(pattern, book, DemoCards.CardFor(calendar, date, everyday), date,
                purchase.Payee, purchase.BankPayee, Category(purchase.Category), amount, memo, tags);
        }

        // The count is the whole calendar month's, and each day is drawn over the
        // whole month; in the partial current month the purchases after AsOf are
        // drawn and then dropped, so the month keeps its daily rate and every draw
        // before AsOf is the same whatever AsOf's day is.
        private void Everyday(SpendingStream spec)
        {
            for (var month = spec.FromMonth; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var stream = rng.Stream(pattern.Name + ":" + spec.Name, calendar.MonthStart(month));
                var count = stream.NextCount(spec.MonthlyMean);
                var last = calendar.MonthEnd(month).Day;
                for (var i = 0; i < count; i++)
                {
                    var date = calendar.DayInMonth(month, (int)stream.NextLong(1, last));
                    var purchase = stream.Choice(spec.Purchases);
                    var amount = DemoInflation.Apply(spec.PriceKind, month, stream.NextMinor(purchase.Min, purchase.Max));
                    if (!calendar.Contains(date))
                    {
                        if (spec.DebitShare > 0) stream.Chance(spec.DebitShare);
                        continue;
                    }
                    var txn = Buy(date, spec.Everyday, spec.DebitShare, stream, purchase, amount);
                    if (month == HouseholdTerms.RewardsCardOpens && txn.AccountId == book.Account(DemoAccountRoles.RewardsCard).Id
                        && book.Marked(DemoEvents.RewardsFirst).Count == 0)
                    {
                        book.Mark(DemoEvents.RewardsFirst, txn);
                    }
                }
            }
        }

        // The one-off nursery purchase in the birth month.
        private void Crib()
        {
            var date = calendar.DayInMonth(HouseholdTerms.PaymentPlanOpens, 6);
            var card = DemoCards.CardFor(calendar, date, everyday: false);
            var txn = book.Post(pattern, new DemoTxn
            {
                AccountId = book.Account(card).Id,
                Date = date,
                Description = "Little Sprout Baby Shop",
                Memo = "Crib and car seat",
                BankPayee = "LITTLE SPROUT BABY",
                Exported = !DemoCards.IsCsvOnly(book, book.Account(card).Id, date, DemoEntry.Auto),
                Splits =
                [
                    DemoSplit.Bank(Category("Childcare", "Baby Supplies"), 389_00L, "Crib"),
                    DemoSplit.Bank(Category("Childcare", "Baby Supplies"), 229_00L, "Car seat"),
                ],
            });
            book.Mark(DemoEvents.Crib, txn);
        }

        // Gifts, outings, clothes and decorations from mid-November to Christmas.
        private void Holidays()
        {
            for (var month = 0; month <= DemoCalendar.CurrentLifeMonth; month++)
            {
                var start = calendar.MonthStart(month);
                if (start.Month is not (11 or 12)) continue;
                var stream = rng.Stream(pattern.Name + ":holidays", start);
                var firstDay = start.Month == 11 ? 15 : 1;
                var lastDay = start.Month == 11 ? 30 : 23;
                Purchase[] kinds =
                [
                    new("Corner Gift Shop", "CORNER GIFT SHOP", ["Personal", "Gifts"], 25_00L, 160_00L),
                    new("Gantry Electronics", "GANTRY ELECTRONICS", ["Personal", "Gifts"], 40_00L, 240_00L),
                    new("Orrindale Cinema", "ORRINDALE CINEMA 14", ["Personal", "Entertainment"], 24_00L, 70_00L),
                    new("Tamsett & Co", "TAMSETT AND CO RETAIL", ["Personal", "Clothing"], 30_00L, 150_00L),
                    new("Ferncastle Home Goods", "FERNCASTLE HOME GOODS", ["Home", "Misc"], 15_00L, 90_00L),
                ];
                var count = 3 + stream.NextCount(3m);
                for (var i = 0; i < count; i++)
                {
                    var date = calendar.DayInMonth(month, (int)stream.NextLong(firstDay, lastDay));
                    if (!calendar.Contains(date)) continue;
                    var purchase = i < 2 ? kinds[i] : stream.Choice(kinds);
                    Buy(date, everyday: false, debitShare: 0m, stream, purchase,
                        DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(purchase.Min, purchase.Max)),
                        "Holiday");
                }
            }
        }

        private void SummerTrips()
        {
            var firstYear = calendar.WindowStart.Year;
            for (var year = firstYear; year <= calendar.AsOf.Year; year++)
            {
                var stream = rng.Stream(pattern.Name + ":trip", year, 1);
                var tripMonthOfYear = stream.Chance(0.5m) ? 7 : 8;
                var tripMonth = calendar.LifeMonthOf(new DateOnly(year, tripMonthOfYear, 1));
                if (tripMonth < 0 || tripMonth >= DemoCalendar.CurrentLifeMonth) continue;
                var lifeYear = DemoCalendar.LifeYearOf(tripMonth);
                var tag = lifeYear == LowerCaseTripYear ? TripTag.ToLowerInvariant() : TripTag;
                var shortTrip = lifeYear == ShortTripYear;
                var nights = shortTrip ? (int)stream.NextLong(2, 3) : (int)stream.NextLong(5, 8);
                var firstDay = calendar.DayInMonth(tripMonth, (int)stream.NextLong(3, 18));

                void Trip(DateOnly date, string payee, string bankPayee, string[] category, long amount, string memo) =>
                    book.Mark(TripMark, DemoCards.Purchase(pattern, book, DemoCards.CardFor(calendar, date, everyday: false),
                        date, payee, bankPayee, Category(category),
                        DemoInflation.Apply(DemoInflation.Kind.General, calendar.LifeMonthOf(date), amount), memo, tag));

                var bookingMonth = calendar.LifeMonthOf(new DateOnly(year, stream.Chance(0.5m) ? 4 : 5, 1));
                var booked = calendar.DayInMonth(bookingMonth < 0 ? 0 : bookingMonth, (int)stream.NextLong(2, 26));
                if (bookingMonth >= 0 && calendar.Contains(booked))
                {
                    Trip(booked, "Bluecrest Airways", "BLUECREST AIR 0062", ["Vacation", "Travel", "Airfare"],
                        stream.NextMinor(640_00L, 1_180_00L), "Flights");
                }

                Trip(firstDay, "Driftline Car Rental", "DRIFTLINE RENTAL", ["Vacation", "Travel", "Car Rental"],
                    stream.NextMinor(220_00L, 360_00L) * nights / 5, "Car rental");
                Trip(firstDay.AddDays(nights), "Skerrowby Inn", "SKERROWBY INN LODGING", ["Vacation", "Lodging"],
                    stream.NextMinor(135_00L, 210_00L) * nights, "Lodging");
                Trip(firstDay.AddDays(1), "Northbound Rail", "NORTHBOUND RAIL TICKETS", ["Vacation", "Travel"],
                    stream.NextMinor(38_00L, 96_00L), "Rail day trip");
                Trip(firstDay.AddDays(nights), "Harbour Parking", "HARBOUR PARKING GARAGE", ["Vacation", "Travel"],
                    stream.NextMinor(14_00L, 42_00L), "Parking");
                var meals = shortTrip ? 2 : 3 + (int)stream.NextLong(0, 3);
                for (var i = 0; i < meals; i++)
                {
                    Trip(firstDay.AddDays(i % (nights + 1)), "Saltmarsh Grill", "SALTMARSH GRILL", ["Personal", "Dining"],
                        stream.NextMinor(32_00L, 120_00L), "Dinner out");
                }
            }
        }

        // A part return of a clothing or furnishing purchase every two or three months.
        private void Returns()
        {
            var clothing = Category("Personal", "Clothing");
            var furnishing = Category("Home", "Furnishing");
            var month = 1;
            while (month < DemoCalendar.CurrentLifeMonth)
            {
                var stream = rng.Stream(pattern.Name + ":returns", calendar.MonthStart(month));
                var category = stream.Chance(0.6m) ? clothing : furnishing;
                var monthStart = calendar.MonthStart(month);
                var monthEnd = calendar.MonthEnd(month);

                var bought = book.Transactions
                    .Where(t => t.Pattern == pattern.Name && t.Date >= monthStart && t.Date <= monthEnd.AddDays(-4)
                        && t.Splits.Count == 1 && t.Splits[0].AccountId == category && t.Splits[0].SplitAmount > 0)
                    .OrderBy(t => t.Date).ThenBy(t => t.Ordinal)
                    .FirstOrDefault();
                if (bought is null)
                {
                    var payee = category == clothing
                        ? new Purchase("Tamsett & Co", "TAMSETT AND CO RETAIL", ["Personal", "Clothing"], 45_00L, 160_00L)
                        : new Purchase("Larkspur Furnishings", "LARKSPUR FURNISHINGS", ["Home", "Furnishing"], 60_00L, 280_00L);
                    var date = calendar.DayInMonth(month, (int)stream.NextLong(2, 14));
                    bought = Buy(date, everyday: false, debitShare: 0m, stream, payee,
                        DemoInflation.Apply(DemoInflation.Kind.General, month, stream.NextMinor(payee.Min, payee.Max)));
                }

                var returned = bought.Date.AddDays((int)stream.NextLong(3, 9));
                if (returned > monthEnd) returned = monthEnd;
                var part = DemoAmortization.RoundCents(bought.Splits[0].SplitAmount * (stream.Chance(0.5m) ? 1m : 0.5m));
                var account = book.AccountById(bought.AccountId)!;
                if (calendar.Contains(returned) && account.IsActiveOn(returned))
                {
                    book.Mark(ReturnMark, DemoCards.PurchaseOn(pattern, book, account, returned, bought.Description,
                        (bought.BankPayee ?? bought.Description.ToUpperInvariant()) + " RETURN", category, -part, "Returned"));
                }
                month += 2 + (int)stream.NextLong(0, 1);
            }
        }
    }
}
