using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Every transaction line names its own leg, so a split's lines, which share their
/// header and their account, can be told apart: in MCP <c>list_transactions</c>, with and
/// without an account, and in the budget drill-down, which keys its rows by line.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionLineLegIdTests : DatabaseTest
{
    public TransactionLineLegIdTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_splits_lines_carry_their_own_distinct_legs_in_the_listing_and_the_budget_drill()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var categoryA = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");

        // A split whose two lines both go to one category: same header, same account.
        const decimal LineOne = -6m, LineTwo = -4m;
        var posted = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = posted,
                Payee = "Payee A",
                SourceAccountId = accountA.Id,
                Postings =
                [
                    new TransactionPosting { CounterpartyAccountId = categoryA.Id, Amount = LineOne },
                    new TransactionPosting { CounterpartyAccountId = categoryA.Id, Amount = LineTwo },
                ],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // The split's legs on Account A: what each line must name.
        HashSet<Guid> legsOnA;
        await using (var db = Db.NewDbContext())
        {
            legsOnA = [.. await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId && l.AccountId == accountA.Id)
                .Select(l => l.Id)
                .ToListAsync()];
        }
        Assert.Equal(2, legsOnA.Count);

        // MCP list_transactions, as a connected app's bearer runs it: the app role, RLS on.
        await using var appDb = Db.NewAppDbContextAsUser(ledger.UserId);
        var repository = new ReportingRepository(appDb, TimeProvider.System);
        foreach (var lines in new[]
                 {
                     (await ReportingTools.ListTransactions(repository, ledger.LedgerId, accountId: accountA.Id)).Lines,
                     (await ReportingTools.ListTransactions(repository, ledger.LedgerId)).Lines,
                 })
        {
            var ofSplit = lines.Where(l => l.HeaderId == headerId).ToList();
            Assert.Equal(2, ofSplit.Count);
            Assert.All(ofSplit, l => Assert.Equal(accountA.Id, l.AccountId));
            Assert.Equal(legsOnA, ofSplit.Select(l => l.LegId).ToHashSet());
        }

        // The budget drill-down for the category's month, over the wire.
        using var drill = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/transactions?month=2026-02&categoryId={categoryA.Id}"));
        var drilled = drill.RootElement.GetProperty("lines").EnumerateArray()
            .Where(l => l.GetProperty("headerId").GetGuid() == headerId)
            .ToList();
        Assert.Equal(2, drilled.Count);
        Assert.Equal(legsOnA, drilled.Select(l => l.GetProperty("legId").GetGuid()).ToHashSet());
    }
}
