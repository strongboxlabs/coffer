using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// ADR-0076: the register page, the date-rail buckets, and the status-count
/// badges all read ONE filter definition — the <c>register_filtered_entries</c>
/// primitive (migration 167). These tests pin that the three surfaces agree:
/// the same filter selects the same set on each. That's the guarantee the old
/// hand-synced LINQ twin (ApplyRegisterFilterPredicates) couldn't make — its
/// drift was the #322 class of bug. Two dimensions (search, amount) are checked
/// because they select DIFFERENT subsets, so a consumer that honored one and
/// dropped the other would still be caught.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterFilterConsistencyTests
{
    private readonly PostgresFixture _fixture;

    public RegisterFilterConsistencyTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Day(int d) => new(2026, 1, d, 12, 0, 0, DateTimeKind.Utc);

    // 3 of 5 payees carry the "zzz" token; amounts split 3 ≥ 25 / 2 < 25 — the
    // search and amount dimensions therefore pick DIFFERENT triples.
    private static readonly (decimal Amount, string Payee)[] Rows =
    {
        (50m, "oak zzz"),
        (10m, "cedar zzz"),
        (30m, "ash zzz"),
        (20m, "dogwood"),
        (40m, "birch"),
    };

    private async Task<(SyntheticLedger Ledger, Guid AccountId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var day = 10;
        foreach (var (amount, payee) in Rows)
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, amount, Day(day++), payee);
        return (ledger, acct.Id);
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private async Task<int> PageCountAsync(HttpClient c, SyntheticLedger l, Guid a, string q) =>
        (await c.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{l.LedgerId}/transactions?account_id={a}&limit=100&{q}"))!.Entries.Count;

    private async Task<int> RailTotalAsync(HttpClient c, SyntheticLedger l, Guid a, string q) =>
        (await c.GetFromJsonAsync<List<IndexBucketDto>>(
            $"/api/ledgers/{l.LedgerId}/transactions/index-buckets?account_id={a}&{q}"))!.Sum(b => b.Count);

    private async Task<int> CountsAllAsync(HttpClient c, SyntheticLedger l, Guid a, string q) =>
        (await c.GetFromJsonAsync<RegisterStatusCounts>(
            $"/api/ledgers/{l.LedgerId}/transactions/status-counts?account_id={a}&{q}"))!.All;

    [Fact]
    public async Task Page_rail_and_counts_select_the_same_set_under_a_filter()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Unfiltered: all three agree on the full count.
        Assert.Equal(5, await PageCountAsync(client, ledger, acct, ""));
        Assert.Equal(5, await RailTotalAsync(client, ledger, acct, ""));
        Assert.Equal(5, await CountsAllAsync(client, ledger, acct, ""));

        // Search "zzz" → the 3 tagged payees, identically on every surface.
        Assert.Equal(3, await PageCountAsync(client, ledger, acct, "search=zzz"));
        Assert.Equal(3, await RailTotalAsync(client, ledger, acct, "search=zzz"));
        Assert.Equal(3, await CountsAllAsync(client, ledger, acct, "search=zzz"));

        // A different dimension (amount ≥ 25 → a DIFFERENT triple) — guards
        // against a surface that honors search but silently drops amount.
        Assert.Equal(3, await PageCountAsync(client, ledger, acct, "amount_min=25"));
        Assert.Equal(3, await RailTotalAsync(client, ledger, acct, "amount_min=25"));
        Assert.Equal(3, await CountsAllAsync(client, ledger, acct, "amount_min=25"));
    }
}
