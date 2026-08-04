using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>PUT /api/ledgers/{ledgerId}/transactions/{headerId}/recon-status</c>.
/// Reconciliation is per-account (ADR-0082): status lives on the account's
/// leg in the <c>txn_leg_recon</c> overlay, so these assert the leg's row
/// (not the header) and exercise the cleared-audit consistency + the
/// per-account independence of a transfer.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PutReconStatusTests
{
    private readonly PostgresFixture _fixture;

    public PutReconStatusTests(PostgresFixture fixture)
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

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var (fromLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -25m,
            new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc));
        await using var db = _fixture.NewDbContext();
        var headerId = await db.TxnLegs.AsNoTracking()
            .Where(l => l.Id == fromLegId)
            .Select(l => l.HeaderId)
            .SingleAsync();
        return new Seed(ledger, bank.Id, groceries.Id, headerId, fromLegId);
    }

    private sealed record Seed(
        SyntheticLedger Ledger, Guid BankId, Guid GroceriesId, Guid HeaderId, Guid BankLegId);

    /// <summary>Read the per-leg recon row for a leg (null when none — i.e.
    /// the default 'uncleared' with no overlay row).</summary>
    private async Task<TxnLegReconRow?> ReconAsync(Guid legId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnLegRecon.AsNoTracking()
            .FirstOrDefaultAsync(r => r.LegId == legId);
    }

    [Fact]
    public async Task Put_marks_as_cleared_and_writes_audit_pair()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PutAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = seed.BankId });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recon = await ReconAsync(seed.BankLegId);
        Assert.NotNull(recon);
        Assert.Equal("cleared", recon!.Status);
        Assert.NotNull(recon.ClearedAt);
        Assert.Equal(seed.Ledger.UserId, recon.ClearedByUserId);
    }

    [Fact]
    public async Task Put_marks_as_reconciling_without_audit_pair()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PutAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "reconciling", AccountId = seed.BankId });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recon = await ReconAsync(seed.BankLegId);
        Assert.NotNull(recon);
        Assert.Equal("reconciling", recon!.Status);
        Assert.Null(recon.ClearedAt);
        Assert.Null(recon.ClearedByUserId);
    }

    [Fact]
    public async Task Put_uncleared_after_cleared_resets_the_audit_pair()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var first = await client.PutAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = seed.BankId });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Back to uncleared. Audit columns must clear so the overlay's
        // (status='cleared' ⇔ cleared_at IS NOT NULL) CHECK still holds.
        var second = await client.PutAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "uncleared", AccountId = seed.BankId });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var recon = await ReconAsync(seed.BankLegId);
        Assert.NotNull(recon);
        Assert.Equal("uncleared", recon!.Status);
        Assert.Null(recon.ClearedAt);
        Assert.Null(recon.ClearedByUserId);
    }

    [Fact]
    public async Task Put_clears_one_account_of_a_transfer_independently()
    {
        // ADR-0082 core: a transfer cleared in one account stays uncleared in
        // the other — status is per-account, not per-header.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var (checkingLegId, savingsLegId) = await ledger.AddTransactionPairAsync(
            checking.Id, savings.Id, -100m,
            new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc), payee: "transfer");
        Guid headerId;
        await using (var db = _fixture.NewDbContext())
        {
            headerId = await db.TxnLegs.AsNoTracking()
                .Where(l => l.Id == checkingLegId).Select(l => l.HeaderId).SingleAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Clear it in checking only.
        var resp = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = checking.Id });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // The resolved view (per-leg) shows cleared on checking, uncleared on savings.
        await using var db2 = _fixture.NewDbContext();
        var checkingStatus = await db2.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.HeaderId == headerId && rt.AccountId == checking.Id)
            .Select(rt => rt.Status).FirstAsync();
        var savingsStatus = await db2.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.HeaderId == headerId && rt.AccountId == savings.Id)
            .Select(rt => rt.Status).FirstAsync();
        Assert.Equal("cleared", checkingStatus);
        Assert.Equal("uncleared", savingsStatus);
    }

    [Fact]
    public async Task Put_with_invalid_status_returns_422()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PutAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "totally-not-a-status", AccountId = seed.BankId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("transaction-recon-status-invalid", body);
    }

    [Fact]
    public async Task Put_with_cross_ledger_header_returns_422()
    {
        var seedA = await SeedAsync();
        var seedB = await SeedAsync();

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seedA.Ledger);

        // Ledger A's user addressing ledger B's header via A's URL: no leg on
        // A's account for that header → transaction-not-in-ledger.
        var response = await client.PutAsJsonAsync(
            $"/api/ledgers/{seedA.Ledger.LedgerId}/transactions/{seedB.HeaderId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = seedA.BankId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("transaction-not-in-ledger", body);
    }
}
