using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Import;
using Coffer.Api.Ingest;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Scheduling;
using Coffer.Api.Snapshots;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// What each examples step adds to a Demo, read back through the app's own readers where it
/// has one: the account folders, the investment settings, the budget targets, the register's
/// renamed, hidden and reconciling rows, the card statement's layout, import and merges, the
/// reminders, the saved reports and their pins, the Home layout, the snapshot; and the
/// statement's import undone, then brought back by a restore of that snapshot. And what a step
/// does with a manifest the app's endpoints would refuse, or with a layout or snapshots the
/// owner saved first: it fails, and writes nothing over them.
/// </summary>
/// <remarks>
/// Each test adds the three-month span through the hub's endpoint, so its examples phase
/// runs as it does for a person. The expected values are worked out here from the manifest
/// and the rules the steps state, not from the steps' own helpers.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DemoSeedingTests : DatabaseTest
{
    public DemoSeedingTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DemoManifest Manifest => DemoTestExports.Tiny.Value.Manifest;

    [Fact]
    public async Task The_folders_step_files_each_folder_s_accounts_at_the_top_of_its_type()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "folders");
        var ids = await ResolveAsync(ledgerId);

        var folders = await client.GetFromJsonAsync<List<AccountFolderDto>>($"/api/ledgers/{ledgerId}/account-folders");
        Assert.NotNull(folders);
        Assert.Equal(
            Manifest.Folders.Select(f => (f.Name, f.AccountType)).Order(),
            folders.Select(f => (f.Name, f.AccountType)).Order());
        Assert.All(folders, f => Assert.Null(f.ParentId));
        foreach (var expected in Manifest.Folders)
        {
            var folder = Assert.Single(folders, f => f.Name == expected.Name);
            Assert.Equal(
                expected.AccountIds.Select(id => ids.AccountsByMdId[id]).Order(),
                folder.AccountIds.Order());
        }
        // Both folders the Demo is meant to show, with every member each should hold.
        Assert.Equal(2, folders.Count);
        Assert.Equal(6, folders.Sum(f => f.AccountIds.Count));
    }

    [Fact]
    public async Task The_investments_step_sets_the_commission_flag_the_look_through_and_quarterly_prices()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "investments");
        var ids = await ResolveAsync(ledgerId);
        await using var db = Db.NewDbContext();

        // (a) Only the brokerage treats its fees as trade commission.
        var flagged = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.IsTradeCommission).Select(a => a.Id).ToListAsync();
        Assert.Equal([ids.Accounts[DemoAccountRoles.Brokerage]], flagged);

        // (b) The target-date fund's look-through: one sleeve per component, its weight.
        var targetDate = ids.Securities[DemoSecurities.TargetDate];
        var components = await client.GetFromJsonAsync<List<SecurityComponentDto>>(
            $"/api/ledgers/{ledgerId}/securities/{targetDate}/components");
        Assert.NotNull(components);
        var weights = DemoSecurities.TargetDateWeights;
        Assert.Equal(
            new[]
            {
                ("equity", "us", weights[DemoSecurities.Vti]),
                ("equity", "developed_ex_us", weights[DemoSecurities.Vxus]),
                ("fixed_income", "us", weights[DemoSecurities.Bnd]),
            }.Order(),
            components.Select(c => (c.AssetClass, c.Region!, c.Weight)).Order());
        Assert.Equal(1m, components.Sum(c => c.Weight));

        // (c) The stable-value fund's hand-entered prices: each quarter's end from its first
        // buy through AsOf, at the manifest's month-end value, and no other day.
        var stableValue = ids.Securities[DemoSecurities.StableValue];
        var firstBuy = await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == ledgerId && leg.SecurityId == stableValue && leg.Quantity > 0m
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                select header.PostedAt)
            .MinAsync();
        var firstBuyDay = DateOnly.FromDateTime(firstBuy);
        var expected = Manifest.StableValueMonthEnds
            .Where(m => m.Date.Month is 3 or 6 or 9 or 12 && m.Date == LastDayOf(m.Date))
            .Where(m => m.Date >= firstBuyDay && m.Date <= DemoTestExports.AsOf)
            .Select(m => (m.Date, m.UnitValue))
            .ToList();
        Assert.NotEmpty(expected);
        var manual = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.Source == PriceSource.Manual)
            .OrderBy(p => p.PriceDate)
            .Select(p => new { p.SecurityId, p.PriceDate, p.Price, p.CurrencyCode })
            .ToListAsync();
        Assert.All(manual, p => Assert.Equal(stableValue, p.SecurityId));
        Assert.Equal(expected, manual.Select(p => (p.PriceDate, p.Price)));
        Assert.All(manual, p => Assert.Equal("USD", p.CurrencyCode));

        // No security's automatic pricing was touched: the step changes no security.
        Assert.All(
            await db.Securities.AsNoTracking().Where(s => s.LedgerId == ledgerId).ToListAsync(),
            s => Assert.True(s.AutoPrice, s.Name));
    }

    [Fact]
    public async Task The_budgets_step_sets_the_manifest_s_targets_for_the_year_ending_in_AsOf()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "budgets");
        var ids = await ResolveAsync(ledgerId);
        await using var db = Db.NewDbContext();

        var stored = await db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId)
            .Select(t => new { t.CategoryId, t.TargetMonth, t.Amount, t.CurrencyCode })
            .ToListAsync();
        var expected = Manifest.BudgetTargets
            .Select(t => (ids.Category(t.Category), new DateOnly(t.Year, t.Month, 1), t.Amount / 100m))
            .Order()
            .ToList();
        Assert.Equal(expected, stored.Select(t => (t.CategoryId, t.TargetMonth, t.Amount)).Order());
        Assert.All(stored, t => Assert.Equal("USD", t.CurrencyCode));

        // The twelve months ending in AsOf's month, each with a target.
        var asOfMonth = new DateOnly(DemoTestExports.AsOf.Year, DemoTestExports.AsOf.Month, 1);
        Assert.Equal(
            Enumerable.Range(0, 12).Select(i => asOfMonth.AddMonths(i - 11)),
            stored.Select(t => t.TargetMonth).Distinct().Order());

        // The layout: the budget roles' categories, and nothing else.
        Assert.Equal(
            DemoManifestBuilder.BudgetRoles.Select(r => ids.Categories[r]).Order(),
            stored.Select(t => t.CategoryId).Distinct().Order());
    }

    [Fact]
    public async Task The_reminders_step_adds_an_estimated_bill_the_managed_mortgage_and_one_skip()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "reminders");
        var ids = await ResolveAsync(ledgerId);
        await using var db = Db.NewDbContext();
        var asOf = DemoTestExports.AsOf;
        var checking = ids.Accounts[DemoAccountRoles.JointChecking];

        // (a) The Electric bill: the one reminder the Demo has that neither came from the
        // export nor is a loan's. Monthly on its day, auto-posted, estimating from as many
        // occurrences as the export left out.
        var day = Manifest.ElectricDayOfMonth!.Value;
        var electric = await db.RecurringTransactions.AsNoTracking()
            .SingleAsync(r => r.LedgerId == ledgerId && r.ExternalId == null && !r.IsLoanReminder);
        Assert.Equal($"FREQ=MONTHLY;BYMONTHDAY={day}", electric.Rrule);
        Assert.Equal(checking, electric.SourceAccountId);
        Assert.Equal(3, electric.AutoCommitDaysBefore);
        Assert.Equal(3, electric.EstimateSampleCount);

        // Each left-out bill is posted as its own month's slot, on its own business day, at
        // its own amount, from checking to Bills > Electric.
        var expected = Manifest.ElectricOmissions
            .Select(o => (Slot: new DateOnly(o.Date.Year, o.Date.Month, day), Posted: o.Date, Amount: -o.Amount / 100m))
            .OrderBy(o => o.Slot)
            .ToList();
        Assert.Equal(3, expected.Count);
        Assert.Equal(expected[0].Slot, electric.StartDate);
        var fired = await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.RecurringTransactionId == electric.Id && !header.IsRecurringTemplate
                join leg in db.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == checking
                orderby header.OccurrenceDate
                select new { header.OccurrenceDate, header.PostedAt, leg.Amount, header.Id })
            .ToListAsync();
        Assert.Equal(
            expected.Select(e => ((DateOnly?)e.Slot, e.Posted, e.Amount)),
            fired.Select(f => (f.OccurrenceDate, DateOnly.FromDateTime(f.PostedAt), f.Amount)));
        var categoryLegs = await db.TxnLegs.AsNoTracking()
            .Where(l => fired.Select(f => f.Id).Contains(l.HeaderId) && l.AccountId != checking)
            .Select(l => l.AccountId).Distinct().ToListAsync();
        Assert.Equal([ids.Categories["electric"]], categoryLegs);

        // The estimate shows, from every linked bill: their mean, rounded once to cents.
        var list = await client.GetFromJsonAsync<List<ReminderSummary>>($"/api/ledgers/{ledgerId}/reminders");
        Assert.NotNull(list);
        var estimate = Assert.Single(list, r => r.Id == electric.Id).Estimate;
        Assert.NotNull(estimate);
        Assert.Equal(3, estimate.RequestedSampleCount);
        Assert.Equal(3, estimate.AvailableSampleCount);
        Assert.Null(estimate.UnavailableReason);
        Assert.Equal(
            Math.Round(expected.Sum(e => e.Amount) / 3m, 2, MidpointRounding.AwayFromZero),
            estimate.Amount);

        // Auto-post is on: the ledger's job is configured and enabled, and the agenda says
        // the job posts the bill's coming occurrences.
        var schedule = await db.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost);
        Assert.True(schedule.Enabled);
        var upcoming = await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledgerId}/reminders/upcoming?from={asOf.AddDays(1):yyyy-MM-dd}&to={asOf.AddDays(62):yyyy-MM-dd}");
        Assert.NotNull(upcoming);
        var coming = upcoming.Where(o => o.ReminderId == electric.Id && o.Kind == "reminder").ToList();
        Assert.NotEmpty(coming);
        Assert.All(coming, o => Assert.Null(Assert.IsType<UpcomingAutoPost>(o.AutoPost).ExcludedReason));

        // (b) The mortgage's managed payment from checking, from its first due date after
        // AsOf, over terms that name the escrow account and the interest category.
        var mortgage = ids.Accounts[DemoAccountRoles.Mortgage];
        var terms = await db.LoanTerms.AsNoTracking().SingleAsync(t => t.LedgerId == ledgerId && t.AccountId == mortgage);
        Assert.Equal(ids.Accounts[DemoAccountRoles.MortgageEscrow], terms.EscrowAccountId);
        Assert.Equal(ids.Categories["mortgage-interest"], terms.InterestAccountId);
        var dueDay = terms.FirstPaymentDate!.Value.Day;
        var firstDue = new DateOnly(asOf.Year, asOf.Month, dueDay);
        if (firstDue <= asOf) firstDue = firstDue.AddMonths(1);
        var loan = await db.RecurringTransactions.AsNoTracking()
            .SingleAsync(r => r.LedgerId == ledgerId && r.IsLoanReminder);
        Assert.Equal(mortgage, loan.LoanAccountId);
        Assert.Equal(checking, loan.SourceAccountId);
        Assert.Equal(firstDue, loan.StartDate);
        Assert.Equal(firstDue, loan.NextDueDate);
        Assert.Equal(
            new[] { checking, mortgage, terms.InterestAccountId!.Value, terms.EscrowAccountId!.Value }.Order(),
            (await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == loan.TemplateHeaderId)
                .Select(l => l.AccountId).Distinct().ToListAsync()).Order());

        // (c) One skip: the imported Internet bill's first occurrence after AsOf, by the
        // owner, and nothing else skipped anywhere in the Demo.
        var nextInternet = new DateOnly(asOf.Year, asOf.Month, BillsPattern.InternetDay);
        if (nextInternet <= asOf) nextInternet = nextInternet.AddMonths(1);
        var skip = Assert.Single(await db.RecurringOccurrenceExceptions.AsNoTracking()
            .Where(e => e.LedgerId == ledgerId).ToListAsync());
        Assert.Equal(ids.InternetReminder, skip.RecurringTransactionId);
        Assert.Equal(nextInternet, skip.OccurrenceDate);
        Assert.Equal(job.UserId, skip.CreatedByUserId);
        var internet = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == ids.InternetReminder);
        Assert.Equal(nextInternet.AddMonths(1), internet.NextDueDate);
    }

    [Fact]
    public async Task The_reminders_step_runs_the_handlers_checks_and_writes_nothing_they_refuse()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        var ids = await ResolveAsync(ledgerId);
        var before = await RemindersAsync(ledgerId);

        // A posting to an account outside the Demo: the bank create's account check refuses
        // it before the repository is reached.
        var categories = ids.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        categories["electric"] = Guid.NewGuid();
        // The step checks its skip target before anything else, and this Demo's Internet
        // reminder is already skipped once: the last day before its next due date gives a
        // target that check accepts, so the Electric reminder's own checks are reached.
        DateOnly nextInternet;
        await using (var db = Db.NewDbContext())
        {
            nextInternet = (await db.RecurringTransactions.AsNoTracking()
                .SingleAsync(r => r.Id == ids.InternetReminder)).NextDueDate!.Value;
        }
        var run = new DemoSeedRun(
            new DemoSeedContext(ledgerId, job.UserId, nextInternet.AddDays(-1), Manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, [], factory.Services.GetRequiredService<SyncConnectionLock>())
        {
            Ids = ids with { Categories = categories },
        };
        await using (var db = Db.NewDbContext())
        {
            var failure = await Assert.ThrowsAsync<DemoSeedStepException>(
                () => DemoReminderSteps.RemindersAsync(run, db, CancellationToken.None));
            Assert.Equal(
                "the Electric reminder was refused: account-not-in-ledger: "
                + "A posting's counterparty account does not belong to this ledger.",
                failure.Message);
        }

        Assert.Equal(before, await RemindersAsync(ledgerId));
    }

    [Fact]
    public async Task A_skip_target_the_reminder_is_not_due_on_refuses_the_reminders_step_before_it_writes()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        var ids = await ResolveAsync(ledgerId);
        var before = await RemindersAsync(ledgerId);

        // Run again over the seeded Demo, with the day before the skipped occurrence as the
        // last day: the reminder's next due date is then not its first occurrence after the
        // last day, as when an occurrence is still due, so a skip there would skip another
        // too. The step is refused before the Electric and mortgage reminders are written.
        DateOnly skipped;
        await using (var read = Db.NewDbContext())
        {
            skipped = (await read.RecurringOccurrenceExceptions.AsNoTracking()
                .SingleAsync(e => e.RecurringTransactionId == ids.InternetReminder)).OccurrenceDate;
        }
        var asOf = skipped.AddDays(-1);
        var run = new DemoSeedRun(
            new DemoSeedContext(ledgerId, job.UserId, asOf, Manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, [], factory.Services.GetRequiredService<SyncConnectionLock>())
        {
            Ids = ids,
        };
        await using (var db = Db.NewDbContext())
        {
            var failure = await Assert.ThrowsAsync<DemoSeedStepException>(
                () => DemoReminderSteps.RemindersAsync(run, db, CancellationToken.None));
            Assert.StartsWith("the imported Internet reminder is next due on ", failure.Message);
        }

        Assert.Equal(before, await RemindersAsync(ledgerId));
    }

    [Fact]
    public async Task The_register_step_renames_beside_the_originals_hides_the_re_sent_copies_and_half_reconciles()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "register");
        await DemoSeedingHarness.AssertRegisterAsync(Db, ledgerId, await ResolveAsync(ledgerId), Manifest);
    }

    [Fact]
    public async Task A_row_to_mark_with_no_leg_on_checking_refuses_the_register_step_before_it_writes()
    {
        // A Demo whose own register step failed before its body, so nothing of it is there.
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DemoSeeder>().BeforeStepAsync = (step, _) =>
            step.Key == "register" ? throw new InvalidOperationException("held out") : Task.CompletedTask;
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        var marker = await DemoSeedingHarness.MarkerAsync(Db, ledgerId);
        Assert.Equal(0, DemoSeedingHarness.Details(marker.DetailsJson)["register"]);
        var ids = await ResolveAsync(ledgerId);
        var before = await RegisterAsync(ledgerId, ids);

        // The step, with the last row to mark found as a card row, which has no leg on
        // checking: it is refused before the renames and the hides it would write first.
        var statement = Manifest.CheckingReconcile!;
        var transactions = ids.Transactions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        transactions[statement.ReconcilingTxnIds[^1]] = ids.Transactions[Manifest.CardStatement!.ExportedRows[0].TxnId];
        var run = new DemoSeedRun(
            new DemoSeedContext(ledgerId, job.UserId, DemoTestExports.AsOf, Manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, [], factory.Services.GetRequiredService<SyncConnectionLock>())
        {
            Ids = ids with { Transactions = transactions },
        };
        await using (var db = Db.NewDbContext())
        {
            var failure = await Assert.ThrowsAsync<DemoSeedStepException>(
                () => DemoRegisterSteps.RegisterAsync(run, db, CancellationToken.None));
            Assert.Equal(
                $"1 of the {statement.ReconcilingTxnIds.Count} checking rows the manifest marks reconciling have no leg on checking",
                failure.Message);
        }

        Assert.Equal(before, await RegisterAsync(ledgerId, ids));
    }

    [Fact]
    public async Task The_card_statement_step_saves_its_layout_imports_into_review_and_merges_the_rows_typed_in()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "card-statement");
        var ids = await ResolveAsync(ledgerId);
        var csv = DemoTestExports.Tiny.Value.CardStatementCsv;

        var import = await DemoSeedingHarness.AssertCardStatementAsync(Db, client, ledgerId, job.UserId, ids, Manifest, csv);
        await DemoSeedingHarness.AssertActivityAsync(client, ledgerId, job.UserId);

        // Unlike the examples' own entry, undo-import accepts the statement's import as it
        // accepts any file import's: the dry run finds every row it saved that is still in review. A merged
        // copy stays with the row it was folded into, as after any import.
        var undo = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/ledger-operations/{import.Id}/undo-import?dryRun=true", content: null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var found = await undo.Content.ReadFromJsonAsync<UndoImportResult>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        Assert.Equal(lines - Manifest.CardStatement!.ExportedRows.Count, found!.Found);
        Assert.Equal(0, found.Deleted);
    }

    [Fact]
    public async Task Undoing_the_statement_s_import_keeps_its_merges_and_restoring_the_Demo_s_snapshot_brings_it_back()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "card-statement");
        await AssertAddedAsync(ledgerId, "snapshot");
        var ids = await ResolveAsync(ledgerId);
        var card = ids.Accounts[DemoAccountRoles.RewardsCard];
        var winners = Manifest.CardStatement!.ExportedRows.Select(r => ids.Transactions[r.TxnId]).ToHashSet();
        var lines = DemoTestExports.Tiny.Value.CardStatementCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var inReview = lines - winners.Count;

        Guid importId;
        await using (var db = Db.NewDbContext())
            importId = await db.LedgerOperations.AsNoTracking()
                .Where(o => o.LedgerId == ledgerId && o.ProviderKey == "csv-generic")
                .Select(o => o.Id).SingleAsync();
        var asAdded = await CardRowsAsync(ledgerId, card);
        var merges = await MergesAsync(ledgerId, winners);
        Assert.Equal(winners.Count, merges.Count);
        Assert.Equal(inReview, await CardReviewCountAsync(ledgerId, card));

        // (1) The real undo, as the import dialog sends it: every row the import saved that is
        // still in review goes; each merged copy stays folded into the row typed in.
        var undo = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/ledger-operations/{importId}/undo-import", content: null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undone = await undo.Content.ReadFromJsonAsync<UndoImportResult>();
        Assert.Equal((inReview, inReview), (undone!.Found, undone.Deleted));
        await AssertHealthyAsync(client, ledgerId);
        Assert.Equal(0, await CardReviewCountAsync(ledgerId, card));
        Assert.Equal(merges, await MergesAsync(ledgerId, winners));
        await using (var db = Db.NewDbContext())
        {
            var survivors = await db.TxnHeaders.AsNoTracking().Where(h => winners.Contains(h.Id)).ToListAsync();
            Assert.Equal(winners.Count, survivors.Count);
            Assert.All(survivors, h => Assert.True(
                !h.IsHidden && h.IsMergedInto == null && h.IsMergeWinner && !h.NeedsReview,
                $"the typed-in row {h.Id} did not survive the undo as a settled merge winner"));
        }

        // (2) The Demo's one snapshot, taken after every example, brings the statement back:
        // its rows in review and its merges, exactly as added. A restore keeps the run log, so
        // the Demo still reads as complete.
        var snapshot = Assert.Single((await client.GetFromJsonAsync<List<SnapshotSummaryDto>>($"/api/ledgers/{ledgerId}/snapshots"))!);
        var restored = await client.PostAsync($"/api/ledgers/{ledgerId}/snapshots/{snapshot.Id}/restore", content: null);
        Assert.True(restored.StatusCode == HttpStatusCode.NoContent, await restored.Content.ReadAsStringAsync());
        await AssertHealthyAsync(client, ledgerId);
        Assert.Equal(asAdded, await CardRowsAsync(ledgerId, card));
        Assert.Equal(merges, await MergesAsync(ledgerId, winners));
        Assert.Equal(inReview, await CardReviewCountAsync(ledgerId, card));
        Assert.Equal(DemoMarker.Completed, (await DemoSeedingHarness.MarkerAsync(Db, ledgerId)).Status);
        Assert.Null(await DemoSeedingHarness.MissingAsync(client, ledgerId));
    }

    public static TheoryData<string> RefusedStatements =>
        new() { "typed-row-not-on-the-statement", "typed-row-not-on-the-card", "layout-name-taken" };

    [Theory]
    [MemberData(nameof(RefusedStatements))]
    public async Task A_statement_the_step_cannot_merge_or_a_taken_layout_name_refuses_it_before_it_imports(string probe)
    {
        // A Demo whose own card statement step failed before its body, so nothing of it is there.
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DemoSeeder>().BeforeStepAsync = (step, _) =>
            step.Key == "card-statement" ? throw new InvalidOperationException("held out") : Task.CompletedTask;
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        Assert.Equal(0, DemoSeedingHarness.Details((await DemoSeedingHarness.MarkerAsync(Db, ledgerId)).DetailsJson)["card-statement"]);
        var ids = await ResolveAsync(ledgerId);

        var statement = Manifest.CardStatement!;
        var transactions = ids.Transactions.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var (manifest, refusal) = probe switch
        {
            // The last row typed in, found as a checking row: a settled, visible row the
            // merge would take, but not the card's, so the statement's copy must not fold
            // into it.
            "typed-row-not-on-the-card" => (
                Manifest,
                $"the row with the Moneydance id {statement.ExportedRows[^1].TxnId} is not a settled, visible card row"),
            // The last row typed in, a cent off its statement line: the statement has no line
            // for it, so there would be nothing to merge into it.
            "typed-row-not-on-the-statement" => (
                Manifest with
                {
                    CardStatement = statement with
                    {
                        ExportedRows = [.. statement.ExportedRows.SkipLast(1), statement.ExportedRows[^1] with { Amount = statement.ExportedRows[^1].Amount - 1 }],
                    },
                },
                "the statement has 0 rows of "),
            "layout-name-taken" => (Manifest, $"the layout name '{DemoSeedContent.CardStatementLayoutName}' is taken by another layout"),
            _ => throw new ArgumentOutOfRangeException(nameof(probe)),
        };
        if (probe == "layout-name-taken")
        {
            // The owner saved a layout of their own under the name first.
            var saved = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/csv-mappings", new CsvMappingWriteRequest
            {
                Name = DemoSeedContent.CardStatementLayoutName.ToUpperInvariant(),
                DefinitionYaml = DemoSeedContent.CardStatementLayoutYaml.Replace("header_rows: 1", "header_rows: 2", StringComparison.Ordinal),
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        if (probe == "typed-row-not-on-the-card")
            transactions[statement.ExportedRows[^1].TxnId] = ids.Transactions[Manifest.CheckingReconcile!.ReconcilingTxnIds[0]];
        var before = await CardStatementAsync(ledgerId);

        var run = new DemoSeedRun(
            new DemoSeedContext(ledgerId, job.UserId, DemoTestExports.AsOf, manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, factory.Services.GetServices<IFileProvider>().ToList(), factory.Services.GetRequiredService<SyncConnectionLock>())
        {
            Ids = ids with { Transactions = transactions },
        };
        await using (var db = Db.NewDbContext())
        {
            var failure = await Assert.ThrowsAsync<DemoSeedStepException>(
                () => DemoCardStatementSteps.CardStatementAsync(run, db, CancellationToken.None));
            Assert.Contains(refusal, failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, await CardStatementAsync(ledgerId));
    }

    [Fact]
    public async Task The_card_statement_s_importer_has_the_app_s_file_readers_and_no_feed_or_quote_source()
    {
        // Nothing the step builds can reach out: the importer's only sources are the app's
        // file readers, which read the stream they are given, and its quote orchestrator,
        // which the file path never calls, has no source at all.
        await using var factory = DemoSeedingHarness.Factory(Db);
        var readers = factory.Services.GetServices<IFileProvider>().ToList();
        var run = new DemoSeedRun(
            new DemoSeedContext(Guid.NewGuid(), Guid.NewGuid(), DemoTestExports.AsOf, Manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, readers, factory.Services.GetRequiredService<SyncConnectionLock>());
        await using var db = Db.NewDbContext();
        var importer = DemoCardStatementSteps.Importer(run, db);

        Assert.Empty(Collection(Field(importer, "_pullProviders")));
        var files = Assert.IsAssignableFrom<IReadOnlyDictionary<string, IFileProvider>>(Field(importer, "_fileProviders"));
        Assert.Equal(readers.Select(r => r.ProviderKey).Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(Coffer.Api.Ingest.Csv.CsvGenericFileProvider.Key, files.Keys);
        Assert.Same(factory.Services.GetRequiredService<SyncConnectionLock>(), Field(importer, "_connectionLock"));

        var quotes = Assert.IsType<Coffer.Api.Quotes.QuoteOrchestrator>(Field(importer, "_quotes"));
        Assert.Empty(Collection(Field(quotes, "_pullProviders")));
        Assert.Empty(Collection(Field(quotes, "_pushProviders")));
        Assert.Null(Field(quotes, "_marketSeries"));

        static object? Field(object owner, string name)
        {
            var field = owner.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.True(field is not null, $"{owner.GetType().Name} has no field {name}");
            return field.GetValue(owner);
        }

        static IEnumerable<object> Collection(object? value) =>
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(value).Cast<object>();
    }

    public static TheoryData<string> RefusedManifests =>new() { "negative-target", "target-outside-the-year", "folder-name-too-long" };

    [Theory]
    [MemberData(nameof(RefusedManifests))]
    public async Task A_manifest_value_an_endpoint_would_refuse_fails_its_step_and_writes_nothing(string probe)
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        var ids = await ResolveAsync(ledgerId);
        var before = await BudgetAndFoldersAsync(ledgerId);

        // Every valid target is retyped first and the refused value comes last, so a step
        // that wrote before it checked would leave the retyped amounts behind.
        var retyped = Manifest.BudgetTargets.Select(t => t with { Amount = t.Amount + 1_000 }).ToList();
        var first = Manifest.BudgetTargets[0];
        var (manifest, body, refusal) = probe switch
        {
            "negative-target" => (
                Manifest with { BudgetTargets = [.. retyped, first with { Amount = -1_000 }] },
                (DemoSeedStepBody)DemoLedgerSteps.BudgetsAsync,
                "is negative"),
            "target-outside-the-year" => (
                Manifest with { BudgetTargets = [.. retyped, first with { Year = first.Year - 2 }] },
                (DemoSeedStepBody)DemoLedgerSteps.BudgetsAsync,
                "is outside the 12 months ending in 2026-11"),
            "folder-name-too-long" => (
                Manifest with { Folders = [new DemoFolder(new string('x', 81), "bank", [Manifest.Folders[0].AccountIds[0]])] },
                (DemoSeedStepBody)DemoLedgerSteps.FoldersAsync,
                "is refused: TooLong"),
            _ => throw new ArgumentOutOfRangeException(nameof(probe)),
        };

        var run = new DemoSeedRun(
            new DemoSeedContext(ledgerId, job.UserId, DemoTestExports.AsOf, manifest, DemoTestExports.Tiny.Value.CardStatementCsv),
            TimeProvider.System, [], factory.Services.GetRequiredService<SyncConnectionLock>())
        {
            Ids = ids,
        };
        await using (var db = Db.NewDbContext())
        {
            var failure = await Assert.ThrowsAsync<DemoSeedStepException>(() => body(run, db, CancellationToken.None));
            Assert.Contains(refusal, failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, await BudgetAndFoldersAsync(ledgerId));
    }

    /// <summary>The saved reports' names, in the order the step saves and pins them.</summary>
    private static readonly string[] SavedReportNames =
        ["Spending by category, monthly", "Income versus spending, monthly", "Income by payee, yearly"];

    [Fact]
    public async Task The_reports_step_saves_three_private_reports_that_run_and_pins_them_before_the_catalog_s()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "reports");

        using var saved = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/reports/saved"));
        var reports = saved.RootElement.EnumerateArray().ToList();
        Assert.Equal(SavedReportNames.Order(StringComparer.Ordinal), reports.Select(r => r.GetProperty("name").GetString()!));
        Assert.All(reports, r => Assert.Equal("private", r.GetProperty("visibility").GetString()));
        Assert.All(reports, r => Assert.Equal(job.UserId, r.GetProperty("ownerUserId").GetGuid()));

        // Each stored spec runs as Reports runs it: its summary parameters as the query.
        foreach (var report in reports)
        {
            var spec = report.GetProperty("spec");
            var query = string.Join("&", spec.EnumerateObject()
                .Where(p => p.Name is not ("view" or "chartStack"))
                .Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
            var summary = await client.GetAsync($"/api/ledgers/{ledgerId}/reports/summary?{query}&today={DemoSeedingHarness.Today}");
            Assert.True(summary.IsSuccessStatusCode, report.GetProperty("name").GetString() + ": " + await summary.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
            Assert.NotEmpty(result.RootElement.GetProperty("rows").EnumerateArray());
        }

        // The rail: the three in the order they were saved, then the catalog reports.
        var idsByName = reports.ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
        using var pins = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/reports/pins"));
        var rail = pins.RootElement.EnumerateArray()
            .Select(p => p.GetProperty("savedReportId").ValueKind == JsonValueKind.Null
                ? "catalog:" + p.GetProperty("catalogKey").GetString()
                : "saved:" + p.GetProperty("savedReportId").GetGuid())
            .ToList();
        Assert.Equal(
            SavedReportNames.Select(n => "saved:" + idsByName[n]).Concat(["catalog:income-and-expenses", "catalog:returns"]),
            rail);
    }

    [Fact]
    public async Task The_home_step_saves_the_layout_with_the_Demo_s_ids_and_the_endpoint_takes_it_back()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (_, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "home");
        var ids = await ResolveAsync(ledgerId);
        var dashboardPath = $"/api/ledgers/{ledgerId}/preferences/dashboard";

        var body = await client.GetStringAsync(dashboardPath);
        using var layout = JsonDocument.Parse(body);
        Assert.Equal(2, layout.RootElement.GetProperty("version").GetInt32());
        var widgets = layout.RootElement.GetProperty("widgets").EnumerateArray().ToList();

        var watchlist = Assert.Single(widgets, w => w.GetProperty("key").GetString() == "watchlist");
        Assert.True(watchlist.GetProperty("visible").GetBoolean());
        Assert.Equal(
            new[] { "Groceries", "Dining", "Entertainment", "Clothing" }
                .Select(leaf => ids.Category(new DemoCategoryRef(MdCategoryType.Expense, ["Personal", leaf])))
                .Append(ids.Categories["electric"]),
            watchlist.GetProperty("settings").GetProperty("categoryIds").EnumerateArray().Select(e => e.GetGuid()));

        var forecast = Assert.Single(widgets, w => w.GetProperty("key").GetString() == "cash-forecast");
        Assert.True(forecast.GetProperty("visible").GetBoolean());
        Assert.Equal(
            ids.Accounts[DemoAccountRoles.JointChecking],
            forecast.GetProperty("settings").GetProperty("focusAccountId").GetGuid());

        using var saved = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/reports/saved"));
        var idsByName = saved.RootElement.EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
        var tiles = widgets.Where(w => w.GetProperty("key").GetString() == "report").ToList();
        Assert.Equal(
            SavedReportNames.Select(n => "report:saved:" + idsByName[n]),
            tiles.Select(w => w.GetProperty("id").GetString()));
        Assert.All(tiles, w => Assert.True(w.GetProperty("visible").GetBoolean()));

        // Saved back as read, the endpoint accepts it and stores the same widgets.
        var put = await client.PutAsync(dashboardPath, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        using var reread = JsonDocument.Parse(await client.GetStringAsync(dashboardPath));
        Assert.Equal(
            layout.RootElement.GetProperty("widgets").GetRawText(),
            reread.RootElement.GetProperty("widgets").GetRawText());
    }

    [Fact]
    public async Task A_layout_saved_before_the_home_step_is_kept_and_the_step_says_so()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        const string Own =
            "{\"version\":2,\"columns\":\"single\",\"widgets\":[{\"id\":\"net-worth\",\"key\":\"net-worth\","
            + "\"visible\":true,\"column\":\"main\",\"settings\":{}}],\"updatedAt\":null}";
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The owner opens the Demo and saves a layout of their own before the step runs.
        seeder.BeforeStepAsync = async (step, ct) =>
        {
            if (step.Key != "home") return;
            var ledgerId = await started.Task.ConfigureAwait(false);
            var put = await client.PutAsync(
                $"/api/ledgers/{ledgerId}/preferences/dashboard",
                new StringContent(Own, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            put.EnsureSuccessStatusCode();
        };

        var (jobId, demo) = await DemoSeedingHarness.StartAsync(client);
        started.SetResult(demo);
        Assert.Equal(ImportJobState.Succeeded, (await DemoSeedingHarness.WaitForEndAsync(registry, jobId)).State);

        var marker = await DemoSeedingHarness.MarkerAsync(Db, demo);
        Assert.Equal(DemoMarker.Partial, marker.Status);
        var details = DemoSeedingHarness.Details(marker.DetailsJson);
        Assert.Equal(0, details["home"]);
        Assert.Equal(1, details["reports"]);
        Assert.Equal(1, details["check"]);
        Assert.Contains(
            "the Home layout: failed: a Home layout was saved on this Demo before the examples reached it, and it is kept",
            marker.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("the Home layout", await DemoSeedingHarness.MissingAsync(client, demo) ?? []);

        using var kept = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{demo}/preferences/dashboard"));
        Assert.Equal("single", kept.RootElement.GetProperty("columns").GetString());
        Assert.Equal(["net-worth"], kept.RootElement.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task The_snapshot_step_takes_one_manual_snapshot_holding_every_example_before_it()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var (job, ledgerId) = await DemoSeedingHarness.AddDemoAsync(factory, client);
        await AssertAddedAsync(ledgerId, "snapshot");
        await DemoSeedingHarness.AssertSnapshotAsync(Db, client, ledgerId, job.UserId, await ResolveAsync(ledgerId));
    }

    [Fact]
    public async Task A_Demo_already_at_the_snapshot_limit_fails_the_snapshot_step_and_keeps_its_snapshots()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The owner opens the Demo and takes as many snapshots as a ledger keeps before the
        // step runs.
        seeder.BeforeStepAsync = async (step, ct) =>
        {
            if (step.Key != "snapshot") return;
            var ledgerId = await started.Task.ConfigureAwait(false);
            for (var i = 0; i < LedgerSnapshotsRepository.SnapshotCap; i++)
            {
                var created = await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledgerId}/snapshots", new CreateSnapshotRequest("own " + i), ct).ConfigureAwait(false);
                created.EnsureSuccessStatusCode();
            }
        };

        var (jobId, demo) = await DemoSeedingHarness.StartAsync(client);
        started.SetResult(demo);
        Assert.Equal(ImportJobState.Succeeded, (await DemoSeedingHarness.WaitForEndAsync(registry, jobId)).State);

        var marker = await DemoSeedingHarness.MarkerAsync(Db, demo);
        Assert.Equal(DemoMarker.Partial, marker.Status);
        var details = DemoSeedingHarness.Details(marker.DetailsJson);
        Assert.Equal(0, details["snapshot"]);
        Assert.Equal(1, details["check"]);
        Assert.Equal(
            "the snapshot: failed: the Demo already has as many snapshots as a ledger keeps, and a manual one never replaces another",
            marker.ErrorMessage);
        Assert.Equal(["the snapshot"], await DemoSeedingHarness.MissingAsync(client, demo) ?? []);

        // The owner's own snapshots are all kept, and none is the examples'.
        var listed = await client.GetFromJsonAsync<List<SnapshotSummaryDto>>($"/api/ledgers/{demo}/snapshots");
        Assert.Equal(
            Enumerable.Range(0, LedgerSnapshotsRepository.SnapshotCap).Select(i => "own " + i).Order(StringComparer.Ordinal),
            listed!.Select(s => s.Description!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_saved_reports_failure_skips_the_home_layout_and_both_are_listed()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();
        var seeder = factory.Services.GetRequiredService<DemoSeeder>();
        var registry = factory.Services.GetRequiredService<ImportJobRegistry>();
        seeder.BeforeStepAsync = (step, _) => step.Key == "reports"
            ? throw new InvalidOperationException("the reports step was told to fail")
            : Task.CompletedTask;

        var (jobId, ledgerId) = await DemoSeedingHarness.StartAsync(client);
        Assert.Equal(ImportJobState.Succeeded, (await DemoSeedingHarness.WaitForEndAsync(registry, jobId)).State);

        var marker = await DemoSeedingHarness.MarkerAsync(Db, ledgerId);
        Assert.Equal(DemoMarker.Partial, marker.Status);
        var details = DemoSeedingHarness.Details(marker.DetailsJson);
        Assert.Equal(0, details["reports"]);
        Assert.Equal(0, details["home"]);
        Assert.Equal(1, details["check"]);
        Assert.Contains("saved reports: failed: the reports step was told to fail", marker.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("the Home layout: skipped: needs saved reports", marker.ErrorMessage, StringComparison.Ordinal);

        var expected = DemoSeedSteps.All
            .Where(s => s.Key is "reports" or "home")
            .Select(s => s.MissingName!);
        Assert.Equal(expected, await DemoSeedingHarness.MissingAsync(client, ledgerId));

        await using var db = Db.NewDbContext();
        Assert.False(await db.SavedReports.AnyAsync(r => r.LedgerId == ledgerId));
        Assert.False(await db.ReportPins.AnyAsync(p => p.LedgerId == ledgerId));
        Assert.False(await db.UserPreferences.AnyAsync(p => p.LedgerId == ledgerId));
    }

    /// <summary>The step is recorded as added on the Demo's marker.</summary>
    private async Task AssertAddedAsync(Guid ledgerId, string key)
    {
        var marker = await DemoSeedingHarness.MarkerAsync(Db, ledgerId);
        Assert.True(DemoSeedingHarness.Details(marker.DetailsJson)[key] == 1, marker.ErrorMessage);
    }

    private async Task<DemoSeedIds> ResolveAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await DemoSeedResolver.ResolveAsync(db, ledgerId, Manifest, CancellationToken.None);
    }

    /// <summary>Everything the budgets and folders steps write, as one comparable string.</summary>
    private async Task<string> BudgetAndFoldersAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var targets = await db.BudgetTargets.AsNoTracking().Where(t => t.LedgerId == ledgerId)
            .OrderBy(t => t.CategoryId).ThenBy(t => t.TargetMonth)
            .Select(t => t.CategoryId + "/" + t.TargetMonth + "/" + t.Amount)
            .ToListAsync();
        var folders = await db.AccountFolders.AsNoTracking().Where(f => f.LedgerId == ledgerId)
            .OrderBy(f => f.Name).Select(f => f.Name + "/" + f.AccountType).ToListAsync();
        var members = await db.AccountFolderMembers.AsNoTracking().Where(m => m.LedgerId == ledgerId)
            .OrderBy(m => m.AccountId).Select(m => m.AccountId + "/" + m.FolderId).ToListAsync();
        Assert.NotEmpty(targets);
        Assert.NotEmpty(folders);
        return string.Join("\n", targets.Concat(folders).Concat(members));
    }

    /// <summary>Everything the register step writes, as one comparable string: the payee and
    /// visibility of each row it renames or hides, their originals, and every leg status the
    /// Demo holds.</summary>
    private async Task<string> RegisterAsync(Guid ledgerId, DemoSeedIds ids)
    {
        await using var db = Db.NewDbContext();
        var touched = Manifest.EditCandidates.Select(e => ids.Transactions[e.TxnId])
            .Concat(Manifest.DuplicateTxnIds.Select(d => ids.Transactions[d]))
            .ToList();
        var headers = await db.TxnHeaders.AsNoTracking().Where(h => touched.Contains(h.Id))
            .OrderBy(h => h.Id).Select(h => h.Id + "/" + h.Payee + "/" + h.IsHidden + "/" + h.NeedsReview)
            .ToListAsync();
        var originals = await db.TxnHeaderOriginals.AsNoTracking().Where(o => touched.Contains(o.HeaderId))
            .OrderBy(o => o.HeaderId).Select(o => o.HeaderId + "/" + o.Payee)
            .ToListAsync();
        var statuses = await db.TxnLegRecon.AsNoTracking().Where(r => r.LedgerId == ledgerId)
            .OrderBy(r => r.LegId).Select(r => r.LegId + "/" + r.Status)
            .ToListAsync();
        Assert.Equal(touched.Count, headers.Count);
        Assert.NotEmpty(statuses);
        return string.Join("\n", headers.Concat(originals).Concat(statuses));
    }

    /// <summary>Everything the card statement step writes, as one comparable string: the
    /// layouts, the file imports, and every row on the card with its review and merge
    /// state.</summary>
    private async Task<string> CardStatementAsync(Guid ledgerId)
    {
        var card = (await ResolveAsync(ledgerId)).Accounts[DemoAccountRoles.RewardsCard];
        await using var db = Db.NewDbContext();
        var layouts = await db.FeedCsvMappings.AsNoTracking().Where(m => m.LedgerId == ledgerId)
            .OrderBy(m => m.Id).Select(m => m.Id + "/" + m.Name + "/" + m.DefinitionYaml).ToListAsync();
        var imports = await db.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledgerId && o.ProviderKey != LedgerOperationsRepository.DemoMarkerProviderKey)
            .OrderBy(o => o.Id).Select(o => o.Id + "/" + o.ProviderKey + "/" + o.Status).ToListAsync();
        var rows = await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId
                join leg in db.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == card
                orderby header.Id
                select header.Id + "/" + header.PostedAt + "/" + header.NeedsReview + "/" + header.IsMergedInto + "/"
                    + header.IsMergeWinner + "/" + header.ProviderKey)
            .ToListAsync();
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == card).Select(a => a.ImportProviderKey).SingleAsync();
        Assert.NotEmpty(rows);
        return string.Join("\n", layouts.Concat(imports).Concat(rows).Append(account ?? "(none)"));
    }

    /// <summary>Every row with a line on the card, as one comparable string: its day, review,
    /// visibility and merge state.</summary>
    private async Task<string> CardRowsAsync(Guid ledgerId, Guid card)
    {
        await using var db = Db.NewDbContext();
        var rows = await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId
                join leg in db.TxnLegs.AsNoTracking() on header.Id equals leg.HeaderId
                where leg.AccountId == card
                orderby header.Id, leg.Id
                select header.Id + "/" + leg.Id + "/" + header.PostedAt + "/" + leg.Amount + "/" + header.NeedsReview + "/"
                    + header.IsHidden + "/" + header.IsMergedInto + "/" + header.IsMergeWinner + "/" + header.LedgerOperationId)
            .ToListAsync();
        Assert.NotEmpty(rows);
        return string.Join("\n", rows);
    }

    /// <summary>Each row merged into one of <paramref name="winners"/>, with the row it was
    /// merged into, in a fixed order.</summary>
    private async Task<List<string>> MergesAsync(Guid ledgerId, IReadOnlySet<Guid> winners)
    {
        await using var db = Db.NewDbContext();
        var merged = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.IsMergedInto != null)
            .Select(h => new { h.Id, Into = h.IsMergedInto!.Value })
            .ToListAsync();
        Assert.All(merged, m => Assert.Contains(m.Into, winners));
        return merged.OrderBy(m => m.Id).Select(m => m.Id + "->" + m.Into).ToList();
    }

    /// <summary>How many rows with a line on the card wait in review.</summary>
    private async Task<int> CardReviewCountAsync(Guid ledgerId, Guid card)
    {
        await using var db = Db.NewDbContext();
        return await (
                from header in db.TxnHeaders.AsNoTracking()
                where header.LedgerId == ledgerId && header.NeedsReview && !header.IsHidden && header.IsMergedInto == null
                where db.TxnLegs.Any(l => l.HeaderId == header.Id && l.AccountId == card)
                select header.Id)
            .CountAsync();
    }

    /// <summary>The consistency check, run as a person runs it from Settings, finds every
    /// projection healthy.</summary>
    private static async Task AssertHealthyAsync(HttpClient client, Guid ledgerId)
    {
        var check = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/balances/consistency?today={DemoSeedingHarness.Today}", content: null);
        Assert.True(check.IsSuccessStatusCode, await check.Content.ReadAsStringAsync());
        var report = await check.Content.ReadFromJsonAsync<LedgerConsistencyReport>();
        Assert.NotNull(report);
        Assert.True(report.Healthy, "unhealthy: " + string.Join(", ", report.Projections.Where(p => !p.Healthy).Select(p => p.Projection)));
    }

    /// <summary>Every reminder, template line, linked posting and skip of the Demo, as one
    /// comparable string.</summary>
    private async Task<string> RemindersAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var series = await db.RecurringTransactions.AsNoTracking().Where(r => r.LedgerId == ledgerId)
            .OrderBy(r => r.Id).Select(r => r.Id + "/" + r.Rrule + "/" + r.EstimateSampleCount + "/" + r.NextDueDate)
            .ToListAsync();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && (h.IsRecurringTemplate || h.RecurringTransactionId != null))
            .OrderBy(h => h.Id).Select(h => h.Id + "/" + h.OccurrenceDate).ToListAsync();
        var skips = await db.RecurringOccurrenceExceptions.AsNoTracking().Where(e => e.LedgerId == ledgerId)
            .OrderBy(e => e.Id).Select(e => e.RecurringTransactionId + "/" + e.OccurrenceDate).ToListAsync();
        Assert.NotEmpty(series);
        return string.Join("\n", series.Concat(headers).Concat(skips));
    }

    private static DateOnly LastDayOf(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}
