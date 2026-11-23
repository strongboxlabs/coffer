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
public sealed class SnapshotsTests : DatabaseTest
{
    public SnapshotsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>
    /// Block until the monitor's boot pass has recorded a verdict for this ledger.
    /// </summary>
    /// <remarks>
    /// <c>SchedulerService</c> runs its first tick at process start with no initial
    /// delay, so every test that builds an <c>ApiFactory</c> gets one consistency
    /// pass whether it wants one or not, at a moment nothing controls. Setting up
    /// around it is a coin flip; waiting for it makes the state deterministic.
    /// <para>Once it lands the ledger is inside the 12h cadence window, so no later
    /// tick will touch the row.</para>
    /// </remarks>
    private async Task WaitForBootCheckAsync(SyntheticLedger ledger)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var svc = Db.NewServiceDbContext())
            {
                if (await svc.LedgerConsistencyStates.AsNoTracking()
                        .AnyAsync(r => r.LedgerId == ledger.LedgerId && r.LastCheckedAt != null))
                    return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The consistency monitor never recorded a verdict for this ledger.");
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

    // -----------------------------------------------------------------
    // Walker round-trip + restore atomicity
    // -----------------------------------------------------------------

    /// <summary>
    /// The auto-snapshot repo, built the way PRODUCTION builds it: over the
    /// service-role context the scheduler hands to <c>SnapshotJobHandler</c>.
    /// </summary>
    /// <remarks>
    /// Not resolved out of <c>factory.Services.CreateScope()</c>: that is the app-role
    /// context with no request behind it, so <c>ICurrentUserAccessor.IsAuthenticated</c>
    /// is false, <c>app.user_id</c> is never set, and RLS is fail-closed on every scoped
    /// table — a capture over it sees nothing. It would also be a wrong mirror of
    /// production: the auto-snap path is the scheduler, which runs service-role
    /// precisely because the app role would see no ledgers.
    /// </remarks>
    private LedgerSnapshotsRepository AutoSnapshotRepo() =>
        new(Db.NewDbContext(),
            NullLogger<LedgerSnapshotsRepository>.Instance, TimeProvider.System);

    [Fact]
    public async Task Create_then_restore_round_trips_the_ledger_state()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
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

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

        await using (var db = Db.NewDbContext())
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

        await using (var db = Db.NewDbContext())
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
        // (family 'snapshot', provider 'snapshot-restore'). Asserted through the public timeline endpoint (the
        // read path the SPA uses), not the internal table.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
    public async Task Restoring_invalidates_the_ledgers_consistency_verdict()
    {
        // A restore replaces the rows the last verdict was about, so the verdict
        // stops being a statement about anything and must go back to unknown
        // (mig 242). Nulling last_checked_at is also what makes the ledger due on
        // the monitor's next tick.
        //
        // The cadence is what gives this teeth. At twice a day a snapshot captured
        // WHILE drifted would otherwise sit behind
        // a stale green for half a day — the failure direction that matters, since
        // a green mark is read as "nothing to do here".
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // A settled verdict to invalidate, written over whatever the monitor's boot
        // pass left. Through the SERVICE context — the table has no write policy
        // for the app role, which is the posture being relied on here, not worked
        // around.
        var checkedAt = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
        await WaitForBootCheckAsync(ledger);
        await using (var svc = Db.NewServiceDbContext())
        {
            var seeded = await svc.LedgerConsistencyStates
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            seeded.LastCheckedAt = checkedAt;
            seeded.Healthy = true;
            seeded.FailingProjections = [];
            seeded.LastRepairedAt = checkedAt;
            await svc.SaveChangesAsync();
        }

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var svc = Db.NewServiceDbContext())
        {
            var state = await svc.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Null(state.Healthy);
            Assert.Null(state.LastCheckedAt);

            // The repair history survives. It is a fact about what someone did,
            // not a claim about the current rows, so a rollback must not rewrite
            // it — the same reason ledger_events is left out of the snapshot.
            Assert.Equal(checkedAt, state.LastRepairedAt);
        }
    }

    [Fact]
    public async Task A_restore_does_not_invent_a_consistency_row()
    {
        // No row already means "never checked", which is exactly what an
        // invalidation is trying to say — so there is nothing to write. Worth a
        // test because the obvious implementation is an upsert, and an upsert
        // here would manufacture a row for every ledger anyone ever restores.
        //
        // RESTORED TWICE ON PURPOSE. "No row afterwards" is satisfied by an
        // endpoint that does nothing at all — a single restore would pass with
        // the whole invalidation block deleted, which is the failure mode of
        // every assertion whose subject is an absence. The first restore
        // is the anchor: it proves the mechanism is live by watching it null a
        // verdict that was there. Only then does the absence after the second
        // restore mean what it claims.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("checking");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("baseline"));
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Anchor: a settled verdict, then a restore, then no verdict.
        await WaitForBootCheckAsync(ledger);
        await using (var seedCtx = Db.NewServiceDbContext())
        {
            var seeded = await seedCtx.LedgerConsistencyStates
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            seeded.LastCheckedAt = new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc);
            seeded.Healthy = true;
            seeded.FailingProjections = [];
            await seedCtx.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null)).StatusCode);

        await using (var afterFirst = Db.NewServiceDbContext())
        {
            var state = await afterFirst.LedgerConsistencyStates.AsNoTracking()
                .SingleAsync(r => r.LedgerId == ledger.LedgerId);
            Assert.Null(state.Healthy);
        }

        // Now the case under test: nothing recorded, and a restore on top of it.
        await using (var cleanup = Db.NewServiceDbContext())
        {
            await cleanup.LedgerConsistencyStates
                .Where(r => r.LedgerId == ledger.LedgerId)
                .ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null)).StatusCode);

        await using var svc = Db.NewServiceDbContext();
        Assert.False(await svc.LedgerConsistencyStates.AsNoTracking()
            .AnyAsync(r => r.LedgerId == ledger.LedgerId));
    }

    [Fact]
    public async Task Snapshot_payload_is_captured_server_side_in_chunked_parts()
    {
        // mig 179 captured the in-scope graph into content_json (jsonb) so it never
        // entered managed memory. That removes the API-side peak but not the
        // Postgres-side one: the whole document is still built as one value in
        // backend anon memory, far larger than the finished artifact and growing
        // with the ledger, until a large enough ledger draws the kernel OOM killer.
        //
        // mig 193 (v3): rows are captured per table in chunks into
        // ledger_snapshot_parts, so peak memory tracks chunk size rather than ledger
        // size. content_json is left NULL and the legacy gzip `content` stays empty —
        // presence of parts rows is the v3 gate.
        var ledger = await SyntheticLedger.CreateAsync(Db);
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
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("baseline"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        await using var db = Db.NewDbContext();   // service role: sees past RLS
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
        // Since migration 193 capture produces v3 (chunked parts), so every other
        // test in this file exercises the v3 path and none exercises v2 — although
        // 193 also modified the v2 restore function, extracting its delete block
        // into fn_ledger_snapshot_clear so both paths share one FK ordering.
        //
        // That matters because an install upgrading to 193 keeps its existing v2
        // snapshots (nothing is rewritten in place), and those restore through the
        // refactored function. A pre-upgrade snapshot is the one an operator
        // restores to undo an upgrade, so its path needs its own test.
        //
        // So: capture normally (v3), convert the row to a genuine v2 snapshot, and
        // restore it through the real endpoint. fn_ledger_snapshot_parts_payload is
        // the reassembly helper 193 added for diagnostics; here it manufactures the
        // single content_json document v2 stored, byte-order-identical to what mig
        // 179's capture would have written.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -25m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "lunch");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -12m,
            new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc), payee: "coffee");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("v2-shaped"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Rewrite it into the v2 format: payload in content_json, no parts rows.
        await using (var svc = Db.NewDbContext())
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
        await using (var db = Db.NewDbContext())
        {
            Assert.Equal(3, await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.LedgerId == ledger.LedgerId));
        }

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var db = Db.NewDbContext())
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
        await using var db = Db.NewDbContext();
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
        // If realized_gains were neither captured nor rebuilt, the realized-gains
        // report would still reflect the discarded post-snapshot sales after a
        // restore even though the transactions themselves rolled back. Since mig
        // 188 the payload captures realized_gains and the restore writes it back
        // directly, rather than recomputing the whole ledger's derived investment
        // state, whose only unique output is this table.
        //
        // The guarantee asserted here is mechanism-independent: after a restore,
        // realized gains reflect the SNAPSHOT, not later sales.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var broker = await ledger.AddInvestmentAccountAsync("Brokerage");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task PostInv(object req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // Buy a lot (2020), sell half of it at twice the price (2021) → one realized
        // gain, equal to the sold half's original cost.
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc) });
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "sell", SecurityId = security, Shares = -5m, Price = 200m, PostedAt = new DateTime(2021, 1, 1, 12, 0, 0, DateTimeKind.Utc) });

        var atSnapshot = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(500m, atSnapshot.TotalRealizedGain);

        // Snapshot the one-sale realized state.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("one-sale"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Sell the remaining half at three times the price (2022) → a SECOND, larger
        // gain (the realized total triples).
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = broker.Id, Action = "sell", SecurityId = security, Shares = -5m, Price = 300m, PostedAt = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc) });
        var afterSecondSale = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(1500m, afterSecondSale.TotalRealizedGain);

        // Restore the snapshot — the second sale is discarded.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        // realized_gains must reflect ONLY the snapshot state (the first sale's gain)
        // — not the discarded second sale. Pre-mig-181 this read the two-sale total
        // (stale).
        var afterRestore = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(500m, afterRestore.TotalRealizedGain);
        Assert.Single(afterRestore.Rows);

        // And holdings are back to the unsold half at its original per-share cost.
        await using var db = Db.NewDbContext();
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == broker.HoldingsAccountId!.Value && h.SecurityId == security);
        Assert.Equal(5m, holding.Quantity);
        Assert.Equal(500m, holding.CostBasis);
    }

    [Fact]
    public async Task Restore_round_trips_reconciliation_status()
    {
        // Mig 181. txn_leg_recon (ADR-0082 per-leg reconciliation) has ON DELETE
        // CASCADE off txn_legs, so restore's `DELETE FROM txn_legs` wipes it, and
        // only the payload can bring it back. Recon is user judgment (not
        // derivable), so it must round-trip through the snapshot.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -40m,
            new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc), payee: "market");

        // Mark the bank leg reconciled (cleared).
        Guid legId;
        await using (var db = Db.NewDbContext())
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

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Snapshot the reconciled state.
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("reconciled"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Un-reconcile (simulate a later change to be rolled back).
        await using (var db = Db.NewDbContext())
        {
            await db.TxnLegRecon.Where(r => r.LegId == legId).ExecuteDeleteAsync();
        }

        // Restore — the reconciliation mark must come back.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using (var db = Db.NewDbContext())
        {
            var recon = await db.TxnLegRecon.AsNoTracking()
                .SingleOrDefaultAsync(r => r.LegId == legId && r.LedgerId == ledger.LedgerId);
            Assert.NotNull(recon);                       // null if cascade-wiped and not captured
            Assert.Equal("cleared", recon!.Status);
        }
    }

    [Fact]
    public async Task Restore_round_trips_a_ledger_with_recurring_linked_transactions()
    {
        // Mig 183. A committed occurrence header carries recurring_transaction_id.
        // fn_ledger_snapshot_restore deletes recurring_transactions while that
        // header still exists; a composite FK (recurring_transaction_id,
        // ledger_id) -> recurring_transactions declared ON DELETE SET NULL with NO
        // column list would null BOTH columns — including the NOT NULL ledger_id —
        // throwing 23502 and failing the whole restore. mig 183 makes it SET NULL
        // (recurring_transaction_id) only. A round-trip fixture without a
        // recurring-linked header cannot reach this, hence this test.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            // Both owned by the bank, the source of their one posting (mig 263), so the
            // round trip below also carries the originating account.
            var template = TxnHeaderRow.NewTemplate(templateId, ledger.LedgerId, bank.Id);
            template.Payee = "Rent";
            template.PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            template.TransactedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.TxnHeaders.Add(template);
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "manual",
                SourceAccountId = bank.Id,
            });
            // The committed occurrence — a real transaction stamped with the series id.
            var occurrence = TxnHeaderRow.NewOccurrence(occurrenceId, ledger.LedgerId, bank.Id);
            occurrence.Payee = "Rent";
            occurrence.PostedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            occurrence.TransactedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            occurrence.RecurringTransactionId = seriesId;
            db.TxnHeaders.Add(occurrence);
            foreach (var headerId in new[] { templateId, occurrenceId })
            {
                db.TxnLegs.AddRange(
                    new TxnLegRow
                    {
                        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId,
                        AccountId = bank.Id, PostingIndex = 0, Amount = -700m,
                    },
                    new TxnLegRow
                    {
                        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId,
                        AccountId = category.Id, PostingIndex = 0, Amount = 700m,
                    });
            }
            await db.SaveChangesAsync();
        }
        // Written past every writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeAllNotesAsync();

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var db2 = Db.NewDbContext();
        var occ = await db2.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == occurrenceId && h.LedgerId == ledger.LedgerId);
        Assert.Equal(seriesId, occ.RecurringTransactionId);
        Assert.True(await db2.RecurringTransactions.AsNoTracking()
            .AnyAsync(r => r.Id == seriesId && r.LedgerId == ledger.LedgerId));
        // And the stored owner of both (mig 263): the restore re-inserts accounts
        // before headers, so the reference lands.
        Assert.Equal(bank.Id, occ.OriginatingAccountId);
        Assert.Equal(bank.Id, (await db2.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == templateId)).OriginatingAccountId);
    }

    [Fact]
    public async Task Concurrent_creates_never_exceed_the_cap()
    {
        // The cap was checked outside the capture transaction, so two creates for
        // one ledger could both see four and both insert — six snapshots, the oldest
        // hidden by the list's Take(5) and never evicted. Creates for a ledger now
        // take a per-ledger lock before the check (migration 247).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        for (var i = 0; i < 40; i++)
            await ledger.AddTransactionPairAsync(
                bank.Id, dining.Id, -1m - i, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i));

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/snapshots",
                new CreateSnapshotRequest($"concurrent {i}"))));

        Assert.Equal(LedgerSnapshotsRepository.SnapshotCap,
            responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        await using var db = Db.NewDbContext();
        Assert.Equal(LedgerSnapshotsRepository.SnapshotCap,
            await db.LedgerSnapshots.CountAsync(s => s.LedgerId == ledger.LedgerId));
    }

    [Fact]
    public async Task Capture_and_restore_refuse_to_run_below_repeatable_read()
    {
        // At READ COMMITTED each statement of a capture sees its own moment, so a
        // write landing between the headers and the legs gives a snapshot holding
        // legs without their header — unrestorable, and silent until needed. The
        // functions refuse rather than trust every caller to remember (mig 247).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        var snapshotId = Guid.NewGuid();

        var capture = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT ledger_snapshot_write({snapshotId}, {ledger.LedgerId})"));
        Assert.Contains("REPEATABLE READ", capture.MessageText);

        var restore = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_ledger_snapshot_restore_parts({ledger.LedgerId}, {snapshotId})"));
        Assert.Contains("REPEATABLE READ", restore.MessageText);
    }

    [Fact]
    public async Task Restore_verification_names_the_tables_that_differ()
    {
        // The check that ends every restore: the ledger must hold exactly the rows
        // the snapshot does, per table, or the restore rolls back. Driven directly,
        // because a restore that reaches it with a difference is a bug this exists
        // to catch, not a state a test can produce honestly.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBankAccountAsync("checking");
        await using var db = Db.NewDbContext();

        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_ledger_snapshot_verify_restored({ledger.LedgerId}, '{{\"accounts\": 7}}'::jsonb)"));
        Assert.Contains("accounts: snapshot holds 7, ledger now has 1", ex.MessageText);
    }

    [Fact]
    public async Task Restore_unlinks_accounts_whose_bank_connection_was_deleted_after_the_snapshot()
    {
        // Feed connections survive a rollback, so they are not in the snapshot —
        // but the accounts that point at them are. Take a snapshot, delete the
        // connection (its accounts' link is SET NULL), restore: the restored
        // account still names the deleted connection. Before mig 247 that was an
        // FK violation and the restore failed; the snapshot was unrestorable for
        // as long as it existed. The restore now does what deleting the connection
        // did to the live row — unlinks it — and keeps everything else.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.FeedConnections.Add(new FeedConnectionRow
            {
                Id = connectionId, LedgerId = ledger.LedgerId,
                Provider = "simplefin", Status = "active",
            });
            db.Accounts.Add(new AccountRow
            {
                Id = accountId, LedgerId = ledger.LedgerId, Name = "linked checking",
                AccountType = "bank", CurrencyCode = "USD", OpeningBalance = 0m,
                IsActive = true, FeedConnectionId = connectionId,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("before disconnect"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        var deleteResp = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connectionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        await using var db2 = Db.NewDbContext();
        var restored = await db2.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        Assert.Null(restored.FeedConnectionId);
        Assert.Equal("linked checking", restored.Name);
    }

    // -----------------------------------------------------------------
    // Runs pruned by audit retention after the snapshot was taken
    // -----------------------------------------------------------------

    // A real file import: two buys into an investment account. Synthesised.
    private const string TwoBuysQif = """
        !Type:Invst
        D01/05/2024
        NBuy
        YGROWTH FUND(AAAA)
        I100.00000
        Q5.000
        U500.00
        T500.00
        ^
        D01/06/2024
        NBuy
        YGROWTH FUND(AAAA)
        I110.00000
        Q2.000
        U220.00
        T220.00
        ^
        """;

    private const int RetentionDays = 180;

    /// <summary>
    /// One pass of the real retention prune, with the clock standing exactly one
    /// retention window after <paramref name="cutoffUtc"/>: every run that started
    /// before it is deleted, and a run that started at it is kept (the prune's
    /// comparison is strict). The event knob is off so only the run log is in play.
    /// </summary>
    private async Task PruneRunsStartedBeforeAsync(DateTime cutoffUtc)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new Coffer.Api.Configuration.ApiOptions
        {
            ServiceConnectionString = Db.ServiceConnectionString,
            AuditRetentionDays = RetentionDays,
            EventRetentionDays = 0,
        });
        var clock = new Coffer.Api.Tests.Support.TestClock(
            new DateTimeOffset(DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc)).AddDays(RetentionDays));
        await new Coffer.Api.Audit.AuditRetentionService(
            PostgresFixture.ServiceFactoryFor(opts), opts,
            NullLogger<Coffer.Api.Audit.AuditRetentionService>.Instance, clock).PruneAsync(default);
    }

    /// <summary>The prune as if the window had passed for every run written so far.</summary>
    private Task PruneEveryRunAsync() => PruneRunsStartedBeforeAsync(DateTime.UtcNow.AddDays(1));

    private async Task<DateTime> RunStartedAtAsync(Guid runId)
    {
        await using var db = Db.NewDbContext();
        return (await db.LedgerOperations.AsNoTracking().SingleAsync(r => r.Id == runId)).StartedAt;
    }

    private async Task<bool> RunExistsAsync(Guid runId)
    {
        await using var db = Db.NewDbContext();
        return await db.LedgerOperations.AsNoTracking().AnyAsync(r => r.Id == runId);
    }

    private static async Task AssertConsistencyHealthyAsync(HttpClient client, Guid ledgerId)
    {
        var check = await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", content: null);
        var body = await check.Content.ReadAsStringAsync();
        Assert.True(check.StatusCode == HttpStatusCode.OK, $"{(int)check.StatusCode}: {body}");
        Assert.True((await check.Content.ReadFromJsonAsync<LedgerConsistencyReport>())!.Healthy, body);
    }

    [Fact]
    public async Task Restore_unstamps_only_the_imported_rows_whose_import_run_was_pruned_after_the_snapshot()
    {
        // An import stamps every row it writes with its run (mig 221), and audit
        // retention deletes runs past its window, unstamping the live rows (SET NULL).
        // A snapshot taken before the prune still holds the stamp. The restore does
        // what the prune did to the live row — clears the stamp — and keeps the row.
        // A run the prune kept keeps its stamps, so its import can still be undone.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<(Guid Run, List<Guid> Headers)> ImportAsync(string qif)
        {
            var upload = new MultipartFormDataContent();
            var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(qif));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            upload.Add(file, "file", "export.qif");
            upload.Add(new StringContent(brokerage.Id.ToString()), "accountId");
            upload.Add(new StringContent("qif"), "providerAccountId");
            var resp = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/qif/import", upload);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var run = (await resp.Content.ReadFromJsonAsync<FileIngestImportResponse>())!.SyncRunId;
            await using var db = Db.NewDbContext();
            var headers = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId && h.LedgerOperationId == run)
                .Select(h => h.Id).ToListAsync();
            Assert.Equal(2, headers.Count);
            return (run, headers);
        }

        var older = await ImportAsync(TwoBuysQif);
        var newer = await ImportAsync(TwoBuysQif.Replace("2024", "2025").Replace("500.00", "510.00"));

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("after imports"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        await PruneRunsStartedBeforeAsync(await RunStartedAtAsync(newer.Run));
        Assert.False(await RunExistsAsync(older.Run), "the retention prune did not delete the older import's run");
        Assert.True(await RunExistsAsync(newer.Run), "the retention prune deleted the newer import's run");

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.True(restoreResp.StatusCode == HttpStatusCode.NoContent,
            $"{(int)restoreResp.StatusCode}: {await restoreResp.Content.ReadAsStringAsync()}");

        await using (var after = Db.NewDbContext())
        {
            var stamps = await after.TxnHeaders.AsNoTracking()
                .Where(h => older.Headers.Contains(h.Id) || newer.Headers.Contains(h.Id))
                .ToDictionaryAsync(h => h.Id, h => h.LedgerOperationId);
            Assert.Equal(4, stamps.Count);
            Assert.All(older.Headers, id => Assert.Null(stamps[id]));
            Assert.All(newer.Headers, id => Assert.Equal(newer.Run, stamps[id]));
        }

        await AssertConsistencyHealthyAsync(client, ledger.LedgerId);

        var undo = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ledger-operations/{newer.Run}/undo-import", content: null);
        var undoBody = await undo.Content.ReadAsStringAsync();
        Assert.True(undo.StatusCode == HttpStatusCode.OK, $"{(int)undo.StatusCode}: {undoBody}");
        var undone = (await undo.Content.ReadFromJsonAsync<UndoImportResult>())!;
        Assert.Equal(2, undone.Found);
        Assert.Equal(2, undone.Deleted);

        await using var final = Db.NewDbContext();
        var left = await final.TxnHeaders.AsNoTracking()
            .Where(h => older.Headers.Contains(h.Id) || newer.Headers.Contains(h.Id))
            .Select(h => h.Id).ToListAsync();
        Assert.Equal(older.Headers.Order(), left.Order());
    }

    [Fact]
    public async Task Restore_drops_only_the_promotions_whose_sync_run_was_pruned_after_the_snapshot()
    {
        // A promotion (pending -> cleared) belongs to the sync run that saw it clear,
        // and is deleted with that run (CASCADE) when audit retention prunes it. A
        // snapshot taken before the prune still holds the promotion. The restore does
        // what the prune did to the live row — drops it — and keeps the transaction.
        // A promotion whose run the prune kept is restored as it was.
        //
        // The rows' own stamps are pruned BEFORE the snapshot (the first prune), so
        // the snapshot holds unstamped headers and the promotions are the only
        // references to runs: this fact is about the promotion's FKs alone.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        // Two holds, each clearing at a different amount in a later sync of its own.
        var firstAmount = "-42.50";
        var firstPending = true;
        var secondAmount = "-18.00";
        var secondPending = true;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<Coffer.Api.Sync.SimpleFin.SimpleFinClient>(_ => StubSimpleFin(() => $$"""
                {"connections":[
                  {"conn_id":"c-P","name":"Bank P","org_id":"bankp","sfin_url":"https://sfin/bankp"}
                ],"errlist":[],"accounts":[
                  {"id":"sf-p","conn_id":"c-P","name":"Checking","currency":"USD","balance":"100.00","transactions":[
                    {"id":"pending-1","posted":1715000000,"amount":"{{firstAmount}}","payee":"Corner Cafe","description":"CORNER CAFE 0042","pending":{{(firstPending ? "true" : "false")}}},
                    {"id":"pending-2","posted":1715100000,"amount":"{{secondAmount}}","payee":"Fuel Stop","description":"FUEL STOP 0007","pending":{{(secondPending ? "true" : "false")}}}
                  ]}
                ]}
                """));
        using var client = await AuthedClientAsync(factory, ledger);

        var claimUrl = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://x/c"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var connection = (await (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = claimUrl }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking");
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-p",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        async Task SyncAsync()
        {
            var resp = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // The feed row's external_id carries the provider's id as its suffix.
        async Task<LedgerOperationPromotionRow> PromotionOfAsync(string providerId)
        {
            await using var db = Db.NewDbContext();
            var header = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId
                    && h.ExternalId != null && h.ExternalId.EndsWith(providerId));
            Assert.Null(header.LedgerOperationId);
            return await db.LedgerOperationPromotions.AsNoTracking().SingleAsync(p => p.HeaderId == header.Id);
        }

        await SyncAsync();
        await PruneEveryRunAsync();

        firstPending = false;
        firstAmount = "-45.00";
        await SyncAsync();
        var older = await PromotionOfAsync("pending-1");

        secondPending = false;
        secondAmount = "-20.00";
        await SyncAsync();
        var newer = await PromotionOfAsync("pending-2");
        Assert.NotEqual(older.LedgerOperationId, newer.LedgerOperationId);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("after promotions"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        await PruneRunsStartedBeforeAsync(await RunStartedAtAsync(newer.LedgerOperationId));
        Assert.False(await RunExistsAsync(older.LedgerOperationId),
            "the retention prune did not delete the older promoting run");
        Assert.True(await RunExistsAsync(newer.LedgerOperationId),
            "the retention prune deleted the newer promoting run");

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.True(restoreResp.StatusCode == HttpStatusCode.NoContent,
            $"{(int)restoreResp.StatusCode}: {await restoreResp.Content.ReadAsStringAsync()}");

        await using (var after = Db.NewDbContext())
        {
            var kept = await after.LedgerOperationPromotions.AsNoTracking()
                .SingleAsync(p => p.LedgerId == ledger.LedgerId);
            Assert.Equal(
                (newer.Id, newer.LedgerOperationId, newer.HeaderId, newer.WasAmount, newer.BecameAmount, newer.PromotedAt),
                (kept.Id, kept.LedgerOperationId, kept.HeaderId, kept.WasAmount, kept.BecameAmount, kept.PromotedAt));

            foreach (var (headerId, cleared) in new[] { (older.HeaderId, -45.00m), (newer.HeaderId, -20.00m) })
            {
                var header = await after.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId);
                Assert.False(header.IsPending);
                var bankLeg = await after.TxnLegs.AsNoTracking()
                    .SingleAsync(l => l.HeaderId == headerId && l.AccountId == bank.Id);
                Assert.Equal(cleared, bankLeg.Amount);
            }
        }

        await AssertConsistencyHealthyAsync(client, ledger.LedgerId);
    }

    private static Coffer.Api.Sync.SimpleFin.SimpleFinClient StubSimpleFin(Func<string> body)
    {
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        return new Coffer.Api.Sync.SimpleFin.SimpleFinClient(new HttpClient(new StubHandler(req =>
            req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) })), TimeProvider.System);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(impl(request));
    }

    // -----------------------------------------------------------------
    // Eviction rule (5-cap, auto-evicts-auto-first, manual-at-cap-refuses)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Manual_snapshot_at_cap_returns_422_snapshot_manual_at_cap()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var db = Db.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task Auto_snapshot_at_cap_evicts_oldest_auto_then_inserts()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

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

        await using var db = Db.NewDbContext();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using var db = Db.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task List_returns_at_most_cap_snapshots_newest_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        await using (var db = Db.NewDbContext())
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

    [Fact]
    public async Task List_says_which_snapshots_are_restorable_and_agrees_with_the_refusal()
    {
        // The panel hides Restore on !Restorable. That flag and the endpoint's
        // refusal are two readings of one rule, so the thing worth testing is
        // that they AGREE — a flag that said "restorable" on a row the server
        // rejects would be the defect this closes, just moved.
        //
        // Both snapshots are created through the API, then one is forged stale
        // the same way the refusal test above does it.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var freshResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("fresh"));
        Assert.Equal(HttpStatusCode.OK, freshResp.StatusCode);
        var fresh = (await freshResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        var staleResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("stale"));
        Assert.Equal(HttpStatusCode.OK, staleResp.StatusCode);
        var stale = (await staleResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // A snapshot the API just made is on the live schema by construction —
        // assert it BEFORE forging anything, so "restorable" is not a value that
        // merely happens to be true.
        Assert.True(fresh.Restorable);
        Assert.True(stale.Restorable);

        await using (var db = Db.NewDbContext())
        {
            await db.LedgerSnapshots
                .Where(s => s.Id == stale.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.SchemaVersion, "001_some_ancient_mig.sql"));
        }

        var listResp = await client.GetAsync($"/api/ledgers/{ledger.LedgerId}/snapshots");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = (await listResp.Content.ReadFromJsonAsync<List<SnapshotSummaryDto>>())!;

        Assert.True(list.Single(s => s.Id == fresh.Id).Restorable);
        Assert.False(list.Single(s => s.Id == stale.Id).Restorable);

        // ...and the flag matches what actually happens: the one marked
        // not-restorable is refused, the one marked restorable is not.
        var refused = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{stale.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        // 204, not 200 — a successful restore returns NoContent.
        var accepted = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{fresh.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    // -----------------------------------------------------------------
    // Cross-ledger probe / not-found / delete
    // -----------------------------------------------------------------

    [Fact]
    public async Task Restore_returns_snapshot_not_found_when_snapshot_belongs_to_another_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
    public async Task A_snapshots_size_is_what_is_stored_and_the_create_response_and_the_list_agree()
    {
        // Enough repetitive rows for Postgres to compress the chunks it stores: a chunk under
        // the TOAST threshold is kept as it is, and says nothing about compression.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        for (var i = 0; i < 300; i++)
        {
            await ledger.AddTransactionPairAsync(
                bank.Id, dining.Id, -(10m + i % 7),
                new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i % 200),
                payee: $"the corner cafe, order number {i}, with the usual extra shot");
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("sized"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        var listed = (await client.GetFromJsonAsync<List<SnapshotSummaryDto>>(
            $"/api/ledgers/{ledger.LedgerId}/snapshots"))!;
        var fromList = Assert.Single(listed, s => s.Id == created.Id);

        await using var db = Db.NewDbContext();
        var capturedTextLength = await db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.Id == created.Id)
            .Select(s => s.ContentSizeUncompressed)
            .SingleAsync();
        var storedInDb = await db.Database.SqlQueryRaw<long>(
            """
            SELECT COALESCE(sum(pg_column_size(content)), 0)::bigint AS "Value"
              FROM ledger_snapshot_parts WHERE snapshot_id = {0}
            """, created.Id).SingleAsync();

        Assert.True(created.ContentSizeStored > 0);
        Assert.Equal(created.ContentSizeStored, fromList.ContentSizeStored);
        // The figure is the database's own measure of what it holds, not a second computation of it.
        Assert.Equal(storedInDb, created.ContentSizeStored);
        // And it is the compressed figure: well under the length of the JSON text captured.
        Assert.True(created.ContentSizeStored < capturedTextLength,
            $"stored {created.ContentSizeStored} B should be under the {capturedTextLength} B of JSON text");
    }

    [Fact]
    public async Task Stored_size_covers_the_two_older_snapshot_formats()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        // v1 (gzip bytes) and v2 (a jsonb value) predate the chunk table, so nothing new creates
        // them; they are seeded directly, as the RLS tests seed theirs.
        async Task<Guid> SeedAsync(byte[] content, string? contentJson)
        {
            await using var svc = Db.NewDbContext();
            var snap = new LedgerSnapshotRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = ledger.UserId,
                Kind = "manual",
                SchemaVersion = "111_ledger_snapshots.sql",
                Content = content,
                ContentJson = contentJson,
                ContentSizeUncompressed = 0,
            };
            svc.LedgerSnapshots.Add(snap);
            await svc.SaveChangesAsync();
            return snap.Id;
        }

        var v1 = await SeedAsync(new byte[1234], contentJson: null);
        var v2 = await SeedAsync([], contentJson: """{"accounts":[{"id":1},{"id":2}]}""");

        var sizes = await AutoSnapshotRepo().StoredSizesAsync(ledger.LedgerId);

        Assert.Equal(1234, sizes[v1]);            // the gzip bytes themselves
        Assert.True(sizes[v2] > 0);               // the jsonb value as stored
    }

    [Fact]
    public async Task Delete_removes_the_snapshot_and_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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

        await using var db = Db.NewDbContext();
        var count = await db.LedgerSnapshots.AsNoTracking()
            .CountAsync(s => s.LedgerId == ledger.LedgerId);
        Assert.Equal(0, count);
    }
}
