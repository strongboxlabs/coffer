using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit.Sdk;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Import;
using Coffer.Api.Provisioning.Demo;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// The whole five-year Demo, added once through the hub's endpoint and its job as a person
/// adds it: an import that loses nothing, the examples its seeding phase adds and records,
/// every projection healthy afterwards, the shapes only the whole window carries, and the
/// breadth a ledger needs to answer the questions it is for.
/// </summary>
/// <remarks>
/// <para><b>One Demo per class.</b> <see cref="FullDemoFixture"/> adds the Demo once, before
/// the first fact, and every fact reads that one ledger, so the full import runs once per
/// test process. Every other Demo test uses a trailing span of the same generator. Its
/// waits are a ceiling, not a speed assertion.</para>
/// <para><b>Balances on the whole window only.</b> A trailing span keeps every end balance
/// but not the days before its cut, so the facts that read a balance at a past month end
/// run here.</para>
/// <para><b>Breadth floors, not counts.</b> The history is generated and may be retuned;
/// emptying a shape should fail, retuning it should not. The floors read the imported rows
/// (a non-null <c>external_id</c>), so the rows the examples add do not count towards them.
/// The invariants (memo and tag placement, the originating account) hold over every row.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class FullDemoTests : IClassFixture<FullDemoFixture>
{
    private readonly FullDemoFixture _demo;

    public FullDemoTests(FullDemoFixture demo, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(output);
        _demo = demo;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"the full Demo was added in {demo.Elapsed.TotalSeconds:F1} s"));
    }

    private static DemoManifest Manifest => DemoTestExports.Full.Value.Manifest;

    private static DateOnly AsOf => DemoTestExports.AsOf;

    private Guid LedgerId => _demo.LedgerId;

    private HttpClient Client => _demo.Client;

    private DemoSeedIds Ids => _demo.Ids;

    // --- the import ---------------------------------------------------------

    [Fact]
    public void The_whole_Demo_imports_losslessly()
    {
        // As the job reports it: no warning, no transaction left out.
        var job = _demo.Job;
        Assert.Equal(ImportJobState.Succeeded, job.State);
        Assert.Empty(job.Warnings);
        Assert.Empty(job.SkippedTransactions);
        Assert.Equal(0, job.SkippedTransactionsNotListed);

        // As the import saw it: every check passed, and no transaction step skipped a row,
        // counted or listed.
        var result = _demo.ImportResult;
        Assert.Equal(LedgerId, result.LedgerId);
        var failed = result.Validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Message}").ToList();
        Assert.True(failed.Count == 0, "failed checks:\n" + string.Join("\n", failed));
        // The other steps' skips are structural (a currency is not a security; the
        // export's root and its per-security sub-accounts are not ledger accounts), so the
        // transaction steps are the ones read.
        var transactionSteps = result.Steps
            .Where(s => s.StepName is "transactions" or "investment_transactions").ToList();
        Assert.Equal(2, transactionSteps.Count);
        Assert.All(transactionSteps, s =>
        {
            Assert.True(s.Skipped == 0, $"{s.StepName} skipped {s.Skipped}");
            Assert.Empty(s.Skips ?? []);
        });
    }

    // --- the examples phase's record ------------------------------------------

    [Fact]
    public async Task The_marker_records_every_step_added_and_the_hub_names_none_missing()
    {
        Assert.Equal(DemoProgressLabels.Total, _demo.Job.Total);
        Assert.Equal(DemoProgressLabels.Total, _demo.Job.Completed);
        var marker = await DemoSeedingHarness.MarkerAsync(_demo.Db, LedgerId);
        Assert.Equal(DemoMarker.Completed, marker.Status);
        Assert.NotNull(marker.CompletedAt);
        Assert.Null(marker.ErrorMessage);

        var details = DemoSeedingHarness.Details(marker.DetailsJson);
        Assert.Equal(DemoSeedSteps.All.Select(s => s.Key).Order(StringComparer.Ordinal), details.Keys.Order(StringComparer.Ordinal));
        Assert.All(details, d => Assert.True(d.Value == 1, d.Key + ": " + marker.ErrorMessage));

        // The hub lists the Demo as an ordinary ledger, with no example missing.
        Assert.Null(await DemoSeedingHarness.MissingAsync(Client, LedgerId));
    }

    [Fact]
    public async Task Activity_lists_the_Moneydance_import_the_Demo_examples_and_the_card_statement_s_import()
    {
        // Newest first: the card statement's import, the examples that ran it, the
        // Moneydance import they follow.
        await DemoSeedingHarness.AssertActivityAsync(Client, LedgerId, _demo.Job.UserId);
        using var list = JsonDocument.Parse(await Client.GetStringAsync($"/api/ledgers/{LedgerId}/ledger-operations"));
        var rows = list.RootElement.EnumerateArray().ToList();
        var import = Assert.Single(rows, r => r.GetProperty("providerKey").GetString() == "moneydance");

        // The examples' entry: completed, its details one entry per step, each added, so
        // its summary counts every step of the examples as added.
        var examples = Assert.Single(rows, r => r.GetProperty("providerKey").GetString() == "demo");
        Assert.Equal(DemoMarker.Completed, examples.GetProperty("status").GetString());
        var details = examples.GetProperty("details").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        Assert.Equal(DemoSeedSteps.All.Select(s => s.Key).Order(StringComparer.Ordinal), details.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(DemoSeedSteps.Count, details.Values.Count(v => v == 1));

        // The examples started after the import they follow, and the card statement's
        // import inside them, so the timeline lists it above them.
        var statement = Assert.Single(rows, r => r.GetProperty("providerKey").GetString() == Coffer.Api.Ingest.Csv.CsvGenericFileProvider.Key);
        Assert.True(
            examples.GetProperty("startedAt").GetDateTimeOffset() >= import.GetProperty("startedAt").GetDateTimeOffset(),
            $"the examples started {examples.GetProperty("startedAt")}, the import {import.GetProperty("startedAt")}");
        Assert.True(
            statement.GetProperty("startedAt").GetDateTimeOffset() >= examples.GetProperty("startedAt").GetDateTimeOffset()
            && statement.GetProperty("completedAt").GetDateTimeOffset() <= examples.GetProperty("completedAt").GetDateTimeOffset(),
            $"the statement's import ran {statement.GetProperty("startedAt")} to {statement.GetProperty("completedAt")}, "
            + $"the examples {examples.GetProperty("startedAt")} to {examples.GetProperty("completedAt")}");
    }

    [Fact]
    public async Task The_register_step_renames_beside_the_originals_hides_the_re_sent_copies_and_half_reconciles()
    {
        await DemoSeedingHarness.AssertRegisterAsync(_demo.Db, LedgerId, Ids, Manifest);
    }

    [Fact]
    public async Task The_card_statement_step_saves_its_layout_imports_into_review_and_merges_the_rows_typed_in()
    {
        await DemoSeedingHarness.AssertCardStatementAsync(
            _demo.Db, Client, LedgerId, _demo.Job.UserId, Ids, Manifest, DemoTestExports.Full.Value.CardStatementCsv);
    }

    [Fact]
    public async Task The_snapshot_step_takes_one_manual_snapshot_holding_every_example_before_it()
    {
        // At full size: the snapshot of a five-year ledger, chunked as any large one is.
        await DemoSeedingHarness.AssertSnapshotAsync(_demo.Db, Client, LedgerId, _demo.Job.UserId, Ids);
    }

    // --- what each step added -------------------------------------------------

    [Fact]
    public async Task The_folders_step_files_the_everyday_and_retirement_accounts()
    {
        var folders = await Client.GetFromJsonAsync<List<AccountFolderDto>>($"/api/ledgers/{LedgerId}/account-folders");
        Assert.NotNull(folders);
        Assert.Equal(Manifest.Folders.Select(f => (f.Name, f.AccountType)).Order(), folders.Select(f => (f.Name, f.AccountType)).Order());
        Assert.All(folders, f => Assert.Null(f.ParentId));
        foreach (var expected in Manifest.Folders)
            Assert.Equal(
                expected.AccountIds.Select(id => Ids.AccountsByMdId[id]).Order(),
                Assert.Single(folders, f => f.Name == expected.Name).AccountIds.Order());

        // The bank folder holds the two everyday accounts; the investment folder the four
        // retirement accounts.
        Assert.Equal(
            new[] { Ids.Accounts[DemoAccountRoles.JointChecking], Ids.Accounts[DemoAccountRoles.CashWallet] }.Order(),
            Assert.Single(folders, f => f.AccountType == "bank").AccountIds.Order());
        Assert.Equal(4, Assert.Single(folders, f => f.AccountType == "investment").AccountIds.Count);
    }

    [Fact]
    public async Task The_investments_step_sets_the_commission_flag_the_look_through_and_quarterly_prices()
    {
        await using var db = _demo.Db.NewDbContext();
        Assert.Equal(
            [Ids.Accounts[DemoAccountRoles.Brokerage]],
            await db.Accounts.AsNoTracking().Where(a => a.LedgerId == LedgerId && a.IsTradeCommission).Select(a => a.Id).ToListAsync());

        var components = await Client.GetFromJsonAsync<List<SecurityComponentDto>>(
            $"/api/ledgers/{LedgerId}/securities/{Ids.Securities[DemoSecurities.TargetDate]}/components");
        Assert.NotNull(components);
        Assert.Equal(3, components.Count);
        Assert.Equal(1m, components.Sum(c => c.Weight));

        // Each quarter's end from the stable-value fund's first buy through AsOf, at the
        // manifest's month-end value, and no other manual price.
        var stableValue = Ids.Securities[DemoSecurities.StableValue];
        var firstBuy = DateOnly.FromDateTime(await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == LedgerId && leg.SecurityId == stableValue && leg.Quantity > 0m
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                select header.PostedAt)
            .MinAsync());
        var expected = Manifest.StableValueMonthEnds
            .Where(m => m.Date.Month is 3 or 6 or 9 or 12 && m.Date.AddDays(1).Day == 1)
            .Where(m => m.Date >= firstBuy && m.Date <= AsOf)
            .Select(m => (m.Date, m.UnitValue))
            .ToList();
        Assert.NotEmpty(expected);
        var manual = await db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == LedgerId && p.Source == PriceSource.Manual)
            .OrderBy(p => p.PriceDate)
            .Select(p => new { p.SecurityId, p.PriceDate, p.Price })
            .ToListAsync();
        Assert.All(manual, p => Assert.Equal(stableValue, p.SecurityId));
        Assert.Equal(expected, manual.Select(p => (p.PriceDate, p.Price)));
    }

    [Fact]
    public async Task The_budgets_step_sets_the_manifest_s_targets()
    {
        await using var db = _demo.Db.NewDbContext();
        var stored = await db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == LedgerId)
            .Select(t => new { t.CategoryId, t.TargetMonth, t.Amount })
            .ToListAsync();
        Assert.NotEmpty(stored);
        Assert.Equal(
            Manifest.BudgetTargets.Select(t => (Ids.Category(t.Category), new DateOnly(t.Year, t.Month, 1), t.Amount / 100m)).Order(),
            stored.Select(t => (t.CategoryId, t.TargetMonth, t.Amount)).Order());
    }

    [Fact]
    public async Task The_reminders_step_leaves_a_resolving_estimate_the_managed_mortgage_one_skip_and_auto_post_on()
    {
        await using var db = _demo.Db.NewDbContext();
        var electric = await db.RecurringTransactions.AsNoTracking()
            .SingleAsync(r => r.LedgerId == LedgerId && r.ExternalId == null && !r.IsLoanReminder);

        // The estimate resolves: as many linked bills as it asks for.
        var list = await Client.GetFromJsonAsync<List<ReminderSummary>>($"/api/ledgers/{LedgerId}/reminders");
        Assert.NotNull(list);
        var estimate = Assert.Single(list, r => r.Id == electric.Id).Estimate;
        Assert.NotNull(estimate);
        Assert.Equal(3, estimate.RequestedSampleCount);
        Assert.True(estimate.AvailableSampleCount >= estimate.RequestedSampleCount,
            $"{estimate.AvailableSampleCount} of {estimate.RequestedSampleCount} samples");
        Assert.Null(estimate.UnavailableReason);
        Assert.NotNull(estimate.Amount);

        // The loan reminder starts at the mortgage's first due date after AsOf.
        var mortgage = Ids.Accounts[DemoAccountRoles.Mortgage];
        var firstPayment = (await db.LoanTerms.AsNoTracking().SingleAsync(t => t.LedgerId == LedgerId && t.AccountId == mortgage))
            .FirstPaymentDate!.Value;
        var firstDue = Enumerable.Range(0, 12 * 40).Select(months => firstPayment.AddMonths(months)).First(d => d > AsOf);
        var loan = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.LedgerId == LedgerId && r.IsLoanReminder);
        Assert.Equal(mortgage, loan.LoanAccountId);
        Assert.Equal(firstDue, loan.StartDate);

        // The one skip is the Internet bill's first occurrence after AsOf.
        var nextInternet = new DateOnly(AsOf.Year, AsOf.Month, BillsPattern.InternetDay);
        if (nextInternet <= AsOf) nextInternet = nextInternet.AddMonths(1);
        var skip = Assert.Single(await db.RecurringOccurrenceExceptions.AsNoTracking().Where(e => e.LedgerId == LedgerId).ToListAsync());
        Assert.Equal(Ids.InternetReminder, skip.RecurringTransactionId);
        Assert.Equal(nextInternet, skip.OccurrenceDate);

        var schedule = await db.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.LedgerId == LedgerId && j.JobType == JobTypes.ReminderAutoPost);
        Assert.True(schedule.Enabled);
    }

    /// <summary>The saved reports' names, in the order the step saves and pins them.</summary>
    private static readonly string[] SavedReportNames =
        ["Spending by category, monthly", "Income versus spending, monthly", "Income by payee, yearly"];

    [Fact]
    public async Task The_reports_step_saves_three_reports_that_run_and_pins_them_before_the_catalog_s()
    {
        using var saved = JsonDocument.Parse(await Client.GetStringAsync($"/api/ledgers/{LedgerId}/reports/saved"));
        var reports = saved.RootElement.EnumerateArray().ToList();
        Assert.Equal(SavedReportNames.Order(StringComparer.Ordinal), reports.Select(r => r.GetProperty("name").GetString()!));
        Assert.All(reports, r => Assert.Equal("private", r.GetProperty("visibility").GetString()));

        // Each runs as Reports runs it, over five years of rows.
        foreach (var report in reports)
        {
            var query = string.Join("&", report.GetProperty("spec").EnumerateObject()
                .Where(p => p.Name is not ("view" or "chartStack"))
                .Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
            var summary = await Client.GetAsync($"/api/ledgers/{LedgerId}/reports/summary?{query}&today={DemoSeedingHarness.Today}");
            Assert.True(summary.IsSuccessStatusCode, report.GetProperty("name").GetString() + ": " + await summary.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
            Assert.NotEmpty(result.RootElement.GetProperty("rows").EnumerateArray());
        }

        var idsByName = reports.ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
        using var pins = JsonDocument.Parse(await Client.GetStringAsync($"/api/ledgers/{LedgerId}/reports/pins"));
        Assert.Equal(
            SavedReportNames.Select(n => "saved:" + idsByName[n]).Concat(["catalog:income-and-expenses", "catalog:returns"]),
            pins.RootElement.EnumerateArray().Select(p => p.GetProperty("savedReportId").ValueKind == JsonValueKind.Null
                ? "catalog:" + p.GetProperty("catalogKey").GetString()
                : "saved:" + p.GetProperty("savedReportId").GetGuid()));
    }

    [Fact]
    public async Task The_home_step_saves_the_layout_with_the_Demo_s_ids()
    {
        using var layout = JsonDocument.Parse(await Client.GetStringAsync($"/api/ledgers/{LedgerId}/preferences/dashboard"));
        var widgets = layout.RootElement.GetProperty("widgets").EnumerateArray().ToList();

        var watchlist = Assert.Single(widgets, w => w.GetProperty("key").GetString() == "watchlist");
        Assert.Equal(
            new[] { "groceries", "dining", "entertainment", "clothing", "electric" }.Select(r => Ids.Categories[r]),
            watchlist.GetProperty("settings").GetProperty("categoryIds").EnumerateArray().Select(e => e.GetGuid()));
        var forecast = Assert.Single(widgets, w => w.GetProperty("key").GetString() == "cash-forecast");
        Assert.Equal(Ids.Accounts[DemoAccountRoles.JointChecking], forecast.GetProperty("settings").GetProperty("focusAccountId").GetGuid());

        using var saved = JsonDocument.Parse(await Client.GetStringAsync($"/api/ledgers/{LedgerId}/reports/saved"));
        var idsByName = saved.RootElement.EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("id").GetGuid());
        Assert.Equal(
            SavedReportNames.Select(n => "report:saved:" + idsByName[n]),
            widgets.Where(w => w.GetProperty("key").GetString() == "report").Select(w => w.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task The_check_step_passed_and_the_consistency_check_finds_every_projection_healthy()
    {
        var marker = await DemoSeedingHarness.MarkerAsync(_demo.Db, LedgerId);
        Assert.Equal(1, DemoSeedingHarness.Details(marker.DetailsJson)["check"]);

        // Run again as a person runs it from Settings, over every example: the renamed,
        // hidden and reconciling rows, the statement's merges and its review rows.
        var check = await Client.PostAsync(
            $"/api/ledgers/{LedgerId}/balances/consistency?today={DemoSeedingHarness.Today}", content: null);
        Assert.True(check.IsSuccessStatusCode, await check.Content.ReadAsStringAsync());
        var report = await check.Content.ReadFromJsonAsync<LedgerConsistencyReport>();
        Assert.NotNull(report);
        Assert.True(report.Healthy, "unhealthy: " + string.Join(", ", report.Projections.Where(p => !p.Healthy).Select(p => p.Projection)));
    }

    [Fact]
    public void The_examples_change_no_other_ledger()
    {
        // The seeding runs as the service role, past row-level security: every row of the
        // ledger the install already had is as it was before the Demo was added. Anchored
        // on that ledger's own rows: it was made with its starter categories, which counts
        // read for any other ledger id would not show.
        Assert.True(_demo.OtherLedgerBefore.GetValueOrDefault("accounts") > 0, "the other ledger has no starter categories");
        Assert.Equal(_demo.OtherLedgerBefore, _demo.OtherLedgerAfter);
    }

    [Fact]
    public async Task The_Demo_has_no_feed_quote_webhook_exchange_rate_benchmark_token_or_invite()
    {
        await using var db = _demo.Db.NewDbContext();
        Assert.False(await db.FeedConnections.AnyAsync(f => f.LedgerId == LedgerId));
        Assert.False(await db.FeedConnectionAccounts.AnyAsync(f => f.LedgerId == LedgerId));
        Assert.False(await db.LedgerBenchmarks.AnyAsync(b => b.LedgerId == LedgerId));
        // No delivery target of its own, and no exchange rate or rate backfill: the Demo
        // is in one currency, and nothing it was given reaches out.
        Assert.False(await db.LedgerNotificationSubscribers.AnyAsync(s => s.LedgerId == LedgerId));
        Assert.False(await db.FxPairCoverage.AnyAsync(c => c.LedgerId == LedgerId));
        Assert.False(await db.FxRates.AnyAsync(r => r.LedgerId == LedgerId));
        Assert.Equal(
            [JobTypes.ReminderAutoPost],
            await db.ScheduledJobs.AsNoTracking().Where(j => j.LedgerId == LedgerId).Select(j => j.JobType).ToListAsync());
        // No price came from a quote source: only the export's, the hand-entered ones and
        // those the trades imply.
        var sources = await db.SecurityPrices.AsNoTracking().Where(p => p.LedgerId == LedgerId).Select(p => p.Source).Distinct().ToListAsync();
        Assert.Subset(new HashSet<string>([PriceSource.Import, PriceSource.Manual, PriceSource.Trade], StringComparer.Ordinal), sources.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(PriceSource.Import, sources);
        Assert.Contains(PriceSource.Manual, sources);
        Assert.False(await db.McpAccessTokens.AnyAsync());
        Assert.False(await db.Invites.AnyAsync());
        Assert.Equal(
            [_demo.Job.UserId],
            await db.UserLedgerGrants.AsNoTracking().Where(g => g.LedgerId == LedgerId).Select(g => g.UserId).ToListAsync());
    }

    // --- shapes only the whole window carries ----------------------------------

    [Fact]
    public async Task The_house_s_revaluation_moves_its_balance_as_an_adjustment_and_is_not_spending()
    {
        await using var db = _demo.Db.NewDbContext();
        var adjustment = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == Ids.Categories["valuation-adjustment"]);
        Assert.Equal("category", adjustment.AccountType);
        Assert.Equal("adjustment", adjustment.CategoryKind);
        Assert.Null(adjustment.ParentId);

        var calendar = new DemoCalendar(AsOf);
        var monthStart = calendar.MonthStart(HouseholdTerms.RevaluationMonth).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var monthEnd = calendar.MonthStart(HouseholdTerms.RevaluationMonth + 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // In the revaluation month, the rows on the adjustment move the house, by as much.
        var house = Ids.Accounts[DemoAccountRoles.House];
        var revaluations = await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == LedgerId && leg.AccountId == adjustment.Id
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                where header.PostedAt >= monthStart && header.PostedAt < monthEnd
                select header.Id)
            .Distinct().ToListAsync();
        Assert.NotEmpty(revaluations);
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => revaluations.Contains(l.HeaderId))
            .Select(l => new { l.AccountId, l.Amount })
            .ToListAsync();
        var houseMoved = legs.Where(l => l.AccountId == house).Sum(l => l.Amount);
        Assert.True(houseMoved > 0m, $"the house moved {houseMoved} in the revaluation month");
        Assert.Equal(-houseMoved, legs.Where(l => l.AccountId == adjustment.Id).Sum(l => l.Amount));
        Assert.Equal(0m, legs.Sum(l => l.Amount));

        // That month's spending lists no adjustment.
        using var spending = JsonDocument.Parse(await Client.GetStringAsync(
            $"/api/ledgers/{LedgerId}/reports/summary?measure=Spending&groupBy=Category&dateMode=Static"
            + $"&from={Iso(monthStart)}&to={Iso(monthEnd)}&today={DemoSeedingHarness.Today}"));
        var rows = spending.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.GetProperty("groupId").ValueKind == JsonValueKind.String
            && r.GetProperty("groupId").GetGuid() == adjustment.Id);
    }

    [Fact]
    public async Task The_mortgage_opens_at_minus_its_principal_at_the_closing_month_s_end()
    {
        await using var db = _demo.Db.NewDbContext();
        var mortgage = Ids.Accounts[DemoAccountRoles.Mortgage];
        var end = new DemoCalendar(AsOf).MonthStart(HouseholdTerms.ClosingMonth + 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var opening = await db.Accounts.AsNoTracking().Where(a => a.Id == mortgage).Select(a => a.OpeningBalance).SingleAsync();
        var moved = await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == LedgerId && leg.AccountId == mortgage
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                where header.PostedAt < end && !header.IsRecurringTemplate
                select leg.Amount)
            .SumAsync();
        Assert.Equal(-HouseholdTerms.MortgagePrincipal / 100m, opening + moved);
    }

    [Fact]
    public async Task Each_security_has_its_ticker_s_vehicle_and_the_split_multiplies_the_shares_held()
    {
        await using var db = _demo.Db.NewDbContext();
        var securities = await db.Securities.AsNoTracking().Where(s => s.LedgerId == LedgerId).ToListAsync();
        Assert.Equal(DemoSecurities.All.Count, securities.Count);
        var vehicles = securities.ToDictionary(s => s.Ticker ?? "(none)", s => s.VehicleType, StringComparer.Ordinal);
        Assert.Equal("etf", vehicles[DemoSecurities.Vti]);
        Assert.Equal("etf", vehicles[DemoSecurities.Vnq]);
        Assert.Equal("mutual_fund", vehicles[DemoSecurities.TargetDate]);
        Assert.Equal("money_market", vehicles[DemoSecurities.MoneyMarket]);
        Assert.Equal("stock", vehicles[DemoSecurities.Nvda]);
        Assert.Equal("stock", vehicles[DemoSecurities.Msft]);
        Assert.Equal("mutual_fund", vehicles["(none)"]);

        // The split is inside the window, and every share held before it is ten after it.
        var nvda = Assert.Single(securities, s => s.Ticker == DemoSecurities.Nvda).Id;
        var split = DemoSecurities.SplitDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var moves = await (
                from leg in db.TxnLegs.AsNoTracking()
                where leg.LedgerId == LedgerId && leg.SecurityId == nvda && leg.Quantity != null
                join header in db.TxnHeaders.AsNoTracking() on leg.HeaderId equals header.Id
                where !header.IsRecurringTemplate
                select new { Before = header.PostedAt < split, Quantity = leg.Quantity!.Value })
            .ToListAsync();
        var before = moves.Where(m => m.Before).Sum(m => m.Quantity);
        var after = moves.Where(m => !m.Before).Sum(m => m.Quantity);
        Assert.True(before > 0m, "no share was held before the split");
        var held = await db.Holdings.AsNoTracking().Where(h => h.LedgerId == LedgerId && h.SecurityId == nvda).SumAsync(h => h.Quantity);
        Assert.Equal(before * DemoSecurities.SplitNewShares / DemoSecurities.SplitOldShares + after, held);
    }

    [Fact]
    public async Task Net_worth_history_and_returns_answer_over_the_whole_window()
    {
        var calendar = new DemoCalendar(AsOf);
        var from = Iso(calendar.WindowStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var to = Iso(AsOf.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var netWorth = await Client.GetAsync(
            $"/api/ledgers/{LedgerId}/reports/net-worth-history?interval=Month&from={from}&to={to}&today={DemoSeedingHarness.Today}");
        var netWorthBody = await netWorth.Content.ReadAsStringAsync();
        Assert.True(netWorth.IsSuccessStatusCode, netWorthBody);
        using var history = JsonDocument.Parse(netWorthBody);
        var points = history.RootElement.GetProperty("points").GetArrayLength();
        Assert.True(points >= DemoCalendar.WindowMonths, $"{points} monthly points over the window");

        var returns = await Client.GetAsync(
            $"/api/ledgers/{LedgerId}/reports/returns?from={from}&to={to}&today={DemoSeedingHarness.Today}");
        var returnsBody = await returns.Content.ReadAsStringAsync();
        Assert.True(returns.IsSuccessStatusCode, returnsBody);
        using var result = JsonDocument.Parse(returnsBody);
        Assert.Equal(JsonValueKind.Number, result.RootElement.GetProperty("endValue").ValueKind);
    }

    // --- breadth: what a ledger needs to answer the questions it is for ----------

    [Fact]
    public async Task The_imported_history_can_carry_a_report_and_a_reconcile()
    {
        await using var conn = await _demo.OpenAsync();
        var ledgerId = LedgerId;

        // --- a time axis -----------------------------------------------------
        // A monthly cash-flow report needs months to draw.
        var months = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT date_trunc('month', posted_at AT TIME ZONE 'UTC'))
            FROM txn_headers WHERE ledger_id = @LedgerId AND external_id IS NOT NULL;
            """, new { LedgerId = ledgerId });
        Assert.True(months >= 30, $"expected >= 30 distinct months of history, found {months}");

        // --- something to aggregate ------------------------------------------
        var headers = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_headers WHERE ledger_id = @LedgerId AND external_id IS NOT NULL;",
            new { LedgerId = ledgerId });
        Assert.True(headers >= 500, $"expected >= 500 transactions, found {headers}");

        // --- spread across the taxonomy --------------------------------------
        // A spending report over three categories is a list. These counts are
        // what make a top-N or a category breakdown mean anything.
        var expenseCats = await DistinctCategoriesAsync(conn, ledgerId, "expense");
        var incomeCats = await DistinctCategoriesAsync(conn, ledgerId, "income");
        Assert.True(expenseCats >= 15, $"expected >= 15 distinct expense categories used, found {expenseCats}");
        Assert.True(incomeCats >= 2, $"expected >= 2 distinct income categories used, found {incomeCats}");

        // --- both reconciliation states --------------------------------------
        // ADR-0082 models this as a per-leg overlay where an ABSENT row means
        // uncleared, so "has cleared legs" and "has uncleared legs" are two
        // different queries and a sample needs both to be non-zero. All-cleared
        // would be as useless as all-uncleared.
        var clearedLegs = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_leg_recon r
            JOIN txn_legs l ON l.id = r.leg_id
            JOIN txn_headers h ON h.id = l.header_id
            WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL AND r.status = 'cleared';
            """, new { LedgerId = ledgerId });
        var unclearedLegs = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            JOIN txn_headers h ON h.id = l.header_id
            WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL
              AND a.account_type <> 'category'
              AND NOT EXISTS (SELECT 1 FROM txn_leg_recon r WHERE r.leg_id = l.id);
            """, new { LedgerId = ledgerId });
        Assert.True(clearedLegs > 0, "no cleared legs — nothing to reconcile against");
        Assert.True(unclearedLegs > 0, "every leg is cleared — nothing left to reconcile");

        // --- a real split ----------------------------------------------------
        // The paycheck: gross pay against five withholdings. One posting index
        // per split, so this counts indexes rather than legs (each split emits
        // an origin leg and a counterpart leg).
        var widestSplit = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(DISTINCT l.posting_index) AS c
                FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
                WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL GROUP BY l.header_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestSplit >= 5, $"expected a transaction split >= 5 ways, widest was {widestSplit}");

        // --- a check number --------------------------------------------------
        var withCheck = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId AND external_id IS NOT NULL AND check_number IS NOT NULL;
            """, new { LedgerId = ledgerId });
        Assert.True(withCheck > 0, "no check numbers — the column is never exercised");

        // --- a ledger, not a chequebook --------------------------------------
        // Net worth needs an account whose balance is not a bank feed. Without
        // an asset and a liability the report renders and says nothing, and the
        // mortgage payment — interest against principal — is the everyday
        // transaction most often modelled as a flat expense.
        foreach (var type in new[] { "asset", "liability" })
        {
            var n = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM accounts WHERE ledger_id = @LedgerId AND account_type = @Type AND external_id IS NOT NULL;",
                new { LedgerId = ledgerId, Type = type });
            Assert.True(n > 0, $"no {type} account — net worth has no shape to report");
        }

        // --- the brokerage has a history, not a moment -----------------------
        // Months of spending beside an investment account that exists for ten
        // days gives returns, performance and investment income nothing to
        // read: they all need a time axis.
        var investmentMonths = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(DISTINCT date_trunc('month', h.posted_at AT TIME ZONE 'UTC'))
              FROM txn_headers h
              WHERE h.ledger_id = @LedgerId AND h.external_id IS NOT NULL
                AND EXISTS (SELECT 1 FROM txn_legs l
                            WHERE l.header_id = h.id AND l.security_id IS NOT NULL);",
            new { LedgerId = ledgerId });
        Assert.True(investmentMonths >= 12,
            $"expected >= 12 months carrying investment activity, found {investmentMonths}");

        // --- a refund, posted negative to an EXPENSE category ---------------
        // Every other expense in the sample is positive, which makes "expenses
        // are positive" look like a rule rather than the usual case. A report
        // that totalled magnitudes instead of netting would pass every other
        // assertion here.
        var refunds = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM txn_legs l
              JOIN accounts a ON a.id = l.account_id
              JOIN txn_headers h ON h.id = l.header_id
              WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL
                AND a.account_type = 'category' AND a.category_kind = 'expense'
                AND l.amount < 0;",
            new { LedgerId = ledgerId });
        Assert.True(refunds > 0,
            "no negative posting to an expense category — the sample cannot show a refund");

        // --- tags, at BOTH levels --------------------------------------------
        // Tags sit on headers (an investment event of several postings, a
        // split's transaction-level set) and on postings (a one-split's sides, a
        // split's lines), with different meanings. A sample with tags on only
        // one of them cannot test the distinction, and ReportGroupBy.Tag has to
        // resolve exactly that question before Tag Summary can be built.
        var headerTags = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_header_tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        var legTags = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_leg_tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(headerTags > 0, "no header-level tags");
        Assert.True(legTags > 0, "no posting-level tags — ADR-0103's distinction is untestable");

        // --- and a row that is tagged HEAVILY --------------------------------
        // ADR-0103's TagLine exists for one case and says so: chips are fine at
        // two tags and fall apart at eight long names, when the row grows taller
        // and the amount column stops lining up.
        //
        // A sample where every tagged field carries exactly ONE short tag runs
        // every tag test against the shape the control was NOT built for, and
        // the row-height blow-up it exists for cannot be reproduced from any
        // fixture.
        //
        // Floors, like everything else here: eight is where the ADR says chips
        // fail, and 48 characters is long enough to catch a regeneration that
        // quietly shortens the names. The two levels are checked SEPARATELY — folding
        // leg tags up into the header is the exact bug ADR-0103 removed, and a
        // MAX over the union would pass with one of them empty.
        var widestHeaderTagging = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c FROM txn_header_tags
                WHERE ledger_id = @LedgerId GROUP BY header_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestHeaderTagging >= 8,
            $"no header carries 8+ tags — the case TagLine exists for; widest was {widestHeaderTagging}");

        var widestLegTagging = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c FROM txn_leg_tags
                WHERE ledger_id = @LedgerId GROUP BY leg_id
            ) s;
            """, new { LedgerId = ledgerId });
        Assert.True(widestLegTagging >= 8,
            $"no posting carries 8+ tags — only the header level has the wide case; widest was {widestLegTagging}");

        var longestTagName = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(LENGTH(name)), 0) FROM tags WHERE ledger_id = @LedgerId;",
            new { LedgerId = ledgerId });
        Assert.True(longestTagName >= 48,
            $"longest tag name is {longestTagName} characters — the width TagLine truncates is untested");

        // --- a LOAN, with the terms behind it --------------------------------
        // Without a Moneydance loan account (`type='o'`) in a fixture,
        // `loan_terms` — all of ADR-0050 on the import side — is never written
        // by a test.
        //
        // The row, not just the account: `LoanMapper` returns null and the
        // importer silently seeds NOTHING when principal, rate, payment count
        // or payments-per-year is missing, so asserting the account alone would
        // pass with the mapper never having produced anything.
        var loanAccounts = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE ledger_id = @LedgerId AND account_type = 'loan';",
            new { LedgerId = ledgerId });
        Assert.True(loanAccounts > 0, "no loan account — loan_terms has no path into the fixture");

        // Two loans (the mortgage and the car loan), each with its own terms row.
        var allTerms = (await conn.QueryAsync<(
            string AccountName,
            decimal OriginalPrincipal,
            decimal AnnualInterestRate,
            int PaymentCount,
            int PaymentsPerYear,
            bool PaymentIsComputed,
            decimal? FixedPayment,
            Guid? InterestAccountId,
            DateTime? FirstPaymentDate,
            decimal? EscrowAmount,
            Guid? EscrowAccountId)>(
            """
            SELECT a.name, t.original_principal, t.annual_interest_rate, t.payment_count,
                   t.payments_per_year, t.payment_is_computed, t.fixed_payment,
                   t.interest_account_id, t.first_payment_date,
                   t.escrow_amount, t.escrow_account_id
            FROM loan_terms t JOIN accounts a ON a.id = t.account_id
            WHERE t.ledger_id = @LedgerId;
            """, new { LedgerId = ledgerId })).ToList();
        Assert.Equal(2, allTerms.Count);

        foreach (var terms in allTerms)
        {
            Assert.True(terms.OriginalPrincipal > 0, terms.AccountName);
            Assert.True(terms.AnnualInterestRate > 0,
                "a 0% loan would not exercise the interest leg");
            Assert.True(terms.PaymentCount > 0 && terms.PaymentsPerYear > 0, terms.AccountName);

            // The branch worth having a fixture for: MD sends `monthly_pmt` even
            // when `calc_pmt` says to amortize, and LoanMapper must then store
            // NULL rather than the value it was handed.
            Assert.True(terms.PaymentIsComputed, $"expected an amortizing loan: {terms.AccountName}");
            Assert.Null(terms.FixedPayment);

            // The interest category resolved from an MD uuid to a Coffer id. Left
            // null when the cross-walk misses, which is a silent failure.
            Assert.NotNull(terms.InterestAccountId);
            Assert.NotNull(terms.FirstPaymentDate);
        }

        // Exactly one of them, the mortgage, pays escrow, into the escrow account
        // the export names on it.
        var mortgage = Assert.Single(allTerms, t => t.EscrowAccountId is not null);
        Assert.True(mortgage.EscrowAmount > 0, "the mortgage's escrow payment is not positive");
        Assert.Equal(Ids.Accounts[DemoAccountRoles.MortgageEscrow], mortgage.EscrowAccountId);

        // --- a tax date that is not the posted date -------------------------
        // If `td` equals `dt` on every row, `transacted_at` is
        // populated-but-identical everywhere and tax-year grouping on
        // transactedAt has no data that can distinguish it from grouping on
        // posted_at.
        //
        // The YEAR-CROSSING count is the one that matters. A differing tax date
        // inside one month proves the column round-trips; only one that crosses
        // a period boundary can make two reports disagree.
        var taxDateDiffers = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId AND external_id IS NOT NULL
              AND (posted_at AT TIME ZONE 'UTC')::date <> transacted_at::date;
            """, new { LedgerId = ledgerId });
        Assert.True(taxDateDiffers > 0, "every row's tax date equals its posted date");

        var taxYearCrossing = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId AND external_id IS NOT NULL
              AND EXTRACT(YEAR FROM posted_at AT TIME ZONE 'UTC')
                  <> EXTRACT(YEAR FROM transacted_at);
            """, new { LedgerId = ledgerId });
        Assert.True(taxYearCrossing > 0,
            "no transaction whose tax year differs from its posted year — the only shape "
            + "that can make a tax-year report disagree with a calendar-year one");

        // --- recurring reminders --------------------------------------------
        // Without a reminder in the export, the import's reminder step (ADR-0047)
        // never runs over the Demo.
        //
        // Two rrule branches, chosen because ReminderImportIntegrationTests
        // covers the weekly one and MD's precedence (daily → monthly → weekly →
        // yearly) means each needs the earlier fields clear to be reached at all.
        // The imported reminders only: the examples add two of their own.
        var rrules = (await conn.QueryAsync<string?>(
            "SELECT rrule FROM recurring_transactions WHERE ledger_id = @LedgerId AND external_id IS NOT NULL;",
            new { LedgerId = ledgerId })).ToList();
        Assert.True(rrules.Count >= 2, $"expected >= 2 reminders, found {rrules.Count}");
        Assert.Contains(rrules, r => r is not null && r.StartsWith("FREQ=MONTHLY", StringComparison.Ordinal));
        Assert.Contains(rrules, r => r is not null && r.StartsWith("FREQ=YEARLY", StringComparison.Ordinal));

        // Both halves of auto-commit. MD writes -1 for "manual", which must
        // become NULL rather than being stored as a negative day count.
        var autoCommits = (await conn.QueryAsync<int?>(
            "SELECT auto_commit_days_before FROM recurring_transactions WHERE ledger_id = @LedgerId AND external_id IS NOT NULL;",
            new { LedgerId = ledgerId })).ToList();
        Assert.Contains(autoCommits, a => a is not null and >= 0);
        Assert.Contains(autoCommits, a => a is null);

        // A template is NOT a transaction. The keystone of the balance
        // invariant is that it never reaches a balance row, and a fixture that
        // carries reminders is the only way the Demo's own balances prove it.
        // One template per imported reminder, and one per reminder in all.
        var importedTemplates = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers h
            WHERE h.ledger_id = @LedgerId AND h.is_recurring_template
              AND h.id IN (SELECT r.template_header_id FROM recurring_transactions r
                           WHERE r.ledger_id = @LedgerId AND r.external_id IS NOT NULL);
            """, new { LedgerId = ledgerId });
        Assert.Equal(rrules.Count, importedTemplates);
        var (reminders, templates) = await conn.QuerySingleAsync<(int, int)>(
            """
            SELECT (SELECT COUNT(*) FROM recurring_transactions WHERE ledger_id = @LedgerId),
                   (SELECT COUNT(*) FROM txn_headers WHERE ledger_id = @LedgerId AND is_recurring_template);
            """, new { LedgerId = ledgerId });
        Assert.Equal(reminders, templates);

        // --- a zero-amount posting the USER entered --------------------------
        // Zero-amount investment security legs do not count, because zero cash
        // is structural there. This looks for the ordinary case on a real
        // account: a waived fee, entered so the waiver is on the record.
        //
        // Excludes category legs AND security legs, because either would let
        // the investment rows satisfy it and the assertion would go hollow
        // without failing.
        var zeroOnARealAccount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            JOIN txn_headers h ON h.id = l.header_id
            WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL
              AND l.amount = 0
              AND l.security_id IS NULL
              AND a.account_type NOT IN ('category', 'investment');
            """, new { LedgerId = ledgerId });
        Assert.True(zeroOnARealAccount > 0,
            "no zero-amount posting on a real account — the only 0.00 legs are investment "
            + "security legs, where zero cash is structural rather than a figure anyone entered");

        // --- an account nobody has transacted in -----------------------------
        // The Demo's dormant certificate of deposit carries an opening balance
        // and zero transactions; this pins that the Demo keeps such an account.
        //
        // Non-zero opening balance is the part that bites. An untouched
        // account at 0 proves nothing — every balance path returns 0 for it
        // whether or not it consults the opening balance.
        var untouchedWithBalance = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM accounts a
            WHERE a.ledger_id = @LedgerId AND a.external_id IS NOT NULL
              AND a.account_type NOT IN ('category')
              AND a.opening_balance <> 0
              AND NOT EXISTS (SELECT 1 FROM txn_legs l WHERE l.account_id = a.id);
            """, new { LedgerId = ledgerId });
        Assert.True(untouchedWithBalance > 0,
            "every account with an opening balance has been transacted — nothing exercises "
            + "the path where a balance can only come from the opening figure");

        // --- the feed's own strings ------------------------------------------
        // The importer seeds ol.orig-payee / ol.orig-memo into
        // txn_header_originals where they differ from the curated text, and
        // migration 260 writes the same originals from provider_raw_payload into
        // ledgers imported without them. They have to survive into that payload
        // too: mig 109 keeps the raw row so that a fix to imported data is a SQL
        // migration over it rather than a re-import, and 260 is one.
        var withOrigPayee = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM txn_headers
            WHERE ledger_id = @LedgerId
              AND provider_raw_payload ? 'ol.orig-payee';
            """, new { LedgerId = ledgerId });
        Assert.True(withOrigPayee >= 100,
            $"expected >= 100 headers carrying the feed's own payee string, found {withOrigPayee}");
    }

    [Fact]
    public async Task The_imported_history_carries_every_memo_and_tag_shape_and_keeps_the_placement_rules()
    {
        await using var conn = await _demo.OpenAsync();
        var ledgerId = LedgerId;

        // An event's tags merged past the 20 a user may set: every one kept.
        var widestEvent = await conn.ExecuteScalarAsync<int>("""
            SELECT COALESCE(MAX(c), 0) FROM (
                SELECT COUNT(*) AS c FROM txn_header_tags x JOIN txn_headers h ON h.id = x.header_id
                 WHERE h.ledger_id = @L AND h.external_id IS NOT NULL AND h.action IS NOT NULL GROUP BY x.header_id) s;
            """, new { L = ledgerId });
        Assert.True(widestEvent > 20, $"no event carries more than 20 tags; widest was {widestEvent}");

        // Every line of a split to one category: the symmetric shape.
        var symmetric = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM (
                SELECT l.header_id FROM txn_legs l JOIN accounts a ON a.id = l.account_id
                  JOIN txn_headers h ON h.id = l.header_id
                 WHERE l.ledger_id = @L AND h.external_id IS NOT NULL AND a.account_type = 'category'
                 GROUP BY l.header_id
                HAVING COUNT(DISTINCT l.posting_index) > 1 AND COUNT(DISTINCT l.account_id) = 1
                   AND COUNT(DISTINCT l.posting_index) = (SELECT COUNT(DISTINCT x.posting_index)
                                                            FROM txn_legs x WHERE x.header_id = l.header_id)) s;
            """, new { L = ledgerId });
        Assert.True(symmetric > 0, "no split sends every line to one category");

        // A one-split whose other side keeps a memo of its own.
        var counterpartMemos = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @L AND h.external_id IS NOT NULL
               AND l.account_id <> h.originating_account_id AND l.leg_memo IS NOT NULL
               AND (SELECT COUNT(DISTINCT x.posting_index) FROM txn_legs x WHERE x.header_id = h.id) = 1;
            """, new { L = ledgerId });
        Assert.True(counterpartMemos > 0, "no one-split's other side has a memo of its own");

        // A brokerage-entered cash move (an Xfr owned by the brokerage) beside a
        // bank-entered transfer into it (action-less, owned by the bank).
        var owners = (await conn.QueryAsync<(string? Action, string OwnerType)>("""
            SELECT h.action, a.account_type
              FROM txn_headers h JOIN accounts a ON a.id = h.originating_account_id
             WHERE h.ledger_id = @L AND h.external_id IS NOT NULL AND NOT h.is_recurring_template
               AND (SELECT COUNT(DISTINCT x.posting_index) FROM txn_legs x WHERE x.header_id = h.id) = 1
               AND EXISTS (SELECT 1 FROM txn_legs x JOIN accounts b ON b.id = x.account_id
                            WHERE x.header_id = h.id AND b.holdings_account_id IS NOT NULL)
               AND EXISTS (SELECT 1 FROM txn_legs x JOIN accounts b ON b.id = x.account_id
                            WHERE x.header_id = h.id AND b.account_type = 'bank');
            """, new { L = ledgerId })).ToList();
        Assert.Contains(owners, o => o.Action == "transfer" && o.OwnerType == "investment");
        Assert.Contains(owners, o => o.Action is null && o.OwnerType == "bank");

        // A buy funded from another brokerage, owned by the one that bought.
        var fundedElsewhere = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM txn_headers h
             WHERE h.ledger_id = @L AND h.external_id IS NOT NULL AND h.action = 'buyx'
               AND EXISTS (SELECT 1 FROM txn_legs x JOIN accounts b ON b.id = x.account_id
                            WHERE x.header_id = h.id AND b.holdings_account_id IS NOT NULL
                              AND b.id <> h.originating_account_id);
            """, new { L = ledgerId });
        Assert.True(fundedElsewhere > 0, "no buy funded from another brokerage");

        // One tag per name under the database's key, across every spelling in the file.
        var sharedKeys = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM (
                SELECT tag_name_key(name) FROM tags WHERE ledger_id = @L
                 GROUP BY tag_name_key(name) HAVING COUNT(*) > 1) s;
            """, new { L = ledgerId });
        Assert.Equal(0, sharedKeys);

        // ...and the file really spells one name two ways, each resolving to that
        // one tag; without this the check above holds on a file with no variants.
        var variantsOnOneTag = await conn.ExecuteScalarAsync<int>("""
            WITH spelled AS (
                SELECT DISTINCT btrim(n) AS name
                  FROM txn_headers h,
                       jsonb_each_text(h.provider_raw_payload) f,
                       unnest(string_to_array(f.value, ',')) n
                 WHERE h.ledger_id = @L AND h.provider_raw_payload IS NOT NULL
                   AND (f.key = 'tags' OR f.key ~ '^[0-9]+\.tags$') AND btrim(n) <> '')
            SELECT COUNT(*) FROM (
                SELECT tag_name_key(s.name) AS key FROM spelled s
                 GROUP BY tag_name_key(s.name) HAVING COUNT(*) > 1) v
             WHERE (SELECT COUNT(*) FROM tags t
                     WHERE t.ledger_id = @L AND tag_name_key(t.name) = v.key) = 1;
            """, new { L = ledgerId });
        Assert.True(variantsOnOneTag > 0, "no name is spelled two ways in the file and kept as one tag");

        // The invariants hold over every row, the examples' included: the importer tests'
        // placement rules (one source, MemoPlacementInvariants) and the originating account's.
        Assert.Empty(await MemoPlacementInvariants.ViolationsAsync(conn, ledgerId));
        Assert.Empty(await conn.QueryAsync<Guid>(
            $"SELECT v.header_id FROM ({OriginatingAccountInvariants.ViolationsSql}) v;", new { LedgerId = ledgerId }));
    }

    private static Task<int> DistinctCategoriesAsync(NpgsqlConnection conn, Guid ledgerId, string kind) =>
        conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT a.id)
            FROM txn_legs l
            JOIN accounts a ON a.id = l.account_id
            JOIN txn_headers h ON h.id = l.header_id
            WHERE l.ledger_id = @LedgerId AND h.external_id IS NOT NULL
              AND a.account_type = 'category' AND a.category_kind = @Kind;
            """, new { LedgerId = ledgerId, Kind = kind });

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The whole Demo, added once for <see cref="FullDemoTests"/> through
/// <c>POST /api/ledgers/demo</c> and the job it starts, on a database of the class's own,
/// beside a ledger the install already had.
/// </summary>
/// <remarks>
/// The import is the real one, wrapped only to keep its result for the lossless fact, and
/// the examples phase runs on <see cref="DemoSeederClock"/>, ahead of the real time, so a
/// date it took from the clock rather than from AsOf is seen. The waits allow
/// <see cref="Ceiling"/>, well above what the whole Demo takes; reaching it is a failure,
/// not a measurement. The stored-notes check every test database gets runs before the
/// database is dropped.
/// </remarks>
public sealed class FullDemoFixture : IAsyncLifetime
{
    /// <summary>How long the import, then the job, may take.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(180);

