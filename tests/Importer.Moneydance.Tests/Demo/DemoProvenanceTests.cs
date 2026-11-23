using System.Text.RegularExpressions;
using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// Provenance (<see cref="DemoProvenance"/>) and the status rule
/// (<see cref="DemoStatusRule"/>) over the generated export:
/// FITIDs unique file-wide and free of date prefixes, each account's era classified
/// by the importer's own DecomposeOrigin, feed strings only on feed rows, entry
/// timestamps derived from the date, and every leg's status from its account's rule.
/// </summary>
/// <remarks>
/// A probe pattern posts one known row on each feed account (and a hand-entered
/// cheque) beside the real patterns; the passes treat its rows like any other. It
/// posts before the household's own patterns, so the card payments, the cash
/// withdrawals and the sweeps see its rows and the self-checks still hold.
/// </remarks>
public sealed partial class DemoProvenanceTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 18);

    private static readonly Lazy<DemoBuild> Build = new(() =>
        DemoExportGenerator.Generate(DemoSpec.Full(AsOf), WithProbe));

    private static readonly Lazy<DemoSimulation> Sim = new(() =>
        DemoExportGenerator.Run(DemoSpec.Full(AsOf), WithProbe));

    private static DemoBook Book => Sim.Value.Book;

    private static IReadOnlyList<IDemoPattern> WithProbe(DemoMarket market)
    {
        var patterns = DemoExportGenerator.Patterns(market).ToList();
        patterns.Insert(patterns.FindIndex(p => p is InvestmentsPattern) + 1, new ProbePattern());
        return patterns;
    }

    [GeneratedRegex(@"^(?:(?:CHK|SAV|CLS|RWD)-[0-9]{7}|mdtxtimport:[0-9]+)$")]
    private static partial Regex FitidForm();

    [GeneratedRegex(@"^(?:[0-9]{8}:|[0-9]{4}-[0-9]{2}-[0-9]{2})")]
    private static partial Regex DatePrefix();

    // --- FITIDs ---------------------------------------------------------------------------

    [Fact]
    public void No_feed_id_pair_repeats_in_any_span()
    {
        foreach (var build in new[]
        {
            Build.Value,
            DemoExportGenerator.Generate(new DemoSpec(DemoSpec.DefaultSeed, AsOf, DemoSpan.LastMonths(12)), WithProbe),
            DemoExportGenerator.Generate(new DemoSpec(DemoSpec.DefaultSeed, AsOf, DemoSpan.LastMonths(3)), WithProbe),
        })
        {
            var pairs = Txns(build).Where(t => t.OlFiId is not null && t.OlFitid is not null)
                .Select(t => (t.OlFiId!, t.OlFitid!)).ToList();
            Assert.NotEmpty(pairs);
            Assert.Equal(pairs.Count, pairs.Distinct().Count());
        }
    }

    [Fact]
    public void FITIDs_carry_an_account_tag_and_never_a_date_prefix()
    {
        var fitids = Txns(Build.Value).Select(t => t.OlFitid).OfType<string>().ToList();
        Assert.True(fitids.Count > 100);
        Assert.All(fitids, f => Assert.Matches(FitidForm(), f));
        Assert.DoesNotContain(fitids, f => DatePrefix().IsMatch(f));
        // Mutation guard on the regex itself: a legacy Moneydance+ id is caught.
        Assert.Matches(DatePrefix(), "20240105:CHK-0000001");
    }

    // --- eras ------------------------------------------------------------------------------

    [Fact]
    public void Each_account_era_classifies_by_its_feed_or_manual_origin()
    {
        var calendar = Book.Calendar;
        var (accounts, txns) = Parse(Build.Value);
        var ids = Build.Value.Manifest.AccountIds;
        var seen = new HashSet<(string Role, string Origin, string? Provider)>();
        var handEntered = Book.Transactions.Where(t => t.Entry == DemoEntry.Manual).Select(t => t.Id).ToHashSet();

        foreach (var txn in txns)
        {
            var account = accounts[txn.AcctId];
            var origin = TransactionMapper.DecomposeOrigin(txn,
                new AccountRef(Guid.Empty, "bank", OlbFi: account.OlbFi, OfxImportAcctNum: account.OfxImportAcctNum));
            var role = ids.Single(kv => kv.Value == txn.AcctId).Key;
            var date = DateOnly.ParseExact(txn.Date.ToString(System.Globalization.CultureInfo.InvariantCulture), "yyyyMMdd");
            seen.Add((role, origin.Origin, origin.ProviderKey));

            if (!string.IsNullOrEmpty(txn.InvestTxnType) || txn.CheckNumber is not null || handEntered.Contains(txn.Id))
            {
                Assert.Equal(("manual", (string?)null), origin);
                continue;
            }
            var expected = role switch
            {
                DemoAccountRoles.JointChecking when calendar.LifeMonthOf(date) <= DemoProvenance.CsvEraLastMonth => ("file_import", "csv"),
                DemoAccountRoles.JointChecking or DemoAccountRoles.Savings => ("online_import", "ofx"),
                DemoAccountRoles.ClassicCard => ("online_import", "mdplus"),
                DemoAccountRoles.RewardsCard => ("file_import", "ofx"),
                _ => ("manual", (string?)null),
            };
            Assert.Equal(expected, origin);
        }

        Assert.Contains((DemoAccountRoles.JointChecking, "file_import", (string?)"csv"), seen);
        Assert.Contains((DemoAccountRoles.JointChecking, "online_import", (string?)"ofx"), seen);
        Assert.Contains((DemoAccountRoles.JointChecking, "manual", (string?)null), seen);
        Assert.Contains((DemoAccountRoles.Savings, "online_import", (string?)"ofx"), seen);
        Assert.Contains((DemoAccountRoles.ClassicCard, "online_import", (string?)"mdplus"), seen);
        Assert.Contains((DemoAccountRoles.RewardsCard, "file_import", (string?)"ofx"), seen);
        Assert.Contains((DemoAccountRoles.CashWallet, "manual", (string?)null), seen);
        Assert.Contains((DemoAccountRoles.PlanB, "manual", (string?)null), seen);
    }

    [Fact]
    public void The_online_service_and_the_account_markers_are_written()
    {
        using var export = Build.Value.ToExport();
        var service = Assert.Single(export.AllItems, i => i.ObjType == "olsvc" && i.GetString("fi_org") is not null);
        var accounts = export.AllItems.Where(i => i.ObjType == "acct").Select(MdAcct.From).ToDictionary(a => a.Id);
        var ids = Build.Value.Manifest.AccountIds;

        Assert.Equal(service.Id, accounts[ids[DemoAccountRoles.JointChecking]].OlbFi);
        Assert.Equal(service.Id, accounts[ids[DemoAccountRoles.Savings]].OlbFi);
        Assert.NotNull(accounts[ids[DemoAccountRoles.RewardsCard]].OfxImportAcctNum);
        Assert.Null(accounts[ids[DemoAccountRoles.RewardsCard]].OlbFi);
        foreach (var (role, id) in ids)
        {
            if (role is DemoAccountRoles.JointChecking or DemoAccountRoles.Savings) continue;
            Assert.Null(accounts[id].OlbFi);
            if (role != DemoAccountRoles.RewardsCard) Assert.Null(accounts[id].OfxImportAcctNum);
        }
    }

    [Fact]
    public void Feed_strings_sit_only_on_feed_rows_beside_the_cleaned_payee()
    {
        var txns = Txns(Build.Value);
        Assert.All(txns, t => Assert.Equal(t.OlFiId is not null, t.OlOrigPayee is not null));
        Assert.All(txns.Where(t => t.OlOrigMemo is not null), t => Assert.NotNull(t.OlFiId));

        var paycheck = txns.First(t => t.Description == PayrollTerms.EmployerB && t.OlFiId is not null);
        Assert.Equal(PayrollTerms.BankPayrollPayee(PayrollTerms.EmployerB), paycheck.OlOrigPayee);
        Assert.Equal(PayrollTerms.BankPayrollMemo, paycheck.OlOrigMemo);
        Assert.NotEqual(paycheck.Description, paycheck.OlOrigPayee);

        // A payee with no raw form of its own gets the bank's upper-case one.
        var probe = txns.Single(t => t.Description == ProbePattern.SavingsPayee);
        Assert.Equal(ProbePattern.SavingsPayee.ToUpperInvariant(), probe.OlOrigPayee);
    }

    /// <summary>Beside the edit candidates, a few clean payees arrive from the bank
    /// exactly as the register writes them, so the import keeps no original for
    /// their rows.</summary>
    [Fact]
    public void A_few_clean_payees_arrive_with_no_original_to_keep()
    {
        var edits = Build.Value.Manifest.EditCandidates.Select(e => e.TxnId).ToHashSet(StringComparer.Ordinal);
        var clean = Txns(Build.Value)
            .Where(t => t.OlFiId is not null && !edits.Contains(t.Id)
                && t.OlOrigPayee == t.Description && (t.OlOrigMemo is null || t.OlOrigMemo == t.Memo))
            .ToList();

        var payees = clean.Select(t => t.Description).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(BillsPattern.WaterPayee, payees);
        Assert.Contains(BillsPattern.PhonePayee, payees);
        Assert.True(payees.Count <= 3, "only a few payees are clean: " + string.Join(", ", payees));
    }

    [Fact]
    public void Feed_rows_are_entered_a_day_or_two_late_and_hand_rows_on_the_day()
    {
        const long day = 86_400_000L;
        var latest = new DemoCalendar(AsOf).LatestEpochMillis;
        using var export = Build.Value.ToExport();
        foreach (var item in export.AllItems.Where(i => i.ObjType == "txn"))
        {
            var txn = MdTxn.From(item);
            var date = DateOnly.ParseExact(txn.Date.ToString(System.Globalization.CultureInfo.InvariantCulture), "yyyyMMdd");
            var noon = DemoCalendar.NoonEpochMillis(date);
            var entered = txn.DateEnteredMillis!.Value;
            var ts = item.GetLong("ts")!.Value;

            Assert.True(entered <= latest && ts <= latest, $"txn {txn.Id} is stamped after AsOf");
            Assert.True(ts >= entered, $"txn {txn.Id} changed before it was entered");
            if (entered == latest) continue; // clamped at the end of AsOf
            var lag = entered - noon;
            if (txn.OlFiId is not null) Assert.InRange(lag, day, 3 * day - 1);
            else Assert.InRange(lag, 0L, day - 1);
        }
    }

    // --- status ------------------------------------------------------------------------------

    [Fact]
    public void Every_leg_takes_its_account_status_rule()
    {
        var calendar = Book.Calendar;
        var checking = Book.Account(DemoAccountRoles.JointChecking);
        var lastClose = DemoStatusRule.LastClosedStatement(Book, checking)!.Value;
        Assert.Equal(new DateOnly(2026, 10, 31), lastClose.ClosesOn);

        foreach (var txn in Book.Transactions)
        {
            var own = Book.AccountById(txn.AccountId)!;
            var age = AsOf.DayNumber - txn.Date.DayNumber;
            var feed = txn.OlFiId is not null;
            if (own.Statement is not null)
            {
                var close = DemoStatusRule.LastClosedStatement(Book, own)!.Value;
                var expected = txn.Date <= close.OpensAfter ? "R"
                    : close.Contains(txn.Date) ? (own.Id == checking.Id ? " " : "c")
                    : feed && age > 2 ? "c" : " ";
                Assert.Equal(expected, txn.Status);
            }
            else if (own.MdType == "v")
            {
                Assert.Equal(age > 90 ? "R" : age < 7 ? " " : "c", txn.Status);
            }
            else
            {
                Assert.Equal(age > 30 ? "c" : " ", txn.Status);
            }

            foreach (var split in txn.Splits)
            {
                var other = Book.AccountById(split.AccountId);
                if (other is null || other.IsHolding) Assert.Null(split.Status);
                else Assert.Equal(DemoStatusRule.StatusOf(Book, other, txn.Date, txn.Exported && txn.Entry != DemoEntry.Manual
                    && DemoProvenance.FeedOf(Book, other, txn.Date) != DemoFeed.None), split.Status);
            }
        }

        // Both cleared and uncleared legs are written, and the deferral leg into a
        // plan follows the plan's rule, not checking's.
        var statuses = Txns(Build.Value).Select(t => t.Status).ToHashSet();
        Assert.Contains("R", statuses);
        Assert.Contains("c", statuses);
        Assert.Contains(" ", statuses);
        var recent = Book.Transactions.Last(t => t.Pattern == PayrollPattern.PatternName && t.Route == DemoRoute.Bank
            && lastClose.Contains(t.Date) && t.Splits.Any(s => s.Description == PayrollPattern.DeferralLabel));
        Assert.Equal(" ", recent.Status);
        Assert.Equal("c", recent.Splits.Single(s => s.Description == PayrollPattern.DeferralLabel).Status);
    }

    [Fact]
    public void The_manifest_names_every_other_row_of_checking_S_minus_1()
    {
        var target = Build.Value.Manifest.CheckingReconcile;
        Assert.NotNull(target);
        Assert.Equal(new DateOnly(2026, 10, 1), target.PeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 31), target.PeriodEnd);

        // The copies the bank re-sent are not on the statement, and the seeder hides them,
        // so the count skips them; both lie inside the period, so the rule has work to do.
        var duplicates = Build.Value.Manifest.DuplicateTxnIds;
        Assert.Equal(2, duplicates.Count);
        var checking = Book.Account(DemoAccountRoles.JointChecking).Id;
        var inPeriod = Book.TransactionsInExportOrder()
            .Where(t => t.Exported && t.Date >= target.PeriodStart && t.Date <= target.PeriodEnd
                && (t.AccountId == checking || t.Splits.Any(s => s.AccountId == checking)))
            .ToList();
        Assert.All(duplicates, d => Assert.Contains(inPeriod, t => t.Id == d));
        var rows = inPeriod.Where(t => !duplicates.Contains(t.Id)).ToList();
        Assert.True(rows.Count > 4);
        Assert.Equal((rows.Count + 1) / 2, target.ReconcilingTxnIds.Count);
        Assert.Equal(rows.Where((_, i) => i % 2 == 0).Select(t => t.Id), target.ReconcilingTxnIds);
        Assert.DoesNotContain(target.ReconcilingTxnIds, duplicates.Contains);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static List<MdTxn> Txns(DemoBuild build)
    {
        using var export = build.ToExport();
        return export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From).ToList();
    }

    private static (Dictionary<string, MdAcct> Accounts, List<MdTxn> Txns) Parse(DemoBuild build)
    {
        using var export = build.ToExport();
        return (
            export.AllItems.Where(i => i.ObjType == "acct").Select(MdAcct.From).ToDictionary(a => a.Id),
            export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From).ToList());
    }

    // One row on each account no household pattern touches yet, plus a cheque.
    private sealed class ProbePattern : IDemoPattern
    {
        public const string SavingsPayee = "Probe interest";

        public string Name => "probe";

        public int Rank => 90;

        public void Emit(DemoCalendar calendar, DemoRngFactory rng, DemoBook book)
        {
            var dining = book.Base.Category(MdCategoryType.Expense, "Personal", "Dining");
            var interest = book.Base.Category(MdCategoryType.Income, "Investment", "Interest Received");
            var rent = book.Base.Category(MdCategoryType.Expense, "Bills", "Rent");
            void Row(string role, int month, string payee, DemoSplit split, DemoEntry entry = DemoEntry.Auto, string? check = null) =>
                book.Post(this, new DemoTxn
                {
                    AccountId = book.Account(role).Id,
                    Date = calendar.DayInMonth(month, 10),
                    Description = payee,
                    Entry = entry,
                    CheckNumber = check,
                    Splits = [split],
                });

            Row(DemoAccountRoles.Savings, 30, SavingsPayee, DemoSplit.Bank(interest, -5_00L));
            Row(DemoAccountRoles.ClassicCard, 10, "Probe diner", DemoSplit.Bank(dining, 20_00L));
            Row(DemoAccountRoles.RewardsCard, 40, "Probe diner", DemoSplit.Bank(dining, 20_00L));
            Row(DemoAccountRoles.CashWallet, 40, "Probe cafe", DemoSplit.Bank(dining, 10_00L));
            Row(DemoAccountRoles.JointChecking, 20, "Probe landlord", DemoSplit.Bank(rent, 100_00L), DemoEntry.Manual, "1001");
        }
    }
}
