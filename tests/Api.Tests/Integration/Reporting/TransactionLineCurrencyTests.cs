using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Every transaction line names the currency its amount is in: its own account's, or on a
/// category's line the currency of the account on the posting's other side. In MCP
/// <c>list_transactions</c>, with no account, with a real account and with a category, and
/// in the budget drill-down, where lines of accounts in different currencies sit in one
/// list.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionLineCurrencyTests : DatabaseTest
{
    public TransactionLineCurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_line_on_an_account_in_another_currency_carries_that_accounts_code()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var categoryA = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");

        // An account in another currency, made the way a person makes one.
        const string OtherCurrency = "EUR";
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Account B", AccountType = "bank", CurrencyCode = OtherCurrency });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var accountB = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Anchor: the two accounts' currencies differ, as GET /accounts reads them.
        using (var listed = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledger.LedgerId}/accounts")))
        {
            string CodeOf(Guid id) => listed.RootElement.EnumerateArray()
                .Single(a => a.GetProperty("id").GetGuid() == id).GetProperty("currencyCode").GetString()!;
            Assert.Equal(OtherCurrency, CodeOf(accountB));
            Assert.NotEqual(OtherCurrency, CodeOf(accountA.Id));
        }
        var homeCurrency = accountA.CurrencyCode;

        // One row from each account to the same category, in one month.
        const decimal Amount = -5m;
        var posted = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var onA = await CreateAsync(client, ledger.LedgerId, accountA.Id, categoryA.Id, Amount, posted);
        var onB = await CreateAsync(client, ledger.LedgerId, accountB, categoryA.Id, Amount, posted);

        // MCP list_transactions, as a connected app's bearer runs it: the app role, RLS on.
        await using var appDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var repository = new ReportingRepository(appDb, TimeProvider.System);
        var ledgerWide = (await ReportingTools.ListTransactions(repository, ledger.LedgerId)).Lines;
        Assert.Equal(homeCurrency, Assert.Single(ledgerWide, l => l.HeaderId == onA).CurrencyCode);
        Assert.Equal(OtherCurrency, Assert.Single(ledgerWide, l => l.HeaderId == onB).CurrencyCode);
        var onAccountB = (await ReportingTools.ListTransactions(repository, ledger.LedgerId, accountId: accountB)).Lines;
        Assert.Equal(OtherCurrency, Assert.Single(onAccountB, l => l.HeaderId == onB).CurrencyCode);

        // With the category as the account in scope, each line is the category's own leg,
        // and its amount is the posting's: in the currency of the account that paid it,
        // not the category's code.
        var onCategory = (await ReportingTools.ListTransactions(repository, ledger.LedgerId, accountId: categoryA.Id)).Lines;
        Assert.Equal(2, onCategory.Count);
        Assert.All(onCategory, l => Assert.Equal(categoryA.Id, l.AccountId));
        var categoryLineFromB = Assert.Single(onCategory, l => l.HeaderId == onB);
        Assert.Equal(-Amount, categoryLineFromB.Amount);
        Assert.Equal(OtherCurrency, categoryLineFromB.CurrencyCode);
        Assert.Equal(homeCurrency, Assert.Single(onCategory, l => l.HeaderId == onA).CurrencyCode);

        // The budget drill-down for the category's month, over the wire.
        using var drill = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/transactions?month=2026-02&categoryId={categoryA.Id}"));
        var drilled = drill.RootElement.GetProperty("lines").EnumerateArray()
            .ToDictionary(l => l.GetProperty("headerId").GetGuid(), l => l.GetProperty("currencyCode").GetString());
        Assert.Equal(homeCurrency, drilled[onA]);
        Assert.Equal(OtherCurrency, drilled[onB]);
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client, Guid ledgerId, Guid sourceAccountId, Guid categoryId, decimal amount, DateTime postedAt)
    {
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = "Payee A",
                SourceAccountId = sourceAccountId,
                Postings = [new TransactionPosting { CounterpartyAccountId = categoryId, Amount = amount }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }
}
