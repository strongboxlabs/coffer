using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Balance correctness for multi-account TRANSFERS — a posting whose
/// counterparty is a second asset account, so BOTH accounts' balance
/// chains move. A transfer is a plain <c>POST /transactions</c> whose
/// posting <see cref="TransactionPosting.CounterpartyAccountId"/> points
/// at a second bank account (one header, two legs summing to zero); the
/// counterparty leg drives a real balance chain on the savings account
/// just like the source leg does on checking. Each test asserts the
/// exact <c>balance_after</c> on BOTH accounts (the independent oracle).
/// Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransferBalanceTests
{
    private readonly PostgresFixture _fixture;

    public TransferBalanceTests(PostgresFixture fixture) => _fixture = fixture;

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

    private static async Task<Guid> CreateTransferAsync(
        HttpClient client, Guid ledgerId, Guid fromAccountId, Guid toAccountId,
        decimal amount, DateTime postedAt)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = "transfer",
                SourceAccountId = fromAccountId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = toAccountId, Amount = amount },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateBankTxnAsync(
        HttpClient client, Guid ledgerId, Guid bankId, Guid categoryId,
        decimal amount, DateTime postedAt)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                SourceAccountId = bankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = categoryId, Amount = amount },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    /// <summary>Create a transfer: one balance row on EACH account
    /// (-100 checking outflow, +100 savings inflow).</summary>
    [Fact]
    public async Task Create_transfer_populates_both_account_balance_chains()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var transferId = await CreateTransferAsync(
            client, ledger.LedgerId, checking.Id, savings.Id,
            amount: -100m, postedAt: new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        await using var db = _fixture.NewDbContext();
        var rows = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == transferId)
            .OrderBy(r => r.AccountId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var checkingRow = Assert.Single(rows, r => r.AccountId == checking.Id);
        var savingsRow = Assert.Single(rows, r => r.AccountId == savings.Id);
        Assert.Equal(-100m, checkingRow.NetAmount);
        Assert.Equal(-100m, checkingRow.BalanceAfter);
        Assert.Equal(100m, savingsRow.NetAmount);
        Assert.Equal(100m, savingsRow.BalanceAfter);
    }

    /// <summary>Edit the transfer amount: both legs shift in lockstep
    /// (checking -100 -> -250, savings +100 -> +250).</summary>
    [Fact]
    public async Task Edit_transfer_amount_shifts_both_accounts()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var transferId = await CreateTransferAsync(
            client, ledger.LedgerId, checking.Id, savings.Id,
            amount: -100m, postedAt: new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc));

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{transferId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = checking.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = savings.Id, Amount = -250m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using var db = _fixture.NewDbContext();
        var checkingRow = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == transferId && r.AccountId == checking.Id);
        var savingsRow = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == transferId && r.AccountId == savings.Id);
        Assert.Equal(-250m, checkingRow.NetAmount);
        Assert.Equal(-250m, checkingRow.BalanceAfter);
        Assert.Equal(250m, savingsRow.NetAmount);
        Assert.Equal(250m, savingsRow.BalanceAfter);
    }

    /// <summary>Move the transfer's date LATER past another checking row:
    /// the vacated checking row walks up; the transfer re-anchors; savings
    /// (only row) keeps its value.</summary>
    [Fact]
    public async Task Move_transfer_date_later_past_a_checking_row_recomputes_both_chains()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Checking: May 5 transfer -100 (-> -100), May 12 expense -30 (-> -130).
        var transferId = await CreateTransferAsync(
            client, ledger.LedgerId, checking.Id, savings.Id,
            amount: -100m, postedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var expenseId = await CreateBankTxnAsync(
            client, ledger.LedgerId, checking.Id, category.Id,
            amount: -30m, postedAt: new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.NewDbContext())
        {
            var transferPre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == transferId && r.AccountId == checking.Id);
            var expensePre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == expenseId && r.AccountId == checking.Id);
            Assert.Equal(-100m, transferPre.BalanceAfter);
            Assert.Equal(-130m, expensePre.BalanceAfter);
        }

        // Move the transfer to May 20 — AFTER the expense.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{transferId}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = checking.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = savings.Id, Amount = -100m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            // New checking order: expense (May 12) -> transfer (May 20).
            // The expense is now FIRST on checking -> -30 (NOT stale -130).
            var expensePost = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == expenseId && r.AccountId == checking.Id);
            var transferChecking = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == transferId && r.AccountId == checking.Id);
            Assert.Equal(-30m, expensePost.BalanceAfter);
            Assert.Equal(-130m, transferChecking.BalanceAfter);

            // Savings chain: still the only row, unchanged at +100.
            var transferSavings = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == transferId && r.AccountId == savings.Id);
            Assert.Equal(100m, transferSavings.NetAmount);
            Assert.Equal(100m, transferSavings.BalanceAfter);
        }
    }

    /// <summary>Delete the transfer: no stale rows on EITHER account, the
    /// downstream checking row walks as if it never existed, savings is
    /// empty.</summary>
    [Fact]
    public async Task Delete_transfer_leaves_no_stale_rows_on_either_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Checking: transfer (Jun 3, -100) then expense (Jun 9, -30).
        var transferId = await CreateTransferAsync(
            client, ledger.LedgerId, checking.Id, savings.Id,
            amount: -100m, postedAt: new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc));
        var expenseId = await CreateBankTxnAsync(
            client, ledger.LedgerId, checking.Id, category.Id,
            amount: -30m, postedAt: new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(2, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(r => r.HeaderId == transferId));
            var expensePre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == expenseId && r.AccountId == checking.Id);
            Assert.Equal(-130m, expensePre.BalanceAfter);
        }

        var deleteResp = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{transferId}");
        Assert.True(
            deleteResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)deleteResp.StatusCode}: {await deleteResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            // No rows survive for the deleted transfer on EITHER account.
            Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(r => r.HeaderId == transferId));

            // The downstream checking expense now reflects -30 alone.
            var expensePost = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == expenseId && r.AccountId == checking.Id);
            Assert.Equal(-30m, expensePost.BalanceAfter);

            // Savings chain is now empty.
            Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(r => r.AccountId == savings.Id));
        }
    }
}