    private readonly PostgresFixture _postgres;
    private readonly IMessageSink _diagnostics;
    private readonly RecordingImport _import = new();
    private TestDatabase? _db;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public FullDemoFixture(PostgresFixture postgres, IMessageSink diagnostics)
    {
        _postgres = postgres;
        _diagnostics = diagnostics;
    }

    internal TestDatabase Db => _db ?? throw new InvalidOperationException("the fixture has not started");

    public HttpClient Client => _client ?? throw new InvalidOperationException("the fixture has not started");

    public Guid LedgerId { get; private set; }

    internal ImportJob Job { get; private set; } = null!;

    internal MoneydanceImportResult ImportResult { get; private set; } = null!;

    internal DemoSeedIds Ids { get; private set; } = null!;

    /// <summary>From the press to the job's end.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>The other ledger's rows per table before the Demo was added, and after.</summary>
    public IReadOnlyDictionary<string, long> OtherLedgerBefore { get; private set; } = new Dictionary<string, long>();

    /// <inheritdoc cref="OtherLedgerBefore"/>
    public IReadOnlyDictionary<string, long> OtherLedgerAfter { get; private set; } = new Dictionary<string, long>();

    public async Task InitializeAsync()
    {
        _db = await _postgres.CreateDatabaseAsync(nameof(FullDemoTests), Log).ConfigureAwait(false);
        await TestDatabaseCluster.SetUpOrTearDownAsync(AddTheDemoAsync, DisposeAsync).ConfigureAwait(false);
    }

