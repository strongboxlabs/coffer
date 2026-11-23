using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
public sealed class BulkTransactionsEndpointsTests : DatabaseTest
{
    public BulkTransactionsEndpointsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

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
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        await using var db = Db.NewDbContext();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        // The sum of the two seeded rows' bank legs.
        Assert.Equal(-21m, summary.SumOnAccount);
    }

    [Fact]
    public async Task Summary_all_excludes_a_hidden_row()
    {
        // Bulk selection must act on what the user SEES: a hidden row is
        // excluded from the count + account sum (ADR-0072 D1).
        var seed = await SeedAsync(2);
        await seed.Ledger.HideTransactionAsync(seed.HeaderIds[1]);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        // Only the visible row (header[0], and its bank leg) survives.
        Assert.Equal(1, summary!.Count);
        Assert.Equal(-10m, summary.SumOnAccount);
    }

    [Fact]
    public async Task Summary_all_with_selectedAt_excludes_future_rows()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new { kind = "explicit", accountId = seed.BankId, headerIds = Array.Empty<Guid>() };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("selection-empty", await CodeOfAsync(response));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("explicit")]
    public async Task A_selection_without_an_account_returns_422(string kind)
    {
        // A selection is made on one register, and every register is one
        // account's: there is no ledger-wide selection to resolve.
        var seed = await SeedAsync(2);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var selection = new
        {
            kind,
            statusFilter = "all",
            selectedAt = DateTime.UtcNow.AddSeconds(1),
            headerIds = seed.HeaderIds.ToArray(),
        };
        var summary = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary", selection);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, summary.StatusCode);
        Assert.Equal("account-not-in-ledger", await CodeOfAsync(summary));

        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-delete", new { selection });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal("account-not-in-ledger", await CodeOfAsync(delete));

        await using var db = Db.NewDbContext();
        Assert.Equal(2, await db.TxnHeaders.CountAsync(h => h.LedgerId == seed.Ledger.LedgerId));
    }

    // ----------------------------------------------------------------
    // bulk-recon-status
    // ----------------------------------------------------------------

    [Fact]
    public async Task BulkReconStatus_all_marks_every_matching_header_cleared()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var db = Db.NewDbContext();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var body = new
        {
            selection = new
            {
                kind = "explicit",
                accountId = seed.BankId,
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
        await using var db = Db.NewDbContext();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // First: mark cleared.
        var mark = new
        {
            selection = new { kind = "explicit", accountId = seed.BankId, headerIds = seed.HeaderIds.ToArray() },
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
            selection = new { kind = "explicit", accountId = seed.BankId, headerIds = seed.HeaderIds.ToArray() },
            status = "uncleared",
            accountId = seed.BankId,
        };
        await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status", unclear);

        await using var db = Db.NewDbContext();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Every other field is valid, so the refusal is the selection's kind.
        var body = new
        {
            selection = new { kind = "everything-please", accountId = seed.BankId },
            status = "cleared",
            accountId = seed.BankId,
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("selection-kind-invalid", await CodeOfAsync(response));
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
        await using (var db = Db.NewDbContext())
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
            // the test proves the soft-hide also clears it (ADR-0052 D3).
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_headers (id, ledger_id, origin, provider_key, payee, posted_at, transacted_at, external_id, created_at, needs_review,
                                         originating_account_id)
                VALUES (gen_random_uuid(), {seed.Ledger.LedgerId}, 'manual',        NULL,        'mine',     '2026-02-01','2026-02-01', NULL,    '2026-02-01', false, {bank}),
                       (gen_random_uuid(), {seed.Ledger.LedgerId}, 'online_import', 'simplefin', 'feed-row', '2026-02-02','2026-02-02', 'ext-1', '2026-02-02', true,  {bank});");
        }

        // Add legs to each so the resolved view has rows (not strictly
        // needed for delete predicate, but mirrors real shape).
        await using (var db = Db.NewDbContext())
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
        // Raw SQL reaches no writer: store the presentation as the upgrade's backfill does.
        await seed.Ledger.RecomputeAllNotesAsync();

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

        await using var verifyDb = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        await using (var checkDb = Db.NewDbContext())
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

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

        await using (var afterB = Db.NewDbContext())
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

        await using (var afterA = Db.NewDbContext())
        {
            var gone = await afterA.TxnHeaders
                .AnyAsync(h => h.Id == headerId && h.LedgerId == ledger.LedgerId);
            Assert.False(gone); // A owned it; it's deleted.
        }
    }

    [Fact]
    public async Task Select_all_reaches_only_the_rows_the_register_can_edit()
    {
        // Two shapes put an account on every posting of a header and still
        // leave its register read-only there: a split whose every line lands
        // on one category (the header is the bank's), and a one-posting
        // dividend's category leg (the event is the brokerage's). The posting
        // counts call the category the owner of both. A select-all on the
        // category must take only what its register can edit: the plain
        // one-posting row, editable from either side.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-A");
        var category = await ledger.AddCategoryAsync("category-B");
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage-C");
        var security = await ledger.AddSecurityAsync("security-D", "SECD");
        var postedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var (plainLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, category.Id, -7m, postedAt, payee: "plain");
        var plainHeader = await ledger.ResolveHeaderIdAsync(plainLeg);
        var (_, splitHeader) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] { (category.Id, -10m), (category.Id, -5m) },
            postedAt: postedAt,
            payee: "split-one-category");
        var dividendHeader = await ledger.AddInvestmentDividendAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, security, category.Id, 2m, postedAt);

        // Every category leg passes the posting-count test, so this test tells
        // that rule from the register's.
        await using (var checkDb = Db.NewDbContext())
        {
            var categoryLegs = await checkDb.TxnLegs.AsNoTracking()
                .Where(l => l.AccountId == category.Id)
                .ToListAsync();
            Assert.Equal(4, categoryLegs.Count);
            Assert.All(categoryLegs, l => Assert.Equal(l.HeaderTotalPostings, l.AccountPostingsOnHeader));
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // What the category's register marks: only the plain row is editable.
        var page = (await (await client.GetAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={category.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var rows = page.Entries
            .SelectMany(e => e.Txn is not null
                ? new[] { e.Txn }
                : (IEnumerable<RegisterRowDto>?)e.Legs ?? Array.Empty<RegisterRowDto>())
            .ToList();
        Assert.Equal(
            new[] { plainHeader },
            rows.Where(r => !r.IsReadOnly).Select(r => r.HeaderId).Distinct().ToArray());
        Assert.Contains(rows, r => r.HeaderId == splitHeader && r.IsReadOnly);
        Assert.Contains(rows, r => r.HeaderId == dividendHeader && r.IsReadOnly);

        var selection = new
        {
            kind = "all",
            accountId = category.Id,
            statusFilter = "all",
            selectedAt = DateTime.UtcNow.AddSeconds(1),
        };
        var summary = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary", selection);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(1, (await summary.Content.ReadFromJsonAsync<SelectionSummary>())!.Count);

        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete", new { selection });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deleted = (await delete.Content.ReadFromJsonAsync<BulkDeleteResponse>())!;
        Assert.Equal(1, deleted.HardDeleted);
        Assert.Equal(0, deleted.SoftHidden);

        await using var after = Db.NewDbContext();
        var remaining = await after.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId)
            .Select(h => h.Id)
            .ToListAsync();
        Assert.DoesNotContain(plainHeader, remaining);
        Assert.Contains(splitHeader, remaining);
        Assert.Contains(dividendHeader, remaining);
    }

    [Fact]
    public async Task Summary_counts_every_selected_row_and_names_the_read_only_part()
    {
        // The footer names every row the selection covers, read-only rows
        // included, and how many of them are read-only; the Σ beside it sums those
        // same rows, each by its own legs in the register's scope. A bulk change
        // still acts only on what the register can edit, so `Count` keeps that
        // meaning. The category holds a plain row (editable), a split whose lines
        // both land on it (read-only: the header is the bank's) and a dividend's category leg
        // (read-only: the event is the brokerage's). Its sub-category holds one
        // editable row and one read-only split.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-A");
        var category = await ledger.AddCategoryAsync("category-B");
        var child = await ledger.AddCategoryAsync("category-C", parentId: category.Id);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage-D");
        var security = await ledger.AddSecurityAsync("security-E", "SECE");
        var day1 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);
        var day5 = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc);

        var (plainLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -7m, day1, payee: "plain-row");
        var plainHeader = await ledger.ResolveHeaderIdAsync(plainLeg);
        var (_, splitHeader) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] { (category.Id, -10m), (category.Id, -5m) },
            postedAt: day1,
            payee: "split-row");
        var dividendHeader = await ledger.AddInvestmentDividendAsync(
            brokerage.Id, brokerage.HoldingsAccountId!.Value, security, category.Id, 2m, day5);
        var (childPlainLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, child.Id, -3m, day2, payee: "child-plain-row");
        var childPlainHeader = await ledger.ResolveHeaderIdAsync(childPlainLeg);
        var (_, childSplitHeader) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] { (child.Id, -4m), (child.Id, -1m) },
            postedAt: day2,
            payee: "child-split-row");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary";
        var selectedAt = DateTime.UtcNow.AddSeconds(1);

        async Task<SelectionSummary> SummaryAsync(object selection)
        {
            var response = await client.PostAsJsonAsync(url, selection);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        }

        // Select-all, no filter: three rows, two of them read-only; one is editable.
        var all = await SummaryAsync(new { kind = "all", accountId = category.Id, statusFilter = "all", selectedAt });
        Assert.Equal((3, 2, 1), (all.TotalCount, all.ReadOnlyCount, all.Count));
        // The Σ covers all three, read-only rows included, by the category's own
        // legs: the plain row's 7, the split's two lines 10 + 5, the dividend's -2.
        Assert.Equal(20m, all.SumOnAccount);

        // A search narrowing to the split: one row, read-only, nothing to act on.
        var split = await SummaryAsync(new { kind = "all", accountId = category.Id, statusFilter = "all", selectedAt, search = "split-row" });
        Assert.Equal((1, 1, 0), (split.TotalCount, split.ReadOnlyCount, split.Count));
        Assert.Equal(15m, split.SumOnAccount);

        // A date range narrowing to the dividend: the investment opposite side.
        var dividend = await SummaryAsync(new
        {
            kind = "all", accountId = category.Id, statusFilter = "all", selectedAt,
            dateFrom = "2026-03-05", dateTo = "2026-03-05",
        });
        Assert.Equal((1, 1, 0), (dividend.TotalCount, dividend.ReadOnlyCount, dividend.Count));
        Assert.Equal(-2m, dividend.SumOnAccount);

        // A search narrowing to the plain row: no read-only part.
        var plain = await SummaryAsync(new { kind = "all", accountId = category.Id, statusFilter = "all", selectedAt, search = "plain-row" });
        Assert.Equal((1, 0, 1), (plain.TotalCount, plain.ReadOnlyCount, plain.Count));
        Assert.Equal(7m, plain.SumOnAccount);

        // Widened to the sub-category: its two rows join, one of them read-only.
        var wide = await SummaryAsync(new
        {
            kind = "all", accountId = category.Id, statusFilter = "all", selectedAt, includeSubcategories = true,
        });
        Assert.Equal((5, 3, 2), (wide.TotalCount, wide.ReadOnlyCount, wide.Count));
        // The sub-category's rows add their own legs there: 3, and the split's 4 + 1.
        Assert.Equal(28m, wide.SumOnAccount);

        // Widened and filtered: the sub-category's split alone.
        var wideSplit = await SummaryAsync(new
        {
            kind = "all", accountId = category.Id, statusFilter = "all", selectedAt, includeSubcategories = true,
            search = "child-split-row",
        });
        Assert.Equal((1, 1, 0), (wideSplit.TotalCount, wideSplit.ReadOnlyCount, wideSplit.Count));
        Assert.Equal(5m, wideSplit.SumOnAccount);

        // An explicit selection is counted in the same terms; it is not narrowed,
        // because the endpoints that write refuse it whole instead. Its total is the
        // ids it names, which is the SPA's own count of it, and its read-only part
        // is what the footer shows once the summary arrives.
        var chosenIds = new[] { plainHeader, splitHeader, dividendHeader };
        var chosen = await SummaryAsync(new { kind = "explicit", accountId = category.Id, headerIds = chosenIds });
        Assert.Equal(chosenIds.Length, chosen.TotalCount);
        Assert.Equal((3, 2, 3), (chosen.TotalCount, chosen.ReadOnlyCount, chosen.Count));

        // Both kinds sum the same rows alike: an explicit selection of exactly the
        // rows a select-all covers carries the select-all's Σ, in the plain scope
        // and in the widened one.
        Assert.Equal(all.SumOnAccount, chosen.SumOnAccount);
        var wideIds = new[] { plainHeader, splitHeader, dividendHeader, childPlainHeader, childSplitHeader };
        var wideChosen = await SummaryAsync(new
        {
            kind = "explicit", accountId = category.Id, headerIds = wideIds, includeSubcategories = true,
        });
        Assert.Equal((5, 3), (wideChosen.TotalCount, wideChosen.ReadOnlyCount));
        Assert.Equal(wide.SumOnAccount, wideChosen.SumOnAccount);
    }

    [Fact]
    public async Task A_selection_never_covers_a_recurring_template()
    {
        // A reminder's template is a header with legs on its accounts, but the
        // register never lists it, so no selection may cover it: not in the
        // counts, not in the Σ, and not in what a bulk change acts on.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-A");
        var category = await ledger.AddCategoryAsync("category-B");
        var (liveLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, category.Id, -7m, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), payee: "payee-live");
        var liveHeader = await ledger.ResolveHeaderIdAsync(liveLeg);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = new DateOnly(2026, 1, 1),
            Payee = "payee-series",
            SourceAccountId = bank.Id,
            Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -40m } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The template exists, unhidden, with a leg on each register's account,
        // so only the template rule can keep it out.
        Guid templateHeader;
        await using (var db = Db.NewServiceDbContext())
        {
            var template = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
            templateHeader = template.Id;
            Assert.False(template.IsHidden);
            var accounts = await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == templateHeader)
                .Select(l => l.AccountId)
                .ToListAsync();
            Assert.Contains(bank.Id, accounts);
            Assert.Contains(category.Id, accounts);
        }

        // The register does not list it.
        var page = (await (await client.GetAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var listed = page.Entries
            .SelectMany(e => e.Txn is not null
                ? new[] { e.Txn }
                : (IEnumerable<RegisterRowDto>?)e.Legs ?? Array.Empty<RegisterRowDto>())
            .Select(r => r.HeaderId)
            .Distinct()
            .ToArray();
        Assert.Equal(new[] { liveHeader }, listed);

        var url = $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary";
        var selectedAt = DateTime.UtcNow.AddSeconds(1);
        async Task<SelectionSummary> SummaryAsync(object selection)
        {
            var response = await client.PostAsJsonAsync(url, selection);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SelectionSummary>())!;
        }

        // Select-all with no filter, from either side, and under a search the
        // template's payee matches too: only the live row.
        var onBank = await SummaryAsync(new { kind = "all", accountId = bank.Id, statusFilter = "all", selectedAt });
        Assert.Equal((1, 0, 1, -7m), (onBank.TotalCount, onBank.ReadOnlyCount, onBank.Count, onBank.SumOnAccount));
        var onCategory = await SummaryAsync(new { kind = "all", accountId = category.Id, statusFilter = "all", selectedAt });
        Assert.Equal((1, 1, 7m), (onCategory.TotalCount, onCategory.Count, onCategory.SumOnAccount));
        var searched = await SummaryAsync(new { kind = "all", accountId = bank.Id, statusFilter = "all", selectedAt, search = "payee-" });
        Assert.Equal((1, 1, -7m), (searched.TotalCount, searched.Count, searched.SumOnAccount));

        // An explicit selection naming it does not reach it either.
        var named = await SummaryAsync(new { kind = "explicit", accountId = bank.Id, headerIds = new[] { liveHeader, templateHeader } });
        Assert.Equal((1, 1, -7m), (named.TotalCount, named.Count, named.SumOnAccount));

        // A select-all delete removes the live row and leaves the template.
        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new { selection = new { kind = "all", accountId = bank.Id, statusFilter = "all", selectedAt } });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(new BulkDeleteResponse(1, 0), await delete.Content.ReadFromJsonAsync<BulkDeleteResponse>());
        await using (var after = Db.NewServiceDbContext())
        {
            Assert.True(await after.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == templateHeader));
            Assert.False(await after.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == liveHeader));
        }
    }

    [Fact]
    public async Task Recon_reaches_read_only_rows_alike_from_select_all_and_an_explicit_selection()
    {
        // Reconciliation status is the account's own leg (ADR-0082), so a row the
        // register shows read-only is still the register's to clear. A select-all
        // and an explicit selection of the same rows set the same legs: every leg
        // on the category, of the editable row and the read-only split alike, and
        // none on the bank.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-A");
        var category = await ledger.AddCategoryAsync("category-B");
        var postedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var (plainLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, category.Id, -7m, postedAt, payee: "plain");
        var plainHeader = await ledger.ResolveHeaderIdAsync(plainLeg);
        var (_, splitHeader) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] { (category.Id, -10m), (category.Id, -5m) },
            postedAt: postedAt,
            payee: "split-one-category");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-recon-status";

        async Task<Guid[]> ClearedAsync()
        {
            await using var db = Db.NewDbContext();
            return await db.ResolvedTransactions.AsNoTracking()
                .Where(rt => rt.HeaderId == plainHeader || rt.HeaderId == splitHeader)
                .Where(rt => rt.Status == "cleared")
                .Select(rt => rt.Id)
                .OrderBy(id => id)
                .ToArrayAsync();
        }
        Guid[] categoryLegs;
        await using (var db = Db.NewDbContext())
        {
            categoryLegs = await db.TxnLegs.AsNoTracking()
                .Where(l => l.AccountId == category.Id)
                .Select(l => l.Id)
                .OrderBy(id => id)
                .ToArrayAsync();
        }
        Assert.Equal(3, categoryLegs.Length);

        var all = await client.PostAsJsonAsync(url, new
        {
            selection = new { kind = "all", accountId = category.Id, statusFilter = "all", selectedAt = DateTime.UtcNow.AddSeconds(1) },
            status = "cleared",
            accountId = category.Id,
        });
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var fromSelectAll = await ClearedAsync();

        var explicitSelection = new { kind = "explicit", accountId = category.Id, headerIds = new[] { plainHeader, splitHeader } };
        var reset = await client.PostAsJsonAsync(url, new { selection = explicitSelection, status = "uncleared", accountId = category.Id });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Empty(await ClearedAsync());

        var chosen = await client.PostAsJsonAsync(url, new { selection = explicitSelection, status = "cleared", accountId = category.Id });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        var fromExplicit = await ClearedAsync();

        Assert.Equal(categoryLegs, fromExplicit);
        Assert.Equal(fromExplicit, fromSelectAll);
    }

    [Theory]
    [InlineData("bulk-delete")]
    [InlineData("bulk-unhide")]
    [InlineData("bulk-move-account")]
    public async Task An_explicit_selection_naming_a_read_only_row_is_refused_whole(string endpoint)
    {
        // One row the category's register can edit (a plain one-posting row) and
        // one it shows read-only (a split whose lines both land on the category:
        // the header is the bank's). A bulk change over both is refused before
        // anything is written, the editable row included.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("account-A");
        var category = await ledger.AddCategoryAsync("category-B");
        var otherCategory = await ledger.AddCategoryAsync("category-C");
        var postedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var (plainLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, category.Id, -7m, postedAt, payee: "plain");
        var plainHeader = await ledger.ResolveHeaderIdAsync(plainLeg);
        var (_, splitHeader) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] { (category.Id, -10m), (category.Id, -5m) },
            postedAt: postedAt,
            payee: "split-one-category");
        if (endpoint == "bulk-unhide")
        {
            await ledger.HideTransactionAsync(plainHeader);
            await ledger.HideTransactionAsync(splitHeader);
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<string> StateAsync()
        {
            await using var db = Db.NewDbContext();
            var headers = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .OrderBy(h => h.Id)
                .Select(h => new { h.Id, h.IsHidden, h.NeedsReview })
                .ToListAsync();
            var legs = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId)
                .OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.AccountId })
                .ToListAsync();
            return JsonSerializer.Serialize(new { headers, legs });
        }
        var before = await StateAsync();

        var selection = new
        {
            kind = "explicit",
            accountId = category.Id,
            headerIds = new[] { plainHeader, splitHeader },
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{endpoint}",
            new { selection, targetAccountId = otherCategory.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("selection-includes-read-only", await CodeOfAsync(response));
        Assert.Equal(before, await StateAsync());

        // The editable row alone is accepted: the refusal is about the read-only one.
        var alone = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{endpoint}",
            new
            {
                selection = new { kind = "explicit", accountId = category.Id, headerIds = new[] { plainHeader } },
                targetAccountId = otherCategory.Id,
            });
        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
        Assert.NotEqual(before, await StateAsync());
    }

    // ----------------------------------------------------------------
    // Cross-cutting
    // ----------------------------------------------------------------

    /// <summary>A signed-in client for a second member of the ledger.</summary>
    private async Task<HttpClient> MemberClientAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using (var db = Db.NewDbContext())
        {
            db.AuthSessions.Add(new Coffer.Api.Db.Entities.SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    [Fact]
    public async Task A_viewer_reads_the_selection_summary_and_is_refused_a_bulk_write()
    {
        var seed = await SeedAsync(3);
        var viewerId = await seed.Ledger.AddMemberAsync("viewer");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var viewer = await MemberClientAsync(factory, viewerId);

        var selection = new
        {
            kind = "explicit",
            headerIds = seed.HeaderIds.Take(2).ToArray(),
            accountId = seed.BankId,
        };

        // The summary is a read over rows a viewer can already GET.
        var response = await viewer.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/selection-summary", selection);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<SelectionSummary>();
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Count);
        Assert.Equal(-21m, summary.SumOnAccount);

        // A write in the same group still answers at write level.
        var write = await viewer.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/bulk-recon-status",
            new { selection, status = "cleared", accountId = seed.BankId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, write.StatusCode);
        Assert.Equal("ledger-not-writable", await CodeOfAsync(write));

        await using var db = Db.NewDbContext();
        var statuses = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.AccountId == seed.BankId)
            .Select(rt => rt.Status)
            .ToListAsync();
        Assert.Equal(3, statuses.Count);
        Assert.DoesNotContain("cleared", statuses);
    }

    [Fact]
    public async Task BulkAction_against_other_ledger_returns_422()
    {
        var seed = await SeedAsync(1);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // A well-formed request in every other respect, so the ledger-visibility
        // check is the one that answers.
        var otherLedgerId = Guid.NewGuid();
        var body = new
        {
            selection = new { kind = "explicit", accountId = seed.BankId, headerIds = seed.HeaderIds.ToArray() },
            status = "cleared",
            accountId = seed.BankId,
        };
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{otherLedgerId}/transactions/bulk-recon-status",
            body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ledger-not-visible", await CodeOfAsync(response));
    }

    [Fact]
    public async Task Summary_uncleared_filter_excludes_cleared_rows()
    {
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Mark one cleared.
        await using (var db = Db.NewDbContext())
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
        // A select-all while on the "Needs review" tab must not widen to
        // the whole account, which it would if the tab mapped to
        // statusFilter "all" for want of a needs_review predicate on the
        // wire. The summary count + delete must match exactly the flagged rows.
        var seed = await SeedAsync(3);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Flag exactly one of the three headers for review.
        await using (var db = Db.NewDbContext())
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

        await using (var after = Db.NewDbContext())
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

    [Fact]
    public async Task Select_all_on_a_rollup_category_covers_the_subtree_it_displays()
    {
        // The fourth consumer of the shared register filter (ADR-0076), and the
        // only one that ACTS rather than displays. A select-all that ignored the
        // sub-category scope would show the reader every descendant's rows and
        // then hand a bulk delete the parent's own postings instead — of which a
        // rollup parent has NONE. Wrong set, destructive verb, no error.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);
        var medicare = await ledger.AddCategoryAsync("Medicare", parentId: federal.Id);

        var when = new DateTime(2026, 4, 3, 0, 30, 0, DateTimeKind.Utc);
        var (fedLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, federal.Id, -100m, when, payee: "fed");
        await ledger.AddTransactionPairAsync(bank.Id, medicare.Id, -20m, when, payee: "medicare");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var url = $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary";
        var selectedAt = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        // Narrow: the parent holds nothing of its own.
        var narrow = await client.PostAsJsonAsync(url, new
        {
            kind = "all",
            accountId = taxes.Id,
            statusFilter = "all",
            selectedAt,
            includeSubcategories = false,
        });
        Assert.Equal(HttpStatusCode.OK, narrow.StatusCode);
        var narrowSummary = await narrow.Content.ReadFromJsonAsync<SelectionSummary>();
        Assert.Equal(0, narrowSummary!.Count);

        // Widened: the same two entries the register itself shows, grandchild
        // included — the walk is not one level deep.
        var wide = await client.PostAsJsonAsync(url, new
        {
            kind = "all",
            accountId = taxes.Id,
            statusFilter = "all",
            selectedAt,
            includeSubcategories = true,
        });
        Assert.Equal(HttpStatusCode.OK, wide.StatusCode);
        var wideSummary = await wide.Content.ReadFromJsonAsync<SelectionSummary>();
        Assert.Equal(2, wideSummary!.Count);

        // An explicit change is judged in the same scope: a sub-category's row is
        // the widened register's to change, and not the narrow one's.
        var fedHeader = await ledger.ResolveHeaderIdAsync(fedLeg);
        var deleteUrl = $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete";
        var narrowDelete = await client.PostAsJsonAsync(deleteUrl, new
        {
            selection = new { kind = "explicit", accountId = taxes.Id, headerIds = new[] { fedHeader } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, narrowDelete.StatusCode);
        Assert.Equal("selection-includes-read-only", await CodeOfAsync(narrowDelete));
        var wideDelete = await client.PostAsJsonAsync(deleteUrl, new
        {
            selection = new
            {
                kind = "explicit", accountId = taxes.Id, includeSubcategories = true, headerIds = new[] { fedHeader },
            },
        });
        Assert.Equal(HttpStatusCode.OK, wideDelete.StatusCode);
        Assert.Equal(1, (await wideDelete.Content.ReadFromJsonAsync<BulkDeleteResponse>())!.HardDeleted);
    }
}
