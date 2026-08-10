using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Targeted reproduction harness for the "stale balance after
/// merge/hide/sync" class of bugs. Reproduces a reported scenario
/// where a savings-account balance was off by exactly the
/// duplicate-row amount after merging an MD-imported row into a
/// SimpleFIN-imported row; `fn_recompute_balances_for_account`
/// produced the correct value when re-run manually, which means
/// SOME write path failed to invoke the interceptor's recompute
/// when the merge happened.
///
/// These tests pin down which path. Each scenario:
///   1. Sets up a chain of transactions with known balances.
///   2. Performs a merge / hide / sync-shape mutation through the
///      HTTP surface (not raw SQL — to exercise the real
///      interceptor wiring).
///   3. Asserts the downstream balance matches the no-duplicate
///      ground truth.
///
/// A test FAILING here is the bug, not the test.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BalanceMergeHideSyncTests
{
    private readonly PostgresFixture _fixture;

    public BalanceMergeHideSyncTests(PostgresFixture fixture) => _fixture = fixture;

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

    private sealed record Scenario(
        SyntheticLedger Ledger,
        Coffer.Api.Db.Entities.AccountRow Bank,
        Coffer.Api.Db.Entities.AccountRow Category,
        HttpClient Client,
        ApiFactory Factory);

    private async Task<Scenario> SetUpAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var client = await AuthedClientAsync(factory, ledger);
        return new Scenario(ledger, bank, groceries, client, factory);
    }

    private async Task<Guid> PostTxnAsync(
        Scenario s, int day, decimal amount, string? payee = null)
    {
        var resp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, day, 12, 0, 0, DateTimeKind.Utc),
                Payee = payee,
                SourceAccountId = s.Bank.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = amount },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    private async Task<decimal?> ReadBalanceAsync(Scenario s, Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == headerId && r.AccountId == s.Bank.Id)
            .Select(r => (decimal?)r.BalanceAfter)
            .SingleOrDefaultAsync();
    }

    // ===================================================================
    // Scenario 1 — baseline: PATCH merge between two manual rows leaves
    // downstream balance correct. If this fails, the interceptor is
    // completely broken on the merge path.
    // ===================================================================
    [Fact]
    public async Task Merge_two_manual_rows_downstream_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        // Chain: A (Apr 10, -10), B (Apr 20, -20), C (Apr 30, -30).
        // After: balance(C) = -60.
        var a = await PostTxnAsync(s,  10, -10m, payee: "anchor-a");
        var b = await PostTxnAsync(s,  20, -20m);
        var c = await PostTxnAsync(s,  30, -30m);

        Assert.Equal(-60m, await ReadBalanceAsync(s, c));

        // Insert duplicate of A on the same day, same amount: dup.
        // balance(C) is now -70 (over by -10 from the duplicate).
        var dup = await PostTxnAsync(s, 10, -10m, payee: "anchor-a-dup");
        Assert.Equal(-70m, await ReadBalanceAsync(s, c));

        // Merge dup into A via PATCH on A with mergeFromHeaderId=dup.
        // To merge, A must be needs_review (the existing gate). Flip
        // it tracked-side so the merge gate accepts.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == a)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.NeedsReview, true));
        }

        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{a}",
            new PatchTransactionRequest { MergeFromHeaderId = dup });
        Assert.True(
            patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"merge PATCH failed: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

        // After merge: dup excluded from balance walk, balance(C) back to -60.
        Assert.Equal(-60m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 2 — loser carries an external_id (mimics MD-imported row);
    // winner is manual. external_id shouldn't affect the interceptor but
    // we verify.
    // ===================================================================
    [Fact]
    public async Task Merge_with_external_id_on_loser_downstream_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var a = await PostTxnAsync(s, 10, -10m, payee: "anchor-a");
        var c = await PostTxnAsync(s, 30, -30m);
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        var dup = await PostTxnAsync(s, 10, -10m, payee: "anchor-a-dup");
        // Stamp external_id on the dup (mimics an imported row).
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == dup)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-import-1"));
            await db.TxnHeaders.Where(h => h.Id == a)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.NeedsReview, true));
        }

        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{a}",
            new PatchTransactionRequest { MergeFromHeaderId = dup });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 3 — closest reproduction of the user's case: BOTH rows
    // have an external_id (loser was MD-imported, winner was
    // SimpleFIN-imported). The merge should still trigger correct
    // recompute regardless of provenance.
    // ===================================================================
    [Fact]
    public async Task Merge_with_external_id_on_both_downstream_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var a = await PostTxnAsync(s, 10, -10m, payee: "Interest Paid");
        var c = await PostTxnAsync(s, 30, -30m);
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        var dup = await PostTxnAsync(s, 10, -10m, payee: "Test Bank");
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == dup)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-import-1"));
            await db.TxnHeaders.Where(h => h.Id == a)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "simplefin-1")
                                              .SetProperty(h => h.NeedsReview, true));
        }
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{a}",
            new PatchTransactionRequest { MergeFromHeaderId = dup });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 4 — body carrying BOTH a postings reshape AND a merge
    // stamp. Inverted-merge direction: the editor's body content
    // (reshape) is applied to a row that's about to become a loser,
    // so the visible balance reflects the SURVIVOR (dup) — NOT the
    // wasted reshape. SPA flows that fold-into-candidate don't ship
    // a postings reshape anymore (it's pointless), but the server
    // remains tolerant of combined bodies; this test pins that
    // behavior.
    // ===================================================================
    [Fact]
    public async Task Merge_with_postings_reshape_downstream_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var rentCategory = await s.Ledger.AddCategoryAsync("rent");

        var a = await PostTxnAsync(s, 10, -10m, payee: "anchor-a");
        var c = await PostTxnAsync(s, 30, -30m);
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        var dup = await PostTxnAsync(s, 10, -10m, payee: "anchor-a-dup");
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == a)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.NeedsReview, true));
        }
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // Patch a (the editor row) with a reshape AND merge in one
        // call. Inverted direction: a becomes loser of dup, the
        // reshape on a is moot for the balance walk.
        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{a}",
            new PatchTransactionRequest
            {
                MergeFromHeaderId = dup,
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = s.Bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = rentCategory.Id, Amount = -15m },
                    },
                },
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

        // a is the loser (hidden from balance walk). dup -10 is the
        // surviving winner. c -30. balance(c) = -40.
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 5 — removed. The hide-via-header-override path isn't
    // exercised by any production code today (DELETE soft-hide
    // mutates the raw `txn_headers.is_hidden` column, not the
    // override layer). The test that previously sat here used the
    // bare service DbContext which bypasses the interceptor — so
    // it was testing the wrong invariant. Coverage for the raw
    // is_hidden path is in BalanceConsistencyTests.
    // ===================================================================

    // ===================================================================
    // Scenario 6 — hidden via the bank DELETE soft-hide endpoint,
    // then a SECOND row is added later. Balance walk should respect
    // the hide even after subsequent inserts.
    // ===================================================================
    [Fact]
    public async Task Soft_hide_then_later_insert_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var a = await PostTxnAsync(s, 10, -10m);
        var b = await PostTxnAsync(s, 20, -20m);
        Assert.Equal(-30m, await ReadBalanceAsync(s, b));

        // External_id stamp so DELETE takes soft-hide branch.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == b)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "feed-x"));
        }
        var del = await s.Client.DeleteAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{b}");
        Assert.True(del.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        // After hide: balance through A = -10.
        Assert.Equal(-10m, await ReadBalanceAsync(s, a));

        // Add a later row C; its balance should not include B.
        var c = await PostTxnAsync(s, 30, -30m);
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 7 — closest reproduction of a representative sequence:
    //   * Existing MD-imported row on the bank (loser-to-be).
    //   * Later "SimpleFIN sync" inserts a duplicate row on the same
    //     date + amount (winner). At this point balance(C) is
    //     OVER by the duplicate amount.
    //   * User merges the loser into the winner via SPA PATCH.
    //   * After merge: balance(C) should be back to correct.
    //
    // Insertion is via the HTTP POST endpoint (not raw `db.Add`) so
    // the BalanceRecomputeInterceptor actually fires — the previous
    // version of this test used the bare service DbContext, which
    // doesn't have interceptors and so didn't reproduce production.
    // external_id is stamped afterwards via ExecuteUpdate to mark
    // the rows as "imported" (mirrors the data shape the user had).
    // ===================================================================
    [Fact]
    public async Task Sync_style_insert_then_merge_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var loser = await PostTxnAsync(s, 10, -10m, payee: "Test Bank");
        var c = await PostTxnAsync(s, 30, -30m);
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        // "Sync inserts the dup" — POST a second row on the same
        // date + amount. Interceptor fires, balance(C) bumps to -50.
        var winner = await PostTxnAsync(s, 10, -10m, payee: "Interest Paid");
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // Stamp external_id + needs_review (winner side) and external_id
        // (loser side) to mimic the post-sync state. external_id alone
        // shouldn't change balance — we're checking the merge path
        // still works for imported-on-both-sides rows.
        await using (var db = _fixture.NewDbContext())
        {
            // Mig 107: origin/provider_key are now two columns —
            // origin is icon-level (manual/online_import/file_import),
            // provider_key is the per-provider tag.
            await db.TxnHeaders.Where(h => h.Id == loser)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-1")
                                              .SetProperty(h => h.Origin, "file_import")
                                              .SetProperty(h => h.ProviderKey, "qif"));
            await db.TxnHeaders.Where(h => h.Id == winner)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-1")
                                              .SetProperty(h => h.Origin, "online_import")
                                              .SetProperty(h => h.ProviderKey, "simplefin")
                                              .SetProperty(h => h.NeedsReview, true));
        }
        // The ExecuteUpdate bypasses the interceptor, but external_id /
        // origin / needs_review aren't balance-affecting — balance
        // should still read -50.
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // Merge loser into winner via SPA PATCH.
        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winner}",
            new PatchTransactionRequest { MergeFromHeaderId = loser });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

        // After merge: loser excluded from balance walk, balance(C) = -40.
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 8 — a representative sequence per their description:
    //   * Existing MD-imported row on bank.
    //   * SimpleFIN sync inserts MULTIPLE "weird balance" rows on the
    //     bank account.
    //   * User soft-deletes each via DELETE endpoint (external_id set,
    //     so soft-hide branch).
    //   * Then user merges a separate MD-imported dup into a kept
    //     SimpleFIN row.
    //   * Final downstream balance must be correct.
    // ===================================================================
    [Fact]
    public async Task Sync_inserts_balance_rows_then_user_deletes_then_merges()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        // Earlier MD row (loser-to-be).
        var loser = await PostTxnAsync(s, 10, -10m, payee: "Test Bank");
        // Anchor row much later in time — balance(c) is the assertion target.
        var c = await PostTxnAsync(s, 30, -30m);

        // SimpleFIN sync inserts: 2 weird "balance" rows + 1 real
        // duplicate of the MD row.
        var balanceJunk1 = await PostTxnAsync(s, 15, -1m, payee: "Balance: $123.45");
        var balanceJunk2 = await PostTxnAsync(s, 16, -2m, payee: "Balance: $122.45");
        var winner = await PostTxnAsync(s, 10, -10m, payee: "Interest Paid");

        // Stamp external_ids + needs_review so DELETE takes
        // the soft-hide branch (external_id != null).
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == loser)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-1"));
            await db.TxnHeaders.Where(h => h.Id == balanceJunk1)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-b1"));
            await db.TxnHeaders.Where(h => h.Id == balanceJunk2)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-b2"));
            await db.TxnHeaders.Where(h => h.Id == winner)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-w")
                                              .SetProperty(h => h.NeedsReview, true));
        }

        // Current balance: loser (-10) + dummy junk (-1 -2) + winner (-10) + c (-30) = -53.
        Assert.Equal(-53m, await ReadBalanceAsync(s, c));

        // User soft-deletes both junk rows via DELETE endpoint.
        foreach (var junkId in new[] { balanceJunk1, balanceJunk2 })
        {
            var del = await s.Client.DeleteAsync(
                $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{junkId}");
            Assert.True(del.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
                $"DELETE failed: {(int)del.StatusCode} {await del.Content.ReadAsStringAsync()}");
        }

        // After hides: -10 (loser) + -10 (winner) + -30 (c) = -50.
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // Merge loser into winner.
        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winner}",
            new PatchTransactionRequest { MergeFromHeaderId = loser });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

        // After merge: loser excluded, balance(C) = -10 (winner) + -30 (c) = -40.
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 11 — RAW-SQL loser insertion (mimics MD importer's
    // Dapper bulk insert which bypasses the BalanceRecomputeInterceptor),
    // followed by HTTP POST winner (interceptor fires), then
    // HTTP PATCH merge. This more faithfully mirrors the user's
    // production data: the loser existed in the DB before any
    // interceptor recompute could see it.
    //
    // The importer's BalanceRecomputeStep runs at end-of-import to
    // populate balance rows for raw-SQL inserts. We replicate by
    // calling fn_recompute_balances_for_account directly after the
    // raw insert.
    // ===================================================================
    [Fact]
    public async Task Imported_loser_then_sync_winner_then_merge_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        // 1. Raw-SQL insert the loser (mimics importer Dapper path).
        //    No interceptor fires.
        var loserId = Guid.NewGuid();
        var loserCashLeg = Guid.NewGuid();
        var loserCatLeg = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            // Mig 107: origin='file_import' + provider_key='qif'
            // covers what an MD-imported QIF row looks like today.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_headers
                    (id, ledger_id, origin, provider_key, external_id, payee, posted_at, transacted_at, created_at)
                VALUES ({loserId}, {s.Ledger.LedgerId}, 'file_import', 'qif', 'md-1',
                        'Test Bank', '2026-04-30 00:00:00+00'::timestamptz,'2026-04-30 00:00:00+00'::timestamptz,
                        '2026-04-22 12:00:00+00'::timestamptz);
                INSERT INTO txn_legs
                    (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES
                    ({loserCashLeg}, {loserId}, {s.Ledger.LedgerId}, {s.Bank.Id},     0, -10),
                    ({loserCatLeg},  {loserId}, {s.Ledger.LedgerId}, {s.Category.Id}, 0,  10);
            ");

            // Mimic importer's end-of-step balance backfill.
            await db.Database.ExecuteSqlRawAsync(
                "SELECT fn_recompute_balances_for_account({0}, '0001-01-01'::timestamptz);",
                s.Bank.Id);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT fn_recompute_balances_for_account({0}, '0001-01-01'::timestamptz);",
                s.Category.Id);
        }

        // 2. POST C (May 31) — interceptor fires, balance(C) reflects
        //    loser-only state: -10 + -30 = -40.
        var cResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = -30m } },
            });
        var c = (await cResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        // 3. POST winner (Apr 30, noon UTC — DIFFERENT timestamptz
        //    from loser's midnight). Interceptor fires, balance(C)
        //    should bump to -50 because winner is between loser and C
        //    in canonical (posted_at, seq) order.
        var wResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Interest Paid",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = -10m } },
            });
        var winnerId = (await wResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // Stamp external_id + needs_review on the winner via raw
        // (these aren't balance-affecting).
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == winnerId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-1")
                                              .SetProperty(h => h.NeedsReview, true));
        }

        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // 4. PATCH merge with header-override (payee/memo/postedAt) +
        //    Approve + mergeFromHeaderId. EXACT shape the SPA sends.
        var patchResp = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winnerId}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid USD special other Interest:Interest Earned",
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                MergeFromHeaderId = loserId,
                Approve = true,
            });
        Assert.True(patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patchResp.StatusCode} {await patchResp.Content.ReadAsStringAsync()}");

        // 5. After merge: balance(C) should be -10 (winner) + -30 (c) = -40.
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 10 — REAL user scenario reproduction. The PATCH had
    // BOTH `mergeFromHeaderId` AND `postedAt` override AND
    // `payee`/`memo` override AND the winner's raw posted_at
    // differs from the override posted_at. This is what the actual
    // SPA merge-from-candidate-with-prefill flow sends.
    //
    // The winner had raw posted_at = noon UTC (SimpleFIN insertion);
    // user merged with prefill from loser → override posted_at =
    // midnight UTC (matches loser). Both are still "April 30" but
    // they're distinct TIMESTAMPTZ values.
    // ===================================================================
    [Fact]
    public async Task Merge_with_header_override_and_offset_posted_at_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        // Loser at midnight UTC, Apr 30.
        var loser = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                Payee = "Test Bank",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = -10m } },
            });
        var loserId = (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // C at May 31 — well after both loser and winner.
        var cResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = -30m } },
            });
        var c = (await cResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));

        // Winner at noon UTC, Apr 30 (different timestamptz than loser
        // but same calendar day — exactly mirrors the user's data
        // shape).
        var winner = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Interest Paid",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = -10m } },
            });
        var winnerId = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        Assert.Equal(-50m, await ReadBalanceAsync(s, c));

        // Stamp external_ids + needs_review.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == loserId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-1"));
            await db.TxnHeaders.Where(h => h.Id == winnerId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-1")
                                              .SetProperty(h => h.NeedsReview, true));
        }

        // PATCH winner with: payee/memo override (prefill from loser),
        // postedAt override (midnight UTC, matching loser),
        // mergeFromHeaderId = loser. This is the full real-world shape.
        var patchResp = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winnerId}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid USD special other Interest:Interest Earned",
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                MergeFromHeaderId = loserId,
                Approve = true,
            });
        Assert.True(patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH failed: {(int)patchResp.StatusCode} {await patchResp.Content.ReadAsStringAsync()}");

        // After merge: loser excluded, balance(C) = -40.
        Assert.Equal(-40m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Scenario 12 — REAL user sequence: merge PATCH at t0 (winner +
    // mergeFromHeaderId), then a SECOND PATCH at t1 on a DOWNSTREAM
    // row that just adds a payee/memo/postedAt override (no merge,
    // no postings reshape). A representative `txn_header_overrides`
    // table showed two override rows updated 39 seconds apart — this
    // is exactly that pattern.
    //
    // If the second PATCH's recompute reads a stale v_starting from
    // a balance row that wasn't fully refreshed by the first PATCH,
    // we'd see balance drift.
    // ===================================================================
    [Fact]
    public async Task Merge_then_downstream_override_balance_correct()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var loserResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                Payee = "Test Bank",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 726.47m } },
            });
        var loserId = (await loserResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var b10Resp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Northwind Bank",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 1500m } },
            });
        var b10 = (await b10Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var may31Resp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Interest Paid",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 735.44m } },
            });
        var may31 = (await may31Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var winnerResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Interest Paid",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 726.47m } },
            });
        var winnerId = (await winnerResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // Pre-merge: loser(+726.47) + winner(+726.47) + b10(+1500) + may31(+735.44) = 3688.38
        Assert.Equal(3688.38m, await ReadBalanceAsync(s, may31));

        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == loserId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-1"));
            await db.TxnHeaders.Where(h => h.Id == winnerId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-1")
                                              .SetProperty(h => h.NeedsReview, true));
        }

        // PATCH 1: merge + override on winner (the actual user flow).
        var p1 = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winnerId}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid USD special other Interest:Interest Earned",
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                MergeFromHeaderId = loserId,
                Approve = true,
            });
        Assert.True(p1.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH 1 failed: {(int)p1.StatusCode} {await p1.Content.ReadAsStringAsync()}");

        // After PATCH 1: loser excluded. may31 balance = +726.47 + 1500 + 735.44 = +2961.91
        Assert.Equal(2961.91m, await ReadBalanceAsync(s, may31));

        // PATCH 2: override on may31 (payee/memo/postedAt). NO merge.
        // This exact pattern can occur shortly after
        // PATCH 1.
        var p2 = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{may31}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid Balance: $2,961.91",
                PostedAt = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                Approve = true,
            });
        Assert.True(p2.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH 2 failed: {(int)p2.StatusCode} {await p2.Content.ReadAsStringAsync()}");

        // After PATCH 2: balance unchanged at +2961.91.
        Assert.Equal(2961.91m, await ReadBalanceAsync(s, may31));
    }

    // ===================================================================
    // Scenario 13 — REAL user sequence per sync_runs timestamps:
    //   * Merge PATCH on winner (03:07:03 in user's case)
    //   * Downstream override PATCH on May 31 (03:07:42)
    //   * THIRD: a SimpleFIN sync re-runs at 03:10:15. alreadyKnown=2
    //     means the sync touched 2 existing rows that matched on
    //     FITID. If one of those was the post-merged winner or the
    //     post-override may31, the orchestrator's alreadyKnown branch
    //     could mutate fields in a way that, on the final
    //     SaveChangesAsync, lets a stale balance row survive.
    //
    // Simulate by manually invoking the alreadyKnown-shape state
    // change (a TxnHeaderRow Modified with payee + provider_raw_payload)
    // through the EF tracker — this is exactly what
    // IngestOrchestrator does in that branch, minus the network call.
    // ===================================================================
    [Fact]
    public async Task Merge_then_already_known_sync_does_not_reintroduce_stale_balance()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var loserResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                Payee = "Test Bank",
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 726.47m } },
            });
        var loserId = (await loserResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 1500m } },
            });

        var may31Resp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 735.44m } },
            });
        var may31 = (await may31Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var winnerResp = await s.Client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = s.Bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = s.Category.Id, Amount = 726.47m } },
            });
        var winnerId = (await winnerResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        // Mimic the post-sync state: loser is an MD-imported row;
        // winner + may31 are SimpleFIN-imported rows. Mig 107 split
        // origin into (origin, provider_key); SimpleFIN dedup scopes
        // by provider_key, so set both columns here to line rows up
        // with the alreadyKnown branch.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == loserId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "md-1"));
            await db.TxnHeaders.Where(h => h.Id == winnerId)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-1")
                                              .SetProperty(h => h.Origin, "online_import")
                                              .SetProperty(h => h.ProviderKey, "simplefin")
                                              .SetProperty(h => h.NeedsReview, true));
            await db.TxnHeaders.Where(h => h.Id == may31)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.ExternalId, "sf-may31")
                                              .SetProperty(h => h.Origin, "online_import")
                                              .SetProperty(h => h.ProviderKey, "simplefin"));
        }

        // PATCH 1: merge + override on winner.
        var p1 = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winnerId}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid USD",
                PostedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                MergeFromHeaderId = loserId,
                Approve = true,
            });
        Assert.True(p1.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        // PATCH 2: override on may31.
        var p2 = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{may31}",
            new PatchTransactionRequest
            {
                Payee = "Test Bank",
                Memo = "Interest Paid Balance: $2,961.91",
                PostedAt = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                Approve = true,
            });
        Assert.True(p2.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        // Sanity: correct balance after both PATCHes.
        Assert.Equal(2961.91m, await ReadBalanceAsync(s, may31));

        // SIMULATE the alreadyKnown sync branch hitting may31 + winner.
        // The orchestrator does: load existing tracked → backfill
        // provider_raw_payload + maybe payee/memo → SaveChanges.
        await using (var db = _fixture.NewDbContext())
        {
            var existingMay31 = await db.TxnHeaders.SingleAsync(h => h.Id == may31);
            existingMay31.ProviderRawPayload = "{\"id\":\"sf-may31\",\"amount\":735.44}";
            // alreadyKnown might also update payee/memo on the
            // pre-split SimpleFIN heuristic — not balance-affecting.
            existingMay31.Payee = "Refreshed by sync";

            var existingWinner = await db.TxnHeaders.SingleAsync(h => h.Id == winnerId);
            existingWinner.ProviderRawPayload = "{\"id\":\"sf-1\",\"amount\":726.47}";

            await db.SaveChangesAsync();
        }

        // After the alreadyKnown re-sync: balance MUST still be correct.
        Assert.Equal(2961.91m, await ReadBalanceAsync(s, may31));
    }

    // ===================================================================
    // Scenario 9 — merge a row whose posted_at is EARLIER than the
    // winner's. The interceptor anchors recompute at the LOSER's
    // posted_at; if it anchored at the winner's instead, the loser's
    // pre-merge balance contribution would survive in rows between
    // (loser.posted_at, winner.posted_at).
    // ===================================================================
    [Fact]
    public async Task Merge_loser_older_than_winner_anchors_at_loser_posted_at()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        // Apr 5 winner-to-be; Apr 1 loser; Apr 20 anchor.
        var winner = await PostTxnAsync(s, 5, -5m, payee: "winner");
        var c = await PostTxnAsync(s, 20, -20m);
        Assert.Equal(-25m, await ReadBalanceAsync(s, c));

        var loser = await PostTxnAsync(s, 1, -5m, payee: "dup-earlier");
        Assert.Equal(-30m, await ReadBalanceAsync(s, c));

        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.Id == winner)
                .ExecuteUpdateAsync(s2 => s2.SetProperty(h => h.NeedsReview, true));
        }
        var patch = await s.Client.PatchAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/transactions/{winner}",
            new PatchTransactionRequest { MergeFromHeaderId = loser });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        // After merge: balance(C) = -25.
        Assert.Equal(-25m, await ReadBalanceAsync(s, c));
    }

    // ===================================================================
    // Health endpoint — healthy path. Post normal transactions; the
    // interceptor wires every balance row. /balances/health should
    // report `healthy: true` with zero drift.
    // ===================================================================
    [Fact]
    public async Task BalancesHealth_reports_healthy_when_no_drift()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        await PostTxnAsync(s, 10, -10m);
        await PostTxnAsync(s, 20, -20m);
        await PostTxnAsync(s, 30, -30m);

        var resp = await s.Client.PostAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/balances/health", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var report = await resp.Content.ReadFromJsonAsync<BalanceHealthReport>();
        Assert.NotNull(report);
        Assert.True(report!.Healthy);
        Assert.Equal(0, report.DriftedCount);
        Assert.Empty(report.Drifted);
        Assert.True(report.RowsChecked >= 3);
        Assert.True(report.AccountsChecked >= 1);
    }

    // ===================================================================
    // Health endpoint — drift detection + heal. Corrupt a balance row
    // by writing directly to the DB (bypassing the interceptor — this
    // simulates a writer that skipped the recompute, which IS the
    // bug class this endpoint exists to catch). Call /balances/health.
    // Expect: drifted list contains the corrupted row, and a second
    // read of the underlying row shows the healed value.
    // ===================================================================
    [Fact]
    public async Task BalancesHealth_detects_drift_and_heals_row()
    {
        var s = await SetUpAsync();
        await using var _ = s.Factory;

        var headerId = await PostTxnAsync(s, 10, -10m);
        Assert.Equal(-10m, await ReadBalanceAsync(s, headerId));

        // Manually corrupt the row: bump balance_after by +999. This is
        // the kind of drift that would appear if some writer mutated a
        // balance-relevant column via ExecuteUpdate / raw SQL without
        // a follow-up recompute call.
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaderAccountBalances
                .Where(r => r.HeaderId == headerId && r.AccountId == s.Bank.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.BalanceAfter, r => r.BalanceAfter + 999m));
        }
        Assert.Equal(989m, await ReadBalanceAsync(s, headerId));

        var resp = await s.Client.PostAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/balances/health", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var report = await resp.Content.ReadFromJsonAsync<BalanceHealthReport>();
        Assert.NotNull(report);
        Assert.False(report!.Healthy);
        Assert.Equal(1, report.DriftedCount);

        var drift = Assert.Single(report.Drifted);
        Assert.Equal(headerId, drift.HeaderId);
        Assert.Equal(s.Bank.Id, drift.AccountId);
        Assert.Equal(989m, drift.StoredBefore);
        Assert.Equal(-10m, drift.RecomputedAfter);
        Assert.Equal(-999m, drift.Diff);

        // Heal step: the row should now be the correct -10.
        Assert.Equal(-10m, await ReadBalanceAsync(s, headerId));
    }
}
