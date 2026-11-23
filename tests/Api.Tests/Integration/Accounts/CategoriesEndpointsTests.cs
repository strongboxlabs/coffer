using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// Category-management HTTP surface (Slice A): the
/// <c>/api/ledgers/{ledgerId}/categories</c> endpoints — list-with-usage,
/// reparent, merge, delete. The underlying repository semantics (legs moved,
/// children reparented, cycle/kind guards, delete-gate) are proven at the repo
/// level in <see cref="McpWriteSurfaceTests"/>; these tests pin the HTTP
/// contract instead — routing, request/response DTO binding, the
/// result-enum → <see cref="BusinessError"/> code mapping, and the
/// ledger-visibility gate on every handler — plus end-to-end coverage of the
/// net-new usage-list read. Atomic per-test ledger; shared-table reads are
/// scoped by the test's ledger id (via the URL and the LedgerId filter).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CategoriesEndpointsTests : DatabaseTest
{
    public CategoriesEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static DateTime Utc(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    // ----- list with usage -----------------------------------------------------

    [Fact]
    public async Task List_returns_categories_with_hierarchy_and_usage_counts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var parent = await ledger.AddCategoryAsync("Auto", "expense");
        var child = await ledger.AddCategoryAsync("Fuel", "expense", parentId: parent.Id);
        var income = await ledger.AddCategoryAsync("Salary", "income");
        await ledger.AddTransactionPairAsync(bank.Id, parent.Id, -40m, Utc(2));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -10m, Utc(3));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -11m, Utc(4));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -12m, Utc(5));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var list = (await client.GetFromJsonAsync<CategoryListDto>(
            $"/api/ledgers/{ledger.LedgerId}/categories"))!.Categories;

        var p = Assert.Single(list!, c => c.Id == parent.Id);
        Assert.Equal("Auto", p.Name);
        Assert.Equal("expense", p.CategoryKind);
        Assert.Null(p.ParentId);
        Assert.Equal(1, p.TransactionCount);   // one posting directly on the parent
        Assert.Equal(1, p.ChildCount);          // Fuel
        // Raw signed sum of the category's own legs. The pair posts the amount
        // negative on the bank leg, so the category leg is its positive offset
        // (expense nets positive).
        Assert.Equal(40m, p.Total);

        var c = Assert.Single(list!, x => x.Id == child.Id);
        Assert.Equal(parent.Id, c.ParentId);
        Assert.Equal(3, c.TransactionCount);
        Assert.Equal(0, c.ChildCount);
        Assert.Equal(33m, c.Total);             // the three child postings, summed

        var inc = Assert.Single(list!, x => x.Id == income.Id);
        Assert.Equal("income", inc.CategoryKind);
        Assert.Equal(0, inc.TransactionCount);
        Assert.Equal(0, inc.ChildCount);
        Assert.Equal(0m, inc.Total);
    }

    [Fact]
    public async Task List_excludes_inactive_by_default_and_includes_with_flag()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var active = await ledger.AddCategoryAsync("Active", "expense");
        var inactive = await ledger.AddCategoryAsync("Retired", "expense");
        await ledger.SetIsActiveAsync(inactive.Id, false);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/categories";

        var activeOnly = (await client.GetFromJsonAsync<CategoryListDto>(baseUrl))!.Categories;
        Assert.Contains(activeOnly, c => c.Id == active.Id);
        Assert.DoesNotContain(activeOnly, c => c.Id == inactive.Id);

        var all = (await client.GetFromJsonAsync<CategoryListDto>($"{baseUrl}?includeInactive=true"))!.Categories;
        Assert.Contains(all, c => c.Id == active.Id);
        var retired = Assert.Single(all, c => c.Id == inactive.Id);
        Assert.False(retired.IsActive);
    }

    /// <summary>
    /// A category has no currency of its own (ADR-0109 D9): the list names the ledger's
    /// reporting currency (ADR-0114 D7), the one the budget month carries, and not the
    /// code stored on a category's row. The accounts' currencies do not move it.
    /// </summary>
    [Fact]
    public async Task List_names_the_ledgers_reporting_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        await ledger.AddInvestmentAccountAsync("Brokerage", currencyCode: "EUR");
        var dividends = await ledger.AddCategoryAsync("Dividends", "income");
        Assert.Equal("USD", dividends.CurrencyCode);   // the stored code the list must not use

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/categories";

        var single = (await client.GetFromJsonAsync<CategoryListDto>(url))!;
        Assert.Contains(single.Categories, c => c.Id == dividends.Id);
        Assert.Equal("EUR", single.CurrencyCode);

        var budget = (await client.GetFromJsonAsync<BudgetProgressDto>(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month=2026-01&today=2026-01-15"))!;
        Assert.Equal(budget.CurrencyCode, single.CurrencyCode);

        await ledger.AddBankAccountAsync("Checking");   // USD beside the EUR brokerage
        var after = (await client.GetFromJsonAsync<CategoryListDto>(url))!;
        Assert.Equal("EUR", after.CurrencyCode);
    }

    // ----- reparent ------------------------------------------------------------

    [Fact]
    public async Task Reparent_moves_under_parent_then_back_to_top_level()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var parent = await ledger.AddCategoryAsync("Home", "expense");
        var moving = await ledger.AddCategoryAsync("Repairs", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/categories";

        var under = await client.PatchAsJsonAsync(
            $"{baseUrl}/{moving.Id}/parent", new ReparentCategoryRequest(parent.Id));
        Assert.Equal(HttpStatusCode.NoContent, under.StatusCode);

        await using (var read = Db.NewDbContext())
            Assert.Equal(parent.Id, (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == moving.Id)).ParentId);

        var top = await client.PatchAsJsonAsync(
            $"{baseUrl}/{moving.Id}/parent", new ReparentCategoryRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, top.StatusCode);

        await using (var read = Db.NewDbContext())
            Assert.Null((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == moving.Id)).ParentId);
    }

    [Fact]
    public async Task Reparent_rejects_a_cycle()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var parent = await ledger.AddCategoryAsync("Parent", "expense");
        var child = await ledger.AddCategoryAsync("Child", "expense", parentId: parent.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Moving the parent under its own descendant would close a loop.
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{parent.Id}/parent",
            new ReparentCategoryRequest(child.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryCycle, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Reparent_rejects_a_non_category_parent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}/parent",
            new ReparentCategoryRequest(bank.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountParentInvalid, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Reparent_rejects_when_target_is_not_a_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{bank.Id}/parent",
            new ReparentCategoryRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotACategory, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Reparent_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        var aliceCat = await alice.AddCategoryAsync("Alice Only", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.PatchAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/categories/{aliceCat.Id}/parent",
            new ReparentCategoryRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));
    }

    // ----- merge ---------------------------------------------------------------

    [Fact]
    public async Task Merge_moves_transactions_and_children_and_returns_counts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Groceries (old)", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var child = await ledger.AddCategoryAsync("Snacks", "expense", parentId: source.Id);
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -50m, Utc(6));
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -20m, Utc(7));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
            new MergeCategoryRequest(target.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(2, body.TransactionsMoved);
        Assert.Equal(1, body.ChildrenReparented);
        Assert.False(body.DryRun);

        await using var read = Db.NewDbContext();
        Assert.Equal(0, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.Equal(2, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == target.Id));
        Assert.Equal(target.Id, (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == child.Id)).ParentId);
        Assert.False((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_dry_run_reports_counts_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("A", "expense");
        var target = await ledger.AddCategoryAsync("B", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -10m, Utc(8));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
            new MergeCategoryRequest(target.Id, DryRun: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(1, body.TransactionsMoved);
        Assert.True(body.DryRun);

        await using var read = Db.NewDbContext();
        // Untouched: leg still on the source, source still active.
        Assert.Equal(1, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.True((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_may_cross_kinds()
    {
        // ADR-0017. Flipping a category's kind in place is the same
        // reclassification as a cross-kind merge, done silently to the category's
        // entire history with no counts and no way back. So the flip is refused
        // and the merge is the one sanctioned way to reclassify; refusing this too
        // would leave no route at all.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var expense = await ledger.AddCategoryAsync("Exp", "expense");
        var income = await ledger.AddCategoryAsync("Inc", "income");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{expense.Id}/merge",
            new MergeCategoryRequest(income.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // And the source is deactivated rather than destroyed — which is what
        // makes the reclassification reversible, and the flip's key advantage
        // over it imaginary.
        await using var read = Db.NewDbContext();
        Assert.False((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == expense.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_rejects_merging_into_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var cat = await ledger.AddCategoryAsync("Solo", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}/merge",
            new MergeCategoryRequest(cat.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryMergeSelf, await ErrorCodeAsync(resp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_refuses_a_target_beneath_the_source_and_changes_nothing(bool dryRun)
    {
        // The source's children are reparented to the target. A child that IS the target, or that
        // contains it, would end up beneath itself: a loop in the tree no read walks out of. The
        // refusal comes before anything is counted, so a preview refuses too.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Auto", "expense");
        var child = await ledger.AddCategoryAsync("Fuel", "expense", parentId: source.Id);
        var grandchild = await ledger.AddCategoryAsync("Premium", "expense", parentId: child.Id);
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -10m, Utc(8));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        foreach (var target in new[] { child.Id, grandchild.Id })
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
                new MergeCategoryRequest(target, DryRun: dryRun));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Equal(BusinessError.Codes.CategoryCycle, await ErrorCodeAsync(resp));
        }

        await using var read = Db.NewDbContext();
        var parents = await read.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledger.LedgerId && a.AccountType == "category")
            .ToDictionaryAsync(a => a.Id, a => a.ParentId);
        Assert.Null(parents[source.Id]);
        Assert.Equal(source.Id, parents[child.Id]);
        Assert.Equal(child.Id, parents[grandchild.Id]);
        Assert.Equal(1, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.True((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_into_an_ancestor_or_a_sibling_is_still_allowed()
    {
        // The guard names descendants only. Upward and sideways merges are how a taxonomy gets
        // tidied, and they must keep working.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var parent = await ledger.AddCategoryAsync("Auto", "expense");
        var child = await ledger.AddCategoryAsync("Fuel", "expense", parentId: parent.Id);
        var sibling = await ledger.AddCategoryAsync("Parking", "expense", parentId: parent.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var up = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{child.Id}/merge", new MergeCategoryRequest(parent.Id));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var sideways = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{sibling.Id}/merge", new MergeCategoryRequest(parent.Id));
        Assert.Equal(HttpStatusCode.OK, sideways.StatusCode);
    }

    [Fact]
    public async Task Merge_echoes_the_budget_targets_it_moved()
    {
        // The repository reports them (ADR-0099); the endpoint used to leave them out, so a
        // caller could not tell that a merge had touched a number somebody typed.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Groceries (old)", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(source.Id, "2026-04", 200m))).StatusCode);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge", new MergeCategoryRequest(target.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(1, body.BudgetTargetsMoved);
        Assert.Equal(0, body.BudgetTargetsDropped);
    }

    [Fact]
    public async Task Merge_echoes_a_target_it_dropped_and_a_preview_reports_none()
    {
        // Dropped is the count a caller most needs: a number somebody typed is gone. The child sits
        // under the source, which holds no target, and the destination holds one for the same
        // month; both were legal until the merge put the child beneath the destination.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Utilities", "expense");
        var child = await ledger.AddCategoryAsync("Electric", "expense", parentId: source.Id);
        var target = await ledger.AddCategoryAsync("Home", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        foreach (var (category, amount) in new[] { (child.Id, 90m), (target.Id, 400m) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/budget/targets",
                new SetBudgetTargetRequest(category, "2026-04", amount))).StatusCode);
        }
        var url = $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge";

        // A preview does not read the targets, and changes nothing.
        var preview = await client.PostAsJsonAsync(url, new MergeCategoryRequest(target.Id, DryRun: true));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = (await preview.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(0, previewBody.BudgetTargetsMoved);
        Assert.Equal(0, previewBody.BudgetTargetsDropped);
        await using (var read = Db.NewDbContext())
            Assert.Equal(2, await read.BudgetTargets.CountAsync(t => t.LedgerId == ledger.LedgerId));

        var resp = await client.PostAsJsonAsync(url, new MergeCategoryRequest(target.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(0, body.BudgetTargetsMoved);
        Assert.Equal(1, body.BudgetTargetsDropped);
    }

    [Fact]
    public async Task Merge_rejects_target_outside_the_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Source", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
            new MergeCategoryRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotInLedger, await ErrorCodeAsync(resp));
    }

    // ----- delete --------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_an_unused_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var cat = await ledger.AddCategoryAsync("Disposable", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var read = Db.NewDbContext();
        Assert.False(await read.Accounts.AnyAsync(a => a.Id == cat.Id));
    }

    [Fact]
    public async Task Delete_rejects_a_category_with_transactions()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Used", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -5m, Utc(9));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryInUse, await ErrorCodeAsync(resp));

        await using var read = Db.NewDbContext();
        Assert.True(await read.Accounts.AnyAsync(a => a.Id == cat.Id));   // preserved
    }

    [Fact]
    public async Task Delete_rejects_a_category_with_children()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var parent = await ledger.AddCategoryAsync("Parent", "expense");
        await ledger.AddCategoryAsync("Child", "expense", parentId: parent.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{parent.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryInUse, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Delete_rejects_a_non_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{bank.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotACategory, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Delete_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);
        var aliceCat = await alice.AddCategoryAsync("Alice Only", "expense");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.DeleteAsync(
            $"/api/ledgers/{alice.LedgerId}/categories/{aliceCat.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        // And Alice's category is untouched.
        await using var read = Db.NewDbContext();
        Assert.True(await read.Accounts.AnyAsync(a => a.Id == aliceCat.Id));
    }
}
