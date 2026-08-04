using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>POST /api/ledgers/{ledgerId}/transactions/balances?account_id=...</c>
/// — the bulk-balance lookup that powers the SPA's in-place register
/// refresh on save. Returns one row per requested header that has a
/// balance row on the specified account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HeaderBalancesEndpointTests
{
    private readonly PostgresFixture _fixture;

    public HeaderBalancesEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Returns_balance_after_and_net_amount_for_requested_headers()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> createAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await createAsync(10, -10m);
        var h2 = await createAsync(12, -20m);
        var h3 = await createAsync(14, -30m);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={bank.Id}",
            new HeaderBalancesRequest { HeaderIds = new[] { h1, h2, h3 } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var balances = await resp.Content.ReadFromJsonAsync<HeaderBalanceDto[]>();
        Assert.NotNull(balances);
        Assert.Equal(3, balances!.Length);

        var byId = balances.ToDictionary(b => b.HeaderId);
        Assert.Equal(-10m, byId[h1].BalanceAfter);
        Assert.Equal(-10m, byId[h1].NetAmount);
        Assert.Equal(-30m, byId[h2].BalanceAfter);
        Assert.Equal(-20m, byId[h2].NetAmount);
        Assert.Equal(-60m, byId[h3].BalanceAfter);
        Assert.Equal(-30m, byId[h3].NetAmount);
    }

    [Fact]
    public async Task Empty_header_id_list_returns_empty_array_without_querying()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={bank.Id}",
            new HeaderBalancesRequest { HeaderIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var balances = await resp.Content.ReadFromJsonAsync<HeaderBalanceDto[]>();
        Assert.NotNull(balances);
        Assert.Empty(balances!);
    }

    [Fact]
    public async Task Filters_to_the_requested_account_only()
    {
        // The same header has balance rows on multiple accounts (cash
        // leg on bank, counterparty leg on category). The bulk lookup
        // returns ONLY the row for the requested account.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -42m },
                },
            });
        var headerId = (await createResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();

        var bankResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={bank.Id}",
            new HeaderBalancesRequest { HeaderIds = new[] { headerId } });
        var bankBalances = await bankResp.Content.ReadFromJsonAsync<HeaderBalanceDto[]>();
        Assert.Single(bankBalances!);
        Assert.Equal(-42m, bankBalances![0].BalanceAfter);

        var groceriesResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={groceries.Id}",
            new HeaderBalancesRequest { HeaderIds = new[] { headerId } });
        var groceriesBalances = await groceriesResp.Content.ReadFromJsonAsync<HeaderBalanceDto[]>();
        Assert.Single(groceriesBalances!);
        Assert.Equal(42m, groceriesBalances![0].BalanceAfter);
    }

    [Fact]
    public async Task Returns_422_when_account_id_missing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances",
            new HeaderBalancesRequest { HeaderIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Returns_422_when_account_in_another_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);
        var otherBank = await otherLedger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/balances?account_id={otherBank.Id}",
            new HeaderBalancesRequest { HeaderIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
