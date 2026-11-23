using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Per-line tags of a split end to end (ADR-0103, migration 243).
/// </summary>
/// <remarks>
/// The case per-line tags exist for: one header whose postings are about different
/// things — a payment split across two projects, each line tagged with its project —
/// and the question the tags exist to answer is which posting the money went to.
/// A line's notes travel in <c>notes.lines</c> by index, apart from the postings, and
/// land on the split side's leg of that line. These go through HTTP because the
/// contract — omitted versus <c>[]</c>, and a postings list replaced wholesale — is
/// the part that can be wrong while every repository method is right.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LegTagsTests : DatabaseTest
{
    public LegTagsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Posted = new(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>The tag names on each leg of a header, by leg id.</summary>
    private async Task<Dictionary<Guid, List<string>>> LegTagsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var pairs = await (
            from lt in db.TxnLegTags.AsNoTracking()
            join l in db.TxnLegs.AsNoTracking() on lt.LegId equals l.Id
            join t in db.Tags.AsNoTracking() on lt.TagId equals t.Id
            where l.HeaderId == headerId
            select new { lt.LegId, t.Name })
            .ToListAsync();

        return pairs
            .GroupBy(x => x.LegId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());
    }

    private sealed record Split(Guid HeaderId, Guid BankId, Guid SuppliesId, HttpClient Client, SyntheticLedger Ledger, ApiFactory Factory);

    /// <summary>
    /// A two-line split entered from the bank, tagged "work" as a whole, each line
    /// with its given tag (null: none).
    /// </summary>
    private async Task<Split> ProjectSplitAsync(string? first, string? second)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var supplies = await ledger.AddCategoryAsync("Supplies");

        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = await ClientAsync(factory, ledger);

        var lines = new[] { first, second }
            .Select((t, i) => t is null ? null : new TransactionLineNotesRequest { Index = i, Tags = [t] })
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                Payee = "Supplies",
                SourceAccountId = bank.Id,
                Postings =
                [
                    new TransactionPosting { CounterpartyAccountId = supplies.Id, Amount = -100m },
                    new TransactionPosting { CounterpartyAccountId = supplies.Id, Amount = -50m },
                ],
                Notes = new TransactionNotesRequest { Side = bank.Id, Tags = ["work"], Lines = lines },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new Split(doc.RootElement.GetProperty("headerId").GetGuid(), bank.Id, supplies.Id, client, ledger, factory);
    }

    [Fact]
    public async Task Each_posting_keeps_its_own_tag()
    {
        var split = await ProjectSplitAsync("Project A", "Project B");
        await using var _f = split.Factory;

        var byLeg = await LegTagsAsync(split.HeaderId);
        Assert.Equal(2, byLeg.Count);
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Project A" }));
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Project B" }));
        // On the split side's legs, never the opposite side's.
        await using var db = Db.NewDbContext();
        var tagged = byLeg.Keys.ToList();
        Assert.All(await db.TxnLegs.AsNoTracking().Where(l => tagged.Contains(l.Id)).Select(l => l.AccountId).ToListAsync(),
            a => Assert.Equal(split.BankId, a));

        // The header keeps its OWN tag and does not absorb the lines' —
        // neither level is derived from the other (ADR-0103 D1).
        var headerTags = await (
            from ht in db.TxnHeaderTags.AsNoTracking()
            join t in db.Tags.AsNoTracking() on ht.TagId equals t.Id
            where ht.HeaderId == split.HeaderId
            select t.Name).ToListAsync();
        Assert.Equal(["work"], headerTags);
    }

    [Fact]
    public async Task A_name_used_at_both_levels_is_one_dictionary_row()
    {
        // The tracker-pending case: the header and a line both introduce
        // "work" in ONE request. Resolving per call without consulting pending
        // inserts would create the tag twice and leave the ledger with two
        // identically named tags nobody can tell apart.
        var split = await ProjectSplitAsync("work", null);
        await using var _f = split.Factory;

        await using var db = Db.NewDbContext();
        var rows = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == split.Ledger.LedgerId && t.Name == "work")
            .ToListAsync();
        Assert.Single(rows);

        var byLeg = await LegTagsAsync(split.HeaderId);
        Assert.Equal(["work"], Assert.Single(byLeg).Value);
    }

    [Fact]
    public async Task A_patch_that_omits_notes_leaves_them_alone()
    {
        // The trap this contract exists for. The postings list is replaced
        // WHOLESALE, so a client that reads a transaction and echoes its postings
        // back must not strip the lines' tags by omission.
        var split = await ProjectSplitAsync("Project A", "Project B");
        await using var _f = split.Factory;

        var resp = await split.Client.PatchAsJsonAsync(
            $"/api/ledgers/{split.Ledger.LedgerId}/transactions/{split.HeaderId}",
            new PatchTransactionRequest { Postings = await EchoPostingsAsync(split, firstAmount: -150m) });
        Assert.True(resp.IsSuccessStatusCode,
            $"patch failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        var byLeg = await LegTagsAsync(split.HeaderId);
        Assert.Equal(2, byLeg.Count);
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Project A" }));
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Project B" }));
    }

    [Fact]
    public async Task A_patch_with_an_empty_list_clears_them()
    {
        // The other half of the contract: omitted means leave alone, [] means
        // clear. Without both, there is no way to remove a line's last tag.
        var split = await ProjectSplitAsync("Project A", null);
        await using var _f = split.Factory;

        var resp = await split.Client.PatchAsJsonAsync(
            $"/api/ledgers/{split.Ledger.LedgerId}/transactions/{split.HeaderId}",
            new PatchTransactionRequest
            {
                Postings = await EchoPostingsAsync(split, firstAmount: -100m),
                Notes = new TransactionNotesRequest
                {
                    Side = split.BankId,
                    Lines = [new TransactionLineNotesRequest { Index = 0, Tags = [] }],
                },
            });
        Assert.True(resp.IsSuccessStatusCode,
            $"patch failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        Assert.Empty(await LegTagsAsync(split.HeaderId));
    }

    [Fact]
    public async Task The_register_reports_each_postings_own_tags()
    {
        // What the editor reads. Without it a split's postings come back with no
        // tags and the next save would look like a deliberate clear.
        var split = await ProjectSplitAsync("Project A", "Project B");
        await using var _f = split.Factory;

        // The SOURCE account: both postings sit on it, so the register collapses
        // them into one group entry whose legs are the postings. Reading a
        // category account instead gives target-side single entries and no legs,
        // so the test would find nothing to check.
        var page = await split.Client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{split.Ledger.LedgerId}/transactions?account_id={split.BankId}");

        var entries = page.GetProperty("entries").EnumerateArray().ToList();
        var group = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "group");

        // A split-side line shows its own tags: they are the row's tags, and no raw
        // leg field is on the row.
        var seen = new List<string>();
        foreach (var leg in group.GetProperty("legs").EnumerateArray())
        {
            Assert.False(leg.TryGetProperty("legTags", out _));
            foreach (var t in leg.GetProperty("tags").EnumerateArray())
                seen.Add(t.GetString()!);
        }

        Assert.Contains("Project A", seen);
        Assert.Contains("Project B", seen);
    }

    /// <summary>The split's two postings as a client that read them would send them back.</summary>
    private async Task<PatchTransactionPostings> EchoPostingsAsync(Split split, decimal firstAmount)
    {
        await using var db = Db.NewDbContext();
        var bankLegs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == split.HeaderId && l.AccountId == split.BankId)
            .OrderBy(l => l.PostingIndex)
            .ToListAsync();
        return new PatchTransactionPostings
        {
            SourceAccountId = split.BankId,
            Items =
            [
                new TransactionPosting { LegId = bankLegs[0].Id, CounterpartyAccountId = split.SuppliesId, Amount = firstAmount },
                new TransactionPosting { LegId = bankLegs[1].Id, CounterpartyAccountId = split.SuppliesId, Amount = bankLegs[1].Amount },
            ],
        };
    }
}
