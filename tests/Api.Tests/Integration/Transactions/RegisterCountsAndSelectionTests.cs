using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The mig-165 register additions: the "reconciling" status view, the
/// filter-aware per-status counts endpoint, and select-all honoring the active
/// filter. The select-all test is the load-bearing one — it exercises the new
/// BuildSelectionQuery filter intersect (which reuses the register's own
/// predicate) against a live DB, so a bad LINQ→SQL translation surfaces here.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterCountsAndSelectionTests
{
    private readonly PostgresFixture _fixture;

    public RegisterCountsAndSelectionTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Reconciling_is_its_own_view_and_uncleared_excludes_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 10), "unc");
        var (recLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2026, 1, 11), "rec");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Mark the second row reconciling through the recon-status endpoint.
        var recHeader = await ledger.ResolveHeaderIdAsync(recLeg);
        var put = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{recHeader}/recon-status",
            new { status = "reconciling", accountId = acct.Id });   // per-account (ADR-0082)
        Assert.True(put.IsSuccessStatusCode, $"recon-status PUT failed: {put.StatusCode}");

        // status=reconciling → only the reconciling row (mig 165 branch).
        var rec = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&status=reconciling&today=2026-07-13"))!;
        Assert.Equal("rec", Assert.Single(rec.Entries).Txn!.Payee);

        // status=uncleared → excludes the reconciling row (it used to be lumped
        // in with uncleared; mig 165 split them so the view matches its label).
        var unc = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&status=uncleared&today=2026-07-13"))!;
        Assert.Equal("unc", Assert.Single(unc.Entries).Txn!.Payee);
    }

    [Fact]
    public async Task Status_counts_narrow_with_the_active_filter()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        for (var i = 0; i < 3; i++)
            await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 10m + i, Day(2026, 1, 10 + i), $"g{i}");
        for (var i = 0; i < 2; i++)
            await ledger.AddTransactionPairAsync(acct.Id, dining.Id, 20m + i, Day(2026, 2, 10 + i), $"d{i}");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // No filter → all five uncleared on the account.
        var all = (await client.GetFromJsonAsync<RegisterStatusCounts>(
            $"/api/ledgers/{ledger.LedgerId}/transactions/status-counts?account_id={acct.Id}&today=2026-07-13"))!;
        Assert.Equal(5, all.All);
        Assert.Equal(5, all.Uncleared);
        Assert.Equal(0, all.Reconciling);

        // Category filter → every count narrows to the three groceries rows.
        var filtered = (await client.GetFromJsonAsync<RegisterStatusCounts>(
            $"/api/ledgers/{ledger.LedgerId}/transactions/status-counts?account_id={acct.Id}&category_id={groceries.Id}&today=2026-07-13"))!;
        Assert.Equal(3, filtered.All);
        Assert.Equal(3, filtered.Uncleared);
    }

    [Fact]
    public async Task Select_all_honors_the_active_filter()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        for (var i = 0; i < 3; i++)
            await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 10m, Day(2026, 1, 10 + i), $"g{i}");
        for (var i = 0; i < 2; i++)
            await ledger.AddTransactionPairAsync(acct.Id, dining.Id, 20m, Day(2026, 2, 10 + i), $"d{i}");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // How many entries the register shows under the groceries filter — the
        // select-all count must match this exactly.
        var reg = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&category_id={groceries.Id}"))!;
        var filteredEntryCount = reg.Entries.Count;
        Assert.True(filteredEntryCount > 0, "expected the groceries filter to show entries");

        // 'all'-mode select-all WITH the groceries filter. SelectedAt in the
        // future so every existing row is in scope.
        var filteredSummary = await SelectionCountAsync(client, ledger, new SelectionRequest
        {
            Kind = "all",
            AccountId = acct.Id,
            StatusFilter = "all",
            SelectedAt = Day(2030, 1, 1),
            CategoryId = groceries.Id,
        });

        // 'all'-mode select-all with NO filter → the whole account.
        var wholeAccount = await SelectionCountAsync(client, ledger, new SelectionRequest
        {
            Kind = "all",
            AccountId = acct.Id,
            StatusFilter = "all",
            SelectedAt = Day(2030, 1, 1),
        });

        // The filtered select-all covers exactly the filtered view — not the
        // whole account (the bug this fixes).
        Assert.Equal(filteredEntryCount, filteredSummary);
        Assert.True(filteredSummary < wholeAccount,
            $"filtered select-all ({filteredSummary}) should be fewer than the whole account ({wholeAccount})");
    }

    [Fact]
    public async Task Select_all_on_a_status_view_is_exactly_what_the_register_shows_for_that_date()
    {
        // The register decides each view from the user's LOCAL date and
        // leaves pending rows out of the recon views; the bulk predicate used to
        // judge "future" by an instant and keep pending rows, so a bulk clear over
        // "all uncleared" could act on rows the reader was not shown. The dates are
        // fixed and the date is sent, so this holds on any day the suite runs.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 10), "uncleared");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 11m, Day(2026, 1, 12), "tomorrow");
        var (pendingLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 12m, Day(2026, 1, 9), "pending");
        var (clearedLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 13m, Day(2026, 1, 8), "cleared");
        var (futureClearedLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 14m, Day(2026, 1, 13), "cleared-future");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        foreach (var leg in new[] { clearedLeg, futureClearedLeg })
        {
            var put = await client.PutAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions/{await ledger.ResolveHeaderIdAsync(leg)}/recon-status",
                new { status = "cleared", accountId = acct.Id });
            Assert.True(put.IsSuccessStatusCode, $"recon-status PUT failed: {put.StatusCode}");
        }
        // Pending is set by bank feeds; nothing on the write API sets it.
        var pendingHeader = await ledger.ResolveHeaderIdAsync(pendingLeg);
        await using (var db = _fixture.NewDbContext())
            await db.TxnHeaders.Where(h => h.Id == pendingHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsPending, true));

        var today = new DateOnly(2026, 1, 11);
        var expected = new Dictionary<string, int>
        {
            ["uncleared"] = 1,   // not "tomorrow" (scheduled), not "pending"
            ["cleared"] = 1,     // not "cleared-future" (scheduled)
            ["scheduled"] = 2,   // "tomorrow" and "cleared-future"
        };
        foreach (var (status, count) in expected)
        {
            var shown = (await client.GetFromJsonAsync<RegisterPage>(
                $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100"
                + $"&status={status}&today={today:yyyy-MM-dd}"))!.Entries.Count;
            var selected = await SelectionCountAsync(client, ledger, new SelectionRequest
            {
                Kind = "all",
                AccountId = acct.Id,
                StatusFilter = status,
                SelectedAt = Day(2030, 1, 1),
                Today = today,
            });
            Assert.Equal(count, shown);
            Assert.True(selected == shown,
                $"select-all on '{status}' covers {selected} rows; the register shows {shown}");
        }

        // A caller with no account in scope gets the same date rule for scheduled.
        var ledgerWide = await SelectionCountAsync(client, ledger, new SelectionRequest
        {
            Kind = "all",
            StatusFilter = "scheduled",
            SelectedAt = Day(2030, 1, 1),
            Today = today,
        });
        Assert.Equal(2, ledgerWide);
    }

    private static async Task<int> SelectionCountAsync(
        HttpClient client, SyntheticLedger ledger, SelectionRequest body)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var summary = (await resp.Content.ReadFromJsonAsync<SelectionSummary>())!;
        return summary.Count;
    }
}
