using System.Globalization;
using Coffer.Api.Provisioning.Demo;
using Coffer.Domain.Reminders;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The written export's shape on any AsOf: the window, the density
/// band, balanced splits, unique feed ids, account windows, the categories and
/// actions it may use, the reminders, the manifest's rows and the card statement.
/// </summary>
/// <remarks>
/// <para>Read through the importer's own typed readers (<see cref="MdTxn"/>,
/// <see cref="MdReminder"/>), so a field the generator spells differently from the
/// importer reads as missing here. The self-checks, the dated life events in order,
/// the cash floors, the escrow and the oversell rule over the same sweep are in
/// <see cref="DemoHouseholdTests"/>; the investment route's in
/// <see cref="DemoSecuritiesTests"/>.</para>
/// <para>Each AsOf is generated once, in its own theory case, and nothing is cached
/// between cases: the sweep holds one export in memory at a time.</para>
/// </remarks>
public sealed class DemoGeneratorShapeTests
{
    private static readonly HashSet<string> Adr0027TxnTypes = new(StringComparer.Ordinal)
    {
        "buy", "buyx", "sell", "sellx", "div", "divr", "divx", "inc", "exp", "bank",
    };

    /// <summary>24 consecutive month-ends, a leap day, a Dec 31, a Jan 1, a holiday
    /// Monday and a DST day.</summary>
    public static TheoryData<string> SweepDates() => DemoHouseholdTests.SweepDates();

    [Theory]
    [MemberData(nameof(SweepDates))]
    public void The_export_keeps_its_shape_on_any_AsOf(string asOf)
    {
        var date = DateOnly.Parse(asOf, CultureInfo.InvariantCulture);
        var calendar = new DemoCalendar(date);
        var build = DemoExportGenerator.Generate(DemoSpec.Full(date));
        using var export = build.ToExport();
        var demoBase = DemoBase.Instance;

        var accounts = export.AllItems.Where(i => i.ObjType == "acct").ToDictionary(i => i.Id, StringComparer.Ordinal);
        var txns = export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From).ToList();
        var reminders = export.AllItems.Where(i => i.ObjType == "reminder").Select(MdReminder.From).ToList();
        string Role(string role) => build.Manifest.AccountIds[role];

        // --- five years ending on AsOf ---------------------------------------------------
        Assert.Equal(DemoCalendar.MdDate(date), export.Metadata.ExportDate);
        Assert.Equal(new DateOnly(date.Year, date.Month, 1).AddMonths(-DemoCalendar.WindowMonths), calendar.WindowStart);
        var dates = txns.Select(t => ToDate(t.Date)).ToList();
        Assert.Equal(calendar.WindowStart.Month, dates.Min().Month);
        Assert.Equal(calendar.WindowStart.Year, dates.Min().Year);
        Assert.True(dates.Min() >= calendar.WindowStart, $"a row is dated {dates.Min()} before the window");
        Assert.True(dates.Max() <= date, $"a row is dated {dates.Max()} after AsOf");
        Assert.Equal(DemoCalendar.CurrentLifeMonth - 1, calendar.LifeMonthOf(dates.Where(d => d < calendar.MonthStart(DemoCalendar.CurrentLifeMonth)).Max()));
        Assert.All(export.AllItems.Where(i => i.ObjType == "csnap"),
            p => Assert.True(calendar.Contains(ToDate(int.Parse(p.GetString("dt")!, CultureInfo.InvariantCulture)))));

        // --- the density band: every full calendar year in the window, and the trailing
        // twelve months, carry a row count inside the band asserted here ----------------
        for (var year = calendar.WindowStart.Year; year <= date.Year; year++)
        {
            if (new DateOnly(year, 1, 1) < calendar.WindowStart || new DateOnly(year, 12, 31) > date) continue;
            var count = dates.Count(d => d.Year == year);
            Assert.True(count is >= 1_500 and <= 2_500, $"{year} has {count} rows");
        }
        var trailing = dates.Count(d => d > date.AddMonths(-12));
        Assert.True(trailing is >= 1_500 and <= 2_500, $"the trailing twelve months have {trailing} rows");

