using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Balance correctness when a transaction's EFFECTIVE posted date moves.
/// When the date shifts, the recompute must anchor at <c>MIN(old, new)</c>
/// so the rows in the vacated <c>[old, new)</c> range are re-walked — not
/// just the rows from the new date forward. Moving a row LATER across
/// intervening rows is the highest-value stress of that logic (the bug
/// that drifted real balances by the moved txn's amount).
///
/// <para>Important code-path note: the bank PATCH endpoint routes EVERY
/// header-field edit — posted_at included — through
/// <c>txn_header_overrides</c> (ADR-0003); it never writes
/// <c>txn_headers.posted_at</c> directly. So a date edit via HTTP
/// exercises the interceptor's <c>CaptureHeaderOverrideEntry</c> anchor:
/// the FIRST date edit ADDs an override row (no prior override → old
/// effective is the raw header date), a SECOND edit MODIFIES it (old
/// effective is the prior override date). Both directions are covered
/// below. The recompute resolves the effective date via
/// <c>COALESCE(o.posted_at, h.posted_at)</c>.</para>
///
/// Each test pins exact <c>balance_after</c> values (the independent
/// oracle) and uses distinct dates so the canonical order is
/// unambiguous. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DateMoveBalanceTests
{
    private readonly PostgresFixture _fixture;

    public DateMoveBalanceTests(PostgresFixture fixture) => _fixture = fixture;

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

    private static async Task<Guid> CreateAsync(
        HttpClient client, Guid ledgerId, Guid bankId, Guid categoryId, int day, decimal amount)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
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

    private static async Task<decimal> BalanceAfterAsync(
        AppDbContext db, Guid headerId, Guid accountId) =>
        (await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == headerId && r.AccountId == accountId))
        .BalanceAfter;

    /// <summary>
    /// PATCH a later row's posted date to BEFORE an earlier row (override
    /// ADD, moved earlier). The row that was earlier is now downstream and
    /// must pick up the moved row's amount; the moved row sits first.
    /// </summary>
    [Fact]
    public async Task Date_moved_earlier_recomputes_now_downstream_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var tEarly = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 5, 1000m);
        var tLate = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 20, -200m);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1000m, await BalanceAfterAsync(db, tEarly, bank.Id));
            Assert.Equal(800m, await BalanceAfterAsync(db, tLate, bank.Id));
        }

        // Move tLate to May 1 — BEFORE tEarly. New order: tLate (-200)
        // then tEarly (800).
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tLate}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(-200m, await BalanceAfterAsync(db, tLate, bank.Id));
            Assert.Equal(800m, await BalanceAfterAsync(db, tEarly, bank.Id));
        }
    }

    /// <summary>
    /// Seed five dated rows, then move the SECOND one to AFTER the
    /// fourth. Every row between the old and new positions must be
    /// re-walked to drop the moved row's contribution; the moved row
    /// re-accrues at its new slot. Asserts the absolute balance on EVERY
    /// row — a recompute that anchored at the NEW date only would leave
    /// the three skipped-over rows drifted by the moved row's amount.
    /// </summary>
    [Fact]
    public async Task Date_moved_across_multiple_intervening_rows_recomputes_each()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        //   May 2  +100  -> 100
        //   May 6  +500  -> 600   (this one moves)
        //   May 10  +30  -> 630
        //   May 14   +7  -> 637
        //   May 18   +4  -> 641
        var t1 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 2, 100m);
        var t2 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 6, 500m);
        var t3 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 10, 30m);
        var t4 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 14, 7m);
        var t5 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 18, 4m);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(641m, await BalanceAfterAsync(db, t5, bank.Id));
        }

        // Move t2 (+500) to May 16 — between t4 (May 14) and t5 (May 18).
        //   May 2  t1 +100 -> 100
        //   May 10 t3  +30 -> 130   (vacated: was 630)
        //   May 14 t4   +7 -> 137   (vacated: was 637)
        //   May 16 t2 +500 -> 637
        //   May 18 t5   +4 -> 641
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{t2}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(100m, await BalanceAfterAsync(db, t1, bank.Id));
            Assert.Equal(130m, await BalanceAfterAsync(db, t3, bank.Id)); // vacated
            Assert.Equal(137m, await BalanceAfterAsync(db, t4, bank.Id)); // vacated
            Assert.Equal(637m, await BalanceAfterAsync(db, t2, bank.Id)); // re-accrued
            Assert.Equal(641m, await BalanceAfterAsync(db, t5, bank.Id)); // unchanged total
        }
    }

    /// <summary>
    /// PATCH a row's posted date to a different TIME on the SAME calendar
    /// day. The row keeps its <c>(posted_at, seq)</c> order relative to
    /// its neighbours, so every balance is unchanged — guards against an
    /// over-eager "any posted_at write reshuffles the account" regression.
    /// </summary>
    [Fact]
    public async Task Same_day_time_only_change_leaves_balances_unchanged()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // May 10 +100 -> 100; May 11 +25 -> 125; May 12 -40 -> 85.
        var t1 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 10, 100m);
        var t2 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 11, 25m);
        var t3 = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 12, -40m);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(125m, await BalanceAfterAsync(db, t2, bank.Id));
            Assert.Equal(85m, await BalanceAfterAsync(db, t3, bank.Id));
        }

        // Move t2 from 12:00 to 08:30 on the SAME day (May 11). It stays
        // strictly between t1 (May 10) and t3 (May 12), so every balance
        // is identical.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{t2}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 11, 8, 30, 0, DateTimeKind.Utc),
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(100m, await BalanceAfterAsync(db, t1, bank.Id));
            Assert.Equal(125m, await BalanceAfterAsync(db, t2, bank.Id));
            Assert.Equal(85m, await BalanceAfterAsync(db, t3, bank.Id));
        }
    }

    /// <summary>
    /// The override-MODIFY anchor (distinct from the override-ADD anchor
    /// the other tests hit). The row already carries an effective date set
    /// via a prior override; a second PATCH moves that override LATER. The
    /// interceptor must read the OLD override value from OriginalValues and
    /// anchor at the earlier of old/new so the vacated range re-walks.
    /// </summary>
    [Fact]
    public async Task Date_moved_later_via_override_modify_recomputes_vacated_range()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // May 5 +1000 -> 1000; May 10 -200 -> 800 (tX); May 15 +50 -> 850.
        var tA = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 5, 1000m);
        var tX = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 10, -200m);
        var tB = await CreateAsync(client, ledger.LedgerId, bank.Id, category.Id, 15, 50m);

        // First PATCH: set tX's effective date to May 8 via the override
        // ADD path (still before tB). Installs the override row to MODIFY.
        var firstResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tX}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            firstResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)firstResp.StatusCode}: {await firstResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(850m, await BalanceAfterAsync(db, tB, bank.Id));
        }

        // Second PATCH (under test): MODIFY tX's override date to May 20 —
        // AFTER tB. New order: tA (1000) -> tB (1050) -> tX (850). tB is the
        // vacated row; the interceptor anchors at MIN(old=May 8, new=May 20)
        // = May 8 and re-walks the vacated range.
        var secondResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tX}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            secondResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)secondResp.StatusCode}: {await secondResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1000m, await BalanceAfterAsync(db, tA, bank.Id));
            Assert.Equal(1050m, await BalanceAfterAsync(db, tB, bank.Id)); // vacated
            Assert.Equal(850m, await BalanceAfterAsync(db, tX, bank.Id));  // re-accrued
        }
    }
}
