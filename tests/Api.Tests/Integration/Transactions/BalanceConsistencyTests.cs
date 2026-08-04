using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks that the <c>BalanceRecomputeInterceptor</c>
/// (mig 102 / ADR-0032 / ADR-0034) keeps
/// <c>txn_header_account_balances</c> coherent across every API
/// writer. Each test exercises a writer through the HTTP surface,
/// then queries the DB to assert the expected balance rows are
/// present with the expected values — the invariant the trigger
/// family used to guard, now guarded by an EF
/// <c>SaveChangesInterceptor</c>.
///
/// Specifically targets the merge-with-reshape scenario that
/// surfaced the underlying batch-fire-order bug in the trigger
/// family — proves the interceptor handles it cleanly because the
/// recompute fires once per <c>SaveChanges</c> with a complete
/// <c>ChangeTracker</c> snapshot.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BalanceConsistencyTests
{
    private readonly PostgresFixture _fixture;

    public BalanceConsistencyTests(PostgresFixture fixture) => _fixture = fixture;

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
    /// POST a single-posting transaction; assert balance rows exist
    /// for both the source and counterparty accounts with the right
    /// net_amount.
    /// </summary>
    [Fact]
    public async Task Bank_create_populates_balance_rows_for_both_accounts()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -42.50m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();
        var rows = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == headerId)
            .OrderBy(r => r.AccountId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AccountId == bank.Id && r.NetAmount == -42.50m);
        Assert.Contains(rows, r => r.AccountId == groceries.Id && r.NetAmount == 42.50m);
    }

    /// <summary>
    /// The merge-with-reshape pattern that broke the trigger family.
    /// Two manual transactions on different categories; a PATCH that
    /// re-categorises one AND merges the other into it. The survivor
    /// should end up with balance rows on Checking + the NEW category;
    /// the loser's rows should be wiped (it's now is_merged_into).
    /// The Uncategorized / old category should have no row for either
    /// header.
    /// </summary>
    [Fact]
    public async Task Bank_patch_with_reshape_and_merge_keeps_balance_rows_coherent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddCategoryAsync("uncategorized");
        var newCategory = await ledger.AddCategoryAsync("real-category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Seed: SURVIVOR (will be the patched row; starts on uncategorized).
        var survivorResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Payee = "MerchantA",
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = uncategorized.Id, Amount = -25m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, survivorResp.StatusCode);
        var survivorId = (await survivorResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Seed: LOSER (will be merged into survivor). Older date so the
        // recompute window catches the survivor when the merge fires.
        var loserResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                Payee = "MerchantA-manual",
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = newCategory.Id, Amount = -25m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, loserResp.StatusCode);
        var loserId = (await loserResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Mark the survivor needs_review so the merge gate accepts it
        // as a merge target (the SPA's actual merge UX is on bank-feed
        // rows; mirror that state).
        await using (var seedDb = _fixture.NewDbContext())
        {
            await seedDb.TxnHeaders
                .Where(h => h.Id == survivorId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true));
        }

        // The PATCH: reshape survivor's counter from uncategorized to
        // newCategory AND merge in the loser. This is the exact shape
        // that broke the trigger family.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{survivorId}",
            new PatchTransactionRequest
            {
                MergeFromHeaderId = loserId,
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            CounterpartyAccountId = newCategory.Id,
                            Amount = -25m,
                        },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        // Inverted-merge direction: the manual row (created above as
        // `loserId`) is the actual SURVIVOR. The editor row
        // (`survivorId` in the legacy var naming) became the loser.
        // The manual survivor's legs (bank + newCategory) drive its
        // balance rows; the editor's prior balance rows are wiped.
        await using var db = _fixture.NewDbContext();
        var actualSurvivorBalances = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == loserId)
            .OrderBy(r => r.AccountId)
            .ToListAsync();

        Assert.Equal(2, actualSurvivorBalances.Count);
        Assert.Contains(actualSurvivorBalances, r => r.AccountId == bank.Id);
        Assert.Contains(actualSurvivorBalances, r => r.AccountId == newCategory.Id);
        Assert.DoesNotContain(actualSurvivorBalances, r => r.AccountId == uncategorized.Id);

        // The editor row (legacy `survivorId`) is now the loser —
        // no balance rows (is_merged_into excludes from header_net
        // CTE → recompute wipes any prior rows).
        var actualLoserBalances = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == survivorId)
            .ToListAsync();
        Assert.Empty(actualLoserBalances);
    }

    /// <summary>
    /// BulkDelete uses <c>ExecuteDeleteAsync</c>, which bypasses the
    /// EF ChangeTracker — the interceptor can't see it. The repository
    /// makes an explicit recompute call (the #4 call-site pattern) for
    /// the hard-delete branch. This test confirms it works: a manual
    /// transaction is hard-deleted in a bulk operation, and afterward
    /// no stale balance rows remain on either affected account.
    /// </summary>
    [Fact]
    public async Task Bulk_hard_delete_invokes_explicit_recompute_no_stale_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -10m },
                },
            });
        var headerId = (await createResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Confirm balance rows present before bulk delete.
        await using (var db = _fixture.NewDbContext())
        {
            var preDelete = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == headerId).CountAsync();
            Assert.Equal(2, preDelete);
        }

        // Hard-delete via bulk endpoint (no external_id → hard-delete branch).
        // The bulk endpoint wraps the SelectionRequest in a `{ selection }`
        // body — matches the SPA's request shape.
        var deleteResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new { selection = new { kind = "explicit", headerIds = new[] { headerId } } });
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // After delete: no balance rows for the deleted header. The
        // FK cascade dropped them; the explicit recompute call in
        // BulkDeleteAsync re-derived state for the affected accounts
        // (Checking + groceries) so nothing stale lingers.
        await using var dbAfter = _fixture.NewDbContext();
        var postDelete = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == headerId).CountAsync();
        Assert.Equal(0, postDelete);
    }

    /// <summary>
    /// Mig 103: <c>is_hidden=true</c> excludes a header from the
    /// balance walk. Seed three transactions in time order, mark the
    /// middle one feed-imported (external_id non-null) so the DELETE
    /// endpoint routes it to the soft-hide branch
    /// (<c>header.IsHidden = true; SaveChanges</c>). After save, the
    /// third transaction's <c>balance_after</c> on the checking
    /// account must reflect the first + third only.
    /// </summary>
    [Fact]
    public async Task Soft_hide_via_delete_endpoint_excludes_header_from_balance_walk()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> createAsync(int day, decimal amount)
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

        var h1 = await createAsync(10, -10m);
        var h2 = await createAsync(12, -20m); // will be hidden
        var h3 = await createAsync(14, -30m);

        // Mark h2 as feed-imported so DELETE routes to the soft-hide
        // branch (external_id non-null). external_id isn't balance-
        // relevant, so the ExecuteUpdate bypass is fine here.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders
                .Where(h => h.Id == h2)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExternalId, "feed-test-h2"));
        }

        // Sanity: pre-hide balance on bank account.
        await using (var db = _fixture.NewDbContext())
        {
            var h3Pre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(-60m, h3Pre.BalanceAfter);
        }

        var deleteResp = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{h2}");
        Assert.True(
            deleteResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)deleteResp.StatusCode}: {await deleteResp.Content.ReadAsStringAsync()}");

        // h3's balance now reflects h1 + h3 only.
        await using (var db = _fixture.NewDbContext())
        {
            var h3Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == h3 && r.AccountId == bank.Id);
            Assert.Equal(-40m, h3Post.BalanceAfter);

            // h2's balance row is gone (recompute's DELETE step ran;
            // the INSERT skipped h2 because is_hidden=true).
            var h2Post = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == h2).CountAsync();
            Assert.Equal(0, h2Post);
        }
    }

    /// <summary>
    /// Mig 103 + BulkDeleteAsync's #4 recompute call: the bulk
    /// soft-hide branch uses <c>ExecuteUpdateAsync</c> which bypasses
    /// the EF ChangeTracker — the repository captures the affected
    /// (account, posted_at) pairs and invokes
    /// <see cref="BalanceRecomputeService"/> explicitly, same as the
    /// hard-delete branch. This test seeds a feed-imported header
    /// (external_id non-null → soft-hide branch), bulk-deletes it,
    /// and asserts the downstream manual transaction's balance walks
    /// past it.
    /// </summary>
    [Fact]
    public async Task Bulk_soft_hide_invokes_explicit_recompute()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Earlier header — will be marked as feed-imported (external_id
        // non-null) so the bulk endpoint routes it to the soft-hide branch.
        var feedResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -100m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, feedResp.StatusCode);
        var feedHeaderId = (await feedResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Stamp external_id so the bulk endpoint takes the soft-hide
        // path. ExecuteUpdateAsync bypasses the interceptor here, but
        // external_id isn't balance-relevant, so that's fine for setup.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders
                .Where(h => h.Id == feedHeaderId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExternalId, "feed-test-001"));
        }

        // Later manual header — downstream of the feed row.
        var laterResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -25m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, laterResp.StatusCode);
        var laterHeaderId = (await laterResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Pre-bulk-delete: later header's balance = -125 (-100 + -25).
        await using (var db = _fixture.NewDbContext())
        {
            var laterPre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == laterHeaderId && r.AccountId == bank.Id);
            Assert.Equal(-125m, laterPre.BalanceAfter);
        }

        var bulkResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new { selection = new { kind = "explicit", headerIds = new[] { feedHeaderId } } });
        Assert.Equal(HttpStatusCode.OK, bulkResp.StatusCode);
        using var bulkDoc = JsonDocument.Parse(await bulkResp.Content.ReadAsStringAsync());
        Assert.Equal(0, bulkDoc.RootElement.GetProperty("hardDeleted").GetInt32());
        Assert.Equal(1, bulkDoc.RootElement.GetProperty("softHidden").GetInt32());

        // Post-bulk-delete: later header's balance walks past the
        // hidden feed row → -25 (just the later txn).
        await using (var dbAfter = _fixture.NewDbContext())
        {
            var laterPost = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == laterHeaderId && r.AccountId == bank.Id);
            Assert.Equal(-25m, laterPost.BalanceAfter);

            // Feed header's balance row is gone (recompute excluded it).
            var feedPost = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == feedHeaderId).CountAsync();
            Assert.Equal(0, feedPost);
        }
    }

    /// <summary>
    /// Regression: moving a transaction's posted DATE LATER must
    /// recompute the rows it "vacated" (between the old and new date),
    /// not just from the new date forward. Seed three dated rows; PATCH
    /// the middle one to AFTER the last; the row that was after it must
    /// pick up the freed balance. Before the
    /// <c>LegDerivedRecomputeInterceptor</c> anchored at MIN(old, new),
    /// the vacated rows stayed drifted by the moved txn's amount until a
    /// manual Verify-balances.
    /// </summary>
    [Fact]
    public async Task Patch_posted_at_later_recomputes_the_vacated_range()
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

        var tA = await CreateAsync(5, 1000m);   // May 5  -> 1000
        var tX = await CreateAsync(10, -200m);  // May 10 -> 800
        var tB = await CreateAsync(15, 50m);    // May 15 -> 850

        // Sanity: before the move, tB sits at 850 (1000 - 200 + 50).
        await using (var db = _fixture.NewDbContext())
        {
            var tBpre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tB && r.AccountId == bank.Id);
            Assert.Equal(850m, tBpre.BalanceAfter);
        }

        // Move tX to May 20 — AFTER tB. Mimics the editor's date edit
        // (full body: new PostedAt + the unchanged posting).
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tX}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -200m },
                    },
                },
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        // New date order: tA (1000) -> tB (1050) -> tX (850). tB is the
        // vacated row — it must reflect 1000 + 50 = 1050, NOT the stale
        // 850 it would keep if only [new-date, ...] were recomputed.
        await using (var db = _fixture.NewDbContext())
        {
            var tAbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tA && r.AccountId == bank.Id);
            var tBbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tB && r.AccountId == bank.Id);
            var tXbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tX && r.AccountId == bank.Id);
            Assert.Equal(1000m, tAbal.BalanceAfter);
            Assert.Equal(1050m, tBbal.BalanceAfter);
            Assert.Equal(850m, tXbal.BalanceAfter);
        }
    }

    /// <summary>
    /// Investment Create inserts legs as EF-tracked rows, so both
    /// LegDerivedRecomputeInterceptor (balances + posting counts) and
    /// HoldingsRecomputeInterceptor (holdings + lots) fire from the
    /// ChangeTracker on the persisting SaveChanges — no explicit
    /// recompute call (the insert_investment_legs TVF that once forced
    /// hand-driven recomputes was retired in mig 120). This test proves
    /// both recomputes fired by asserting balance rows on BOTH leg
    /// accounts AND a holdings + lot row for the bought security.
    /// </summary>
    [Fact]
    public async Task Investment_buy_populates_balance_holdings_and_lot()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 650m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var db = _fixture.NewDbContext();

        // Balance rows for both legs (the brokerage cash side and the
        // holdings-sibling side). Without the explicit BalanceRecompute
        // call, these rows would be absent because BalanceRecompute-
        // Interceptor can't see TVF-inserted legs.
        var balances = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == headerId)
            .OrderBy(r => r.AccountId)
            .ToListAsync();
        Assert.Equal(2, balances.Count);
        Assert.Contains(balances, r => r.AccountId == brokerage.Id     && r.NetAmount == -6500m);
        Assert.Contains(balances, r => r.AccountId == holdingsAccountId && r.NetAmount == 6500m);

        // Holdings row + lot. Without the explicit HoldingsRecompute
        // call (which runs recompute_holdings_cost_basis's auto-create
        // path), GetHoldingIdAsync's SingleAsync would have thrown
        // "Sequence contains no elements" — the test would never
        // reach this assertion.
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(10m, holding.Quantity);
        Assert.Equal(6500m, holding.CostBasis);

        var lot = await db.Lots.AsNoTracking()
            .SingleAsync(l => l.HoldingId == holding.Id);
        Assert.Equal(10m, lot.Quantity);
        Assert.False(lot.IsClosed);
    }
}
