using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Server-side register filtering + search (mig 164). Each dimension pushes into
/// register_entry_keys so the windowed keyset cursor walks ONLY matching entries
/// — the property client-side filtering can't provide. The pagination test is
/// the load-bearing one: a filter with more matches than the page size must
/// return every match across pages and never leak a non-match.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterFilterTests
{
    private readonly PostgresFixture _fixture;

    public RegisterFilterTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Search_matches_payee_case_insensitively()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 50m, Day(2026, 1, 10), "COSTCO");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 60m, Day(2026, 1, 11), "AMAZON");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 70m, Day(2026, 1, 12), "Costco Gas");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&search=costco"))!;

        // Both "COSTCO" (upper) and "Costco Gas" match the case-insensitive
        // ILIKE; "AMAZON" is excluded.
        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, e => e.Txn!.Payee == "AMAZON");
        Assert.All(page.Entries, e => Assert.Contains("ostco", e.Txn!.Payee!.ToLowerInvariant()));
    }

    [Fact]
    public async Task Date_range_bounds_are_inclusive()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 15), "jan");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2026, 2, 15), "feb");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 30m, Day(2026, 3, 15), "mar");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&date_from=2026-02-01&date_to=2026-02-28"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("feb", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Amount_range_filters_by_magnitude()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 50m, Day(2026, 1, 10), "small");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 150m, Day(2026, 1, 11), "mid");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 500m, Day(2026, 1, 12), "big");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&amount_min=100&amount_max=200"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("mid", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Status_scheduled_matches_future_dated_only()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2020, 1, 1), "past");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2030, 1, 1), "future");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var scheduled = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&status=scheduled&today=2026-07-13"))!;
        var entry = Assert.Single(scheduled.Entries);
        Assert.Equal("future", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Tag_filter_matches_tagged_entries()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var (taggedLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 10), "rental");
        await ledger.AddTagAsync(taggedLeg, "Property A");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2026, 1, 11), "untagged");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&tag={Uri.EscapeDataString("Property A")}"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("rental", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Category_filter_matches_entries_posting_to_that_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 40m, Day(2026, 1, 10), "market");
        await ledger.AddTransactionPairAsync(acct.Id, dining.Id, 25m, Day(2026, 1, 11), "cafe");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&category_id={groceries.Id}"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("market", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Security_filter_matches_entries_involving_that_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var other = await ledger.AddBankAccountAsync("checking");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var buy = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Day(2026, 1, 10),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
            });
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);
        // A non-security cash deposit into the brokerage — must NOT match.
        await ledger.AddTransactionPairAsync(brokerage.Id, other.Id, 500m, Day(2026, 1, 11), "cash deposit");

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={brokerage.Id}&limit=100&security_id={securityId}"))!;

        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e => Assert.NotEqual("cash deposit", e.Txn?.Payee));
    }

    [Fact]
    public async Task Filter_pages_across_the_cursor_returning_all_matches_and_no_non_matches()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");

        // 12 matches + 12 non-matches, interleaved by date so any window
        // straddles both — the filter must survive the keyset walk.
        const int matches = 12;
        for (var i = 0; i < matches; i++)
        {
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m + i, Day(2026, 1, 1).AddDays(i * 2), "COSTCO");
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m + i, Day(2026, 1, 1).AddDays(i * 2 + 1), "AMAZON");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var seen = 0;
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=5&search=costco"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}&direction=before");
            var page = (await client.GetFromJsonAsync<RegisterPage>(url))!;
            Assert.All(page.Entries, e => Assert.Equal("COSTCO", e.Txn!.Payee));
            seen += page.Entries.Count;
            cursor = page.CursorForOlder;
            if (cursor is null) break;
        }

        Assert.Equal(matches, seen);
    }
}