        // --- every split balances, under the cap, on an account the export has ------------
        foreach (var txn in txns)
        {
            Assert.True(txn.Splits.Count is >= 1 and <= 32, $"{txn.Id} has {txn.Splits.Count} splits");
            Assert.True(accounts.ContainsKey(txn.AcctId), $"{txn.Id} is on an account the export lacks");
            foreach (var split in txn.Splits)
            {
                Assert.True(accounts.ContainsKey(split.AcctId), $"{txn.Id} splits to an account the export lacks");
                if (split.InvestSplitType == "sec") continue;
                Assert.True(split.ParentAmount == -split.SplitAmount,
                    $"{txn.Id} split {split.Index} moves {split.ParentAmount} against {split.SplitAmount}");
            }
        }

        // --- no feed id pair twice -------------------------------------------------------
        var pairs = txns.Where(t => t.OlFiId is not null || t.OlFitid is not null).Select(t => (t.OlFiId, t.OlFitid)).ToList();
        Assert.NotEmpty(pairs);
        Assert.All(pairs, p => Assert.True(p.OlFiId is not null && p.OlFitid is not null, "a half feed id pair"));
        Assert.Equal(pairs.Count, pairs.Distinct().Count());

        // --- only ADR-0027 actions; both capital-gain leaves; never Loan > Payment ---------
        Assert.All(txns.Where(t => t.InvestTxnType is not null),
            t => Assert.Contains(t.InvestTxnType!, Adr0027TxnTypes));
        var postedTo = txns.SelectMany(t => t.Splits).Select(s => s.AcctId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(demoBase.Category(MdCategoryType.Income, "Investment", "Long-Term Cap. Gains"), postedTo);
        Assert.Contains(demoBase.Category(MdCategoryType.Income, "Investment", "Short-Term Cap. Gains"), postedTo);
        Assert.DoesNotContain(demoBase.Category(MdCategoryType.Expense, "Loan", "Payment"), postedTo);

        // Every category a row posts to is a base row.
        foreach (var id in postedTo.Where(id => accounts[id].GetString("type") is "e" or "i"))
            Assert.True(demoBase.IsCategory(id), $"{id} is a category the base does not have");

        // --- account windows -------------------------------------------------------------
        (DateOnly First, DateOnly Last)? Span(string role)
        {
            var id = Role(role);
            var on = txns.Where(t => t.AcctId == id || t.Splits.Any(s => s.AcctId == id)).Select(t => ToDate(t.Date)).ToList();
            return on.Count == 0 ? null : (on.Min(), on.Max());
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
        foreach (var closed in new[]
                 {
                     DemoAccountRoles.ClassicCard, DemoAccountRoles.CarLoan, DemoAccountRoles.PaymentPlan, DemoAccountRoles.OldPlanA,
                 })
        {
            Assert.Equal("1", accounts[Role(closed)].GetString("is_inactive"));
        }

        // --- reminders: none for the mortgage or Electric; acknowledged through the
        // occurrence of the last bill in the history, so the next one has no bill and
        // falls after AsOf unless its bill (moved to the next business day) is in flight.
        // The monthly one's bill posts on its occurrence, so it is never in flight.
        Assert.Equal(2, reminders.Count);
        var mortgageInterest = demoBase.Category(MdCategoryType.Expense, "Bills", "Mortgage Interest");
        var electric = demoBase.Category(MdCategoryType.Expense, "Bills", "Electric");
        foreach (var reminder in reminders)
        {
            Assert.False(reminder.IsLoanReminder);
            Assert.NotEqual(Role(DemoAccountRoles.Mortgage), reminder.Txn!.AcctId);
            Assert.DoesNotContain(reminder.Txn.Splits, s => s.AcctId == mortgageInterest || s.AcctId == electric
                || s.AcctId == Role(DemoAccountRoles.Mortgage));

            var rrule = MdReminderRrule.Build(reminder);
            var start = ToDate(reminder.StartDate);
            var acknowledged = ToDate(reminder.AcknowledgedDate!.Value);
            DateOnly? end = reminder.LastDate is { } last ? ToDate(last) : null;
            Assert.True(acknowledged <= date, $"{reminder.Description} is acknowledged past AsOf");
            Assert.Equal(acknowledged,
                NextDueCalculator.NextDue(new RecurrenceExpander(), rrule, start, end, acknowledged.AddDays(-1)));
            var next = NextDueCalculator.NextDue(new RecurrenceExpander(), rrule, start, end, acknowledged);
            Assert.NotNull(next);
            var monthly = reminder.MonthlyDay is not null;
            DateOnly BillDate(DateOnly occurrence) => monthly ? occurrence : DemoCalendar.OnOrNextBusinessDay(occurrence);
            Assert.True(next > date || BillDate(next!.Value) > date,
                $"{reminder.Description}'s next occurrence {next} is due by AsOf with its bill already posted");
            if (monthly) Assert.True(next > date, $"{reminder.Description}'s occurrence {next} is in flight on {date}");

            // Every acknowledged occurrence has its bill; the next one has none.
            var bills = txns.Where(t => t.AcctId == reminder.Txn.AcctId && t.Description == reminder.Description)
                .Select(t => ToDate(t.Date)).ToList();
            Assert.Contains(bills, d => d == BillDate(acknowledged));
            Assert.DoesNotContain(bills, d => d >= next!.Value);
        }

        // --- every manifest-referenced row is exported inside the last two months ----------
        var manifest = build.Manifest;
        var from = calendar.MonthStart(DemoCalendar.CurrentLifeMonth - 2);
        var byId = txns.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var referenced = manifest.DuplicateTxnIds
            .Concat(manifest.EditCandidates.Select(e => e.TxnId))
            .Concat(manifest.CheckingReconcile!.ReconcilingTxnIds)
            .Concat(manifest.CardStatement!.ExportedRows.Select(r => r.TxnId))
            .ToList();
        Assert.True(referenced.Count >= 2 + 3 + 2 + 1, $"the manifest names only {referenced.Count} rows");
        foreach (var id in referenced)
        {
            Assert.True(byId.TryGetValue(id, out var txn), $"{id} is named by the manifest but not exported");
            Assert.True(ToDate(txn!.Date) >= from, $"{id} is dated {txn.Date}, before {from}");
        }

        // --- the CSV is the statement the manifest names ----------------------------------
        var close = manifest.CardStatement.CloseDate;
        Assert.True(close <= date);
        var csv = build.CardStatementCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CardStatementCsv.Header, csv[0]);
        var rows = csv.Skip(1).Select(ParseCsv).ToList();
        Assert.True(rows.Count > manifest.CardStatement.ExportedRows.Count, "the CSV brings no new row");
        Assert.All(rows, r => Assert.True(r.Date > close.AddMonths(-1) && r.Date <= close, $"{r.Date} is outside the statement"));
        foreach (var exported in manifest.CardStatement.ExportedRows)
        {
            Assert.Single(rows, r => r.Date == exported.Date && r.Amount == exported.Amount);
            var txn = byId[exported.TxnId];
            Assert.Equal(Role(DemoAccountRoles.RewardsCard), txn.AcctId);
            Assert.Equal(exported.Date, ToDate(txn.Date));
            Assert.Null(txn.OlFiId);
        }
    }

