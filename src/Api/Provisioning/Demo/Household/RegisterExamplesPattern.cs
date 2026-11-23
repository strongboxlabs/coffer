namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The rows the import pins and the seeding step need in a fixed shape: a grocery row with two lines on one category, a one-split row whose
/// other side has a memo of its own, two feed rows the bank sends twice, three feed
/// rows whose payee is still the bank's text, and two hand-entered rows on the
/// replacement card's latest closed statement.
/// </summary>
/// <remarks>
/// Everything the manifest names lies in the last full month before AsOf's (or, for
/// the card rows, in the statement the CSV carries), so a three-month span carries
/// it. The rows are marked so the manifest finds them without matching their text.
/// </remarks>
public sealed class RegisterExamplesPattern : IDemoPattern
{
    /// <summary>The pattern's name.</summary>
    public const string PatternName = "register-examples";

    /// <summary>The life month of the grocery row with two lines on one category.</summary>
    public const int SymmetricSplitMonth = 47;

    /// <summary>The life month of the one-split row with its own counterpart memo.</summary>
    public const int CounterpartMemoMonth = 48;

    /// <summary>The counterpart memo of that row.</summary>
    public const string CounterpartMemo = "Receipt in the kitchen drawer";

    /// <summary>The mark of each re-sent duplicate (the second of each pair).</summary>
    public const string DuplicateMark = "duplicate";

    /// <summary>The mark of each edit candidate, in the order of <see cref="EditCandidates"/>.</summary>
    public const string EditCandidateMark = "edit-candidate";

    /// <summary>The mark of each hand-entered row on the CSV statement.</summary>
    public const string CardStatementMark = "card-statement-exported";

    /// <summary>The edit candidates: the bank's text (also the row's payee), the
    /// payee the seeder gives the row, its category and its amount.</summary>
    public static IReadOnlyList<(string BankText, string NewPayee, string[] Category, long Amount)> EditCandidates { get; } =
    [
        ("SQ *MAPLEWICK CAFE 0231", "Maplewick Cafe", ["Personal", "Dining"], 18_40L),
        ("POS DEBIT 0417 TARNBY FLORIST", "Tarnby Florist", ["Personal", "Gifts"], 46_00L),
        ("ACH WEB CORVELL FITNESS DUES", "Corvell Fitness", ["Personal", "Sporting Goods"], 39_00L),
    ];

    /// <summary>The month before AsOf's: the last full month.</summary>
    public static int ExampleMonth => DemoCalendar.CurrentLifeMonth - 1;

    /// <inheritdoc />
    public string Name => PatternName;

    /// <inheritdoc />
    public int Rank => 75;

    /// <inheritdoc />
    public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking).Id;
        string Category(params string[] segments) => book.Base.Category(MdCategoryType.Expense, segments);

        DemoTxn Row(DateOnly date, string payee, string? bankPayee, string? memo, IReadOnlyList<DemoSplit> splits) =>
            book.Post(this, new DemoTxn
            {
                AccountId = checking,
                Date = date,
                Description = payee,
                Memo = memo,
                BankPayee = bankPayee,
                Splits = splits,
            });

        var groceries = Category("Personal", "Groceries");
        book.Mark(DemoEvents.SymmetricSplit, Row(calendar.DayInMonth(SymmetricSplitMonth, 8), "Hollow Lane Market",
            "HOLLOW LANE MKT 0042", "Two receipts",
            [DemoSplit.Bank(groceries, 42_10L, "Weekly shop"), DemoSplit.Bank(groceries, 13_80L, "Party supplies")]));

        book.Mark(DemoEvents.CounterpartMemo, Row(calendar.DayInMonth(CounterpartMemoMonth, 10), "Corner Hardware",
            "CORNER HARDWARE 17", "Shelf brackets",
            [DemoSplit.Bank(Category("Home", "Maintenance"), 21_50L, CounterpartMemo)]));

        // Two debit purchases the bank sends twice: same date, payee and amount; the
        // provenance pass gives the copy a FITID of its own.
        var month = ExampleMonth;
        foreach (var (day, payee, bank, category, amount) in new[]
        {
            (9, "Pinewood Grocery", "POS DEBIT PINEWOOD GROCERY 88", Category("Personal", "Groceries"), 63_27L),
            (21, "Westgate Fuel", "POS DEBIT WESTGATE FUEL 221", Category("Automotive", "Fuel"), 48_15L),
        })
        {
            var date = calendar.DayInMonth(month, day);
            Row(date, payee, bank, null, [DemoSplit.Bank(category, amount)]);
            book.Mark(DuplicateMark, Row(date, payee, bank, null, [DemoSplit.Bank(category, amount)]));
        }

        // Feed rows whose payee is the bank's own text, so the import keeps no
        // original beside them; the seeder renames them.
        for (var i = 0; i < EditCandidates.Count; i++)
        {
            var (text, _, category, amount) = EditCandidates[i];
            book.Mark(EditCandidateMark, Row(calendar.DayInMonth(month, 12 + i * 4), text, text, null,
                [DemoSplit.Bank(Category(category), amount)]));
        }

        // Two rows typed in by hand on the statement the CSV carries; the seeder
        // merges each with its CSV copy.
        var period = DemoCards.LatestRewardsStatement(book);
        var rewards = book.Account(DemoAccountRoles.RewardsCard);
        foreach (var (back, payee, bank, category, amount) in new[]
        {
            (11, "Saltmarsh Grill", "SALTMARSH GRILL", Category("Personal", "Dining"), 47_13L),
            (4, "Kestrel Outfitters", "KESTREL OUTFITTERS", Category("Personal", "Sporting Goods"), 128_62L),
        })
        {
            var date = period.ClosesOn.AddDays(-back);
            if (date <= period.OpensAfter) date = period.OpensAfter.AddDays(1);
            book.Mark(CardStatementMark, DemoCards.PurchaseOn(this, book, rewards, date, payee, bank,
                category, amount, "Entered from the receipt", entry: DemoEntry.Manual));
        }
    }
}
