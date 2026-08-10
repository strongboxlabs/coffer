using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for the bulk-action endpoints introduced in
/// ADR-0024: selection-summary, bulk-recon-status, bulk-delete. Each
/// test mints a fresh synthetic ledger + accounts + a small number of
/// seeded transactions, then drives the bulk endpoint over HTTP and
/// asserts against the resulting DB state.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BulkTransactionsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public BulkTransactionsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private sealed record SeededLedger(
        SyntheticLedger Ledger,
        Guid BankId,
        Guid GroceriesId,
        IReadOnlyList<Guid> HeaderIds);

    /// <summary>
    /// Seed N transaction pairs and capture each header id. Each pair
    /// is 1 day apart starting at firstPostedAt. Returns the ids in
    /// posted_at-ASC order (so caller can slice by recency easily).
    /// </summary>
    private async Task<SeededLedger> SeedAsync(int pairCount)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var firstPostedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var headerIds = new List<Guid>(pairCount);
        for (var i = 0; i < pairCount; i++)
        {
            await ledger.AddTransactionPairAsync(
                fromAccountId: bank.Id,
                toAccountId: groceries.Id,
                amount: -(10m + i),
                postedAt: firstPostedAt.AddDays(i),
                payee: $"merchant-{i:D3}");
        }
        // Walk txn_headers in posted_at ASC to capture the ids.
        await using var db = _fixture.NewDbContext();
        var ids = await db.TxnHeaders
            .Where(h => h.LedgerId == ledger.LedgerId)
            .OrderBy(h => h.PostedAt)
            .Select(h => h.Id)
            .ToListAsync();
        return new SeededLedger(ledger, bank.Id, groceries.Id, ids);
    }

    // ----------------------------------------------------------------
    // selection-summary
    // ----------------------------------------------------------------

    [Fact]
    public async Task Summary_explicit_returns_count_and_account_sum()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            kind = "explicit",
            headerIds = seed.HeaderIds.Take(2).ToArray(),
            accountId = seed.BankId,
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<SelectionSummary>();
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Count);
        // Bank leg amounts are -10 and -11.
        Assert.Equal(-21m, summary.SumOnAccount);
    }

    [Fact]
    public async Task Summary_all_excludes_a_row_hidden_via_override()
    {
        // Bulk selection must act on what the user SEES: a row hidden by
        // override is excluded from the count + account sum even though
        // its base row is visible. (ADR-0003 — effective visibility.)
        var seed = await SeedAsync(2);
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_overrides (header_id, ledger_id, is_hidden)
                VALUES ({seed.HeaderIds[1]}, {seed.Ledger.LedgerId}, true);");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            kind = "all",
            accountId = seed.BankId,
            statusFilter = "all",
            selectedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<SelectionSummary>();
        Assert.NotNull(summary);
        // Only the visible row (header[0], bank leg -10) survives.
        Assert.Equal(1, summary!.Count);
        Assert.Equal(-10m, summary.SumOnAccount);
    }

    [Fact]
    public async Task Summary_all_with_selectedAt_excludes_future_rows()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // SeedAsync seeded 3 rows at postedAt 2026-01-01 / 02 / 03 12:00
        // (and the seeder pins created_at = postedAt). Pick selectedAt
        // a few days after the last seed so all 3 are in-scope, and any
        // later-added row is out-of-scope.
        var selectedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var body = new
        {
            kind = "all",
            accountId = seed.BankId,
            statusFilter = "all",
            selectedAt,
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(3, summary.Count);

        // Add a row whose postedAt (and thus created_at) is after
        // selectedAt — predicate must exclude it.
        await seed.Ledger.AddTransactionPairAsync(
            fromAccountId: seed.BankId,
            toAccountId: seed.GroceriesId,
            amount: -99m,
            postedAt: new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            payee: "later");

        var response2 = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        var summary2 = (await response2.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(3, summary2.Count);
        Assert.Equal(summary.SumOnAccount, summary2.SumOnAccount);
    }

    [Fact]
    public async Task Summary_all_honors_excludeIds()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            kind = "all",
            accountId = seed.BankId,
            statusFilter = "all",
            selectedAt = DateTime.UtcNow.AddSeconds(1),
            excludeIds = new[] { seed.HeaderIds[0] },
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        var summary = (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(2, summary.Count);
    }

    [Fact]
    public async Task Summary_explicit_empty_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new { kind = "explicit", headerIds = Array.Empty<Guid>() };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Summary_ledger_wide_omits_sum()
    {
        var seed = await SeedAsync(2);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // No accountId — predicate spans the whole ledger.
        var body = new
        {
            kind = "all",
            statusFilter = "all",
            selectedAt = DateTime.UtcNow.AddSeconds(1),
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        var summary = (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(2, summary.Count);
        Assert.Null(summary.SumOnAccount);
    }

    // ----------------------------------------------------------------
    // bulk-recon-status
    // ----------------------------------------------------------------

    [Fact]
    public async Task BulkReconStatus_all_marks_every_matching_header_cleared()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new
            {
                kind = "all",
                accountId = seed.BankId,
                statusFilter = "all",
                selectedAt = DateTime.UtcNow.AddSeconds(1),
            },
            status = "cleared",
            accountId = seed.BankId,
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = (await response.Content.ReadFromJsonAsync<BulkReconStatusResponse>())!;
        Assert.Equal(3, payload.Updated);

        // Per-account (ADR-0082): every bank-account row is now cleared with a
        // cleared_at, read via the resolved view's per-leg status.
        await using var db = _fixture.NewDbContext();
        var rows = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.AccountId == seed.BankId)
            .Select(rt => new { rt.Status, rt.ClearedAt })
            .ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("cleared", r.Status));
        Assert.All(rows, r => Assert.NotNull(r.ClearedAt));
    }

    [Fact]
    public async Task BulkReconStatus_explicit_only_marks_listed_headers()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new
            {
                kind = "explicit",
                headerIds = new[] { seed.HeaderIds[0] },
            },
            status = "cleared",
            accountId = seed.BankId,
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Per-account: only the first header's bank leg is cleared.
        await using var db = _fixture.NewDbContext();
        var statuses = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.AccountId == seed.BankId)
            .OrderBy(rt => rt.PostedAt)
            .Select(rt => rt.Status)
            .ToListAsync();
        Assert.Equal(new[] { "cleared", "uncleared", "uncleared" }, statuses);
    }

    [Fact]
    public async Task BulkReconStatus_unclear_clears_audit_columns()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // First: mark cleared.
        var mark = new
        {
            selection = new { kind = "explicit", headerIds = seed.HeaderIds.ToArray() },
            status = "cleared",
            accountId = seed.BankId,
        };
        await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status", mark);

        // Then: mark uncleared — audit columns must reset to null so
        // the DB CHECK (status='cleared') ⇔ (cleared_at IS NOT NULL)
        // still holds.
        var unclear = new
        {
            selection = new { kind = "explicit", headerIds = seed.HeaderIds.ToArray() },
            status = "uncleared",
            accountId = seed.BankId,
        };
        await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status", unclear);

        await using var db = _fixture.NewDbContext();
        var row = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.HeaderId == seed.HeaderIds[0] && rt.AccountId == seed.BankId)
            .Select(rt => new { rt.Status, rt.ClearedAt, rt.ClearedByUserId })
            .FirstAsync();
        Assert.Equal("uncleared", row.Status);
        Assert.Null(row.ClearedAt);
        Assert.Null(row.ClearedByUserId);
    }

    [Fact]
    public async Task BulkReconStatus_invalid_status_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new { kind = "explicit", headerIds = seed.HeaderIds.ToArray() },
            status = "nonsense",
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task BulkReconStatus_invalid_kind_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new { kind = "everything-please" },
            status = "cleared",
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ----------------------------------------------------------------
    // bulk-delete
    // ----------------------------------------------------------------

    [Fact]
    public async Task BulkDelete_hard_deletes_manual_rows_and_soft_hides_external()
    {
        var seed = await SeedAsync(0);
        var bank = seed.BankId;
        var groceries = seed.GroceriesId;
        // Manually seed two headers: one with external_id (feed/import),
        // one without (manual). The bulk action should hard-delete
        // the manual one and soft-hide the imported one.
        await using (var db = _fixture.NewDbContext())
        {
            // Pin created_at to posted_at so the selectedAt predicate
            // doesn't race the Postgres container clock (see
            // SyntheticLedger.AddTransactionPairAsync for the same fix
            // on the generic seed path).
            // Manual row → origin='manual' + external_id NULL
            // (mig 109 CHECK: external_id IS NOT NULL OR origin =
            // 'manual'); SimpleFIN row carries external_id.
            // Mig 107: provider_key is the per-provider tag — NULL
            // for manual, 'simplefin' for the feed row.
            // The feed row carries needs_review=true (awaiting acceptance) so
            // the test proves the soft-hide also clears it (D3).
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_headers (id, ledger_id, origin, provider_key, payee, posted_at, transacted_at, external_id, created_at, needs_review)
                VALUES (gen_random_uuid(), {seed.Ledger.LedgerId}, 'manual',        NULL,        'mine',     '2026-02-01','2026-02-01', NULL,    '2026-02-01', false),
                       (gen_random_uuid(), {seed.Ledger.LedgerId}, 'online_import', 'simplefin', 'feed-row', '2026-02-02','2026-02-02', 'ext-1', '2026-02-02', true);");
        }

        // Add legs to each so the resolved view has rows (not strictly
        // needed for delete predicate, but mirrors real shape).
        await using (var db = _fixture.NewDbContext())
        {
            var headers = await db.TxnHeaders
                .Where(h => h.LedgerId == seed.Ledger.LedgerId)
                .ToListAsync();
            foreach (var h in headers)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                    VALUES
                        (gen_random_uuid(), {h.Id}, {h.LedgerId}, {bank},      0, -5),
                        (gen_random_uuid(), {h.Id}, {h.LedgerId}, {groceries}, 0,  5);");
            }
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new
            {
                kind = "all",
                accountId = seed.BankId,
                statusFilter = "all",
                selectedAt = DateTime.UtcNow.AddSeconds(1),
            },
        };

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-delete",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = (await response.Content.ReadFromJsonAsync<BulkDeleteResponse>())!;
        Assert.Equal(1, payload.HardDeleted);
        Assert.Equal(1, payload.SoftHidden);

        await using var verifyDb = _fixture.NewDbContext();
        var remaining = await verifyDb.TxnHeaders
            .Where(h => h.LedgerId == seed.Ledger.LedgerId)
            .ToListAsync();
        // Manual row gone, imported row is still there but is_hidden=true.
        Assert.Single(remaining);
        Assert.NotNull(remaining[0].ExternalId);
        Assert.True(remaining[0].IsHidden);
        // ADR-0052 D3: the soft-hidden feed row's review flag is cleared too,
        // so it can't linger in the review queue as hidden-but-pending.
        Assert.False(remaining[0].NeedsReview);
    }

    [Fact]
    public async Task BulkDelete_all_mode_only_touches_headers_the_account_originates()
    {
        // A multi-posting header that ORIGINATES in account A: A is on
        // every posting; postings target B and a category C. For A the
        // header has account_postings_on_header == header_total_postings
        // (originating). For B it's a TARGET split
        // (account_postings_on_header < header_total_postings, ADR-0036)
        // — read-only from B's register. An all-mode bulk-delete scoped
        // to B must NOT delete it; scoped to A it must.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var accountA = await ledger.AddBankAccountAsync("account-A");
        var accountB = await ledger.AddBankAccountAsync("account-B");
        var categoryC = await ledger.AddCategoryAsync("category-C");
        var postedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        // Two postings, both originating in A: posting 0 → B, posting 1 → C.
        var (_, headerId) = await ledger.AddMultiSplitAsync(
            primaryAccountId: accountA.Id,
            legs: new[]
            {
                (accountB.Id, 10m),
                (categoryC.Id, 5m),
            },
            postedAt: postedAt,
            payee: "split-origin-A");

        // Sanity: confirm the denormalized counts landed as expected so
        // the assertions below test the originating predicate, not a
        // mis-seeded fixture.
        await using (var checkDb = _fixture.NewDbContext())
        {
            var aLeg = await checkDb.TxnLegs.FirstAsync(
                l => l.HeaderId == headerId && l.AccountId == accountA.Id);
            var bLeg = await checkDb.TxnLegs.FirstAsync(
                l => l.HeaderId == headerId && l.AccountId == accountB.Id);
            // A is on every posting → originating.
            Assert.Equal(aLeg.HeaderTotalPostings, aLeg.AccountPostingsOnHeader);
            // B is on a strict subset → target split (read-only for B).
            Assert.True(bLeg.AccountPostingsOnHeader < bLeg.HeaderTotalPostings);
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var selectedAt = DateTime.UtcNow.AddSeconds(1);

        // All-mode summary for B excludes the target-split header (count
        // consistency with what a B-scoped delete would touch).
        var summaryB = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary",
            new
            {
                kind = "all",
                accountId = accountB.Id,
                statusFilter = "all",
                selectedAt,
            });
        Assert.Equal(HttpStatusCode.OK, summaryB.StatusCode);
        var summaryBody = (await summaryB.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(0, summaryBody.Count);

        // All-mode bulk-delete scoped to B must NOT delete A's header.
        var deleteB = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new
            {
                selection = new
                {
                    kind = "all",
                    accountId = accountB.Id,
                    statusFilter = "all",
                    selectedAt,
                },
            });
        Assert.Equal(HttpStatusCode.OK, deleteB.StatusCode);
        var deleteBPayload = (await deleteB.Content.ReadFromJsonAsync<BulkDeleteResponse>())!;
        Assert.Equal(0, deleteBPayload.HardDeleted);
        Assert.Equal(0, deleteBPayload.SoftHidden);

        await using (var afterB = _fixture.NewDbContext())
        {
            var stillThere = await afterB.TxnHeaders
                .AnyAsync(h => h.Id == headerId && h.LedgerId == ledger.LedgerId);
            Assert.True(stillThere); // B couldn't delete A's header.
        }

        // All-mode bulk-delete scoped to A (the originating account)
        // deletes the header.
        var deleteA = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new
            {
                selection = new
                {
                    kind = "all",
                    accountId = accountA.Id,
                    statusFilter = "all",
                    selectedAt,
                },
            });
        Assert.Equal(HttpStatusCode.OK, deleteA.StatusCode);
        var deleteAPayload = (await deleteA.Content.ReadFromJsonAsync<BulkDeleteResponse>())!;
        // Manual origin (external_id NULL) → hard delete.
        Assert.Equal(1, deleteAPayload.HardDeleted);
        Assert.Equal(0, deleteAPayload.SoftHidden);

        await using (var afterA = _fixture.NewDbContext())
        {
            var gone = await afterA.TxnHeaders
                .AnyAsync(h => h.Id == headerId && h.LedgerId == ledger.LedgerId);
            Assert.False(gone); // A owned it; it's deleted.
        }
    }

    // ----------------------------------------------------------------
    // Cross-cutting
    // ----------------------------------------------------------------

    [Fact]
    public async Task BulkAction_against_other_ledger_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var otherLedgerId = Guid.NewGuid();
        var body = new
        {
            selection = new { kind = "explicit", headerIds = seed.HeaderIds.ToArray() },
            status = "cleared",
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{otherLedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Summary_uncleared_filter_excludes_cleared_rows()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Mark one cleared.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_leg_recon (leg_id, ledger_id, status, cleared_at, cleared_by_user_id)
                SELECT l.id, l.ledger_id, 'cleared', now(), {seed.Ledger.UserId}
                  FROM txn_legs l
                 WHERE l.header_id = {seed.HeaderIds[0]} AND l.account_id = {seed.BankId};");
        }

        var body = new
        {
            kind = "all",
            accountId = seed.BankId,
            statusFilter = "uncleared",
            selectedAt = DateTime.UtcNow.AddSeconds(1),
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        var summary = (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(2, summary.Count);
    }

    [Fact]
    public async Task NeedsReview_filter_scopes_all_mode_summary_and_delete_to_flagged_rows()
    {
        // Regression: a select-all while on the "Needs review" tab used
        // to widen to the whole account (the tab mapped to statusFilter
        // "all" because the wire had no needs_review predicate). The
        // summary count + delete must now match exactly the flagged rows.
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Flag exactly one of the three headers for review.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers
                   SET needs_review = true
                 WHERE id = {seed.HeaderIds[0]};");
        }

        var selectedAt = DateTime.UtcNow.AddSeconds(1);

        // Summary with the needs_review filter counts only the flagged row.
        var summaryResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            new
            {
                kind = "all",
                accountId = seed.BankId,
                statusFilter = "needs_review",
                selectedAt,
            });
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<SelectionSummary>())!;
        Assert.Equal(1, summary.Count);

        // An all-mode delete with the needs_review filter removes only the
        // flagged row; the other two survive.
        var deleteResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-delete",
            new
            {
                selection = new
                {
                    kind = "all",
                    accountId = seed.BankId,
                    statusFilter = "needs_review",
                    selectedAt,
                },
            });
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        await using (var after = _fixture.NewDbContext())
        {
            var survivors = await after.TxnHeaders
                .Where(h => h.LedgerId == seed.Ledger.LedgerId && !h.IsHidden)
                .Select(h => h.Id)
                .ToListAsync();
            Assert.DoesNotContain(seed.HeaderIds[0], survivors);
            Assert.Contains(seed.HeaderIds[1], survivors);
            Assert.Contains(seed.HeaderIds[2], survivors);
        }
    }

    [Fact]
    public async Task Selection_with_unknown_status_filter_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            new
            {
                kind = "all",
                accountId = seed.BankId,
                statusFilter = "bogus",
                selectedAt = DateTime.UtcNow.AddSeconds(1),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
