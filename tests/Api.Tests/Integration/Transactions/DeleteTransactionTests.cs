using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>DELETE /api/ledgers/{ledgerId}/transactions/{headerId}</c>.
/// Verifies the external-id-based policy: rows with no external_id
/// (manual entries) are hard-deleted (cascading to legs + override
/// rows); rows with external_id (feed / import-keyed) are soft-hidden
/// via is_hidden=true so a subsequent re-source doesn't resurrect
/// them.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DeleteTransactionTests
{
    private readonly PostgresFixture _fixture;

    public DeleteTransactionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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
    /// Insert one manual single-row transaction (no external_id). Returns
    /// the header id.
    /// </summary>
    private async Task<Guid> SeedManualHeaderAsync(SyntheticLedger ledger, Guid bankId, Guid catId)
    {
        var (fromLegId, _) = await ledger.AddTransactionPairAsync(
            bankId, catId, -25m,
            new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc));
        await using var db = _fixture.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.Id == fromLegId)
            .Select(l => l.HeaderId)
            .SingleAsync();
    }

    /// <summary>
    /// Insert one feed-sourced single-row transaction with an
    /// <c>external_id</c>. Returns the header id. Uses raw SQL via
    /// the DbContext so the test exercises the importer-shaped row
    /// without going through the importer.
    /// </summary>
    private async Task<Guid> SeedFeedHeaderAsync(
        SyntheticLedger ledger, Guid bankId, Guid catId, string externalId)
    {
        var headerId = Guid.NewGuid();
        var fromLegId = Guid.NewGuid();
        var toLegId = Guid.NewGuid();
        var postedAt = new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc);

        await using var db = _fixture.NewDbContext();
        // Mig 107: origin/provider_key. An MD-imported QIF row.
        // needs_review=true mirrors a freshly-ingested feed row awaiting
        // acceptance — the state that must be cleared on soft-delete (D3).
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers
                (id, ledger_id, origin, provider_key, external_id, payee, posted_at, needs_review)
            VALUES
                ({headerId}, {ledger.LedgerId}, 'file_import', 'qif',
                 {externalId}, 'feed-payee', {postedAt}, true);
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES
                ({fromLegId}, {headerId}, {ledger.LedgerId}, {bankId}, 0, -25.0),
                ({toLegId},   {headerId}, {ledger.LedgerId}, {catId},  0,  25.0);");
        return headerId;
    }

    [Fact]
    public async Task Delete_manual_header_hard_deletes_and_cascades_legs()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("groceries");
        var headerId = await SeedManualHeaderAsync(ledger, bank.Id, cat.Id);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DeleteTransactionResponse>();
        Assert.NotNull(body);
        Assert.Equal("hard-deleted", body!.Kind);

        await using var db = _fixture.NewDbContext();
        var headerExists = await db.TxnHeaders.AnyAsync(h => h.Id == headerId);
        var legsExist = await db.TxnLegs.AnyAsync(l => l.HeaderId == headerId);
        Assert.False(headerExists);
        Assert.False(legsExist);
    }

    [Fact]
    public async Task Delete_feed_header_soft_hides_and_preserves_legs()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("groceries");
        var headerId = await SeedFeedHeaderAsync(
            ledger, bank.Id, cat.Id, externalId: "md-test-12345");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DeleteTransactionResponse>();
        Assert.NotNull(body);
        Assert.Equal("soft-hidden", body!.Kind);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.True(header.IsHidden);
        // ADR-0052 D3: soft-delete clears needs_review so a deleted row can't
        // linger in the review queue as hidden-but-pending (the limbo that
        // stranded 55 feed rows on the real ledger).
        Assert.False(header.NeedsReview);
        Assert.Equal("md-test-12345", header.ExternalId);

        var legsCount = await db.TxnLegs.CountAsync(l => l.HeaderId == headerId);
        Assert.Equal(2, legsCount);
    }

    [Fact]
    public async Task Delete_cross_ledger_header_returns_422()
    {
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        var bankB = await ledgerB.AddBankAccountAsync("checking");
        var catB = await ledgerB.AddCategoryAsync("groceries");
        var headerB = await SeedManualHeaderAsync(ledgerB, bankB.Id, catB.Id);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledgerA);

        var response = await client.DeleteAsync(
            $"/api/ledgers/{ledgerA.LedgerId}/transactions/{headerB}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("transaction-not-in-ledger", body);

        await using var db = _fixture.NewDbContext();
        var stillThere = await db.TxnHeaders.AnyAsync(h => h.Id == headerB);
        Assert.True(stillThere);
    }
}