    /// <summary>Every category the generator names, in the rows it writes and in the
    /// manifest, is a row of the committed <c>base.json</c> FILE (read from disk, not
    /// the embedded copy), so a base edit that drops a used category fails here.</summary>
    [Fact]
    public void Every_category_the_generator_names_exists_in_base_json()
    {
        var file = DemoBase.Parse(File.ReadAllBytes(LocateBase()));
        var build = DemoTestExports.Full.Value;
        using var export = build.ToExport();
        var accounts = export.AllItems.Where(i => i.ObjType == "acct").ToDictionary(i => i.Id, StringComparer.Ordinal);

        var used = export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From)
            .SelectMany(t => t.Splits.Select(s => s.AcctId).Append(t.AcctId))
            .Concat(export.AllItems.Where(i => i.ObjType == "reminder").Select(MdReminder.From)
                .SelectMany(r => r.Txn!.Splits.Select(s => s.AcctId)))
            .Where(id => accounts[id].GetString("type") is "e" or "i")
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(used.Count >= 40, $"only {used.Count} categories used");
        foreach (var id in used)
            Assert.True(file.IsCategory(id), $"{accounts[id].GetString("name")} ({id}) is not a category in base.json");

        var named = build.Manifest.Categories.Values
            .Concat(build.Manifest.BudgetTargets.Select(t => t.Category))
            .ToList();
        Assert.NotEmpty(named);
        foreach (var category in named) file.Category(category.Type, [.. category.Segments]);
    }

    private static DateOnly ToDate(int mdDate) => new(mdDate / 10000, mdDate / 100 % 100, mdDate % 100);

    private static string LocateBase()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "samples", "moneydance-export-demo.base.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate data/samples/moneydance-export-demo.base.json from " + AppContext.BaseDirectory);
    }

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
