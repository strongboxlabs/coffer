using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Per-posting tags end to end (ADR-0103, migration 243).
/// </summary>
/// <remarks>
/// The rental-property case that reopened ADR-0009: one header, three postings,
/// and the question the tags exist to answer is which posting the money went to.
/// These go through HTTP because the contract — what <c>null</c> means versus
/// <c>[]</c> on a list that is replaced wholesale — is the part that can be wrong
/// while every repository method is right.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LegTagsTests
{
    private readonly PostgresFixture _fixture;

    public LegTagsTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime Posted = new(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>The tag names on each source-side leg of a header, by leg id.</summary>
    private async Task<Dictionary<Guid, List<string>>> LegTagsAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
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

    private async Task<(Guid HeaderId, Guid BankId, HttpClient Client, SyntheticLedger Ledger, ApiFactory Factory)>
        RentalSplitAsync(params string?[] postingTags)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var rent = await ledger.AddCategoryAsync("Rent");

        var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var client = await ClientAsync(factory, ledger);

        var postings = postingTags.Select(t => new TransactionPosting
        {
            CounterpartyAccountId = rent.Id,
            Amount = -100m,
            Tags = t is null ? null : new[] { t },
        }).ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                Payee = "Rent",
                SourceAccountId = bank.Id,
                Postings = postings,
                Tags = new[] { "rental" },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("headerId").GetGuid(), bank.Id, client, ledger, factory);
    }

    [Fact]
    public async Task Each_posting_keeps_its_own_tag()
    {
        var (headerId, _, _, ledger, factory) = await RentalSplitAsync("Property A", "Property B");
        await using var _f = factory;

        var byLeg = await LegTagsAsync(headerId);
        Assert.Equal(2, byLeg.Count);
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Property A" }));
        Assert.Contains(byLeg, kv => kv.Value.SequenceEqual(new[] { "Property B" }));

        // The header keeps its OWN tag and does not absorb the postings' —
        // neither level is derived from the other (ADR-0103 D1).
        await using var db = _fixture.NewDbContext();
        var headerTags = await (
            from ht in db.TxnHeaderTags.AsNoTracking()
            join t in db.Tags.AsNoTracking() on ht.TagId equals t.Id
            where ht.HeaderId == headerId
            select t.Name).ToListAsync();
        Assert.Equal(["rental"], headerTags);
        Assert.DoesNotContain("Property A", headerTags);
    }

    [Fact]
    public async Task A_name_used_at_both_levels_is_one_dictionary_row()
    {
        // The tracker-pending case: the header and a posting both introduce
        // "rental" in ONE request. Resolving per call without consulting pending
        // inserts would create the tag twice and leave the ledger with two
        // identically named tags nobody can tell apart.
        var (headerId, _, _, ledger, factory) = await RentalSplitAsync("rental");
        await using var _f = factory;

        await using var db = _fixture.NewDbContext();
        var rows = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == ledger.LedgerId && t.Name == "rental")
            .ToListAsync();
        Assert.Single(rows);

        var byLeg = await LegTagsAsync(headerId);
        Assert.Equal(["rental"], Assert.Single(byLeg).Value);
    }

    [Fact]
    public async Task A_patch_that_omits_tags_leaves_them_alone()
    {
        // The trap this contract exists for. The postings list is replaced
        // WHOLESALE, so a client that reads a transaction and echoes its postings
        // back — every client written before leg tags existed — must not strip
        // them by omission.
        var (headerId, _, client, ledger, factory) = await RentalSplitAsync("Property A");
        await using var _f = factory;

        var legId = Assert.Single(await LegTagsAsync(headerId)).Key;

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = await LegAccountAsync(legId),
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = legId,
                            CounterpartyAccountId = (await LegCounterpartyAsync(legId)),
                            Amount = -150m,
                            // Tags deliberately omitted.
                        },
                    },
                },
            });
        Assert.True(resp.IsSuccessStatusCode,
            $"patch failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        var byLeg = await LegTagsAsync(headerId);
        Assert.Equal(["Property A"], Assert.Single(byLeg).Value);
    }

    [Fact]
    public async Task A_patch_with_an_empty_list_clears_them()
    {
        // The other half of the contract: omitted means leave alone, [] means
        // clear. Without both, there is no way to remove a posting's last tag.
        var (headerId, _, client, ledger, factory) = await RentalSplitAsync("Property A");
        await using var _f = factory;

        var legId = Assert.Single(await LegTagsAsync(headerId)).Key;

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = await LegAccountAsync(legId),
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = legId,
                            CounterpartyAccountId = (await LegCounterpartyAsync(legId)),
                            Amount = -100m,
                            Tags = Array.Empty<string>(),
                        },
                    },
                },
            });
        Assert.True(resp.IsSuccessStatusCode,
            $"patch failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        Assert.Empty(await LegTagsAsync(headerId));
    }

    [Fact]
    public async Task The_register_reports_each_postings_own_tags()
    {
        // What the editor reads. Without it a split's postings come back with no
        // tags and the next save would look like a deliberate clear.
        var (headerId, bankId, client, ledger, factory) = await RentalSplitAsync("Property A", "Property B");
        await using var _f = factory;

        // The SOURCE account: both postings sit on it, so the register collapses
        // them into one group entry whose legs are the postings. Reading a
        // category account instead gives target-side single entries and no legs,
        // which is how an earlier version of this test found nothing and said so.
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bankId}");

        var entries = page.GetProperty("entries").EnumerateArray().ToList();
        var group = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "group");

        var seen = new List<string>();
        foreach (var leg in group.GetProperty("legs").EnumerateArray())
            foreach (var t in leg.GetProperty("legTags").EnumerateArray())
                seen.Add(t.GetString()!);

        Assert.Contains("Property A", seen);
        Assert.Contains("Property B", seen);
    }

    /// <summary>The account the source-side leg sits on.</summary>
    private async Task<Guid> LegAccountAsync(Guid sourceLegId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.Id == sourceLegId).Select(l => l.AccountId).SingleAsync();
    }

    private async Task<Guid> LegCounterpartyAsync(Guid sourceLegId)
    {
        await using var db = _fixture.NewDbContext();
        var leg = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == sourceLegId);
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == leg.HeaderId
                        && l.PostingIndex == leg.PostingIndex
                        && l.Id != leg.Id)
            .Select(l => l.AccountId)
            .FirstAsync();
    }
}
