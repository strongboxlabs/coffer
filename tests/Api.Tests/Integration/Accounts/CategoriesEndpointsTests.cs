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
public sealed class CategoriesEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public CategoriesEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var parent = await ledger.AddCategoryAsync("Auto", "expense");
        var child = await ledger.AddCategoryAsync("Fuel", "expense", parentId: parent.Id);
        var income = await ledger.AddCategoryAsync("Salary", "income");
        await ledger.AddTransactionPairAsync(bank.Id, parent.Id, -40m, Utc(2));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -10m, Utc(3));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -11m, Utc(4));
        await ledger.AddTransactionPairAsync(bank.Id, child.Id, -12m, Utc(5));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var list = await client.GetFromJsonAsync<List<CategoryNode>>(
            $"/api/ledgers/{ledger.LedgerId}/categories");

        var p = Assert.Single(list!, c => c.Id == parent.Id);
        Assert.Equal("Auto", p.Name);
        Assert.Equal("expense", p.CategoryKind);
        Assert.Null(p.ParentId);
        Assert.Equal(1, p.TransactionCount);   // one posting directly on the parent
        Assert.Equal(1, p.ChildCount);          // Fuel
        // Raw signed sum of the category's own legs. The pair posts -40 on the
        // bank leg, so the category leg is its +40 offset (expense nets positive).
        Assert.Equal(40m, p.Total);

        var c = Assert.Single(list!, x => x.Id == child.Id);
        Assert.Equal(parent.Id, c.ParentId);
        Assert.Equal(3, c.TransactionCount);
        Assert.Equal(0, c.ChildCount);
        Assert.Equal(33m, c.Total);             // 10 + 11 + 12

        var inc = Assert.Single(list!, x => x.Id == income.Id);
        Assert.Equal("income", inc.CategoryKind);
        Assert.Equal(0, inc.TransactionCount);
        Assert.Equal(0, inc.ChildCount);
        Assert.Equal(0m, inc.Total);
    }

    [Fact]
    public async Task List_excludes_inactive_by_default_and_includes_with_flag()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var active = await ledger.AddCategoryAsync("Active", "expense");
        var inactive = await ledger.AddCategoryAsync("Retired", "expense");
        await ledger.SetIsActiveAsync(inactive.Id, false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/categories";

        var activeOnly = await client.GetFromJsonAsync<List<CategoryNode>>(baseUrl);
        Assert.Contains(activeOnly!, c => c.Id == active.Id);
        Assert.DoesNotContain(activeOnly!, c => c.Id == inactive.Id);

        var all = await client.GetFromJsonAsync<List<CategoryNode>>($"{baseUrl}?includeInactive=true");
        Assert.Contains(all!, c => c.Id == active.Id);
        var retired = Assert.Single(all!, c => c.Id == inactive.Id);
        Assert.False(retired.IsActive);
    }

    // ----- reparent ------------------------------------------------------------

    [Fact]
    public async Task Reparent_moves_under_parent_then_back_to_top_level()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var parent = await ledger.AddCategoryAsync("Home", "expense");
        var moving = await ledger.AddCategoryAsync("Repairs", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/categories";

        var under = await client.PatchAsJsonAsync(
            $"{baseUrl}/{moving.Id}/parent", new ReparentCategoryRequest(parent.Id));
        Assert.Equal(HttpStatusCode.NoContent, under.StatusCode);

        await using (var read = _fixture.NewDbContext())
            Assert.Equal(parent.Id, (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == moving.Id)).ParentId);

        var top = await client.PatchAsJsonAsync(
            $"{baseUrl}/{moving.Id}/parent", new ReparentCategoryRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, top.StatusCode);

        await using (var read = _fixture.NewDbContext())
            Assert.Null((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == moving.Id)).ParentId);
    }

    [Fact]
    public async Task Reparent_rejects_a_cycle()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var parent = await ledger.AddCategoryAsync("Parent", "expense");
        var child = await ledger.AddCategoryAsync("Child", "expense", parentId: parent.Id);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceCat = await alice.AddCategoryAsync("Alice Only", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("Groceries (old)", "expense");
        var target = await ledger.AddCategoryAsync("Groceries", "expense");
        var child = await ledger.AddCategoryAsync("Snacks", "expense", parentId: source.Id);
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -50m, Utc(6));
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -20m, Utc(7));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
            new MergeCategoryRequest(target.Id));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(2, body.TransactionsMoved);
        Assert.Equal(1, body.ChildrenReparented);
        Assert.False(body.DryRun);

        await using var read = _fixture.NewDbContext();
        Assert.Equal(0, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.Equal(2, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == target.Id));
        Assert.Equal(target.Id, (await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == child.Id)).ParentId);
        Assert.False((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_dry_run_reports_counts_without_writing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var source = await ledger.AddCategoryAsync("A", "expense");
        var target = await ledger.AddCategoryAsync("B", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, source.Id, -10m, Utc(8));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{source.Id}/merge",
            new MergeCategoryRequest(target.Id, DryRun: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = (await resp.Content.ReadFromJsonAsync<MergeCategoryResponse>())!;
        Assert.Equal(1, body.TransactionsMoved);
        Assert.True(body.DryRun);

        await using var read = _fixture.NewDbContext();
        // Untouched: leg still on the source, source still active.
        Assert.Equal(1, await read.TxnLegs.CountAsync(l => l.LedgerId == ledger.LedgerId && l.AccountId == source.Id));
        Assert.True((await read.Accounts.AsNoTracking().FirstAsync(a => a.Id == source.Id)).IsActive);
    }

    [Fact]
    public async Task Merge_rejects_kind_mismatch()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var expense = await ledger.AddCategoryAsync("Exp", "expense");
        var income = await ledger.AddCategoryAsync("Inc", "income");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{expense.Id}/merge",
            new MergeCategoryRequest(income.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryKindMismatch, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Merge_rejects_merging_into_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var cat = await ledger.AddCategoryAsync("Solo", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}/merge",
            new MergeCategoryRequest(cat.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryMergeSelf, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Merge_rejects_target_outside_the_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddCategoryAsync("Source", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var cat = await ledger.AddCategoryAsync("Disposable", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.Accounts.AnyAsync(a => a.Id == cat.Id));
    }

    [Fact]
    public async Task Delete_rejects_a_category_with_transactions()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Used", "expense");
        await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -5m, Utc(9));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{cat.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryInUse, await ErrorCodeAsync(resp));

        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Accounts.AnyAsync(a => a.Id == cat.Id));   // preserved
    }

    [Fact]
    public async Task Delete_rejects_a_category_with_children()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var parent = await ledger.AddCategoryAsync("Parent", "expense");
        await ledger.AddCategoryAsync("Child", "expense", parentId: parent.Id);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{parent.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.CategoryInUse, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Delete_rejects_a_non_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/categories/{bank.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountNotACategory, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Delete_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceCat = await alice.AddCategoryAsync("Alice Only", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.DeleteAsync(
            $"/api/ledgers/{alice.LedgerId}/categories/{aliceCat.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        // And Alice's category is untouched.
        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Accounts.AnyAsync(a => a.Id == aliceCat.Id));
    }
}
