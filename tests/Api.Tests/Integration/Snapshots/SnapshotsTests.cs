using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Snapshots;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Snapshots;

/// <summary>
/// End-to-end coverage for ADR-0037 / migration 111 — server-side
/// capped per-ledger snapshots. Exercises walker round-trip, the
/// eviction rule's three branches, restore atomicity, and schema-
/// version refusal. Manual-flow snapshots are created through the
/// HTTP endpoints (real serialisation, real auth); restore is
/// exercised the same way.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SnapshotsTests
{
    private readonly PostgresFixture _fixture;

    public SnapshotsTests(PostgresFixture fixture)
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

    // -----------------------------------------------------------------
    // Walker round-trip + restore atomicity
    // -----------------------------------------------------------------

    /// <summary>
    /// The auto-snapshot repo, built the way PRODUCTION builds it: over the
    /// service-role context the scheduler hands to <c>SnapshotJobHandler</c>.
    /// </summary>
    /// <remarks>
    /// These two tests previously resolved the repository out of
    /// <c>factory.Services.CreateScope()</c>. That is the app-role context with no
    /// request behind it, so <c>ICurrentUserAccessor.IsAuthenticated</c> is false,
    /// <c>app.user_id</c> is never set, and RLS is fail-closed on every scoped table
    /// — the exact shape that made
    /// <c>Snapshot_payload_is_captured_server_side_in_content_json</c> capture
    /// nothing and assert that it had captured something. It happens to work here
    /// only because the snapshot tables do not carry RLS policies YET (see
    /// "RLS on the snapshot tables" in follow-ups), so the pattern is a latent break
    /// as well as a wrong mirror of production: the auto-snap path is the scheduler,
    /// which runs service-role precisely because the app role would see no ledgers.
    /// </remarks>
    private LedgerSnapshotsRepository AutoSnapshotRepo() =>
        new(_fixture.NewDbContext(),
            NullLogger<LedgerSnapshotsRepository>.Instance);

    [Fact]
    public async Task Create_then_restore_round_trips_the_ledger_state()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "lunch");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -12m,
            new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
            payee: "coffee");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Snapshot the current state.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>();
        Assert.NotNull(created?.Snapshot);
        Assert.Equal("manual", created!.Snapshot!.Kind);
        Assert.Equal("baseline", created.Snapshot.Description);

        // Mutate the ledger AFTER the snapshot.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -100m,
            new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc),
            payee: "post-snapshot row");

        await using (var db = _fixture.NewDbContext())
        {
            var preCount = await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId);
            Assert.Equal(3, preCount);
        }

        // Restore — the post-snapshot row should vanish.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{created.Snapshot.Id}/restore",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            var postCount = await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId);
            Assert.Equal(2, postCount);
            // Balances re-derived from legs by the SQL restore
            // function — confirm the materialised table is in sync
            // with what the legs say.
            var bankBalanceRows = await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(b => b.AccountId == bank.Id);
            Assert.Equal(2, bankBalanceRows);
        }
    }

    [Fact]
    public async Task Restore_records_a_ledger_operations_audit_row()
    {
        // ADR-0055/0086: a restore replaces the ledger's data in place, so it
        // earns a durable ledger_operations row visible in the Activity timeline
        // (family 'snapshot', provider 'snapshot-restore'). Previously restore left
        // no durable trace. Asserted through the public timeline endpoint (the
        // read path the SPA uses), not the internal table.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        var timeline = await client.GetFromJsonAsync<List<LedgerOperationSummaryDto>>(
            $"/api/ledgers/{ledger.LedgerId}/ledger-operations?provider=snapshot-restore");
        Assert.NotNull(timeline);
        var op = Assert.Single(timeline!);
        Assert.Equal("snapshot", op.Family);
        Assert.Equal("snapshot-restore", op.ProviderKey);
        Assert.Equal("manual", op.TriggeredVia);
        Assert.Equal("completed", op.Status);
        Assert.Equal(ledger.UserId, op.TriggeredByUserId);
        Assert.NotNull(op.CompletedAt);
    }

    [Fact]
    public async Task Snapshot_payload_is_captured_server_side_in_chunked_parts()
    {
        // mig 179 captured the in-scope graph into content_json (jsonb) so it never
        // entered managed memory. That fixed an API-side OOM and left a Postgres-side
        // one: the whole document was still built as one value, measured at 2.49 GB of
        // backend anon memory for a 184 MB artifact, which the kernel OOM-killed.
        //
        // mig 193 (v3): rows are captured per table in chunks into
        // ledger_snapshot_parts, so peak memory tracks chunk size rather than ledger
        // size. content_json is left NULL and the legacy gzip `content` stays empty —
        // presence of parts rows is the v3 gate.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");

        // Created through the endpoint, NOT by resolving the repository directly.
        // Capture reads the in-scope tables as coffer_app, and those tables carry
        // RLS keyed on app.user_id — which only a real request sets. Calling the
        // repository straight out of a DI scope captures a payload with every key
        // present and every array empty. The v2 version of this test did exactly
        // that and still passed, because it only asserted the "accounts" KEY was
        // present, which is equally true of "accounts": []. It would have passed
        // against an entirely empty snapshot.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        await using var db = _fixture.NewDbContext();   // service role: sees past RLS
        var row = await db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.Id == created.Id)
            .Select(s => new { s.ContentJson, s.Content, s.ContentSizeUncompressed })
            .SingleAsync();
        Assert.Null(row.ContentJson);        // v3: the payload lives in parts, not here
        Assert.Empty(row.Content);           // legacy gzip content unused
        Assert.True(row.ContentSizeUncompressed > 0);

        var snapshotId = created.Id;

        // Every table fn_ledger_snapshot_payload declares got a part — including the
        // ones that are empty for this ledger, because capture always writes seq=0.
        // That completeness is what mig 188's realized_gains assertion relies on: it
        // distinguishes an absent key from an empty '[]'.
        var missingParts = await db.Database.SqlQueryRaw<string>(
            """
            SELECT t AS "Value"
              FROM unnest(fn_ledger_snapshot_part_names()) AS t
             WHERE NOT EXISTS (SELECT 1 FROM ledger_snapshot_parts p
                                WHERE p.snapshot_id = {0} AND p.part_name = t)
            """, snapshotId).ToListAsync();
        Assert.Empty(missingParts);

        // ...and nothing was written that the payload does not declare, so capture
        // and restore cannot disagree about the key set.
        var unexpectedParts = await db.Database.SqlQueryRaw<string>(
            """
            SELECT DISTINCT p.part_name AS "Value"
              FROM ledger_snapshot_parts p
             WHERE p.snapshot_id = {0}
               AND NOT (p.part_name = ANY (fn_ledger_snapshot_part_names()))
            """, snapshotId).ToListAsync();
        Assert.Empty(unexpectedParts);

        // The chunks carry the actual rows.
        var accountsPart = await db.Database.SqlQueryRaw<string>(
            """
            SELECT content::text AS "Value"
              FROM ledger_snapshot_parts
             WHERE snapshot_id = {0} AND part_name = 'accounts' AND seq = 0
            """, snapshotId).SingleAsync();
        Assert.Contains("checking", accountsPart);
    }

    [Fact]
    public async Task A_pre_migration_v2_snapshot_still_restores()
    {
        // Migration 193 made capture produce v3 (chunked parts), which means every
        // other test in this file exercises the v3 path and NOTHING exercises v2 —
        // even though 193 also modified the v2 restore function, extracting its
        // delete block into fn_ledger_snapshot_clear so both paths share one FK
        // ordering. Preflight passed with the v2 branch at zero coverage.
        //
        // That matters because an install upgrading to 193 keeps its existing v2
        // snapshots (nothing is rewritten in place), and those restore through the
        // refactored function. The snapshot you would reach for if an upgrade went
        // badly is exactly the one whose path was untested.
        //
        // So: capture normally (v3), convert the row to a genuine v2 snapshot, and
        // restore it through the real endpoint. fn_ledger_snapshot_parts_payload is
        // the reassembly helper 193 added for diagnostics; here it manufactures the
        // single content_json document v2 stored, byte-order-identical to what mig
        // 179's capture would have written.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -12m,
            new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc), payee: "coffee");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("v2-shaped"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Rewrite it into the v2 format: payload in content_json, no parts rows.
        await using (var svc = _fixture.NewDbContext())
        {
            await svc.Database.ExecuteSqlRawAsync(
                """
                UPDATE ledger_snapshots
                   SET content_json = fn_ledger_snapshot_parts_payload({0})
                 WHERE id = {0};
                """, snap.Id);
            await svc.Database.ExecuteSqlRawAsync(
                "DELETE FROM ledger_snapshot_parts WHERE snapshot_id = {0};", snap.Id);

            var shape = await svc.LedgerSnapshots.AsNoTracking()
                .Where(s => s.Id == snap.Id)
                .Select(s => new { HasJson = s.ContentJson != null, s.Content.Length })
                .SingleAsync();
            Assert.True(shape.HasJson);   // v2: document present
            Assert.Equal(0, shape.Length); // and NOT v1 — no gzip bytes, so the
                                           // format probe must route it server-side
        }

        // Mutate after the snapshot so the restore has something to undo.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -100m,
            new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc), payee: "post-snapshot row");
        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(3, await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId));
        }

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(2, await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId));
            // Balances rebuilt by fn_recompute_balances_for_ledger via the v2 path.
            Assert.Equal(2, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(b => b.AccountId == bank.Id));
        }
    }

    [Fact]
    public async Task Capture_and_restore_agree_on_the_table_set()
    {
        // mig 193 necessarily keeps two lists: fn_ledger_snapshot_part_names() in
        // capture order, and fn_ledger_snapshot_insert_order() in forward-FK order,
        // because capture order is not safe for inserts. Two lists that must stay in
        // step are a latent trap — add a table to the payload, forget the restore
        // list, and every restore silently drops it with nothing failing. The
        // per-part assertions elsewhere catch a missing PART, not a missing INSERT.
        //
        // Asserting set equality here is what closes that gap. Order deliberately
        // is not compared: the two orders differing is the entire point.
        await using var db = _fixture.NewDbContext();
        var mismatched = await db.Database.SqlQueryRaw<string>(
            """
            SELECT t AS "Value" FROM (
                SELECT unnest(fn_ledger_snapshot_part_names()) AS t
                EXCEPT
                SELECT unnest(fn_ledger_snapshot_insert_order())
            ) missing_from_restore
            UNION ALL
            SELECT t FROM (
                SELECT unnest(fn_ledger_snapshot_insert_order()) AS t
                EXCEPT
                SELECT unnest(fn_ledger_snapshot_part_names())
            ) missing_from_capture
            """).ToListAsync();

        Assert.Empty(mismatched);
    }

    [Fact]
    public async Task Restore_leaves_realized_gains_matching_the_snapshot_not_later_sales()
    {
        // Regression for mig 181, mechanism updated by mig 188. Originally
        // realized_gains was neither captured nor rebuilt, so after a restore the
        // realized-gains report still reflected the discarded post-snapshot sales
        // even though the transactions themselves rolled back. Mig 181 fixed that
        // by recomputing the whole ledger's derived investment state; mig 188
        // captures realized_gains in the payload and restores it directly, because
        // the recompute cost ~27s and its only unique output was this table (it
        // otherwise overwrote the holdings/lots the payload already carried).
        //
        // The guarantee asserted here is unchanged and mechanism-independent:
        // after a restore, realized gains reflect the SNAPSHOT, not later sales.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var broker = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task PostInv(object req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // Buy 10 @ $100 (2020), sell 5 @ $200 (2021) → one realized gain of $500.
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc) });
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "sell", SecurityId = security, Shares = -5m, Price = 200m, PostedAt = new DateTime(2021, 1, 1, 12, 0, 0, DateTimeKind.Utc) });

        var atSnapshot = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(500m, atSnapshot.TotalRealizedGain);

        // Snapshot the $500-realized state.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("one-sale"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Sell the remaining 5 @ $300 (2022) → a SECOND gain of $1,000 (total $1,500).
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "sell", SecurityId = security, Shares = -5m, Price = 300m, PostedAt = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc) });
        var afterSecondSale = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(1500m, afterSecondSale.TotalRealizedGain);

        // Restore the snapshot — the second sale is discarded.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        // realized_gains must reflect ONLY the snapshot state ($500) — not the
        // discarded $300 sale. Pre-mig-181 this read $1,500 (stale).
        var afterRestore = await new InvestmentReportingRepository(_fixture.NewDbContext())
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(500m, afterRestore.TotalRealizedGain);
        Assert.Single(afterRestore.Rows);

        // And holdings are back to 5 shares @ $100 basis.
        await using var db = _fixture.NewDbContext();
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == broker.HoldingsAccountId!.Value && h.SecurityId == security);
        Assert.Equal(5m, holding.Quantity);
        Assert.Equal(500m, holding.CostBasis);
    }

    [Fact]
    public async Task Restore_round_trips_reconciliation_status()
    {
        // Regression for mig 181. txn_leg_recon (ADR-0082 per-leg reconciliation)
        // has ON DELETE CASCADE off txn_legs, so restore's `DELETE FROM txn_legs`
        // wiped it; it was not in the payload, so it could not come back. Recon is
        // user judgment (not derivable), so it must round-trip through the snapshot.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -40m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "market");

        // Mark the bank leg reconciled (cleared).
        Guid legId;
        await using (var db = _fixture.NewDbContext())
        {
            legId = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId && l.AccountId == bank.Id)
                .Select(l => l.Id)
                .FirstAsync();
            db.TxnLegRecon.Add(new TxnLegReconRow
            {
                LegId = legId,
                LedgerId = ledger.LedgerId,
                Status = "cleared",
                ClearedAt = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
                ClearedByUserId = null,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Snapshot the reconciled state.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("reconciled"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Un-reconcile (simulate a later change to be rolled back).
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnLegRecon.Where(r => r.LegId == legId).ExecuteDeleteAsync();
        }

        // Restore — the reconciliation mark must come back.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            var recon = await db.TxnLegRecon.AsNoTracking()
                .SingleOrDefaultAsync(r => r.LegId == legId && r.LedgerId == ledger.LedgerId);
            Assert.NotNull(recon);                       // pre-fix: null (cascade-wiped, not captured)
            Assert.Equal("cleared", recon!.Status);
        }
    }

    [Fact]
    public async Task Restore_round_trips_a_ledger_with_recurring_linked_transactions()
    {
        // Regression for mig 183 — the exact real-ledger shape that broke prod
        // restore. A committed occurrence header carries recurring_transaction_id.
        // fn_ledger_snapshot_restore deletes recurring_transactions while that
        // header still exists; the composite FK (recurring_transaction_id,
        // ledger_id) -> recurring_transactions was ON DELETE SET NULL with NO
        // column list, so it tried to null BOTH columns — including the NOT NULL
        // ledger_id — throwing 23502 and failing the whole restore. mig 183 makes
        // it SET NULL (recurring_transaction_id) only. The prior round-trip tests
        // passed because their fixtures had no recurring-linked header.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = templateId, LedgerId = ledger.LedgerId, Origin = "manual",
                Payee = "Rent", PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TransactedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsRecurringTemplate = true,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "manual",
            });
            // The committed occurrence — a real transaction stamped with the series id.
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = occurrenceId, LedgerId = ledger.LedgerId, Origin = "manual",
                Payee = "Rent", PostedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), TransactedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                IsRecurringTemplate = false, RecurringTransactionId = seriesId,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("with-recurring"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Restore — pre-mig-183 this 500'd with 23502 on the recurring FK.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        // The occurrence survived with its series link intact.
        await using var db2 = _fixture.NewDbContext();
        var occ = await db2.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == occurrenceId && h.LedgerId == ledger.LedgerId);
        Assert.Equal(seriesId, occ.RecurringTransactionId);
        Assert.True(await db2.RecurringTransactions.AsNoTracking()
            .AnyAsync(r => r.Id == seriesId && r.LedgerId == ledger.LedgerId));
    }

    // -----------------------------------------------------------------
    // Eviction rule (5-cap, auto-evicts-auto-first, manual-at-cap-refuses)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Manual_snapshot_at_cap_returns_422_snapshot_manual_at_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Fill the pool with 5 manual snaps.
        for (var i = 0; i < LedgerSnapshotsRepository.SnapshotCap; i++)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/snapshots",
                new CreateSnapshotRequest($"snap {i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // 6th manual must be refused.
        var sixth = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("over-cap"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sixth.StatusCode);
        using var doc = JsonDocument.Parse(await sixth.Content.ReadAsStringAsync());
        Assert.Equal("snapshot-manual-at-cap",
            doc.RootElement.GetProperty("code").GetString());

        // Pool still 5.
        await using var db = _fixture.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task Auto_snapshot_at_cap_evicts_oldest_auto_then_inserts()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        // Seed 5 auto-snaps via the repo directly (the HTTP endpoint
        // only creates kind=manual; auto-creation is the scheduler's
        // job, which we exercise here through the same repo).
        var repo = AutoSnapshotRepo();

        var firstAuto = await repo.CreateAsync(
            ledger.LedgerId, "auto", UserRow.SystemUserId, description: null);
        Assert.Equal(LedgerSnapshotsRepository.CreateOutcome.Created, firstAuto.Outcome);
        // Tiny delay so the auto-snap timestamps don't collide and
        // the "oldest" ordering is deterministic.
        await Task.Delay(15);
        for (var i = 0; i < 4; i++)
        {
            var r = await repo.CreateAsync(
                ledger.LedgerId, "auto", UserRow.SystemUserId, description: null);
            Assert.Equal(LedgerSnapshotsRepository.CreateOutcome.Created, r.Outcome);
            await Task.Delay(15);
        }

        // 6th auto-snap must evict the oldest auto and land.
        var sixthAuto = await repo.CreateAsync(
            ledger.LedgerId, "auto", UserRow.SystemUserId, description: null);
        Assert.Equal(LedgerSnapshotsRepository.CreateOutcome.Created, sixthAuto.Outcome);

        await using var db = _fixture.NewDbContext();
        var rows = await db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.LedgerId == ledger.LedgerId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync();
        // Still 5 — eviction held the cap.
        Assert.Equal(5, rows.Count);
        // Oldest is gone.
        Assert.DoesNotContain(rows, r => r.Id == firstAuto.Row!.Id);
    }

    [Fact]
    public async Task Auto_snapshot_skips_when_pool_full_of_manual_snaps()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // 5 manual snaps fill the pool.
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/snapshots",
                new CreateSnapshotRequest($"manual {i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // Auto-create must skip (no auto-snap to evict; manual snaps
        // are protected by ADR-0037 retention rule).
        var repo = AutoSnapshotRepo();
        var result = await repo.CreateAsync(
            ledger.LedgerId, "auto", UserRow.SystemUserId, description: null);
        Assert.Equal(
            LedgerSnapshotsRepository.CreateOutcome.SkippedDueToFullPool,
            result.Outcome);

        // Pool unchanged.
        await using var db = _fixture.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task List_returns_at_most_cap_snapshots_newest_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        for (var i = 0; i < 3; i++)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/snapshots",
                new CreateSnapshotRequest($"s{i}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            await Task.Delay(15);
        }

        var listResp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/snapshots");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<List<SnapshotSummaryDto>>();
        Assert.NotNull(list);
        Assert.Equal(3, list!.Count);
        // Newest first.
        Assert.Equal("s2", list[0].Description);
        Assert.Equal("s1", list[1].Description);
        Assert.Equal("s0", list[2].Description);
    }

    // -----------------------------------------------------------------
    // Schema-version refusal
    // -----------------------------------------------------------------

    [Fact]
    public async Task Restore_refuses_with_schema_version_mismatch_when_snapshot_is_stale()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Forge a stale schema_version on the snapshot row to simulate
        // a backup taken on an older schema version (Phase 1 of
        // ADR-0037 refuses cross-version restore — forward-migration
        // is its own ADR).
        await using (var db = _fixture.NewDbContext())
        {
            await db.LedgerSnapshots
                .Where(s => s.Id == snap.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.SchemaVersion, "001_some_ancient_mig.sql"));
        }

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, restoreResp.StatusCode);
        using var doc = JsonDocument.Parse(await restoreResp.Content.ReadAsStringAsync());
        Assert.Equal("snapshot-schema-version-mismatch",
            doc.RootElement.GetProperty("code").GetString());
    }

    // -----------------------------------------------------------------
    // Cross-ledger probe / not-found / delete
    // -----------------------------------------------------------------

    [Fact]
    public async Task Restore_returns_snapshot_not_found_when_snapshot_belongs_to_another_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var aliceClient = await AuthedClientAsync(factory, alice);

        var aliceSnap = await aliceClient.PostAsJsonAsync(
            $"/api/ledgers/{alice.LedgerId}/snapshots",
            new CreateSnapshotRequest("alice-only"));
        var aliceId = (await aliceSnap.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!
            .Snapshot!.Id;

        // Bob tries to restore Alice's snapshot via Bob's ledger URL.
        using var bobClient = await AuthedClientAsync(factory, bob);
        var resp = await bobClient.PostAsync(
            $"/api/ledgers/{bob.LedgerId}/snapshots/{aliceId}/restore",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("snapshot-not-found",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_snapshot_and_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("doomed"));
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        var del1 = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del1.StatusCode);

        // Idempotent — second delete on a now-missing id still 204s.
        var del2 = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del2.StatusCode);

        await using var db = _fixture.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(0, count);
    }
}