    private async Task AddTheDemoAsync()
    {
        _factory = new ApiFactory(Db).WithServices(s =>
        {
            DemoSeederClock.UseFor(s);
            s.RemoveAll<IMoneydanceImportService>();
            s.AddSingleton<IMoneydanceImportService>(_import);
        });
        _client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<ImportJobRegistry>();

        // A ledger the install already had, with its starter categories.
        var created = await _client.PostAsJsonAsync("/api/ledgers", new CreateLedgerRequest { Name = "Household" }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(false)))
        {
            var other = body.RootElement.GetProperty("id").GetGuid();
            OtherLedgerBefore = await RowCountsAsync(other).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            var jobId = await DemoSeedingHarness.PostAsync(_client).ConfigureAwait(false);
            Job = await WaitForEndAsync(registry, jobId).ConfigureAwait(false);
            Elapsed = watch.Elapsed;
            Log(string.Create(CultureInfo.InvariantCulture, $"the full Demo was added in {Elapsed.TotalSeconds:F1} s"));
            OtherLedgerAfter = await RowCountsAsync(other).ConfigureAwait(false);
        }

        Assert.True(Job.State == ImportJobState.Succeeded, Job.Error);
        LedgerId = Job.LedgerId ?? throw new XunitException("the Demo job published no ledger");
        ImportResult = Assert.Single(_import.Results);

