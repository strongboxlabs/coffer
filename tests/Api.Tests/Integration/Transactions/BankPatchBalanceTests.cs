using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Balance-correctness proofs for the BANK transaction PATCH surface.
/// Each test seeds a checking account + category, lays down a chain of
/// dated single-posting rows to establish a running-balance order, then
/// applies one PATCH mutation and asserts the exact resulting
/// <c>balance_after</c> / <c>net_amount</c> values read straight off
/// <c>txn_header_account_balances</c> — the independent, precise oracle
/// (hand-computed expecteds, NOT a reuse of the production recompute).
///
/// Distinct posted dates per row keep canonical <c>(posted_at, seq)</c>
/// order unambiguous so the asserted downstream balances are
/// deterministic. Each test mints its OWN <see cref="SyntheticLedger"/>
/// (atomic isolation per the API engineering standard).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BankPatchBalanceTests
{
    private readonly PostgresFixture _fixture;

    public BankPatchBalanceTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>
    /// Edit a middle row's posting amount. Every downstream row's running
    /// balance must shift by exactly the delta; the patched row's
    /// net_amount + balance update too.
    /// </summary>
    [Fact]
    public async Task Patch_amount_change_shifts_downstream_balances_by_delta()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await CreateAsync(5, 100m);   // May 5:  the opening deposit
        var h2 = await CreateAsync(10, -30m);  // May 10: an outflow (will be patched)
        var h3 = await CreateAsync(15, -20m);  // May 15: a later outflow

        // Sanity: the pre-patch chain is the running sum of the three.
        await using (var db = _fixture.NewDbContext())
        {
            var h3Pre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(50m, h3Pre.BalanceAfter);
        }

        // PATCH h2's amount to a larger outflow. h1 is untouched; h2 and every
        // row after it shift by the delta between the new and old amount.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -50m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            var h2Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id);
            Assert.Equal(-50m, h2Post.NetAmount);
            Assert.Equal(50m, h2Post.BalanceAfter);

            var h3Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(30m, h3Post.BalanceAfter); // shifted down by the same delta as h2
        }
    }

    /// <summary>
    /// Convert a single-posting row into a 2-posting split on the same
    /// source account but a larger total. The row's net on the bank
    /// account becomes the sum of the two legs; downstream balances
    /// reflect the new total.
    /// </summary>
    [Fact]
    public async Task Patch_add_split_updates_row_net_and_downstream_balances()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var fuel = await ledger.AddCategoryAsync("fuel");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount, Guid counterparty)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await CreateAsync(5, 200m, groceries.Id);    // May 5:  the opening deposit
        var h2 = await CreateAsync(10, -40m, groceries.Id);   // May 10: one posting (single -> split)
        var h3 = await CreateAsync(15, -10m, groceries.Id);   // May 15: a later outflow

        await using (var db = _fixture.NewDbContext())
        {
            var h3Pre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(150m, h3Pre.BalanceAfter);
        }

        // PATCH h2 into a 2-posting split: the groceries posting unchanged plus a
        // fuel posting, so h2's net on the bank account becomes their sum and every
        // balance from h2 on drops by the fuel amount.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -40m },
                        new TransactionPosting { CounterpartyAccountId = fuel.Id, Amount = -25m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            // Bank row's net is the aggregate of both source-side legs.
            var h2Bank = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id);
            Assert.Equal(-65m, h2Bank.NetAmount);
            Assert.Equal(135m, h2Bank.BalanceAfter);

            var h3Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(125m, h3Post.BalanceAfter); // the deposit, less the split's total, less h3

            // Both counterparty categories now carry a row for h2.
            var fuelRow = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == fuel.Id);
            Assert.Equal(25m, fuelRow.NetAmount); // the paired leg is the posting's negation
        }
    }

    /// <summary>
    /// Collapse a 2-posting split back down to a single posting. The
    /// row's net + downstream balances follow the reduced total, and the
    /// dropped counterparty's balance row for this header disappears.
    /// </summary>
    [Fact]
    public async Task Patch_remove_split_collapses_to_single_posting()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var fuel = await ledger.AddCategoryAsync("fuel");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateSingleAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await CreateSingleAsync(5, 300m);  // May 5: the opening deposit

        // h2, May 10: a two-posting split, groceries plus fuel.
        var h2Resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -40m },
                    new TransactionPosting { CounterpartyAccountId = fuel.Id, Amount = -25m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, h2Resp.StatusCode);
        var h2 = (await h2Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        var h3 = await CreateSingleAsync(15, -10m); // May 15: a later outflow

        await using (var db = _fixture.NewDbContext())
        {
            var h3Pre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(225m, h3Pre.BalanceAfter); // the deposit, less the split's total, less h3

            // Pre-patch: fuel row exists for h2.
            var fuelPre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == h2 && r.AccountId == fuel.Id).CountAsync();
            Assert.Equal(1, fuelPre);
        }

        // PATCH h2 down to its groceries posting alone. Every balance from h2 on
        // recovers the fuel amount.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -40m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            var h2Bank = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id);
            Assert.Equal(-40m, h2Bank.NetAmount);
            Assert.Equal(260m, h2Bank.BalanceAfter);

            var h3Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(250m, h3Post.BalanceAfter); // the deposit, less the groceries posting, less h3

            // The dropped fuel leg's balance row for h2 is gone.
            var fuelPost = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == h2 && r.AccountId == fuel.Id).CountAsync();
            Assert.Equal(0, fuelPost);
        }
    }

    /// <summary>
    /// Recategorize — move a row's counterparty from one category to
    /// another. The source (bank) account is untouched in value; the old
    /// category's balance row for this header must disappear and the new
    /// category's must appear.
    /// </summary>
    [Fact]
    public async Task Patch_recategorize_moves_counterparty_balance_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var oldCategory = await ledger.AddCategoryAsync("old-category");
        var newCategory = await ledger.AddCategoryAsync("new-category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount, Guid counterparty)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await CreateAsync(5, 100m, oldCategory.Id);   // May 5:  the opening deposit
        var h2 = await CreateAsync(10, -60m, oldCategory.Id);  // May 10: an outflow (recategorize)
        var h3 = await CreateAsync(15, -10m, oldCategory.Id);  // May 15: a later outflow

        await using (var db = _fixture.NewDbContext())
        {
            // Pre-patch: h2's counterparty row is on oldCategory.
            var oldPre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == oldCategory.Id);
            Assert.Equal(60m, oldPre.NetAmount); // the paired leg is the posting's negation

            var newPreCount = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == h2 && r.AccountId == newCategory.Id).CountAsync();
            Assert.Equal(0, newPreCount);
        }

        // PATCH h2's posting: the same amount, counterparty moved from
        // oldCategory to newCategory. The bank chain is unchanged in value.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = newCategory.Id, Amount = -60m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            // Source (bank) value unchanged: h2 nets and sits exactly where it did.
            var h2Bank = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id);
            Assert.Equal(-60m, h2Bank.NetAmount);
            Assert.Equal(40m, h2Bank.BalanceAfter);

            // Old category's row for this header is gone.
            var oldPostCount = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == h2 && r.AccountId == oldCategory.Id).CountAsync();
            Assert.Equal(0, oldPostCount);

            // New category's row is present with the paired net.
            var newPost = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == newCategory.Id);
            Assert.Equal(60m, newPost.NetAmount);
        }
    }

    /// <summary>
    /// A recon-status flip is balance-neutral. Flipping a row's
    /// reconciliation state through <c>PUT .../{headerId}/recon-status</c>
    /// must NOT touch any balance row — guards against a status write
    /// spuriously re-deriving or drifting balances.
    /// </summary>
    [Fact]
    public async Task Recon_status_flip_is_balance_neutral()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        var h1 = await CreateAsync(5, 500m);   // May 5:  the opening deposit
        var h2 = await CreateAsync(10, -75m);  // May 10: an outflow (status will flip)
        var h3 = await CreateAsync(15, -25m);  // May 15: a later outflow

        decimal h2BankBefore, h3BankBefore;
        await using (var db = _fixture.NewDbContext())
        {
            h2BankBefore = (await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id)).BalanceAfter;
            h3BankBefore = (await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id)).BalanceAfter;
            Assert.Equal(425m, h2BankBefore);
            Assert.Equal(400m, h3BankBefore);
        }

        var reconResp = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = bank.Id });
        Assert.True(
            reconResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)reconResp.StatusCode}: {await reconResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            var h2BankAfter = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h2 && r.AccountId == bank.Id);
            var h3BankAfter = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(h2BankBefore, h2BankAfter.BalanceAfter);
            Assert.Equal(h3BankBefore, h3BankAfter.BalanceAfter);
            Assert.Equal(-75m, h2BankAfter.NetAmount);
        }
    }
}
