namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Which card a purchase goes on, the card statements' dates, and the one posting
/// helper every spending pattern uses.
/// </summary>
/// <remarks>
/// <para><b>Routing.</b> Before the replacement card opens, everything goes on the
/// original card. From its opening month the everyday purchases (groceries, dining,
/// fuel) move to the replacement card, and the rest stays on the original until its
/// final statement closes; after that everything is on the replacement card.</para>
///
/// <para><b>The statement the CSV carries.</b> The replacement card's latest
/// closed statement is written to <see cref="CardStatementCsv"/>. Its rows are
/// simulated in full but left out of the export (<see cref="DemoTxn.Exported"/>
/// false), except the hand-entered ones, which the seeder later merges with their
/// CSV copies. <see cref="Purchase"/> applies that rule, so no spending pattern has
/// to.</para>
/// </remarks>
public static class DemoCards
{
    /// <summary>The original card's statement close day.</summary>
    public const int ClassicCloseDay = 22;

    /// <summary>The replacement card's statement close day.</summary>
    public const int RewardsCloseDay = 4;

    /// <summary>Days after a close the autopay draws the statement balance; a shut
    /// day moves to the next business day.</summary>
    public const int AutopayDaysAfterClose = 25;

    /// <summary>The mark of every card payment row.</summary>
    public const string PaymentMark = "card-payment";

    /// <summary>The close of the original card's final statement.</summary>
    public static DateOnly ClassicFinalClose(DemoCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return calendar.DayInMonth(HouseholdTerms.ClassicCardCloses, ClassicCloseDay);
    }

    /// <summary>The card role a purchase dated <paramref name="date"/> goes on;
    /// <paramref name="everyday"/> for groceries, dining and fuel.</summary>
    public static string CardFor(DemoCalendar calendar, DateOnly date, bool everyday)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (date < calendar.MonthStart(HouseholdTerms.RewardsCardOpens)) return DemoAccountRoles.ClassicCard;
        return everyday || date > ClassicFinalClose(calendar) ? DemoAccountRoles.RewardsCard : DemoAccountRoles.ClassicCard;
    }

    /// <summary>The autopay date of a statement that closes on <paramref name="close"/>.</summary>
    public static DateOnly AutopayDate(DateOnly close) => DemoCalendar.OnOrNextBusinessDay(close.AddDays(AutopayDaysAfterClose));

    /// <summary>The replacement card's latest statement closed on or before AsOf:
    /// the one <see cref="CardStatementCsv"/> carries.</summary>
    public static DemoStatementPeriod LatestRewardsStatement(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var periods = book.StatementPeriods(book.Account(DemoAccountRoles.RewardsCard).Id);
        return periods.Count > 0
            ? periods[^1]
            : throw new InvalidOperationException("the replacement card has no closed statement");
    }

    /// <summary>True when a row on <paramref name="accountId"/> dated
    /// <paramref name="date"/> reaches the ledger only through the statement CSV.</summary>
    public static bool IsCsvOnly(DemoBook book, string accountId, DateOnly date, DemoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(book);
        return entry == DemoEntry.Auto
            && accountId == book.Account(DemoAccountRoles.RewardsCard).Id
            && LatestRewardsStatement(book).Contains(date);
    }

    /// <summary>
    /// Post one purchase (a positive <paramref name="amount"/>) or refund (negative)
    /// on <paramref name="accountRole"/> into <paramref name="categoryId"/>. A row on
    /// the CSV statement is simulated but not exported.
    /// </summary>
    public static DemoTxn Purchase(
        IDemoPattern pattern, DemoBook book, string accountRole, DateOnly date, string payee, string? bankPayee,
        string categoryId, long amount, string? memo = null, string? tags = null, DateOnly? taxDate = null,
        DemoEntry entry = DemoEntry.Auto, string? lineDescription = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        return PurchaseOn(pattern, book, book.Account(accountRole), date, payee, bankPayee, categoryId, amount,
            memo, tags, taxDate, entry, lineDescription);
    }

    /// <summary><see cref="Purchase"/> on a given account.</summary>
    public static DemoTxn PurchaseOn(
        IDemoPattern pattern, DemoBook book, DemoAccount account, DateOnly date, string payee, string? bankPayee,
        string categoryId, long amount, string? memo = null, string? tags = null, DateOnly? taxDate = null,
        DemoEntry entry = DemoEntry.Auto, string? lineDescription = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(account);
        return book.Post(pattern, new DemoTxn
        {
            AccountId = account.Id,
            Date = date,
            TaxDate = taxDate,
            Description = payee,
            Memo = memo,
            Tags = tags,
            Entry = entry,
            BankPayee = bankPayee,
            Exported = !IsCsvOnly(book, account.Id, date, entry),
            Splits = [DemoSplit.Bank(categoryId, amount, lineDescription)],
        });
    }

    /// <summary>The CSV rows of the latest replacement-card statement: every row on
    /// the card in the period except payments, in export order, with the bank's
    /// description and the amount as it moved the card.</summary>
    public static IReadOnlyList<(DemoTxn Txn, CardStatementCsvRow Row)> StatementRows(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var card = book.Account(DemoAccountRoles.RewardsCard);
        var period = LatestRewardsStatement(book);
        var rows = new List<(DemoTxn, CardStatementCsvRow)>();
        foreach (var txn in book.TransactionsInExportOrder())
        {
            if (txn.AccountId != card.Id || !period.Contains(txn.Date)) continue;
            var amount = 0L;
            foreach (var split in txn.Splits) amount += split.ParentAmount;
            rows.Add((txn, new CardStatementCsvRow(txn.Date, txn.BankPayee ?? txn.Description.ToUpperInvariant(), amount)));
        }
        return rows;
    }

    /// <summary>The card checks: every statement closed on or before AsOf whose
    /// payment falls by AsOf is paid in full by one marked payment, and no row the CSV
    /// carries is also exported unless it was entered by hand.</summary>
    internal static IEnumerable<string> Problems(DemoBook book)
    {
        var payments = book.Marked(PaymentMark);
        foreach (var role in new[] { DemoAccountRoles.ClassicCard, DemoAccountRoles.RewardsCard })
        {
            var card = book.Account(role);
            foreach (var period in book.StatementPeriods(card.Id))
            {
                var owed = -book.BalanceAtEndOf(card.Id, period.ClosesOn);
                if (owed <= 0) continue;
                var paid = payments.FirstOrDefault(p => p.Splits[0].AccountId == card.Id && p.Memo == PaymentMemo(period.ClosesOn));
                if (paid is null)
                {
                    // The original card's statements are all due long before AsOf (its
                    // last is paid off early); the replacement's latest may not be due yet.
                    if (role == DemoAccountRoles.ClassicCard || AutopayDate(period.ClosesOn) <= book.Calendar.AsOf)
                        yield return $"{role} statement closing {period.ClosesOn:yyyy-MM-dd} is not paid";
                    continue;
                }
                if (paid.Splits[0].SplitAmount != owed)
                    yield return $"{role} statement closing {period.ClosesOn:yyyy-MM-dd} owes {owed} but was paid {paid.Splits[0].SplitAmount}";
            }
        }

        var classic = book.Account(DemoAccountRoles.ClassicCard);
        if (book.BalanceAtEndOf(classic.Id, book.Calendar.AsOf) != 0) yield return "the closed card does not end at zero";

        foreach (var txn in book.Transactions)
        {
            if (txn.Exported && IsCsvOnly(book, txn.AccountId, txn.Date, txn.Entry))
                yield return $"{txn.Pattern} row {txn.Id} is on the CSV statement but exported";
        }

        // The seeder finds a hand-entered row's CSV copy by (date, amount): each
        // must name exactly one CSV row.
        var csv = StatementRows(book);
        foreach (var marked in book.Marked(RegisterExamplesPattern.CardStatementMark))
        {
            var mine = csv.Where(r => r.Txn.Id == marked.Id).Select(r => r.Row).ToList();
            if (mine.Count != 1)
            {
                yield return $"hand-entered row {marked.Id} is not on the CSV statement";
                continue;
            }
            if (csv.Count(r => r.Row.Date == mine[0].Date && r.Row.Amount == mine[0].Amount) != 1)
                yield return $"hand-entered row {marked.Id} shares its date and amount with another CSV row";
        }
    }

    /// <summary>The memo of the payment of the statement closing on <paramref name="close"/>.</summary>
    public static string PaymentMemo(DateOnly close) =>
        "Statement closing " + close.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The household's price levels: a monthly-compounded factor per life
