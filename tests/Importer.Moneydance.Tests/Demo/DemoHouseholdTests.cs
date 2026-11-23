using System.Globalization;
using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The household: density, account windows, the
/// dated life events, the balances that must never go below zero, the escrow, the
/// card statements and the CSV, the reminders and the manifest.
/// </summary>
public sealed class DemoHouseholdTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    private static readonly Lazy<DemoSimulation> Sim =
        new(() => DemoExportGenerator.Run(DemoSpec.Full(AsOf), DemoExportGenerator.Patterns));

    private static readonly Lazy<DemoBuild> Build = new(() => DemoExportGenerator.Generate(DemoSpec.Full(AsOf)));

    private static DemoBook Book => Sim.Value.Book;

    private static string Id(string role) => Book.Account(role).Id;

    // --- density -----------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-18")]
    [InlineData("2028-02-29")]
    [InlineData("2027-01-01")]
    public void Every_full_year_and_the_trailing_twelve_months_are_in_the_density_band(string asOf)
    {
        var date = DateOnly.Parse(asOf, CultureInfo.InvariantCulture);
        var book = date == AsOf ? Book : DemoExportGenerator.Simulate(DemoSpec.Full(date));
        var exported = book.Transactions.Where(t => t.Exported).ToList();

        var counts = new List<string>();
        for (var year = book.Calendar.WindowStart.Year; year <= date.Year; year++)
        {
            if (new DateOnly(year, 1, 1) < book.Calendar.WindowStart || new DateOnly(year, 12, 31) > date) continue;
            var count = exported.Count(t => t.Date.Year == year);
            counts.Add($"{year}: {count}");
            Assert.True(count is >= 1_500 and <= 2_500, $"{year} has {count} rows; " + string.Join(", ", counts));
        }
        Assert.NotEmpty(counts);

        var trailing = exported.Count(t => t.Date > date.AddMonths(-12));
        Assert.True(trailing is >= 1_500 and <= 2_500, $"the trailing twelve months have {trailing} rows");
    }

    /// <summary>The partial current month keeps the window's daily rate: its rows,
    /// and the rows on AsOf itself, stay within a small multiple of the mean per day
    /// of the full months, rather than a whole month's draws squeezed into the days
    /// up to AsOf.</summary>
    [Theory]
    [InlineData("2026-11-18")]
    [InlineData("2026-11-01")]
    [InlineData("2027-01-01")]
    public void The_partial_current_month_keeps_the_daily_rate(string asOf)
    {
        var date = DateOnly.Parse(asOf, CultureInfo.InvariantCulture);
        var book = date == AsOf ? Book : DemoExportGenerator.Simulate(DemoSpec.Full(date));
        var current = book.Calendar.MonthStart(DemoCalendar.CurrentLifeMonth);
        var exported = book.Transactions.Where(t => t.Exported).ToList();

        var fullDays = current.DayNumber - book.Calendar.WindowStart.DayNumber;
        var perDay = (decimal)exported.Count(t => t.Date < current) / fullDays;
        var partial = exported.Count(t => t.Date >= current);
        var onAsOf = exported.Count(t => t.Date == date);

        var partialCeiling = 2m * perDay * date.Day + 10m;
        var dayCeiling = 3m * perDay + 10m;
        Assert.True(partial <= partialCeiling,
            $"the partial month has {partial} rows in {date.Day} days; the window averages {perDay:0.00} a day");
        Assert.True(onAsOf <= dayCeiling,
            $"{date:yyyy-MM-dd} has {onAsOf} rows; the window averages {perDay:0.00} a day");
    }

    /// <summary>24 consecutive month-ends, a leap day, a Dec 31, a Jan 1, a holiday
    /// Monday and a DST day, plus a Saturday. The holiday Monday is the monthly
    /// reminder's occurrence day, whose bill posts on that day all the same. The
    /// Saturday is the yearly reminder's, whose bill moves to a business day after
    /// AsOf, so the reminders' in-flight case is swept.</summary>
    public static TheoryData<string> SweepDates()
    {
        var data = new TheoryData<string>();
        var end = new DateOnly(2026, 11, 30);
        for (var i = 0; i < 24; i++)
        {
            var month = end.AddMonths(-i);
            data.Add(new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        foreach (var extra in new[] { "2028-02-29", "2027-12-31", "2027-01-01", "2027-01-18", "2027-03-14", "2028-06-10" })
            data.Add(extra);
        return data;
    }

    [Theory]
    [MemberData(nameof(SweepDates))]
    public void Every_self_check_passes_across_the_asof_sweep(string asOf)
    {
        var date = DateOnly.Parse(asOf, CultureInfo.InvariantCulture);
        var (book, _) = DemoExportGenerator.RunUnverified(DemoSpec.Full(date), DemoExportGenerator.Patterns);
        Assert.Empty(book.FindProblems());
        foreach (var (mark, lifeMonth) in DemoEvents.All)
        {
            var row = Assert.Single(book.Marked(mark));
            Assert.Equal(lifeMonth, book.Calendar.LifeMonthOf(row.Date));
        }
        Assert.DoesNotContain(book.Transactions, t => t.Date > date);
    }

    // --- balances ------------------------------------------------------------------------

    [Fact]
    public void No_cash_account_ends_a_day_below_zero()
    {
        foreach (var role in new[]
                 {
                     DemoAccountRoles.JointChecking, DemoAccountRoles.Savings, DemoAccountRoles.CashWallet,
                     DemoAccountRoles.MortgageEscrow, DemoAccountRoles.FamilyHsa,
                 })
        {
            var account = Book.Account(role);
            var (lowest, when) = Book.LowestDayEnd(account.Id, account.ActiveFrom, AsOf);
            Assert.True(lowest >= 0, $"{role} ends {when:yyyy-MM-dd} at {lowest}");
        }
        Assert.Empty(Book.FindProblems());
    }

    [Fact]
    public void Checking_stays_at_the_buffer_once_the_sweeps_start()
    {
        var firstSweep = Book.Marked(SavingsPattern.SweepMark).Min(t => t.Date);
        var (lowest, when) = Book.LowestDayEnd(Id(DemoAccountRoles.JointChecking), firstSweep, AsOf);
        Assert.True(lowest >= SavingsPattern.Buffer, $"Checking ends {when:yyyy-MM-dd} at {lowest}");
        Assert.True(Book.Marked(SavingsPattern.SweepMark).Count >= 30, $"{Book.Marked(SavingsPattern.SweepMark).Count} sweeps");
        Assert.Single(Book.Marked(DemoEvents.DownPayment));
    }

    [Fact]
    public void Savings_interest_is_the_prior_month_end_balance_at_the_years_rate()
    {
        var savings = Id(DemoAccountRoles.Savings);
        var interest = Book.Marked(SavingsPattern.InterestMark);
        Assert.Equal(DemoCalendar.CurrentLifeMonth, interest.Count);
        foreach (var row in interest)
        {
            var month = Book.Calendar.LifeMonthOf(row.Date);
            Assert.Equal(Book.Calendar.MonthEnd(month), row.Date);
            var prior = Book.BalanceAtEndOf(savings, Book.Calendar.MonthStart(month).AddDays(-1));
            Assert.Equal(DemoAmortization.RoundCents(prior * SavingsPattern.ApyIn(month) / 100m / 12m), -row.Splits[0].SplitAmount);
        }
    }

    [Fact]
    public void Escrow_never_goes_negative_and_the_deposit_is_the_least_that_does_it()
    {
        var escrow = Id(DemoAccountRoles.MortgageEscrow);
        var closing = HousingPattern.ClosingDate(Book.Calendar);
        var (lowest, when) = Book.LowestDayEnd(escrow, closing, AsOf);
        Assert.True(lowest >= 0, $"escrow ends {when:yyyy-MM-dd} at {lowest}");

        var cushion = HouseholdTerms.EscrowCushionMonths * HouseholdTerms.MonthlyEscrow;
        Assert.InRange(lowest, cushion, cushion + 99);

        var closingRow = Assert.Single(Book.Marked(DemoEvents.Closing));
        var deposit = closingRow.Splits.Single(s => s.AccountId == escrow).SplitAmount;
        Assert.Equal(HousingPattern.InitialEscrowDeposit(Book.Calendar), deposit);
        Assert.Equal(0, (deposit - cushion) % 100);

        var disbursements = Book.Transactions.Where(t => t.AccountId == escrow).ToList();
        Assert.Contains(disbursements, t => t.Date.Month == 4);
        Assert.Contains(disbursements, t => t.Date.Month == 10);
        Assert.Contains(disbursements, t => t.Memo == "Homeowners insurance renewal"
            && Book.Calendar.LifeMonthOf(t.Date) == HouseholdTerms.ClosingMonth + 12);
    }

    [Fact]
    public void The_closing_is_one_multisplit_with_the_heavy_tags()
    {
        var closing = Assert.Single(Book.Marked(DemoEvents.Closing));
        Assert.Equal(Id(DemoAccountRoles.JointChecking), closing.AccountId);
        Assert.Equal(6, closing.Splits.Count);

        var house = closing.Splits.Single(s => s.AccountId == Id(DemoAccountRoles.House));
        Assert.Equal(HouseholdTerms.HousePrice + HouseholdTerms.SettlementCosts - HouseholdTerms.EarnestMoney, house.SplitAmount);
        Assert.Equal(-HouseholdTerms.MortgagePrincipal,
            closing.Splits.Single(s => s.AccountId == Id(DemoAccountRoles.Mortgage)).SplitAmount);

        var headerTags = closing.Tags!.Split(',');
        var houseTags = house.Tags!.Split(',');
        Assert.Equal(9, headerTags.Length);
        Assert.Equal(8, houseTags.Length);
        Assert.Contains(headerTags, t => t.Length >= 48);
        Assert.Contains(houseTags, t => t.Length >= 48);
        Assert.All(headerTags.Concat(houseTags), t => Assert.True(t.Length < 64, t));
        Assert.Empty(headerTags.Intersect(houseTags));

        // The mortgage's balance at the end of the closing month is the principal.
        Assert.Equal(-HouseholdTerms.MortgagePrincipal,
            Book.BalanceAtEndOf(Id(DemoAccountRoles.Mortgage), Book.Calendar.MonthEnd(HouseholdTerms.ClosingMonth)));
        Assert.Equal(HouseholdTerms.HousePrice + HouseholdTerms.SettlementCosts,
            Book.BalanceAtEndOf(Id(DemoAccountRoles.House), Book.Calendar.MonthEnd(HouseholdTerms.ClosingMonth)));
    }

    [Fact]
    public void The_mortgage_follows_its_schedule_and_the_loans_close_at_zero()
    {
        var mortgage = Id(DemoAccountRoles.Mortgage);
        var payments = Book.Transactions
            .Where(t => t.Pattern == HousingPattern.PatternName && t.Memo == HousingPattern.MortgageMemo)
            .OrderBy(t => t.Date).ToList();
        Assert.Equal(DemoCalendar.CurrentLifeMonth - HouseholdTerms.FirstMortgagePayment + 1, payments.Count);
        for (var k = 0; k < payments.Count; k++)
        {
            var principal = payments[k].Splits.Single(s => s.AccountId == mortgage).SplitAmount;
            var interest = payments[k].Splits[0].SplitAmount;
            Assert.Equal(HouseholdTerms.Mortgage.Payment, principal + interest);
            Assert.Equal(HouseholdTerms.Mortgage.InterestOn(HouseholdTerms.Mortgage.BalanceAfter(k)), interest);
            Assert.Equal(HouseholdTerms.MonthlyEscrow, payments[k].Splits[2].SplitAmount);
        }
        Assert.Equal(-HouseholdTerms.Mortgage.BalanceAfter(payments.Count), Book.BalanceAtEndOf(mortgage, AsOf));

        Assert.Equal(0, Book.BalanceAtEndOf(Id(DemoAccountRoles.CarLoan), AsOf));
        Assert.Equal(0, Book.BalanceAtEndOf(Id(DemoAccountRoles.PaymentPlan), AsOf));
        Assert.Equal(0, Book.BalanceAtEndOf(Id(DemoAccountRoles.ClassicCard), AsOf));
        var loanPayment = Book.Base.Category(MdCategoryType.Expense, "Loan", "Payment");
        Assert.DoesNotContain(Book.Transactions, t => t.Splits.Any(s => s.AccountId == loanPayment));
    }

    // --- windows and events ------------------------------------------------------------

    [Fact]
    public void Every_account_has_rows_only_inside_its_window()
    {
        var calendar = Book.Calendar;
        (DateOnly First, DateOnly Last)? Span(string role)
        {
            var id = Id(role);
            var dates = Book.Transactions
                .Where(t => t.AccountId == id || t.Splits.Any(s => s.AccountId == id))
                .Select(t => t.Date).ToList();
            return dates.Count == 0 ? null : (dates.Min(), dates.Max());
        }

        Assert.Null(Span(DemoAccountRoles.CertificateOfDeposit));
        Assert.True(Span(DemoAccountRoles.ClassicCard)!.Value.Last <= calendar.MonthEnd(HouseholdTerms.ClassicCardCloses));
        Assert.True(Span(DemoAccountRoles.RewardsCard)!.Value.First >= calendar.MonthStart(HouseholdTerms.RewardsCardOpens));
        Assert.Equal(HouseholdTerms.CarLoanCloses, calendar.LifeMonthOf(Span(DemoAccountRoles.CarLoan)!.Value.Last));
        var plan = Span(DemoAccountRoles.PaymentPlan)!.Value;
        Assert.Equal(HouseholdTerms.PaymentPlanOpens, calendar.LifeMonthOf(plan.First));
        Assert.Equal(HouseholdTerms.PaymentPlanCloses, calendar.LifeMonthOf(plan.Last));
        Assert.Equal(HouseholdTerms.RolloverMonth, calendar.LifeMonthOf(Span(DemoAccountRoles.RolloverIra)!.Value.First));
        Assert.Equal(HouseholdTerms.HsaOpens, calendar.LifeMonthOf(Span(DemoAccountRoles.FamilyHsa)!.Value.First));
        Assert.Equal(HouseholdTerms.HouseOpens, calendar.LifeMonthOf(Span(DemoAccountRoles.House)!.Value.First));
        Assert.Equal(HouseholdTerms.ClosingMonth, calendar.LifeMonthOf(Span(DemoAccountRoles.MortgageEscrow)!.Value.First));
        Assert.Equal(HouseholdTerms.ClosingMonth, calendar.LifeMonthOf(Span(DemoAccountRoles.Mortgage)!.Value.First));

        // Every account the household spends from has rows across the window.
        foreach (var role in new[] { DemoAccountRoles.JointChecking, DemoAccountRoles.Savings, DemoAccountRoles.CashWallet })
        {
            var span = Span(role)!.Value;
            Assert.Equal(0, calendar.LifeMonthOf(span.First));
            Assert.True(calendar.LifeMonthOf(span.Last) >= DemoCalendar.CurrentLifeMonth - 1, role);
        }
    }

    [Fact]
    public void Every_life_event_happens_once_in_its_month_and_in_order()
    {
        var previous = int.MinValue;
        foreach (var (mark, lifeMonth) in DemoEvents.All)
        {
            var row = Assert.Single(Book.Marked(mark));
            Assert.True(row.Exported, mark);
            Assert.Equal(lifeMonth, Book.Calendar.LifeMonthOf(row.Date));
            Assert.True(lifeMonth >= previous, $"{mark} is out of order");
            previous = lifeMonth;
        }

        // Rent runs through the house month; the news subscription only over its span.
        var rentCheques = Book.Transactions.Where(t => t.Memo == HousingPattern.RentMemo).ToList();
        Assert.Equal(HouseholdTerms.HouseOpens + 1, rentCheques.Count);
        Assert.All(rentCheques, t =>
        {
            Assert.Equal(DemoEntry.Manual, t.Entry);
            Assert.NotNull(t.CheckNumber);
            Assert.Equal(1, t.TaxDate!.Value.Day);
            Assert.True(t.Date > t.TaxDate.Value);
        });
        var numbers = rentCheques.OrderBy(t => t.Date).Select(t => int.Parse(t.CheckNumber!, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(HouseholdTerms.FirstRentCheque, numbers.Count), numbers);
    }

    [Fact]
    public void The_summer_trip_spells_its_tag_two_ways_and_nests_three_deep()
    {
        var trips = Book.Marked(SpendingPattern.TripMark);
        Assert.Contains(trips, t => t.Tags == SpendingPattern.TripTag);
        Assert.Contains(trips, t => t.Tags == SpendingPattern.TripTag.ToLowerInvariant());
        var travel = Book.Base.Category(MdCategoryType.Expense, "Vacation", "Travel");
        var airfare = Book.Base.Category(MdCategoryType.Expense, "Vacation", "Travel", "Airfare");
        Assert.Contains(trips, t => t.Splits[0].AccountId == travel);
        Assert.Contains(trips, t => t.Splits[0].AccountId == airfare);
        Assert.All(trips.Where(t => t.Splits[0].AccountId == airfare), t => Assert.Contains(t.Date.Month, new[] { 4, 5 }));
        Assert.NotEmpty(Book.Marked(SpendingPattern.ReturnMark));
    }

    // --- cards and the CSV -------------------------------------------------------------

    [Fact]
    public void Every_card_statement_is_paid_in_full()
    {
        Assert.Empty(Book.FindProblems());
        foreach (var payment in Book.Marked(DemoCards.PaymentMark))
        {
            var card = payment.Splits[0].AccountId;
            var close = DateOnly.ParseExact(payment.Memo!["Statement closing ".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Equal(-Book.BalanceAtEndOf(card, close), payment.Splits[0].SplitAmount);
        }
        Assert.Single(Book.Marked(DemoEvents.ClassicPayoff));
    }

    [Fact]
    public void The_csv_carries_the_latest_statement_and_matches_the_manifest()
    {
        var build = Build.Value;
        var statement = build.Manifest.CardStatement!;
        var period = DemoCards.LatestRewardsStatement(Book);
        Assert.Equal(period.ClosesOn, statement.CloseDate);

        var lines = build.CardStatementCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CardStatementCsv.Header, lines[0]);
        var rows = lines.Skip(1).Select(ParseCsv).ToList();
        Assert.True(rows.Count >= 10, $"the statement has {rows.Count} rows");
        Assert.All(rows, r => Assert.True(period.Contains(r.Date)));

        Assert.Equal(2, statement.ExportedRows.Count);
        foreach (var exported in statement.ExportedRows)
        {
            Assert.Single(rows, r => r.Date == exported.Date && r.Amount == exported.Amount);
            var txn = Book.Transactions.Single(t => t.Id == exported.TxnId);
            Assert.True(txn.Exported);
            Assert.Equal(DemoEntry.Manual, txn.Entry);
        }

        // Every other row on the card in the period is in the CSV only.
        var card = Id(DemoAccountRoles.RewardsCard);
        var onCard = Book.Transactions.Where(t => t.AccountId == card && period.Contains(t.Date)).ToList();
        Assert.Equal(onCard.Count, rows.Count);
        Assert.Equal(2, onCard.Count(t => t.Exported));

        // The CSV-only rows still move the card: the next autopay is the whole statement.
        var total = rows.Sum(r => r.Amount);
        Assert.Equal(Book.BalanceAtEndOf(card, period.ClosesOn) - Book.BalanceAtEndOf(card, period.OpensAfter)
            - PaymentsInto(card, period), total);
    }

    // --- electric, reminders and the manifest ---------------------------------------------

    [Fact]
    public void Electrics_last_three_bills_are_simulated_but_not_exported()
    {
        var electric = Book.Base.Category(MdCategoryType.Expense, "Bills", "Electric");
        var bills = Book.Transactions.Where(t => t.Splits[0].AccountId == electric && t.Pattern == BillsPattern.PatternName)
            .OrderBy(t => t.Date).ToList();
        var omitted = bills.TakeLast(3).ToList();
        Assert.All(omitted, t => Assert.False(t.Exported));
        Assert.All(bills.SkipLast(3), t => Assert.True(t.Exported));

        var manifest = Build.Value.Manifest;
        Assert.Equal(omitted.Select(t => (t.Date, t.Splits[0].SplitAmount)),
            manifest.ElectricOmissions.Select(o => (o.Date, o.Amount)));
        Assert.Equal(BillsPattern.ElectricDay, manifest.ElectricDayOfMonth);

        using var export = Build.Value.ToExport();
        var ids = export.AllItems.Where(i => i.ObjType == "txn").Select(i => i.Id).ToHashSet();
        Assert.All(omitted, t => Assert.DoesNotContain(t.Id, ids));
    }

    [Fact]
    public void The_two_reminders_are_acknowledged_through_their_last_occurrence()
    {
        using var export = Build.Value.ToExport();
        var reminders = export.AllItems.Where(i => i.ObjType == "reminder").Select(MdReminder.From).ToList();
        Assert.Equal(2, reminders.Count);

        var internet = reminders.Single(r => r.MonthlyDay == BillsPattern.InternetDay);
        Assert.Equal(3, internet.AckDays);
        Assert.Equal(DemoCalendar.MdDate(new DateOnly(2021, 11, BillsPattern.InternetDay)), internet.StartDate);
        Assert.Equal(DemoCalendar.MdDate(new DateOnly(2026, 11, BillsPattern.InternetDay)), internet.AcknowledgedDate);
        Assert.Equal(Build.Value.Manifest.InternetReminderId, internet.Id);
        var template = Assert.Single(internet.Txn!.Splits);
        Assert.Equal(BillsPattern.InternetIn(DemoCalendar.CurrentLifeMonth), template.SplitAmount);
        Assert.Equal(Id(DemoAccountRoles.JointChecking), internet.Txn.AcctId);

        var registration = reminders.Single(r => r.Yearly == 1);
        Assert.Equal(-1, registration.AckDays);
        Assert.Null(registration.MonthlyDay);
        Assert.Equal(DemoCalendar.MdDate(new DateOnly(2022, 6, 10)), registration.StartDate);
        Assert.Equal(DemoCalendar.MdDate(new DateOnly(2026, 6, 10)), registration.AcknowledgedDate);
        Assert.Equal(DemoCalendar.MdDate(new DateOnly(2032, 6, 10)), registration.LastDate);

        // Each acknowledged occurrence has its row in the history.
        Assert.Contains(Book.Marked(BillsPattern.InternetMark), t => t.Date >= new DateOnly(2026, 11, 18));
        Assert.Contains(Book.Marked(BillsPattern.RegistrationMark), t => t.Date.Year == 2026);
        Assert.Equal(5, Book.Marked(BillsPattern.RegistrationMark).Count);

        // No reminder for the mortgage or for Electric.
        var mortgageInterest = Book.Base.Category(MdCategoryType.Expense, "Bills", "Mortgage Interest");
        var electric = Book.Base.Category(MdCategoryType.Expense, "Bills", "Electric");
        Assert.DoesNotContain(reminders, r => r.Txn!.Splits.Any(s => s.AcctId == mortgageInterest || s.AcctId == electric));
    }

    [Fact]
    public void Every_manifest_row_is_exported_inside_the_last_two_full_months()
    {
        var manifest = Build.Value.Manifest;
        var from = Book.Calendar.MonthStart(DemoCalendar.CurrentLifeMonth - 2);
        var ids = new List<string>();
        ids.AddRange(manifest.DuplicateTxnIds);
        ids.AddRange(manifest.EditCandidates.Select(e => e.TxnId));
        ids.AddRange(manifest.CheckingReconcile!.ReconcilingTxnIds);
        ids.AddRange(manifest.CardStatement!.ExportedRows.Select(r => r.TxnId));
        Assert.Equal(2, manifest.DuplicateTxnIds.Count);
        Assert.Equal(3, manifest.EditCandidates.Count);

        using var short3 = DemoExportGenerator.Generate(
            new DemoSpec(DemoSpec.DefaultSeed, AsOf, DemoSpan.LastMonths(3))).ToExport();
        var written = short3.AllItems.Where(i => i.ObjType == "txn").Select(i => i.Id).ToHashSet();
        foreach (var id in ids)
        {
            var txn = Book.Transactions.Single(t => t.Id == id);
            Assert.True(txn.Date >= from, $"{id} is dated {txn.Date}");
            Assert.Contains(id, written);
        }

        // The duplicates are re-sent copies of a feed row: same date, payee and amount.
        foreach (var id in manifest.DuplicateTxnIds)
        {
            var copy = Book.Transactions.Single(t => t.Id == id);
            Assert.Contains(Book.Transactions, t => t.Id != id && t.Date == copy.Date && t.Description == copy.Description
                && t.Splits[0].SplitAmount == copy.Splits[0].SplitAmount);
            Assert.True(DemoProvenance.IsFeedRow(Book, copy));
        }

        // The edit candidates' payee is the bank's own text.
        foreach (var edit in manifest.EditCandidates)
        {
            var txn = Book.Transactions.Single(t => t.Id == edit.TxnId);
            Assert.Equal(txn.Description, txn.OlOrigPayee);
            Assert.NotEqual(txn.Description, edit.NewPayee);
        }
    }

    [Fact]
    public void Budget_targets_cover_the_layout_for_twelve_months_with_a_spread()
    {
        var manifest = Build.Value.Manifest;
        Assert.Equal(DemoManifestBuilder.BudgetRoles.Count * 12, manifest.BudgetTargets.Count);
        Assert.All(manifest.BudgetTargets, t => Assert.True(t.Amount > 0 && t.Amount % 1_000 == 0));
        Assert.Equal(new DateOnly(2026, 11, 1), manifest.BudgetTargets.Max(t => new DateOnly(t.Year, t.Month, 1)));
        Assert.Equal(new DateOnly(2025, 12, 1), manifest.BudgetTargets.Min(t => new DateOnly(t.Year, t.Month, 1)));

        var over = 0;
        var under = 0;
        foreach (var target in manifest.BudgetTargets.Where(t => t.Year != 2026 || t.Month != 11))
        {
            var month = Book.Calendar.LifeMonthOf(new DateOnly(target.Year, target.Month, 1));
            var actual = DemoManifestBuilder.ActualIn(Book, target.Category, month);
            if (actual > target.Amount) over++;
            else under++;
        }
        Assert.True(over >= 10 && under >= 10, $"{over} over, {under} under");

        foreach (var (_, category) in DemoManifestBuilder.CategoryRoles)
            Book.Base.Category(category.Type, [.. category.Segments]);
    }

    [Fact]
    public void Every_written_balance_reaches_the_books_end_balance_in_full_and_short_spans()
    {
        foreach (var span in new[] { DemoSpan.Full, DemoSpan.LastMonths(3), DemoSpan.LastMonths(12) })
        {
            using var export = DemoExportGenerator.Generate(new DemoSpec(DemoSpec.DefaultSeed, AsOf, span)).ToExport();
            var balances = export.AllItems.Where(i => i.ObjType == "acct" && Book.AccountById(i.Id) is { IsHolding: false })
                .ToDictionary(i => i.Id, i => long.Parse(i.GetString("sbal")!, CultureInfo.InvariantCulture));
            foreach (var txn in export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From))
            {
                if (balances.ContainsKey(txn.AcctId)) balances[txn.AcctId] += txn.Splits.Sum(s => s.ParentAmount);
                foreach (var split in txn.Splits)
                {
                    if (split.InvestSplitType != "sec" && balances.ContainsKey(split.AcctId)) balances[split.AcctId] += split.SplitAmount;
                }
            }

            foreach (var (id, balance) in balances)
            {
                var account = Book.AccountById(id)!;
                var expected = Book.BalanceAtEndOf(id, AsOf) - NotExported(account);
                Assert.True(expected == balance, $"{span}: {account.Role} writes {balance}, the book ends at {expected}");
            }
        }
    }

    private static long NotExported(DemoAccount account)
    {
        var total = 0L;
        foreach (var txn in Book.Transactions.Where(t => !t.Exported))
        {
            foreach (var (id, amount) in Book.CashEffects(txn))
            {
                if (id == account.Id) total += amount;
            }
        }
        return total;
    }

    private static long PaymentsInto(string card, DemoStatementPeriod period) =>
        Book.Marked(DemoCards.PaymentMark).Where(p => p.Splits[0].AccountId == card && period.Contains(p.Date))
            .Sum(p => p.Splits[0].SplitAmount);

    private static CardStatementCsvRow ParseCsv(string line)
    {
        var first = line.IndexOf(',', StringComparison.Ordinal);
        var last = line.LastIndexOf(',');
        var description = line[(first + 1)..last];
        if (description.StartsWith('"')) description = description[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return new CardStatementCsvRow(
            DateOnly.ParseExact(line[..first], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            description,
            decimal.ToInt64(decimal.Parse(line[(last + 1)..], CultureInfo.InvariantCulture) * 100m));
    }
}
