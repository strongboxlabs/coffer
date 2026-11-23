using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Reporting;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// The sidebar's totals, <c>GET …/accounts/subtotals</c> (ADR-0106 D6): a section
/// heading's and a folder's figure combine accounts, so each is in the reporting currency,
/// every account converted at the latest rate, summed, rounded once (ADR-0114 D7), and a
/// missing rate leaves it null with the pair named, never a sum of the accounts that
/// converted (ADR-0114 D9). A type's total is the overview's subtotal for it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountSubtotalsEndpointTests : DatabaseTest
{
    public AccountSubtotalsEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record Seed(
        SyntheticLedger Ledger, Guid Home, Guid Abroad, Guid Closed, Guid Outer, Guid Inner, Guid Empty);

    /// <summary>
    /// A USD ledger. Account A (in dollars), the inactive Account C (in dollars) and, in a
    /// sub-folder, Account B (in euros) sit in Folder A; Folder C is empty; Card A (a dollar
    /// debt) is the only credit card.
    /// </summary>
    private async Task<(Seed Seed, HttpClient Client, ApiFactory Factory)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var home = await ledger.AddAccountAsync("Account A", "bank", openingBalance: 100m);
        var abroad = await ledger.AddBankAccountAsync("Account B", openingBalance: 40m, currencyCode: "EUR");
        var closed = await ledger.AddAccountAsync("Account C", "bank", openingBalance: 7m);
        await ledger.SetIsActiveAsync(closed.Id, false);
        await ledger.AddAccountAsync("Card A", "credit_card", openingBalance: -20m);

        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        var outer = await CreateFolderAsync(client, ledger.LedgerId, "Folder A", null);
        var inner = await CreateFolderAsync(client, ledger.LedgerId, "Folder B", outer);
        var empty = await CreateFolderAsync(client, ledger.LedgerId, "Folder C", null);
        await FileAsync(client, ledger.LedgerId, home.Id, outer);
        await FileAsync(client, ledger.LedgerId, closed.Id, outer);
        await FileAsync(client, ledger.LedgerId, abroad.Id, inner);
        return (new Seed(ledger, home.Id, abroad.Id, closed.Id, outer, inner, empty), client, factory);
    }

    private static async Task<Guid> CreateFolderAsync(HttpClient client, Guid ledgerId, string name, Guid? parentId)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/account-folders",
            new CreateAccountFolderRequest { AccountType = "bank", Name = name, ParentId = parentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountFolderDto>())!.Id;
    }

    private static async Task FileAsync(HttpClient client, Guid ledgerId, Guid accountId, Guid folderId)
    {
        var response = await client.PutAsJsonAsync($"/api/ledgers/{ledgerId}/accounts/{accountId}/folder",
            new SetAccountFolderRequest { FolderId = folderId });
        Assert.True(response.IsSuccessStatusCode, $"filing the account answered {response.StatusCode}");
    }

    private static async Task<AccountSubtotalsDto> SubtotalsAsync(HttpClient client, Guid ledgerId)
    {
        var response = await client.GetAsync($"/api/ledgers/{ledgerId}/accounts/subtotals");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountSubtotalsDto>())!;
    }

    /// <summary>EUR to USD at 1.5 yesterday, covered through yesterday: the latest rate.</summary>
    private Task SeedRateAsync(SyntheticLedger ledger)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        return BenchmarkSeed.SeedRatesAsync(Db, ledger, "EUR", "USD", yesterday, yesterday, yesterday, _ => 1.5m);
    }

    [Fact]
    public async Task A_section_and_a_folder_convert_every_account_beneath_them_at_the_latest_rate()
    {
        var (seed, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;
        await SeedRateAsync(seed.Ledger);

        var subtotals = await SubtotalsAsync(client, seed.Ledger.LedgerId);

        Assert.Equal("USD", subtotals.CurrencyCode);
        var types = subtotals.Types.ToDictionary(t => t.AccountType);
        // Account A's dollars, Account B's euros at the rate, and the inactive account's
        // dollars (ADR-0085).
        Assert.Equal(167m, types["bank"].Amount);
        Assert.Empty(types["bank"].RateUnavailable);
        Assert.Equal(-20m, types["credit_card"].Amount);

        var folders = subtotals.Folders.ToDictionary(f => f.FolderId);
        // Folder A holds Account A, the inactive Account C, and Account B through Folder B.
        // An inactive account counts in its folder's total and is listed among the accounts
        // the total covers, since the rail matches the folder on every account in it,
        // inactive ones included (ADR-0106 D6).
        Assert.Equal(167m, folders[seed.Outer].Amount);
        Assert.Equal(new[] { seed.Home, seed.Closed, seed.Abroad }.Order(), folders[seed.Outer].AccountIds.Order());
        Assert.Equal(60m, folders[seed.Inner].Amount);
        Assert.Equal([seed.Abroad], folders[seed.Inner].AccountIds);
        Assert.Equal(0m, folders[seed.Empty].Amount);
        Assert.Empty(folders[seed.Empty].AccountIds);

        // A type's total is the overview's subtotal for it.
        var overview = (await client.GetFromJsonAsync<LedgerOverviewDto>($"/api/ledgers/{seed.Ledger.LedgerId}/overview"))!;
        Assert.Equal(
            overview.AccountGroups.Select(g => (g.AccountType, g.Subtotal)).Order(),
            subtotals.Types.Select(t => (t.AccountType, t.Amount)).Order());
    }

    [Fact]
    public async Task A_missing_rate_withholds_every_total_that_needs_it_and_names_it()
    {
        var (seed, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;

        var subtotals = await SubtotalsAsync(client, seed.Ledger.LedgerId);

        var types = subtotals.Types.ToDictionary(t => t.AccountType);
        var bank = types["bank"];
        Assert.Null(bank.Amount);
        var why = Assert.Single(bank.RateUnavailable);
        Assert.Equal(("EUR", "USD"), (why.FromCurrency, why.ToCurrency));
        // A total that needs no EUR is still there.
        Assert.Equal(-20m, types["credit_card"].Amount);

        var folders = subtotals.Folders.ToDictionary(f => f.FolderId);
        Assert.Null(folders[seed.Outer].Amount);
        Assert.Equal([why], folders[seed.Outer].RateUnavailable);
        Assert.Null(folders[seed.Inner].Amount);
        Assert.Equal(0m, folders[seed.Empty].Amount);
    }

    [Fact]
    public async Task A_ledger_that_is_not_visible_is_refused()
    {
        var (seed, client, factory) = await SeedAsync();
        await using var _ = factory;
        using var __ = client;
        var other = await SyntheticLedger.CreateAsync(Db);

        var response = await client.GetAsync($"/api/ledgers/{other.LedgerId}/accounts/subtotals");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ledger-not-visible", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEqual(seed.Ledger.LedgerId, other.LedgerId);
    }
}