        await using var db = Db.NewDbContext();
        Ids = await DemoSeedResolver.ResolveAsync(db, LedgerId, DemoTestExports.Full.Value.Manifest, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>A connection as the service role, for the breadth floors' reads.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(Db.ServiceConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        return conn;
    }

    /// <summary>The job once it leaves running, for at most <see cref="Ceiling"/>.</summary>
    private static async Task<ImportJob> WaitForEndAsync(ImportJobRegistry registry, Guid jobId)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Ceiling)
        {
            var job = registry.Snapshot(jobId) ?? throw new InvalidOperationException("the job is gone");
            if (job.State != ImportJobState.Running) return job;
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new XunitException($"The full Demo job did not finish within {Ceiling.TotalSeconds:F0} s.");
    }

    /// <summary>Rows per table carrying <c>ledger_id</c> = <paramref name="ledgerId"/>.</summary>
    private async Task<IReadOnlyDictionary<string, long>> RowCountsAsync(Guid ledgerId)
    {
        await using var conn = await OpenAsync().ConfigureAwait(false);
        var tables = await conn.QueryAsync<string>(
            """
            SELECT c.table_name FROM information_schema.columns c
              JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             WHERE c.table_schema = 'public' AND c.column_name = 'ledger_id' AND t.table_type = 'BASE TABLE'
             ORDER BY 1;
            """).ConfigureAwait(false);
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
            counts[table] = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM \"{table}\" WHERE ledger_id = @L;", new { L = ledgerId }).ConfigureAwait(false);
        return counts;
    }

    private void Log(string message) => _diagnostics.OnMessage(new DiagnosticMessage("FullDemoFixture: " + message));

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        try { _client?.Dispose(); } catch (Exception e) { failures.Add(e); }
        try { if (_factory is not null) await _factory.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        _client = null;
        _factory = null;
        if (_db is { } db)
        {
            _db = null;
            try
            {
                await using var context = db.NewServiceDbContext();
                await StoredNotesDatabaseCheck.AssertComputedAsync(context, "At the end of FullDemoTests").ConfigureAwait(false);
            }
            catch (Exception e) { failures.Add(e); }
            try { await db.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    /// <summary>The real import, keeping each result.</summary>
    private sealed class RecordingImport : IMoneydanceImportService
    {
        private readonly MoneydanceImportService _inner = new();
        private readonly List<MoneydanceImportResult> _results = [];

        public IReadOnlyList<MoneydanceImportResult> Results
        {
            get { lock (_results) return _results.ToList(); }
        }

        public MoneydanceImportPreview Preview(MdExport export, string? reportingCurrency = null) =>
            _inner.Preview(export, reportingCurrency);

        public async Task<MoneydanceImportResult> ImportAsync(
            NpgsqlConnection connection, MdExport export, string ledgerName, Guid ownerUserId,
            IProgress<ImportProgressUpdate>? progress, CancellationToken cancellationToken,
            Func<NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit = null,
            string currencyCode = Coffer.Importer.Moneydance.Db.LedgersRepository.DefaultCurrencyCode)
        {
            var result = await _inner.ImportAsync(connection, export, ledgerName, ownerUserId, progress,
                cancellationToken, beforeCommit, currencyCode).ConfigureAwait(false);
            lock (_results) _results.Add(result);
            return result;
        }

        public Task<ReconcileResult> ReconcileAsync(
            NpgsqlConnection connection, MdExport export, Guid ownerUserId, Guid? compareLedgerId,
            IReadOnlyList<string> diffTickers, CancellationToken cancellationToken) =>
            _inner.ReconcileAsync(connection, export, ownerUserId, compareLedgerId, diffTickers, cancellationToken);
    }
}
