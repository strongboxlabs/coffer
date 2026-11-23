using System.Data.Common;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The Category amounts finding (ADR-0114 D13: a category line carries the real side's
/// amount negated). A posting of one real line and one category line whose category line
/// is not the real line negated, whatever its signs or zeros, is counted in the verdict and
/// repaired; every other shape with a category line that is not mirrored is listed in the
/// "Amounts with more decimals than their currency" advisory with its reason, and nothing
/// rewrites it.
/// </summary>
/// <remarks>
/// The released Moneydance import is the only writer that kept a category line's own figure
/// beside a paying account in another currency. Its rows are seeded as it left them: a
/// mirrored pair whose category line is then set under the ledger's write lock and its
/// balances recomputed, as the seed helpers do, and a reminder's template line rewritten in
/// place, as a template has no balances. The check and the repair run through the API.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyCategoryLegsTests : DatabaseTest
{
    public ConsistencyCategoryLegsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<LedgerConsistencyReport> CheckAsync(HttpClient client, Guid ledgerId)
    {
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!;
    }

    private static async Task<ProjectionRepairResult> RepairAsync(HttpClient client, Guid ledgerId)
    {
        var resp = await client.PostAsync(
            $"/api/ledgers/{ledgerId}/balances/consistency/{ConsistencyProjections.CategoryAmounts}/repair", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
    }

    private static ProjectionConsistency CategoryAmounts(LedgerConsistencyReport report) =>
        Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.CategoryAmounts);

    private static string Unhealthy(LedgerConsistencyReport report) =>
        string.Join(", ", report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + "=" + p.MismatchedCount));

    /// <summary>Set one stored leg's amount, as the released import could leave a category
    /// line, under the ledger write lock, then recompute its account's balances as the seed
    /// helpers do, so the balances stay in step.</summary>
    private async Task SetLegAmountAsync(SyntheticLedger ledger, Guid legId, decimal amount)
    {
        Guid accountId;
        await using (var db = Db.NewDbContext())
        {
            await using var write = await db.BeginLedgerWriteAsync(ledger.LedgerId);
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == legId);
            leg.Amount = amount;
            accountId = leg.AccountId;
            await db.SaveChangesAsync();
            await write.CommitAsync();
        }
        await ledger.RecomputeBalancesAsync([accountId]);
    }

    private async Task<decimal> LegAmountAsync(Guid legId)
    {
        await using var db = Db.NewServiceDbContext();
        return (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == legId)).Amount;
    }

    /// <summary>
    /// Every one-real, one-category posting whose category line is not the real line negated
    /// is reported, on a live row and on a reminder's template alike, a zero line on either
    /// side and a same-signed pair included, and the verdict is unhealthy. A mirrored posting
    /// and a pair between two real accounts in two currencies are not reported. The repair
    /// sets each reported category line to the real line negated, and the ledger is then
    /// healthy, balances included.
    /// </summary>
    [Fact]
    public async Task Category_lines_out_of_step_are_reported_on_live_rows_and_templates_and_repaired_to_the_real_line_negated()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var eur = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var usd = await ledger.AddBankAccountAsync("Account B", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        async Task<(Guid Header, Guid CategoryLeg)> SeededAsync(decimal real, decimal categoryLine, string payee)
        {
            var (_, categoryLeg) = await ledger.AddTransactionPairAsync(eur.Id, category.Id, real, PostedAt, payee: payee);
            if (categoryLine != -real) await SetLegAmountAsync(ledger, categoryLeg, categoryLine);
            return (await ledger.ResolveHeaderIdAsync(categoryLeg), categoryLeg);
        }

        var kept = await SeededAsync(-10m, 11m, "Payee A");
        var zeroReal = await SeededAsync(0m, 5m, "Payee B");
        var sameSign = await SeededAsync(-10m, -10m, "Payee C");
        var zeroCategory = await SeededAsync(-7m, 0m, "Payee D");
        var mirrored = await SeededAsync(-3m, 3m, "Payee E");
        var (_, farLeg) = await ledger.AddTransactionPairAsync(eur.Id, usd.Id, -10m, PostedAt, payee: "Payee F");
        await SetLegAmountAsync(ledger, farLeg, 11m);
        var realPair = await ledger.ResolveHeaderIdAsync(farLeg);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = new DateOnly(2026, 1, 1),
            Payee = "Payee G",
            SourceAccountId = eur.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -10m }],
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        Guid template, templateLeg;
        await using (var seed = Db.NewServiceDbContext())
        {
            template = (await seed.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId))
                .TemplateHeaderId!.Value;
            templateLeg = (await seed.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == template && l.AccountId == category.Id)).Id;
            await seed.TxnLegs.Where(l => l.Id == templateLeg).ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, 13m));
        }

        var report = await CheckAsync(client, ledger.LedgerId);

        var found = CategoryAmounts(report);
        Assert.False(found.Healthy);
        Assert.False(report.Healthy);
        Assert.Equal((6, 5), (found.Checked, found.MismatchedCount));
        // A live row names its register and transaction, so its line links there. No
        // register shows a reminder's template, so its line names neither and is not a link.
        Assert.Equal(
            new[] { kept.Header, zeroReal.Header, sameSign.Header, zeroCategory.Header }.Order(),
            found.Mismatches.Where(m => m.HeaderId is not null).Select(m => m.HeaderId!.Value).Order());
        var line = Assert.Single(found.Mismatches, m => m.HeaderId == kept.Header);
        Assert.Equal(("Account A · 2026-03-02 · Payee A", 11m, 10m, eur.Id, false),
            (line.Scope, line.Stored, line.Expected, line.AccountId, line.Hidden));
        var templateLine = Assert.Single(found.Mismatches, m => m.HeaderId is null);
        Assert.Equal(("Payee G · reminder", (Guid?)null, false),
            (templateLine.Scope, templateLine.AccountId, templateLine.Hidden));
        Assert.DoesNotContain(found.Mismatches, m => m.HeaderId == mirrored.Header || m.HeaderId == realPair);
        // Every other projection agrees: the seed kept each balance in step.
        Assert.Equal([ConsistencyProjections.CategoryAmounts],
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection).ToList());
        Assert.Empty(report.UnfitAmounts);

        var repaired = await RepairAsync(client, ledger.LedgerId);

        Assert.Equal(5, repaired.Repaired.MismatchedCount);
        Assert.NotNull(repaired.After);
        Assert.True(repaired.After!.Healthy, "unhealthy after the repair: " + Unhealthy(repaired.After));
        Assert.True(Assert.Single(repaired.After.Projections, p => p.Projection == ConsistencyProjections.Balances).Healthy);
        Assert.Equal((10m, 0m, 10m, 7m, 3m, 10m),
            (await LegAmountAsync(kept.CategoryLeg), await LegAmountAsync(zeroReal.CategoryLeg),
             await LegAmountAsync(sameSign.CategoryLeg), await LegAmountAsync(zeroCategory.CategoryLeg),
             await LegAmountAsync(mirrored.CategoryLeg), await LegAmountAsync(templateLeg)));
        Assert.Equal(11m, await LegAmountAsync(farLeg));
        var state = (await client.GetFromJsonAsync<LedgerConsistencyStateDto>(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency/state"))!;
        Assert.True(state.Healthy);
    }

    /// <summary>
    /// A line out of step on a hidden transaction is reported with its register and
    /// transaction and says it is hidden, so its link opens the Hidden view, the one view
    /// that shows it (ADR-0072 D1). One on a transaction merged into another names neither,
    /// as no register view shows it.
    /// </summary>
    [Fact]
    public async Task A_hidden_line_out_of_step_says_so_and_a_merged_away_one_names_no_register()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        DateOnly hiddenDay = new(2026, 3, 2), mergedDay = new(2026, 3, 9);
        await ImportAsync(client, ledger, account.Id, ("CATEGORY-HIDE", hiddenDay, -10m), ("CATEGORY-MERGE", mergedDay, -12m));
        Dictionary<DateOnly, Guid> imported;
        await using (var db = Db.NewDbContext())
            imported = (await db.TxnHeaders.AsNoTracking()
                    .Where(h => h.LedgerId == ledger.LedgerId && h.ExternalId != null)
                    .Select(h => new { h.Id, h.PostedAt })
                    .ToListAsync())
                .ToDictionary(h => DateOnly.FromDateTime(h.PostedAt), h => h.Id);

        // Out of step, then hidden: an imported row is hidden by a delete.
        var hidden = imported[hiddenDay];
        await SetLegAmountAsync(ledger, await CategoryLegAsync(hidden), 11m);
        using (var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{hidden}"))
            Assert.Contains("soft-hidden", await deleted.Content.ReadAsStringAsync());

        // Merged into a row entered by hand, then out of step.
        var entered = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = mergedDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Payee = "Payee B",
            SourceAccountId = account.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12m }],
        });
        Assert.Equal(HttpStatusCode.Created, entered.StatusCode);
        var enteredId = (await entered.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("headerId").GetGuid();
        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{imported[mergedDay]}/merge",
                   new MergeTransactionRequest { FromHeaderId = enteredId }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
        Guid mergedAway;
        await using (var db = Db.NewDbContext())
            mergedAway = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId && h.IsMergedInto != null)
                .Select(h => h.Id)
                .SingleAsync();
        await SetLegAmountAsync(ledger, await CategoryLegAsync(mergedAway), 13m);

        var found = CategoryAmounts(await CheckAsync(client, ledger.LedgerId));

        Assert.Equal(2, found.MismatchedCount);
        var hiddenLine = Assert.Single(found.Mismatches, m => m.Stored == 11m);
        Assert.Equal((account.Id, hidden, true), (hiddenLine.AccountId, hiddenLine.HeaderId, hiddenLine.Hidden));
        var mergedLine = Assert.Single(found.Mismatches, m => m.Stored == 13m);
        Assert.Equal(((Guid?)null, (Guid?)null, false), (mergedLine.AccountId, mergedLine.HeaderId, mergedLine.Hidden));
        Assert.StartsWith("Account A · ", mergedLine.Scope);
    }

    /// <summary>The leg of <paramref name="headerId"/> on a category.</summary>
    private async Task<Guid> CategoryLegAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Join(db.Accounts.AsNoTracking(), l => l.AccountId, a => a.Id, (l, a) => new { l.Id, a.AccountType })
            .Where(l => l.AccountType == "category")
            .Select(l => l.Id)
            .SingleAsync();
    }

    /// <summary>Import <paramref name="rows"/> onto <paramref name="accountId"/> from an OFX
    /// statement, as a person imports a bank's file.</summary>
    private static async Task ImportAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, params (string FitId, DateOnly On, decimal Amount)[] rows)
    {
        const string AccountNumber = "CATEGORY-1";
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(Transactions.TransactionEntriesEndpointTests.OfxBankStatement(
            AccountNumber, [.. rows.Select(r => (r.FitId, r.On, r.Amount, "PAYEE A"))])));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ofx");
        content.Add(file, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent($"{Transactions.TransactionEntriesEndpointTests.BankNumber}:{AccountNumber}"), "providerAccountId");
        using var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A posting between two categories whose lines are not each other's negation is listed
    /// on each category, in that category's own currency (ADR-0114 D15), with its reason; a
    /// posting with more than one category line beside its real line is listed on the real
    /// line's account. Neither is counted in the verdict, and the repair rewrites neither.
    /// Each is listed once per transaction and account, with its reason, though a line of
    /// each also has more places than its currency.
    /// </summary>
    [Fact]
    public async Task Shapes_the_repair_leaves_are_listed_with_their_reason_and_left_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var eur = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var paying = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category D");
        var from = await ledger.AddCategoryAsync("Category B", currencyCode: "EUR");
        var to = await ledger.AddCategoryAsync("Category C", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var (_, toLeg) = await ledger.AddTransactionPairAsync(from.Id, to.Id, -10m, PostedAt, payee: "Payee A");
        // A fractional line in a whole-unit currency.
        await SetLegAmountAsync(ledger, toLeg, 12.5m);
        var between = await ledger.ResolveHeaderIdAsync(toLeg);

        // A posting of one real line and two category lines: the second split line's category
        // line moved onto the first posting, which then does not sum to zero. Its real line has
        // one place more than its currency.
        var (_, split) = await ledger.AddMultiSplitAsync(eur.Id, [(paying.Id, -6.005m), (second.Id, -4m)], PostedAt, payee: "Payee B");
        Guid movedLeg;
        await using (var db = Db.NewDbContext())
        {
            await using var write = await db.BeginLedgerWriteAsync(ledger.LedgerId);
            var leg = await db.TxnLegs.SingleAsync(l => l.HeaderId == split && l.AccountId == second.Id);
            leg.PostingIndex = 0;
            movedLeg = leg.Id;
            await db.SaveChangesAsync();
            await write.CommitAsync();
        }
        await ledger.RecomputeBalancesAsync([eur.Id, paying.Id, second.Id]);
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeNotesAsync(split);

        var report = await CheckAsync(client, ledger.LedgerId);

        var found = CategoryAmounts(report);
        Assert.True(found.Healthy, "reported: " + string.Join(", ", found.Mismatches.Select(m => m.Scope)));
        Assert.True(report.Healthy, "unhealthy: " + Unhealthy(report));        var listed = report.UnfitAmounts.Where(r => r.HeaderId == between)
            .Select(r => (r.Kind, r.AccountId, r.Account, r.CurrencyCode, r.Places, r.Reason))
            .OrderBy(r => r.Account, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                (UnfitAmountKinds.Transaction, from.Id, "Category B", "EUR", 2, (string?)UnfitAmountReasons.CategoryLineBetweenCategories),
                (UnfitAmountKinds.Transaction, to.Id, "Category C", "JPY", 0, (string?)UnfitAmountReasons.CategoryLineBetweenCategories),
            },
            listed);
        var many = Assert.Single(report.UnfitAmounts, r => r.HeaderId == split);
        Assert.Equal((eur.Id, "EUR", UnfitAmountReasons.CategoryLineManyLines),
            (many.AccountId, many.CurrencyCode, many.Reason));

        var repaired = await RepairAsync(client, ledger.LedgerId);

        Assert.Equal(0, repaired.Repaired.MismatchedCount);
        Assert.Equal((12.5m, 4m), (await LegAmountAsync(toLeg), await LegAmountAsync(movedLeg)));
        Assert.Equal(3, (await CheckAsync(client, ledger.LedgerId)).UnfitAmounts.Count);
    }

    /// <summary>
    /// A shape the repair leaves, on a transaction merged into another, is not listed in the
    /// advisory: no screen shows or opens the row, so its line would be a dead end, as for
    /// the places the advisory lists (decided by the owner, 2026-11-17; ADR-0114 D15). The
    /// same shape on a live row is listed.
    /// </summary>
    /// <remarks>Each posting of one real line and two category lines is seeded as the
    /// released import left one, its second line moved onto the first posting. The merged
    /// one is awaiting review, as a promote-on-clear leaves a merge winner whose loser
    /// cleared at another figure: only such a row folds into another. The merge runs through
    /// the API.</remarks>
    [Fact]
    public async Task A_shape_the_repair_leaves_on_a_merged_away_row_is_not_listed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var eur = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var paying = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        async Task<Guid> ManyLinesAsync(string payee)
        {
            var (_, split) = await ledger.AddMultiSplitAsync(eur.Id, [(paying.Id, -6m), (second.Id, -4m)], PostedAt, payee: payee);
            await using (var db = Db.NewDbContext())
            {
                await using var write = await db.BeginLedgerWriteAsync(ledger.LedgerId);
                var leg = await db.TxnLegs.SingleAsync(l => l.HeaderId == split && l.AccountId == second.Id);
                leg.PostingIndex = 0;
                await db.SaveChangesAsync();
                await write.CommitAsync();
            }
            await ledger.RecomputeBalancesAsync([eur.Id, paying.Id, second.Id]);
            await ledger.RecomputePostingCountsAsync();
            await ledger.RecomputeNotesAsync(split);
            return split;
        }

        var live = await ManyLinesAsync("Payee A");
        var loser = await ManyLinesAsync("Payee B");
        var (winnerLeg, _) = await ledger.AddTransactionPairAsync(eur.Id, paying.Id, -10m, PostedAt, payee: "Payee C");
        var winner = await ledger.ResolveHeaderIdAsync(winnerLeg);
        await using (var seed = Db.NewServiceDbContext())
        {
            await seed.TxnHeaders.Where(h => h.Id == loser)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true));
        }
        // Before the merge both shapes are listed.
        Assert.Equal(2, (await CheckAsync(client, ledger.LedgerId)).UnfitAmounts
            .Count(r => r.Reason == UnfitAmountReasons.CategoryLineManyLines));

        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
                   new MergeTransactionRequest { FromHeaderId = winner }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        var report = await CheckAsync(client, ledger.LedgerId);

        // Anchored on the live shape, listed in the same response.
        var listed = Assert.Single(report.UnfitAmounts, r => r.HeaderId == live);
        Assert.Equal((eur.Id, UnfitAmountReasons.CategoryLineManyLines), (listed.AccountId, listed.Reason));
        Assert.DoesNotContain(report.UnfitAmounts, r => r.HeaderId == loser);
        Assert.True(CategoryAmounts(report).Healthy);
    }

    /// <summary>
    /// An edit committed after the check has counted a posting's shape and before it reads
    /// that posting's lines is judged from the lines it read: a split line out of step that
    /// the edit removed, or rewrote as the real line negated, is not reported, and the check
    /// answers.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_posting_an_edit_changes_while_the_check_runs_is_judged_from_the_lines_it_read(bool keepsTheLine)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var eur = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        var first = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");
        var (origins, header) = await ledger.AddMultiSplitAsync(
            eur.Id, [(first.Id, -6m), (second.Id, -4m)], PostedAt, payee: "Payee A");
        Guid secondLeg;
        await using (var db = Db.NewServiceDbContext())
        {
            secondLeg = (await db.TxnLegs.AsNoTracking()
                .SingleAsync(l => l.HeaderId == header && l.AccountId == second.Id)).Id;
        }
        await SetLegAmountAsync(ledger, secondLeg, 5m);

        // The edit lands just before the check reads the lines of the postings it counted.
        var edit = new BeforeFirstCommand(
            "t.header_id AS \"HeaderId\", t.posting_index AS \"PostingIndex\", t.account_id AS \"AccountId\", t.amount AS \"Amount\"");
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(edit)));
        using var client = await ClientAsync(factory, ledger);
        await WaitForStartPassAsync(ledger.LedgerId);

        HttpStatusCode? patched = null;
        edit.Action = async () =>
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
                $"/api/ledgers/{ledger.LedgerId}/transactions/{header}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest
                {
                    Postings = new PatchTransactionPostings
                    {
                        SourceAccountId = eur.Id,
                        Items = keepsTheLine
                            ?
                            [
                                new TransactionPosting { LegId = origins[0], CounterpartyAccountId = first.Id, Amount = -6m },
                                new TransactionPosting { LegId = origins[1], CounterpartyAccountId = second.Id, Amount = -3m },
                            ]
                            : [new TransactionPosting { LegId = origins[0], CounterpartyAccountId = first.Id, Amount = -10m }],
                    },
                }),
            });
            patched = response.StatusCode;
        };
        edit.Arm();

        using var checkedNow = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency", content: null);

        Assert.True(edit.Fired, "the check never read the counted postings' lines, so nothing was tested");
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.True(checkedNow.StatusCode == HttpStatusCode.OK,
            $"the check answered {(int)checkedNow.StatusCode}: {await checkedNow.Content.ReadAsStringAsync()}");
        var found = CategoryAmounts((await checkedNow.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!);
        Assert.DoesNotContain(found.Mismatches, m => m.HeaderId == header);
        Assert.True(found.Healthy, "reported: " + string.Join(", ", found.Mismatches.Select(m => m.Scope)));
    }

    /// <summary>Block until the monitor's start pass has recorded a verdict for this ledger,
    /// so the pass is not the check an armed interceptor catches.</summary>
    private async Task WaitForStartPassAsync(Guid ledgerId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using (var db = Db.NewDbContext())
            {
                if (await db.LedgerEvents.AsNoTracking()
                        .AnyAsync(e => e.LedgerId == ledgerId && ConsistencyVerdicts.VerdictKeys.Contains(e.EventKey)))
                    return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The monitor's start pass never recorded a verdict for this ledger.");
    }

    /// <summary>Once armed, runs <see cref="Action"/> before the first command whose text
    /// contains <paramref name="marker"/>, then lets that command run.</summary>
    private sealed class BeforeFirstCommand(string marker) : DbCommandInterceptor
    {
        private int _armed;

        public Func<Task>? Action { get; set; }

        public bool Fired { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                await Action!();
            }
            return result;
        }
    }
}
