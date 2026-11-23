using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// What a save hands back for the register it was made from: every entry the surviving
/// header has in that account, in the order and grouping the page shows them, so the
/// register can replace that header's rows in place; nothing when it has none there.
/// </summary>
/// <remarks>
/// Covers every route that answers with the saved entries: the bank and investment
/// PATCH, approve and merge.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SaveResponseEntriesTests : DatabaseTest
{
    public SaveResponseEntriesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The body every save route answers with.</summary>
    private sealed record SavedEntries(List<RegisterEntryDto> Entries);

    [Fact]
    public async Task A_symmetric_split_saved_from_its_category_returns_each_of_its_lines_there_in_page_order()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catC = await ledger.AddCategoryAsync("Category C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await CreateBankAsync(client, ledger, bank.Id, Day(3, 1), "Payee Before", (catA.Id, -1m));
        // The symmetric shape: every line goes to Category A, so the category carries a
        // leg in every posting just as the bank does. Only the bank, the account it was
        // entered from, groups it; Category A's register shows each line as its own entry.
        var split = await CreateBankAsync(client, ledger, bank.Id, Day(3, 2), "Payee Split",
            (catA.Id, -5m), (catA.Id, -7m), (catA.Id, -3m));
        await CreateBankAsync(client, ledger, bank.Id, Day(3, 3), "Payee After", (catA.Id, -2m));

        var url = $"/api/ledgers/{ledger.LedgerId}/transactions/{split}";
        var fromCategory = await SavedAsync(await client.PatchAsJsonAsync($"{url}?account_id={catA.Id}", new { payee = "Payee Renamed" }));
        var categoryPage = await PageAsync(client, ledger, catA.Id);
        var expected = categoryPage.Entries.Where(e => HeaderOf(e) == split).ToList();
        Assert.Equal(3, expected.Count);
        Assert.All(expected, e => Assert.Equal(RegisterEntryDto.KindTxn, e.Kind));
        Assert.Equal(expected.Select(Describe), fromCategory.Entries.Select(Describe));
        Assert.All(fromCategory.Entries, e => Assert.Equal("Payee Renamed", e.Txn!.Payee));

        // From the bank: the split's one entry, its three lines.
        var fromBank = await SavedAsync(await client.PatchAsJsonAsync($"{url}?account_id={bank.Id}", new { payee = "Payee Split" }));
        var bankEntry = Assert.Single(fromBank.Entries);
        Assert.Equal(Describe(Assert.Single((await PageAsync(client, ledger, bank.Id)).Entries, e => HeaderOf(e) == split)),
            Describe(bankEntry));
        Assert.Equal(3, bankEntry.Legs!.Count);

        // An account the header has no entry in, or none named: nothing to replace.
        var elsewhere = await client.PatchAsJsonAsync($"{url}?account_id={catC.Id}", new { payee = "Payee Split" });
        Assert.Equal(HttpStatusCode.NoContent, elsewhere.StatusCode);
        var unnamed = await client.PatchAsJsonAsync(url, new { payee = "Payee Split" });
        Assert.Equal(HttpStatusCode.NoContent, unnamed.StatusCode);
    }

    [Fact]
    public async Task Approve_merge_and_an_investment_save_return_the_surviving_headers_entries()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var catA = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var reviewed = await FeedRowAsync(ledger, bank.Id, catA.Id, Day(3, 1), -21m);
        var bankLoser = await FeedRowAsync(ledger, bank.Id, catA.Id, Day(3, 2), -17m);
        var brokerageReviewed = await FeedRowAsync(ledger, brokerage.Id, catA.Id, Day(3, 3), -30m);
        var brokerageLoser = await FeedRowAsync(ledger, brokerage.Id, catA.Id, Day(3, 4), -40m);
        var bankWinner = await CreateBankAsync(client, ledger, bank.Id, Day(3, 2), "Payee Winner", (catA.Id, -17m));
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = LedgerActions.Buy, SecurityId = security, Shares = 2m, Price = 20m,
            PostedAt = Day(3, 4), Payee = "Payee Buy",
        });

        var api = $"/api/ledgers/{ledger.LedgerId}";
        async Task AssertSurvivorAsync(HttpResponseMessage response, Guid survivor, Guid account)
        {
            var saved = await SavedAsync(response);
            var page = await PageAsync(client, ledger, account);
            Assert.Equal(page.Entries.Where(e => HeaderOf(e) == survivor).Select(Describe), saved.Entries.Select(Describe));
            Assert.NotEmpty(saved.Entries);
        }

        await AssertSurvivorAsync(await client.PostAsync($"{api}/transactions/{reviewed}/approve?account_id={bank.Id}", null),
            reviewed, bank.Id);
        await AssertSurvivorAsync(await client.PostAsJsonAsync($"{api}/transactions/{bankLoser}/merge?account_id={bank.Id}",
            new MergeTransactionRequest { FromHeaderId = bankWinner }), bankWinner, bank.Id);
        await AssertSurvivorAsync(await client.PostAsync(
            $"{api}/investment-transactions/{brokerageReviewed}/approve?account_id={brokerage.Id}", null),
            brokerageReviewed, brokerage.Id);
        await AssertSurvivorAsync(await client.PostAsJsonAsync(
            $"{api}/investment-transactions/{brokerageLoser}/merge?account_id={brokerage.Id}",
            new MergeTransactionRequest { FromHeaderId = buy }), buy, brokerage.Id);
        await AssertSurvivorAsync(await client.PatchAsJsonAsync($"{api}/investment-transactions/{buy}?account_id={brokerage.Id}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, PostedAt = Day(3, 4), Action = LedgerActions.Buy, Payee = "Payee Bought",
                SecurityId = security, Shares = 2m, Price = 20m,
            }), buy, brokerage.Id);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static async Task<SavedEntries> SavedAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("entries").ValueKind);
        return (await response.Content.ReadFromJsonAsync<SavedEntries>())!;
    }

    private static Guid HeaderOf(RegisterEntryDto e) => e.Txn?.HeaderId ?? e.Legs![0].HeaderId;

    /// <summary>An entry's kind, key and rows, in order.</summary>
    private static string Describe(RegisterEntryDto e) =>
        $"{e.Kind}:{e.GroupId}:{string.Join(",", (e.Txn is { } t ? [t] : e.Legs!).Select(r => $"{r.Id}/{r.Payee}"))}";

    /// <summary>A bank-feed row waiting for review, as ingest writes one: one posting against a category.</summary>
    private async Task<Guid> FeedRowAsync(SyntheticLedger ledger, Guid account, Guid category, DateTime on, decimal amount)
    {
        var id = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(id, ledger.LedgerId, account, origin: "file_import", externalId: $"imp-{id:N}");
            header.ProviderKey = "qif";
            header.Payee = "Payee Feed";
            header.PostedAt = on;
            header.TransactedAt = on;
            header.NeedsReview = true;
            db.TxnHeaders.Add(header);
            db.TxnLegs.AddRange(
                new TxnLegRow { Id = Guid.NewGuid(), HeaderId = id, LedgerId = ledger.LedgerId, AccountId = account, PostingIndex = 0, Amount = amount },
                new TxnLegRow { Id = Guid.NewGuid(), HeaderId = id, LedgerId = ledger.LedgerId, AccountId = category, PostingIndex = 0, Amount = -amount });
            await db.SaveChangesAsync();
        }
        await ledger.RecomputePostingCountsAsync();
        await ledger.RecomputeNotesAsync(id);
        await ledger.RecomputeBalancesAsync([account, category]);
        return id;
    }

    private static async Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceAccountId, DateTime postedAt, string payee,
        params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                SourceAccountId = sourceAccountId,
                Postings = postings.Select(p => new TransactionPosting
                {
                    CounterpartyAccountId = p.Counterparty, Amount = p.Amount,
                }).ToArray(),
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<RegisterPage> PageAsync(HttpClient client, SyntheticLedger ledger, Guid accountId)
    {
        var resp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=500");
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<RegisterPage>())!;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }
}
