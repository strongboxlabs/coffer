using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// Tag-dictionary management HTTP surface (Tags v1): the
/// <c>/api/ledgers/{ledgerId}/tags</c> endpoints — list-with-usage, rename /
/// recolor (PATCH), merge, delete (delete-in-use allowed), and cleanup-unused.
/// Pins the HTTP contract (routing, DTO binding, result-enum →
/// <see cref="BusinessError"/> code mapping, and the ledger-visibility gate on
/// every handler) plus the merge dedup + FK-cascade delete semantics. Atomic
/// per-test ledger; shared-table reads are scoped by the test's ledger id.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TagsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public TagsEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static DateTime Utc(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Create a transaction whose single posting carries <paramref name="legTag"/>,
    /// through the real endpoint. Returns the source-side leg id.
    /// </summary>
    /// <remarks>
    /// Deliberately not an EF fixture write. The state under test — a tag that
    /// exists ONLY on a posting — is reachable through the product, and a
    /// hand-built row would prove the repository handles a shape without proving
    /// the shape is one the app can produce.
    /// </remarks>
    private async Task<Guid> TagAPostingAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId, string legTag)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Utc(9),
                Payee = "rent",
                SourceAccountId = bankId,
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        CounterpartyAccountId = catId,
                        Amount = -75m,
                        Tags = new[] { legTag },
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == bankId)
            .Select(l => l.Id).SingleAsync();
    }

    // ----- both levels (ADR-0103) ----------------------------------------------

    [Fact]
    public async Task Usage_counts_a_tag_used_only_on_a_posting()
    {
        // Counting header pairings alone reported 0 here — and 0 is the number the
        // management table shows next to a delete button.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Rent", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await TagAPostingAsync(client, ledger, bank.Id, cat.Id, "Property B");

        var list = await client.GetFromJsonAsync<List<TagDto>>(
            $"/api/ledgers/{ledger.LedgerId}/tags");

        Assert.Equal(1, Assert.Single(list!, t => t.Name == "Property B").UsageCount);
    }

    [Fact]
    public async Task Cleanup_unused_keeps_a_tag_used_only_on_a_posting()
    {
        // The most destructive path in the file before ADR-0103: "unused" meant no
        // HEADER pairing, so a posting-only tag read as an orphan and deleting it
        // cascaded its leg pairings away — data loss from the one action whose
        // whole promise is that it removes only what nothing points at.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Rent", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var legId = await TagAPostingAsync(client, ledger, bank.Id, cat.Id, "Property B");
        // A genuine orphan, so the sweep is shown to still do its job.
        await ledger.AddBareTagAsync("archived");

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/tags/unused");
        Assert.True(resp.IsSuccessStatusCode, $"cleanup failed: {resp.StatusCode}");

        var list = await client.GetFromJsonAsync<List<TagDto>>(
            $"/api/ledgers/{ledger.LedgerId}/tags");
        Assert.Contains(list!, t => t.Name == "Property B");
        Assert.DoesNotContain(list!, t => t.Name == "archived");

        // And the pairing itself survived, not merely the dictionary row.
        await using var db = _fixture.NewDbContext();
        Assert.True(await db.TxnLegTags.AsNoTracking().AnyAsync(x => x.LegId == legId));
    }

    [Fact]
    public async Task Merge_repoints_posting_pairings_instead_of_dropping_them()
    {
        // Merge deletes the source tag at the end, and tags.id cascades to
        // txn_leg_tags — so without repointing the leg side first, a merge silently
        // deletes every posting's attribution instead of moving it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Rent", "expense");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var legId = await TagAPostingAsync(client, ledger, bank.Id, cat.Id, "Flat B");

        var list = await client.GetFromJsonAsync<List<TagDto>>(
            $"/api/ledgers/{ledger.LedgerId}/tags");
        var source = Assert.Single(list!, t => t.Name == "Flat B").Id;
        await ledger.AddBareTagAsync("Property B");
        list = await client.GetFromJsonAsync<List<TagDto>>(
            $"/api/ledgers/{ledger.LedgerId}/tags");
        var target = Assert.Single(list!, t => t.Name == "Property B").Id;

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{source}/merge",
            new MergeTagRequest(target));
        Assert.True(resp.IsSuccessStatusCode, $"merge failed: {resp.StatusCode}");

        await using var db = _fixture.NewDbContext();
        var remaining = await db.TxnLegTags.AsNoTracking()
            .Where(x => x.LegId == legId).ToListAsync();
        var kept = Assert.Single(remaining);
        Assert.Equal(target, kept.TagId);
    }

    // ----- list with usage -----------------------------------------------------

    [Fact]
    public async Task List_returns_tags_with_usage_counts_sorted_by_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var (h2, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(3));
        // "work" on two headers (usage 2), "home" on one (usage 1).
        await ledger.AddTagAsync(h1, "work");
        await ledger.AddTagAsync(h2, "work");
        await ledger.AddTagAsync(h1, "home");
        // An orphan tag (never assigned) reports usage 0.
        await ledger.AddBareTagAsync("archived");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var list = await client.GetFromJsonAsync<List<TagDto>>(
            $"/api/ledgers/{ledger.LedgerId}/tags");

        // Name-sorted, case-insensitive: archived, home, work.
        Assert.Equal(new[] { "archived", "home", "work" }, list!.Select(t => t.Name).ToArray());
        Assert.Equal(0, Assert.Single(list!, t => t.Name == "archived").UsageCount);
        Assert.Equal(1, Assert.Single(list!, t => t.Name == "home").UsageCount);
        Assert.Equal(2, Assert.Single(list!, t => t.Name == "work").UsageCount);
        Assert.Null(Assert.Single(list!, t => t.Name == "work").Color);
    }

    [Fact]
    public async Task List_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.GetAsync($"/api/ledgers/{alice.LedgerId}/tags");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));
    }

    // ----- rename / recolor (PATCH) --------------------------------------------

    [Fact]
    public async Task Patch_renames_a_tag()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest("Groceries", null));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var list = await client.GetFromJsonAsync<List<TagDto>>($"/api/ledgers/{ledger.LedgerId}/tags");
        Assert.Equal("Groceries", Assert.Single(list!, t => t.Id == tagId).Name);
    }

    [Fact]
    public async Task Patch_recolors_a_tag_and_lowercases_the_hex()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("travel");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest(null, "#4F46E5"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var list = await client.GetFromJsonAsync<List<TagDto>>($"/api/ledgers/{ledger.LedgerId}/tags");
        Assert.Equal("#4f46e5", Assert.Single(list!, t => t.Id == tagId).Color);
    }

    [Fact]
    public async Task Patch_renames_and_recolors_in_one_call()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("misc");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest("Miscellaneous", "#10b981"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var tag = Assert.Single(
            (await client.GetFromJsonAsync<List<TagDto>>($"/api/ledgers/{ledger.LedgerId}/tags"))!,
            t => t.Id == tagId);
        Assert.Equal("Miscellaneous", tag.Name);
        Assert.Equal("#10b981", tag.Color);
    }

    [Fact]
    public async Task Patch_rejects_rename_to_an_existing_name_case_insensitive()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await ledger.AddBareTagAsync("Work");
        var home = await ledger.AddBareTagAsync("Home");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Rename "Home" → "work" collides with "Work" (case-insensitive).
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{home}",
            new PatchTagRequest("work", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNameExists, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Patch_allows_a_case_only_self_rename()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("work");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest("Work", null));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var list = await client.GetFromJsonAsync<List<TagDto>>($"/api/ledgers/{ledger.LedgerId}/tags");
        Assert.Equal("Work", Assert.Single(list!, t => t.Id == tagId).Name);
    }

    [Fact]
    public async Task Patch_rejects_an_invalid_colour()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("bills");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest(null, "blue"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagColorInvalid, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Patch_rejects_an_empty_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("bills");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest("   ", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TransactionTagEmpty, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Patch_rejects_a_name_over_the_length_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("bills");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}",
            new PatchTagRequest(new string('x', 65), null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TransactionTagTooLong, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Patch_rejects_an_unknown_tag()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{Guid.NewGuid()}",
            new PatchTagRequest("whatever", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Patch_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceTag = await alice.AddBareTagAsync("alice-only");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.PatchAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/tags/{aliceTag}",
            new PatchTagRequest("hijacked", null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));
    }

    // ----- merge ---------------------------------------------------------------

    [Fact]
    public async Task Merge_repoints_assignments_dedups_and_deletes_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var (h2, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(3));

        // h1: source only. h2: BOTH source and target (dedup case).
        var source = await ledger.AddTagAsync(h1, "groceries-old");
        await ledger.AddTagAsync(h2, "groceries-old");
        var target = await ledger.AddTagAsync(h2, "groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{source}/merge",
            new MergeTagRequest(target));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Only h1's source pairing is repointed; h2's source pairing is dropped
        // (it already carries the target) — so the echo reports 1.
        var body = (await resp.Content.ReadFromJsonAsync<MergeTagResponse>())!;
        Assert.Equal(1, body.TransactionsRepointed);

        await using var read = _fixture.NewDbContext();
        // Source tag gone; target now on both headers exactly once each.
        Assert.False(await read.Tags.AnyAsync(t => t.Id == source));
        Assert.Equal(2, await read.TxnHeaderTags.CountAsync(
            x => x.LedgerId == ledger.LedgerId && x.TagId == target));
        Assert.Equal(0, await read.TxnHeaderTags.CountAsync(
            x => x.LedgerId == ledger.LedgerId && x.TagId == source));
    }

    [Fact]
    public async Task Merge_rejects_merging_into_itself()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var tagId = await ledger.AddBareTagAsync("solo");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{tagId}/merge",
            new MergeTagRequest(tagId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagMergeSelf, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Merge_rejects_an_unknown_target()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddBareTagAsync("source");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/tags/{source}/merge",
            new MergeTagRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Merge_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var src = await alice.AddBareTagAsync("a-src");
        var dst = await alice.AddBareTagAsync("a-dst");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.PostAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/tags/{src}/merge",
            new MergeTagRequest(dst));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        // Alice's tags are untouched.
        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Tags.AnyAsync(t => t.Id == src));
    }

    // ----- delete (delete-in-use allowed) --------------------------------------

    [Fact]
    public async Task Delete_removes_a_tag_and_untags_its_transactions()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var tagId = await ledger.AddTagAsync(h1, "in-use");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/tags/{tagId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var read = _fixture.NewDbContext();
        Assert.False(await read.Tags.AnyAsync(t => t.Id == tagId));
        // FK cascade removed the assignment too.
        Assert.Equal(0, await read.TxnHeaderTags.CountAsync(
            x => x.LedgerId == ledger.LedgerId && x.TagId == tagId));
    }

    [Fact]
    public async Task Delete_rejects_an_unknown_tag()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/tags/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task Delete_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceTag = await alice.AddBareTagAsync("alice-only");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.DeleteAsync($"/api/ledgers/{alice.LedgerId}/tags/{aliceTag}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));

        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Tags.AnyAsync(t => t.Id == aliceTag));
    }

    // ----- cleanup-unused ------------------------------------------------------

    [Fact]
    public async Task CleanupUnused_removes_only_orphan_tags()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var used = await ledger.AddTagAsync(h1, "kept");
        var orphanA = await ledger.AddBareTagAsync("orphan-a");
        var orphanB = await ledger.AddBareTagAsync("orphan-b");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/tags/unused");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<CleanupTagsResponse>())!;
        Assert.Equal(2, body.TagsRemoved);

        await using var read = _fixture.NewDbContext();
        Assert.True(await read.Tags.AnyAsync(t => t.Id == used));
        Assert.False(await read.Tags.AnyAsync(t => t.Id == orphanA));
        Assert.False(await read.Tags.AnyAsync(t => t.Id == orphanB));
    }

    [Fact]
    public async Task CleanupUnused_rejects_when_ledger_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddBareTagAsync("alice-orphan");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.DeleteAsync($"/api/ledgers/{alice.LedgerId}/tags/unused");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, await ErrorCodeAsync(resp));
    }
}