/// month for groceries, dining and everything else.</summary>
public static class DemoInflation
{
    /// <summary>Groceries' yearly rate, as a percent.</summary>
    public const decimal GroceriesPercent = 4m;

    /// <summary>Dining's yearly rate, as a percent.</summary>
    public const decimal DiningPercent = 5m;

    /// <summary>Everything else's yearly rate, as a percent.</summary>
    public const decimal GeneralPercent = 3m;

    private static readonly decimal[] Groceries = Table(GroceriesPercent);
    private static readonly decimal[] Dining = Table(DiningPercent);
    private static readonly decimal[] General = Table(GeneralPercent);

    /// <summary>The kind of price a factor applies to.</summary>
    public enum Kind
    {
        /// <summary>Groceries.</summary>
        Groceries,

        /// <summary>Dining out.</summary>
        Dining,

        /// <summary>Everything else.</summary>
        General,
    }

    /// <summary>The price level of <paramref name="kind"/> in life month
    /// <paramref name="lifeMonth"/>, 1 at the window start.</summary>
    public static decimal Factor(Kind kind, int lifeMonth)
    {
        var month = lifeMonth < 0 ? 0 : lifeMonth > DemoCalendar.CurrentLifeMonth ? DemoCalendar.CurrentLifeMonth : lifeMonth;
        return kind switch
        {
            Kind.Groceries => Groceries[month],
            Kind.Dining => Dining[month],
            _ => General[month],
        };
    }

    /// <summary><paramref name="cents"/> at the price level of
    /// <paramref name="kind"/> in <paramref name="lifeMonth"/>, to the cent.</summary>
    public static long Apply(Kind kind, int lifeMonth, long cents) =>
        DemoAmortization.RoundCents(cents * Factor(kind, lifeMonth));

    private static decimal[] Table(decimal percent)
    {
        var table = new decimal[DemoCalendar.CurrentLifeMonth + 1];
        var monthly = 1m + percent / 100m / 12m;
        var level = 1m;
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = level;
            level *= monthly;
        }
        return table;
    }
}
