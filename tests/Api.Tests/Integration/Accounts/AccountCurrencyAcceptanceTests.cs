using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// An account's currency must be an ISO 4217 currency Coffer accepts: one with an exponent
/// of at most 3 places (ADR-0114 D15). A code that is not one, an "N.A." entry and the two
/// four-place fund units are refused on create and on a change. A code stored before the
/// rule keeps working when an edit does not change it. A category keeps its own rule (the
/// reporting currency), and a price currency is not checked by this refusal.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountCurrencyAcceptanceTests : DatabaseTest
{
    public AccountCurrencyAcceptanceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Invalid = "account-currency-invalid";

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, SyntheticLedger ledger, string accountType, string? currencyCode, string? categoryKind = null) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = $"Account {Guid.NewGuid():N}",
            AccountType = accountType,
            CategoryKind = categoryKind,
            CurrencyCode = currencyCode,
        });

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, UpdateAccountRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}")
        {
            Content = JsonContent.Create(body),
        });

    private async Task<string> StoredCurrencyAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.CurrencyCode).SingleAsync();
    }

    [Theory]
    [InlineData("CLF")] // a fund unit with four places
    [InlineData("UYW")] // the other four-place fund unit
    [InlineData("XAU")] // an "N.A." entry: no exponent
    [InlineData("ABC")] // three letters, not on the list
    public async Task A_new_account_in_a_code_coffer_does_not_accept_is_refused(string code)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(Invalid, await CodeAsync(await CreateAsync(client, ledger, "bank", code)));
        Assert.Equal(Invalid, await CodeAsync(await CreateAsync(client, ledger, "investment", code)));

        await using var db = Db.NewDbContext();
        Assert.False(await db.Accounts.AsNoTracking().AnyAsync(a => a.LedgerId == ledger.LedgerId));
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("usd")]
    public async Task A_new_account_in_an_accepted_code_of_any_places_is_created(string code)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await CreateAsync(client, ledger, "bank", code);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_stored_code_coffer_no_longer_accepts_keeps_working_until_it_is_changed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        // A code the released create took, which checked only that it had three letters.
        var account = await ledger.AddBankAccountAsync("Account A", currencyCode: "XAU");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, ledger, account.Id, new UpdateAccountRequest { Name = "Account B" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, ledger, account.Id, new UpdateAccountRequest { Name = "Account C", CurrencyCode = "XAU" })).StatusCode);
        Assert.Equal("XAU", await StoredCurrencyAsync(account.Id));

        // A change to another code Coffer does not accept is refused, and nothing is written.
        Assert.Equal(Invalid, await CodeAsync(
            await PatchAsync(client, ledger, account.Id, new UpdateAccountRequest { Name = "Account D", CurrencyCode = "CLF" })));
        Assert.Equal("XAU", await StoredCurrencyAsync(account.Id));
        await using var db = Db.NewDbContext();
        Assert.Equal("Account C", await db.Accounts.AsNoTracking().Where(a => a.Id == account.Id).Select(a => a.Name).SingleAsync());
    }

    [Fact]
    public async Task An_accepted_account_cannot_be_changed_to_a_code_coffer_does_not_accept()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Assert.Equal(Invalid, await CodeAsync(
            await PatchAsync(client, ledger, account.Id, new UpdateAccountRequest { CurrencyCode = "XAU" })));
        Assert.Equal("USD", await StoredCurrencyAsync(account.Id));
    }

    [Fact]
    public async Task A_category_takes_a_stored_reporting_code_coffer_no_longer_accepts()
    {
        // A category holds no money, so it keeps its own rule: the reporting currency,
        // whatever it is.
        var ledger = await SyntheticLedger.CreateAsync(Db, currencyCode: "XAU");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await CreateAsync(client, ledger, "category", null, "expense");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var named = await CreateAsync(client, ledger, "category", "XAU", "expense");
        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
    }

    [Fact]
    public async Task A_price_currency_is_not_held_to_the_account_rule()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var security = await ledger.AddSecurityAsync("Security A", ticker: "SECA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{security}/prices",
            new CreateSecurityPriceRequest { Price = 10m, PriceDate = new DateOnly(2026, 3, 2), CurrencyCode = "CLF" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
