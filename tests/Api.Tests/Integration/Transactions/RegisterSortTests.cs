using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Column sort (mig 166): the register can be ordered by a chosen column +
/// direction, not just posted-date DESC. The load-bearing test is
/// <see cref="Keyset_pagination_is_gap_free_under_a_non_date_sort"/> — it drives
/// the dynamic-SQL keyset cursor across page boundaries under an amount sort, so
/// a bad cursor predicate (skipped / duplicated / reordered entries) surfaces
/// here against a live DB. Assertions key on payee order (sign/precision-
/// independent) so they don't bake in the amount-sign convention.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterSortTests
{
    private readonly PostgresFixture _fixture;

    public RegisterSortTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    // Payees deliberately NOT in the same order as amounts or dates, so a working
    // sort must actually reorder — it can't coincidentally match insertion order.
    private static readonly (decimal Amount, string Payee)[] Rows =
    {
        (50m, "elm"),
        (10m, "cedar"),
        (30m, "ash"),
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
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, amount, Day(2026, 1, day++), payee);
        return (ledger, acct.Id);
    }

    private static List<string> Payees(RegisterPage page) =>
        page.Entries.Select(e => e.Txn!.Payee!).ToList();

    private async Task<RegisterPage> FetchAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, string query) =>
        (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&{query}"))!;

    [Fact]
    public async Task Sort_by_payee_orders_alphabetically()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var asc = await FetchAsync(client, ledger, acct, "limit=100&sort=payee&dir=asc");
        Assert.Equal(new[] { "ash", "birch", "cedar", "dogwood", "elm" }, Payees(asc));
    }

    [Fact]
    public async Task Sort_by_amount_desc_is_the_reverse_of_asc()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ascPayees = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=asc"));
        var descPayees = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=desc"));

        Assert.Equal(Rows.Length, ascPayees.Count);
        // Distinct amounts ⇒ a total order ⇒ desc is exactly asc reversed. Proves
        // both the sort column AND direction without assuming the amount sign.
        Assert.Equal(ascPayees.AsEnumerable().Reverse().ToList(), descPayees);
        Assert.NotEqual(ascPayees, descPayees); // guard against a no-op sort
    }

    [Fact]
    public async Task Keyset_pagination_is_gap_free_under_a_non_date_sort()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The reference order: the whole set in one page, amount-desc.
        var full = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=desc"));

        // Walk the same order two-at-a-time via the keyset cursor. Each page's
        // `cursorForOlder` + direction=before continues past its last entry —
        // exactly what the windowed SPA does on scroll.
        var walked = new List<string>();
        var page = await FetchAsync(client, ledger, acct, "limit=2&sort=amount&dir=desc");
        walked.AddRange(Payees(page));
        var guard = 0;
        while (page.CursorForOlder is { } cursor && guard++ < 20)
        {
            page = await FetchAsync(
                client, ledger, acct,
                $"limit=2&sort=amount&dir=desc&cursor={Uri.EscapeDataString(cursor)}&direction=before");
            if (page.Entries.Count == 0) break;
            walked.AddRange(Payees(page));
        }

        // No gaps, no duplicates, same order as the single-page reference.
        Assert.Equal(full, walked);
        Assert.Equal(Rows.Length, walked.Count);
        Assert.Equal(walked.Distinct().Count(), walked.Count);
    }
}
