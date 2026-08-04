using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>GET /api/ledgers/{ledgerId}/transactions/index-buckets?account_id=...</c>.
/// One bucket per month-with-activity for the account's register,
/// most-recent first — drives the SPA's date-aware scroll-track
/// (Google Photos pattern, ADR-0024 follow-up).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class IndexBucketsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public IndexBucketsEndpointTests(PostgresFixture fixture) => _fixture = fixture;

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

    /// <summary>
    /// Six transactions across three months on the bank account:
    /// 3 in May 2026, 2 in March 2026, 1 in January 2026. Expect
    /// three buckets in DESC order with the right counts. The sample
    /// header per bucket is the most-recent in that month — the SPA
    /// uses it as the seek anchor.
    /// </summary>
    [Fact]
    public async Task Returns_one_bucket_per_active_month_most_recent_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        // Seed: post times monotonically increasing within each month so
        // the "most-recent in bucket" pick is deterministic.
        var seeds = new[]
        {
            new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3,  5, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5,  1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc),
        };
        foreach (var (pa, i) in seeds.Select((pa, i) => (pa, i)))
        {
            await ledger.AddTransactionPairAsync(
                fromAccountId: bank.Id, toAccountId: groceries.Id,
                amount: -(10m + i), postedAt: pa, payee: $"m-{i}");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var buckets = await resp.Content.ReadFromJsonAsync<IndexBucketDto[]>();
        Assert.NotNull(buckets);
        Assert.Equal(3, buckets!.Length);

        Assert.Equal("2026-05", buckets[0].YearMonth);
        Assert.Equal(3, buckets[0].Count);

        Assert.Equal("2026-03", buckets[1].YearMonth);
        Assert.Equal(2, buckets[1].Count);

        Assert.Equal("2026-01", buckets[2].YearMonth);
        Assert.Equal(1, buckets[2].Count);

        // Sample header for May 2026 is the May 30 transaction — the
        // most-recent in that month by (posted_at, seq). Verify by
        // looking the header up via posted_at.
        await using var db = _fixture.NewDbContext();
        var may30HeaderId = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId
                     && h.PostedAt == new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc))
            .Select(h => h.Id)
            .SingleAsync();
        Assert.Equal(may30HeaderId, buckets[0].SampleHeaderId);
    }

    /// <summary>
    /// Hidden + merged-away entries are excluded from the scroll-track
    /// (matches the resolved view's visibility predicate the register
    /// itself paginates against — what the SPA can't see shouldn't
    /// shape the scroll affordance).
    /// </summary>
    [Fact]
    public async Task Excludes_hidden_and_merged_entries()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -10m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), "visible-mar");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -20m,
            new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), "to-hide-mar");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -30m,
            new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), "to-merge-mar");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -40m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), "visible-may");

        // Hide one March entry and merge another into the visible-may
        // entry. Both should drop out of the March count; the May
        // count stays at 1 (the merge survivor doesn't gain
        // accidentally).
        await using (var db = _fixture.NewDbContext())
        {
            var marchHide = await db.TxnHeaders
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.Payee == "to-hide-mar");
            marchHide.IsHidden = true;

            var marchLoser = await db.TxnHeaders
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.Payee == "to-merge-mar");
            var mayWinner = await db.TxnHeaders
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.Payee == "visible-may");
            marchLoser.IsMergedInto = mayWinner.Id;

            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var buckets = await resp.Content.ReadFromJsonAsync<IndexBucketDto[]>();
        Assert.NotNull(buckets);
        Assert.Equal(2, buckets!.Length);
        Assert.Equal("2026-05", buckets[0].YearMonth);
        Assert.Equal(1, buckets[0].Count);
        Assert.Equal("2026-03", buckets[1].YearMonth);
        Assert.Equal(1, buckets[1].Count); // only the visible-mar survives
    }

    /// <summary>
    /// Buckets are per-account: a transaction touching account A doesn't
    /// show up in account B's scroll-track.
    /// </summary>
    [Fact]
    public async Task Filters_to_the_requested_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bankA = await ledger.AddBankAccountAsync("checking-A");
        var bankB = await ledger.AddBankAccountAsync("checking-B");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await ledger.AddTransactionPairAsync(bankA.Id, groceries.Id, -10m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(bankB.Id, groceries.Id, -20m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A's track sees only the March bucket.
        var respA = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bankA.Id}");
        var bucketsA = await respA.Content.ReadFromJsonAsync<IndexBucketDto[]>();
        Assert.NotNull(bucketsA);
        Assert.Single(bucketsA!);
        Assert.Equal("2026-03", bucketsA![0].YearMonth);

        // B's track sees only the May bucket.
        var respB = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bankB.Id}");
        var bucketsB = await respB.Content.ReadFromJsonAsync<IndexBucketDto[]>();
        Assert.NotNull(bucketsB);
        Assert.Single(bucketsB!);
        Assert.Equal("2026-05", bucketsB![0].YearMonth);
    }

    /// <summary>
    /// Missing account_id is a 422 — the scroll-track is a per-account
    /// UX; aggregating across all accounts would change the "header
    /// counted once" semantics non-trivially.
    /// </summary>
    [Fact]
    public async Task Returns_422_when_account_id_is_missing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    /// <summary>
    /// Account outside this ledger is a 422 — same gate as the existing
    /// /transactions listing endpoint.
    /// </summary>
    [Fact]
    public async Task Returns_422_when_account_is_in_another_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);
        var otherBank = await otherLedger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={otherBank.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    /// <summary>
    /// Empty register → empty bucket array. The SPA's
    /// RegisterScrollTrack hides itself in this case (no UX value).
    /// </summary>
    [Fact]
    public async Task Returns_empty_array_for_account_with_no_visible_entries()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/index-buckets?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var buckets = await resp.Content.ReadFromJsonAsync<IndexBucketDto[]>();
        Assert.NotNull(buckets);
        Assert.Empty(buckets!);
    }
}
