using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// ADR-0076: the focus/anchor (starting_at) path must respect the active
/// filter. A focused row the filter excludes must NOT be pinned to the top of
/// the register — the backend resolves the anchor through the same
/// register_filtered_entries primitive, so the register never shows a row that
/// doesn't match the filter (the confusing case found in dev: a scheduled
/// "Amazon.com Visa" row pinned under a "Groceries" filter). A non-matching
/// anchor falls through to the most-recent FILTERED page (not an empty one).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterAnchorFilterTests
{
    private readonly PostgresFixture _fixture;

    public RegisterAnchorFilterTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Day(int d) => new(2026, 1, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static List<string?> Payees(RegisterPage page) =>
        page.Entries.Select(e => e.Txn!.Payee).ToList();

    [Fact]
    public async Task Anchor_the_filter_excludes_is_not_pinned()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddBankAccountAsync("groceries");
        var rent = await ledger.AddBankAccountAsync("rent");

        // Two "groceries" entries; one "rent" entry that is the NEWEST — so it's
        // the natural top-of-register row you might have focused / just edited.
        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 10m, Day(10), "market");
        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 20m, Day(11), "market");
        var (rentLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, rent.Id, 30m, Day(20), "landlord");
        var rentHeader = await ledger.ResolveHeaderIdAsync(rentLeg);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Anchor on the RENT header, but filter to the GROCERIES counterparty.
        // The rent row doesn't match, so it must not be pinned — the register
        // shows exactly the two grocery entries (fell through to the filtered
        // page, not an empty one, and not the non-matching anchor).
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100"
            + $"&starting_at={rentHeader}&category_id={groceries.Id}"))!;

        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain("landlord", Payees(page));
        Assert.All(Payees(page), p => Assert.Equal("market", p));
    }

    [Fact]
    public async Task Anchor_the_filter_includes_is_still_pinned()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddBankAccountAsync("groceries");

        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 10m, Day(10), "market-old");
        var (midLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 20m, Day(11), "market-mid");
        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 30m, Day(12), "market-new");
        var midHeader = await ledger.ResolveHeaderIdAsync(midLeg);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Anchor on the MIDDLE grocery header under a groceries filter it
        // matches: it stays pinned as entry[0] (not the newest), followed by
        // the strictly-older grocery entry.
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100"
            + $"&starting_at={midHeader}&category_id={groceries.Id}"))!;

        Assert.NotEmpty(page.Entries);
        Assert.Equal("market-mid", page.Entries[0].Txn!.Payee);
    }
}
