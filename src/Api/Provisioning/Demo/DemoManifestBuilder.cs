namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Builds the manifest (<see cref="DemoManifest"/>) from a verified book: everything the seeding
/// step must find in the imported Demo, by MD id or by value.
/// </summary>
/// <remarks>
/// <para><b>Budget targets</b> cover the twelve months ending in AsOf's month:
/// leaf targets on four Personal categories and three Bills categories, root targets
/// on four subtrees. Each month's target starts from the generator's own actuals:
/// the mean of that month's actual and the trailing twelve full months' average
/// (the average alone for AsOf's partial month), times a factor drawn per category
/// and month, rounded to whole tens. Some months land over the target and some
/// under.</para>
/// </remarks>
public static class DemoManifestBuilder
{
    /// <summary>The lowest budget factor.</summary>
    public const decimal BudgetFactorLow = 0.85m;

    /// <summary>The width of the budget factor's range.</summary>
    public const decimal BudgetFactorRange = 0.30m;

    /// <summary>The categories the manifest names, by role.</summary>
    public static IReadOnlyList<(string Role, DemoCategoryRef Category)> CategoryRoles { get; } =
    [
        ("groceries", Expense("Personal", "Groceries")),
        ("dining", Expense("Personal", "Dining")),
        ("entertainment", Expense("Personal", "Entertainment")),
        ("clothing", Expense("Personal", "Clothing")),
        ("personal", Expense("Personal")),
        ("automotive", Expense("Automotive")),
        ("vacation", Expense("Vacation")),
        ("childcare", Expense("Childcare")),
        ("home", Expense("Home")),
        ("electric", Expense("Bills", "Electric")),
        ("natural-gas", Expense("Bills", "Natural Gas")),
        ("internet", Expense("Bills", "Internet")),
        ("mortgage-interest", Expense("Bills", "Mortgage Interest")),
        ("property-tax", Expense("Tax", "Property Tax")),
        ("home-insurance", Expense("Insurance", "Home")),
        ("valuation-adjustment", Expense("Valuation Adjustment")),
        ("dividends", new DemoCategoryRef(MdCategoryType.Income, ["Investment", "Dividends"])),
        ("interest-received", new DemoCategoryRef(MdCategoryType.Income, ["Investment", "Interest Received"])),
    ];

    /// <summary>The budget layout, by category role.</summary>
    public static IReadOnlyList<string> BudgetRoles { get; } =
    [
        "groceries", "dining", "entertainment", "clothing",
        "automotive", "vacation", "childcare", "home",
        "electric", "natural-gas", "internet",
    ];

    /// <summary>The manifest of <paramref name="book"/>.</summary>
    public static DemoManifest Build(DemoBook book, DemoMarket market, DemoRngFactory rng)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(rng);

        var categories = new Dictionary<string, DemoCategoryRef>(StringComparer.Ordinal);
        foreach (var (role, category) in CategoryRoles)
        {
            // Fails here, not in the seeder, when a role names no base category.
            book.Base.Category(category.Type, [.. category.Segments]);
            categories[role] = category;
        }

        var csv = DemoCards.StatementRows(book);
        var cardRows = book.Marked(RegisterExamplesPattern.CardStatementMark)
            .Select(t => new DemoCardStatementRow(t.Id, t.Date, csv.Single(r => r.Txn.Id == t.Id).Row.Amount))
            .ToList();

        var edits = book.Marked(RegisterExamplesPattern.EditCandidateMark);
        var electric = book.Marked(BillsPattern.ElectricOmittedMark);

        return new DemoManifest
        {
            AccountIds = book.Accounts
                .Where(a => !a.IsHolding)
                .ToDictionary(a => a.Role, a => a.Id, StringComparer.Ordinal),
            Categories = categories,
            DuplicateTxnIds = book.Marked(RegisterExamplesPattern.DuplicateMark).Select(t => t.Id).ToList(),
            EditCandidates = edits
                .Select((t, i) => new DemoEditCandidate(t.Id, RegisterExamplesPattern.EditCandidates[i].NewPayee))
                .ToList(),
            CheckingReconcile = DemoStatusRule.CheckingReconcile(book),
            CardStatement = new DemoCardStatementRef(DemoCards.LatestRewardsStatement(book).ClosesOn, cardRows),
            ElectricOmissions = electric.Select(t => new DemoDatedAmount(t.Date, t.Splits[0].SplitAmount)).ToList(),
            ElectricDayOfMonth = BillsPattern.ElectricDay,
            TargetDateLookThrough = DemoSecurities.TargetDateWeights,
            StableValueMonthEnds = market.StableValueMonthEnds,
            BudgetTargets = BudgetTargets(book, rng, categories),
            InternetReminderId = DemoReminders.InternetId(book.Ids),
            Folders = HouseholdAccounts.Folders(book),
        };
    }

    /// <summary>What a category (and everything beneath it) took in life month
    /// <paramref name="lifeMonth"/>, in cents, over every simulated row.</summary>
    public static long ActualIn(DemoBook book, DemoCategoryRef category, int lifeMonth)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(category);
        var from = book.Calendar.MonthStart(lifeMonth);
        var through = book.Calendar.MonthEnd(lifeMonth);
        var total = 0L;
        foreach (var txn in book.Transactions)
        {
            if (txn.Date < from || txn.Date > through) continue;
            foreach (var split in txn.Splits)
            {
                if (book.Base.IsWithin(split.AccountId, category.Type, category.Segments)) total += split.SplitAmount;
            }
        }
        return total;
    }

    private static List<DemoBudgetTarget> BudgetTargets(
        DemoBook book, DemoRngFactory rng, IReadOnlyDictionary<string, DemoCategoryRef> categories)
    {
        var targets = new List<DemoBudgetTarget>();
        const int current = DemoCalendar.CurrentLifeMonth;
        foreach (var role in BudgetRoles)
        {
            var category = categories[role];
            var trailing = 0L;
            var actuals = new long[13];
            for (var back = 1; back <= 12; back++)
            {
                actuals[back] = ActualIn(book, category, current - back);
                trailing += actuals[back];
            }
            var average = trailing / 12;

            for (var back = 11; back >= 0; back--)
            {
                var month = current - back;
                var start = book.Calendar.MonthStart(month);
                var stream = rng.Stream("budget:" + role, start);
                var basis = back == 0 ? average : (actuals[back] + average) / 2;
                var factor = BudgetFactorLow + BudgetFactorRange * stream.NextUnit();
                var amount = DemoAmortization.RoundCents(basis * factor / 1_000m) * 1_000L;
                if (amount <= 0) continue;
                targets.Add(new DemoBudgetTarget(category, start.Year, start.Month, amount));
            }
        }
        return targets;
    }

    private static DemoCategoryRef Expense(params string[] segments) => new(MdCategoryType.Expense, segments);
}
