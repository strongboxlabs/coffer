using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ledgers;

/// <summary>
/// PATCH (rename) + DELETE (full wipe) on <c>/api/ledgers/{id}</c>
/// (ADR-0020). Owner-only; delete clears the entire ledger footprint via
/// <c>fn_ledger_delete</c> (migration 141). Real cookie auth so the
/// owner/editor gate is exercised per-user.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RenameDeleteLedgerEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public RenameDeleteLedgerEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static HttpRequestMessage Patch(Guid ledgerId, object body) =>
        new(HttpMethod.Patch, $"/api/ledgers/{ledgerId}") { Content = JsonContent.Create(body) };

    /// <summary>Grant a second user a non-owner role on a ledger.</summary>
    private async Task GrantAsync(Guid userId, Guid ledgerId, string role)
    {
        await using var db = _fixture.NewDbContext();
        db.UserLedgerGrants.Add(new UserLedgerGrantRow { UserId = userId, LedgerId = ledgerId, Role = role });
        await db.SaveChangesAsync();
    }

    // -- rename -------------------------------------------------------------

    [Fact]
    public async Task Owner_can_rename_the_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var resp = await client.SendAsync(Patch(alice.LedgerId, new { name = "Renamed Books" }));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var rows = (await client.GetFromJsonAsync<LedgerSummary[]>("/api/ledgers"))!;
        Assert.Equal("Renamed Books", rows.Single(r => r.Id == alice.LedgerId).Name);
    }

    [Fact]
    public async Task Rename_rejects_an_empty_name()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var resp = await client.SendAsync(Patch(alice.LedgerId, new { name = "   " }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-name-required", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rename_by_a_non_owner_is_rejected()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await GrantAsync(bob.UserId, alice.LedgerId, "editor");   // editor, not owner

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.SendAsync(Patch(alice.LedgerId, new { name = "Hijacked" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-owner", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rename_of_an_invisible_ledger_is_rejected()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);   // no grant on alice's ledger

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.SendAsync(Patch(alice.LedgerId, new { name = "x" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible", doc.RootElement.GetProperty("code").GetString());
    }

    // -- delete -------------------------------------------------------------

    [Fact]
    public async Task Owner_delete_wipes_the_entire_footprint_and_leaves_other_ledgers_intact()
    {
        // Seed a broad footprint on alice's ledger.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await alice.AddBankAccountAsync("checking");
        var dining = await alice.AddCategoryAsync("Dining");
        var (legId, _) = await alice.AddTransactionPairAsync(bank.Id, dining.Id, -42m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var headerId = await alice.ResolveHeaderIdAsync(legId);
        await alice.AddTagAsync(headerId, "vacation");
        var brokerage = await alice.AddInvestmentAccountAsync("brokerage");
        var secId = await alice.AddSecurityAsync("ACME");
        await alice.AddHoldingAsync(brokerage.Id, secId, 10m, costBasis: 123.40m);
        await alice.AddSecurityPriceAsync(secId, 12.34m, new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));

        // A second ledger that must survive untouched.
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var otherBank = await other.AddBankAccountAsync("checking");
        await other.AddTransactionPairAsync(otherBank.Id,
            (await other.AddCategoryAsync("Dining")).Id, -7m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var resp = await client.DeleteAsync($"/api/ledgers/{alice.LedgerId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        var lid = alice.LedgerId;
        // Financial / RESTRICT tables — explicitly cleared.
        Assert.Equal(0, await db.Accounts.CountAsync(a => a.LedgerId == lid));
        Assert.Equal(0, await db.TxnHeaders.CountAsync(h => h.LedgerId == lid));
        Assert.Equal(0, await db.TxnLegs.CountAsync(l => l.LedgerId == lid));
        Assert.Equal(0, await db.TxnHeaderAccountBalances.CountAsync(b => b.LedgerId == lid));
        Assert.Equal(0, await db.Securities.CountAsync(s => s.LedgerId == lid));
        Assert.Equal(0, await db.Holdings.CountAsync(h => h.LedgerId == lid));
        Assert.Equal(0, await db.SecurityPrices.CountAsync(p => p.LedgerId == lid));
        Assert.Equal(0, await db.Tags.CountAsync(t => t.LedgerId == lid));
        // Grants — cleared by the CASCADE off the ledgers row delete.
        Assert.Equal(0, await db.UserLedgerGrants.CountAsync(g => g.LedgerId == lid));
        // The ledger row itself is gone.
        Assert.False(await db.Ledgers.AnyAsync(l => l.Id == lid));

        // Isolation: the other ledger is fully intact.
        Assert.True(await db.Ledgers.AnyAsync(l => l.Id == other.LedgerId));
        Assert.True(await db.Accounts.AnyAsync(a => a.LedgerId == other.LedgerId));
        Assert.True(await db.TxnHeaders.AnyAsync(h => h.LedgerId == other.LedgerId));
    }

    [Fact]
    public async Task Delete_by_a_non_owner_is_rejected()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await GrantAsync(bob.UserId, alice.LedgerId, "viewer");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var resp = await bobClient.DeleteAsync($"/api/ledgers/{alice.LedgerId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-owner", doc.RootElement.GetProperty("code").GetString());

        // Nothing deleted.
        await using var db = _fixture.NewDbContext();
        Assert.True(await db.Ledgers.AnyAsync(l => l.Id == alice.LedgerId));
    }

    [Fact]
    public async Task Owner_can_delete_their_only_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, alice);

        var resp = await client.DeleteAsync($"/api/ledgers/{alice.LedgerId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // The picker is now empty for this user (they can create a new one).
        var rows = (await client.GetFromJsonAsync<LedgerSummary[]>("/api/ledgers"))!;
        Assert.DoesNotContain(rows, r => r.Id == alice.LedgerId);
    }
}
