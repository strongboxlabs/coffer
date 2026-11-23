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
/// A category has no currency of its own (ADR-0109 D9): its lines carry the paying
/// account's. So a category the API creates, by <c>POST /accounts</c> or as a new ledger's
/// starter tree, holds the ledger's reporting currency, never a hard-coded USD that would
/// read as foreign on a ledger reported in another; and a create or edit that names another
/// currency for a category is refused with <c>category-currency-not-reporting</c>.
/// </summary>
/// <remarks>
/// Every row is written through the API's own writers on a ledger whose reporting currency
/// is EUR, so a USD default would show.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CategoryCurrencyTests : DatabaseTest
{
    public CategoryCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return client;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static Task<HttpResponseMessage> CreateCategoryAsync(
        HttpClient client, SyntheticLedger ledger, string name, string? currencyCode) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = name, AccountType = "category", CategoryKind = "expense", CurrencyCode = currencyCode,
        });

    private static HttpRequestMessage Patch(SyntheticLedger ledger, Guid accountId, object body) =>
        new(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}") { Content = JsonContent.Create(body) };

    private async Task<string> StoredCurrencyAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.CurrencyCode).SingleAsync();
    }

    [Fact]
    public async Task A_category_created_without_a_code_takes_the_reporting_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        using var resp = await CreateCategoryAsync(client, ledger, "Category A", currencyCode: null);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = (await resp.Content.ReadFromJsonAsync<AccountSummary>())!;
        Assert.Equal("EUR", created.CurrencyCode);
        Assert.Equal("EUR", await StoredCurrencyAsync(created.Id));

        // The reporting currency itself is accepted, in any case, as the one rule normalizes it.
        using var named = await CreateCategoryAsync(client, ledger, "Category B", currencyCode: "eur");
        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
        Assert.Equal("EUR", (await named.Content.ReadFromJsonAsync<AccountSummary>())!.CurrencyCode);
    }

    [Fact]
    public async Task A_category_create_naming_another_currency_is_refused_and_writes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        using var resp = await CreateCategoryAsync(client, ledger, "Category A", currencyCode: "USD");
        Assert.Equal(BusinessError.Codes.CategoryCurrencyNotReporting, await CodeOf(resp));

        await using var db = Db.NewDbContext();
        Assert.False(await db.Accounts.AnyAsync(a => a.LedgerId == ledger.LedgerId && a.Name == "Category A"));

        // A real account keeps its own currency, as before.
        using var bank = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Account A", AccountType = "bank", CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.Created, bank.StatusCode);
        Assert.Equal("USD", (await bank.Content.ReadFromJsonAsync<AccountSummary>())!.CurrencyCode);
    }

    [Fact]
    public async Task A_category_edit_naming_another_currency_is_refused_and_the_reporting_one_is_accepted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        using var created = await CreateCategoryAsync(client, ledger, "Category A", currencyCode: null);
        var category = (await created.Content.ReadFromJsonAsync<AccountSummary>())!;

        using var refused = await client.SendAsync(Patch(ledger, category.Id, new { currencyCode = "USD" }));
        Assert.Equal(BusinessError.Codes.CategoryCurrencyNotReporting, await CodeOf(refused));
        // Refused whole: the rename beside it is not applied either.
        using var withName = await client.SendAsync(Patch(ledger, category.Id, new { name = "Category B", currencyCode = "GBP" }));
        Assert.Equal(BusinessError.Codes.CategoryCurrencyNotReporting, await CodeOf(withName));
        await using (var db = Db.NewDbContext())
        {
            var row = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == category.Id);
            Assert.Equal(("Category A", "EUR"), (row.Name, row.CurrencyCode));
        }

        using var accepted = await client.SendAsync(Patch(ledger, category.Id, new { name = "Category B", currencyCode = "EUR" }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        // What the SPA's rename sends: a name alone.
        using var rename = await client.SendAsync(Patch(ledger, category.Id, new { name = "Category C" }));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal("EUR", await StoredCurrencyAsync(category.Id));

        // A real account's edit into another currency is still allowed.
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "EUR");
        using var moved = await client.SendAsync(Patch(ledger, bank.Id, new { currencyCode = "USD" }));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("USD", await StoredCurrencyAsync(bank.Id));
    }

    [Fact]
    public async Task A_new_ledgers_starter_categories_take_its_reporting_currency()
    {
        var owner = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, owner);

        using var resp = await client.PostAsJsonAsync("/api/ledgers",
            new { name = "Ledger B", currencyCode = "eur", seedDefaultCategories = true });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var ledger = (await resp.Content.ReadFromJsonAsync<LedgerSummary>())!;

        await using var db = Db.NewDbContext();
        var codes = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledger.Id && a.AccountType == "category")
            .Select(a => a.CurrencyCode)
            .ToListAsync();
        Assert.True(codes.Count >= 40);
        Assert.All(codes, c => Assert.Equal("EUR", c));
    }
}
