using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The budget-target write surface, over HTTP.
///
/// <para>The repository suite pins the D1a invariant itself. What these pin is the
/// layer above it, none of which the repository tests touch: that the routes are
/// mapped at all, that the wire format is a MONTH rather than a date, that a
/// refusal arrives as the right business code with the conflicting category NAMED
/// in the detail, and that a fill reports what it skipped instead of failing
/// whole or dropping entries silently.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BudgetTargetEndpointTests
{
    private readonly PostgresFixture _fixture;

    public BudgetTargetEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<(HttpClient Client, ApiFactory Factory)> ClientForAsync(SyntheticLedger ledger)
    {
        var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return (client, factory);
    }

    [Fact]
    public async Task Putting_a_target_shows_up_in_the_next_progress_read()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await ledger.AddCategoryAsync("Food");
        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(food.Id, "2026-04", 250m));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // Read it back through the SAME surface the screen uses, not the table.
        var progress = await client.GetFromJsonAsync<BudgetProgressDto>(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month=2026-04");
        var row = Assert.Single(progress!.Rows, r => r.CategoryId == food.Id);
        Assert.Equal(250m, row.Target);
        Assert.Equal(250m, row.Mark);
    }

    [Fact]
    public async Task A_conflicting_target_is_refused_and_the_detail_names_the_other_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);
        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(taxes.Id, "2026-04", 1000m))).StatusCode);

        var blocked = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(federal.Id, "2026-04", 600m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("budget-target-ancestor-conflict", body);
        // The name is the actionable half. Without it the UI can only say "no".
        Assert.Contains("Taxes", body);
    }

    [Fact]
    public async Task Deleting_is_idempotent_and_returns_the_row_to_its_normal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food");
        foreach (var m in new[] { 1, 2, 3 })
            await ledger.AddTransactionPairAsync(
                bank.Id, food.Id, -90m, new DateTime(2026, m, 1, 0, 30, 0, DateTimeKind.Utc), payee: "f");

        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        await client.PutAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(food.Id, "2026-04", 40m));

        var url = $"/api/ledgers/{ledger.LedgerId}/budget/targets/{food.Id}?month=2026-04";
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url)).StatusCode);
        // Again, on a row that is already gone. "No target" is a legitimate
        // destination, so a caller clearing a clear cell has got what it asked for.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url)).StatusCode);

        var progress = await client.GetFromJsonAsync<BudgetProgressDto>(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month=2026-04");
        var row = Assert.Single(progress!.Rows, r => r.CategoryId == food.Id);
        Assert.Null(row.Target);
        Assert.Equal(90m, row.Mark);   // back to the derived normal
    }

    [Fact]
    public async Task A_fill_applies_what_it_can_and_reports_what_it_skipped()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);
        var food = await ledger.AddCategoryAsync("Food");
        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets/fill",
            new FillBudgetTargetsRequest("2026-04", new[]
            {
                new BudgetTargetEntry(federal.Id, 600m),   // child first, deliberately
                new BudgetTargetEntry(taxes.Id, 1000m),
                new BudgetTargetEntry(food.Id, 250m),
            }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<FillBudgetTargetsResponse>())!;

        Assert.Equal(2, result.Applied);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(federal.Id, skipped.CategoryId);
        Assert.Equal("ancestor-has-target", skipped.Status);
        Assert.Equal("Taxes", skipped.ConflictingCategoryName);
    }

    [Fact]
    public async Task A_negative_target_is_refused_before_it_reaches_the_column_check()
    {
        // The CHECK would catch it, but as a 23514 surfacing through the pipeline —
        // which is not an answer a UI can render. 422, not 400: BusinessError.Problem
        // has a single status and every refusal in this app arrives that way.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await ledger.AddCategoryAsync("Food");
        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        var resp = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(food.Id, "2026-04", -5m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("budget-range-invalid", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_wire_format_is_a_month_and_a_date_is_refused()
    {
        // Accepting "2026-04-17" would silently pick a bucket the caller did not
        // name. The column CHECK only permits the first of a month, so a client
        // sending a date is confused about the contract and should hear so.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await ledger.AddCategoryAsync("Food");
        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        var resp = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(food.Id, "2026-04-17", 100m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("yyyy-MM", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_inline_transaction_list_covers_the_whole_subtree()
    {
        // The budget table rolls up, so a row's figure already contains its
        // descendants. A list of the row's DIRECT postings would not add up to
        // the number the reader just clicked, which is the question they are
        // asking by expanding it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food");
        var groceries = await ledger.AddCategoryAsync("Groceries", parentId: food.Id);
        var wine = await ledger.AddCategoryAsync("Wine", parentId: groceries.Id);
        var other = await ledger.AddCategoryAsync("Fuel");

        var when = new DateTime(2026, 4, 3, 0, 30, 0, DateTimeKind.Utc);
        await ledger.AddTransactionPairAsync(bank.Id, food.Id, -10m, when, payee: "direct");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -20m, when, payee: "child");
        await ledger.AddTransactionPairAsync(bank.Id, wine.Id, -30m, when, payee: "grandchild");
        await ledger.AddTransactionPairAsync(bank.Id, other.Id, -40m, when, payee: "unrelated");
        // Right categories, wrong month.
        await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -50m, new DateTime(2026, 5, 3, 0, 30, 0, DateTimeKind.Utc),
            payee: "next month");

        var (client, factory) = await ClientForAsync(ledger);
        await using var _ = factory;
        using var __ = client;

        var page = await client.GetFromJsonAsync<TransactionLinesResult>(
            $"/api/ledgers/{ledger.LedgerId}/budget/transactions"
            + $"?month=2026-04&categoryId={food.Id}");

        var payees = page!.Lines.Select(l => l.Payee).OrderBy(x => x).ToList();
        // Direct, child and GRANDCHILD — the walk is not one level deep.
        Assert.Equal(new[] { "child", "direct", "grandchild" }, payees);
    }

    [Fact]
    public async Task Another_users_ledger_is_not_writable()
    {
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var stranger = await SyntheticLedger.CreateAsync(_fixture);
        var food = await mine.AddCategoryAsync("Food");

        // Authenticated as the stranger, writing at MY ledger's id.
        var (client, factory) = await ClientForAsync(stranger);
        await using var _ = factory;
        using var __ = client;

        var resp = await client.PutAsJsonAsync(
            $"/api/ledgers/{mine.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(food.Id, "2026-04", 250m));

        Assert.True(
            resp.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound
                or HttpStatusCode.Forbidden,
            $"expected the write to be refused, got {(int)resp.StatusCode}");
    }
}
