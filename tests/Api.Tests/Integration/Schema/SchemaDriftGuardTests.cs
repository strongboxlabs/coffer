using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// Guards against DB-structure drift silently breaking the two invariants that
/// drifted before: snapshot completeness (ADR-0037) and investment-money
/// precision (ADR-0043/0064). These fail CLOSED — when a migration adds a
/// ledger-scoped table or a share/price column, the relevant guard goes red
/// until the author makes a conscious decision, instead of the omission
/// surfacing months later as lost data (txn_leg_recon) or a rounding
/// discrepancy (lots.unit_cost).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SchemaDriftGuardTests
{
    private readonly PostgresFixture _fixture;

    public SchemaDriftGuardTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    // Derived: NOT captured; the restore rebuilds it (fn_recompute_balances_for_ledger).
    // realized_gains sat here until it was found to be captured since mig 188.
    private static readonly HashSet<string> DerivedRebuiltOnRestore = new()
    {
        "txn_header_account_balances",
    };

    // Ledger-scoped tables intentionally NOT snapshotted, each with its reason.
    // "Excluded" is a promise that a restore leaves the table alone, and
    // No_table_outside_the_snapshot_references_a_row_the_restore_replaces holds
    // every entry here to it.
    private static readonly HashSet<string> IntentionallyExcluded = new()
    {
        "ledger_snapshots",          // self-referential: a snapshot cannot contain itself
        "ledger_snapshot_parts",     //   "     " — the chunks of one (mig 193)
        "mcp_tool_invocations",      // MCP write audit log (ADR-0081/0086) — not ledger data
        "scheduled_jobs",            // scheduler infra (snapshot/backup cadence) — not ledger data
        "feed_connections",          // bank/SimpleFin links (ADR-0006): survive a data rollback
        "feed_connection_accounts",  //   "     "
        "ledger_operations",             // feed-import run history/audit
        "ledger_operation_errors",       //   "     "
        // ledger_operation_promotions sat here as "feed-import provenance" while
        // its CASCADE from txn_headers emptied it on every restore. Captured since
        // mig 247 — a promotion describes a transaction, so it rolls back with one.
        "invites",                   // access (ADR-0083): not wiped by a data rollback
        "user_ledger_grants",        // access (ADR-0083): who can see the ledger
        "user_preferences",          // per-user UI settings
        // Notification history (ADR-0096). Same class as ledger_operations and
        // mcp_tool_invocations: a record of what was ANNOUNCED, not ledger data.
        // Restoring it would resurrect stale notices ("drift found" that has since
        // been repaired) and erase newer ones — a rollback must not rewrite what you
        // were already told.
        "ledger_events",
        // Delivery configuration (ADR-0096), excluded for the same reason as
        // feed_connections: it must survive a data rollback. There is also a security
        // edge — capturing it would let restoring an old snapshot re-enable a target
        // the user deliberately removed, and re-arm a webhook URL they had revoked.
        "ledger_notification_subscribers",
        // Import FORMAT descriptions (ADR-0031 Phase 5, mig 222) — the same class as
        // feed_connections and the line above: configuration, not ledger data, and it
        // must survive a data rollback. Rolling a ledger back to last month must not
        // delete a mapping authored last week, and restoring an old snapshot must not
        // resurrect one that was deliberately deleted.
        //
        // The transactions a mapping produced ARE snapshotted — they are ordinary rows
        // in txn_headers. A format description and the money read through it have
        // separate lifetimes, which is also why deleting a mapping leaves its imports
        // alone.
        "feed_csv_mappings",
        // The latest consistency VERDICT (mig 242) — a statement about the ledger's
        // data, not part of it. Capturing it would be capturing the answer along
        // with the question: restoring a snapshot replaces the very rows the verdict
        // was about, so a captured "healthy" would be reinstated as a claim about
        // data it was never checked against. The restore endpoint nulls the verdict
        // instead, which says the true thing — nobody has checked THIS.
        //
        // last_repaired_at is excluded for the ledger_events reason on top of that:
        // it is history, and a rollback must not rewrite what already happened.
        "ledger_consistency_state",
    };

    // References from a captured row OUT of the snapshot, to a table the restore
    // does not bring back — whose row may have been deleted since the snapshot was
    // taken. Every one must be in exactly one of the two sets below.
    //
    // Reconciled: the restore applies the FK's own ON DELETE action to a restored
    // row whose parent is gone (fn_ledger_snapshot_reconcile_external_refs, mig
    // 247), so it must be deferrable to let the row land before the fix-up runs.
    // Each entry needs a test in SnapshotsTests that deletes the parent and
    // restores — this list proves the classification, not the behaviour.
    private static readonly HashSet<string> OutwardReconciledOnRestore = new()
    {
        // Deleting a bank connection unlinks its accounts (SET NULL).
        "accounts_feed_connection_id_ledger_fkey",
    };

    // No app path deletes the parent, so a restored reference cannot dangle. That
    // is a claim about the API, not the schema: when one of these parents gains a
    // delete, move its FKs to the reconciled set and extend the fix-up.
    private static readonly HashSet<string> OutwardParentNeverDeleted = new()
    {
        // ledger_operations go only with their ledger (fn_ledger_delete). A purge
        // of old runs would make every snapshot holding one of their promotions
        // unrestorable — reconcile these (CASCADE: drop the row) before adding one.
        "txn_headers_ledger_operation_id_fkey",
        "ledger_operation_promotions_ledger_operation_id_fkey",
        "ledger_operation_promotions_run_ledger_fkey",
        // users: no endpoint deletes a user.
        "user_account_groups_user_id_fkey",
        "fk_provider_security_mappings_user",
        "fk_recurring_occurrence_exceptions_user",
        "txn_leg_recon_cleared_by_user_id_fkey",
        "saved_reports_owner_user_id_fkey",
        "report_pins_user_id_fkey",
    };

    private static readonly Regex SafeIdent = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    // Ledger-scoped = carries a ledger_id, OR references (transitively) a table
    // that does. The second half is where security_components hid until mig 247:
    // no ledger_id of its own and a CASCADE from securities, so every restore
    // emptied it while a guard that only looked for the column never saw it.
    private static async Task<HashSet<string>> LedgerScopedTablesAsync(DbConnection conn)
    {
        var set = new HashSet<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH RECURSIVE scoped(oid) AS (
                SELECT c.oid
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE c.relkind = 'r' AND n.nspname = 'public'
                  AND a.attname = 'ledger_id' AND a.attnum > 0 AND NOT a.attisdropped
                UNION
                SELECT f.conrelid
                FROM pg_constraint f
                JOIN scoped s ON f.confrelid = s.oid
                WHERE f.contype = 'f'
            )
            SELECT c.relname FROM scoped s JOIN pg_class c ON c.oid = s.oid;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    // The tables a snapshot actually captures: v3 capture walks this list.
    private static async Task<List<string>> TableListAsync(DbConnection conn, string function)
    {
        var list = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT t FROM unnest({function}()) WITH ORDINALITY AS u(t, ord) ORDER BY ord";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<HashSet<string>> CapturedTablesAsync(DbConnection conn)
        => (await TableListAsync(conn, "fn_ledger_snapshot_part_names")).ToHashSet();

    private static async Task<HashSet<string>> PayloadCapturedTablesAsync(DbConnection conn, Guid ledgerId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fn_ledger_snapshot_payload(@l)::text";
        var p = cmd.CreateParameter();
        p.ParameterName = "l";
        p.Value = ledgerId;
        cmd.Parameters.Add(p);
        var json = (string)(await cmd.ExecuteScalarAsync())!;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(o => o.Name).ToHashSet();
    }

    private sealed record ForeignKey(string Name, string Child, string Parent, bool Deferrable);

    private static async Task<List<ForeignKey>> ForeignKeysAsync(DbConnection conn)
    {
        var list = new List<ForeignKey>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.conname, cl.relname, pl.relname, c.condeferrable
            FROM pg_constraint c
            JOIN pg_class cl ON cl.oid = c.conrelid
            JOIN pg_class pl ON pl.oid = c.confrelid
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            WHERE c.contype = 'f' AND n.nspname = 'public';";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ForeignKey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        return list;
    }

    // Row count plus a digest of every row's full content, so a round-trip that
    // keeps the count but loses a column — or nulls a reference — still fails.
    private static async Task<(long Rows, string Digest)> FingerprintAsync(
        DbConnection conn, string table, Guid ledgerId)
    {
        if (!SafeIdent.IsMatch(table)) throw new ArgumentException($"unsafe table ident: {table}");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT count(*),
                   md5(COALESCE(string_agg(to_jsonb(t)::text, E'\n' ORDER BY to_jsonb(t)::text), ''))
            FROM {table} t WHERE t.ledger_id = @l";
        var p = cmd.CreateParameter();
        p.ParameterName = "l";
        p.Value = ledgerId;
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetString(1));
    }

    // -----------------------------------------------------------------
    // Guard 1 — completeness: every ledger-scoped table is classified.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Snapshot_payload_classifies_every_ledger_scoped_table()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var ledgerScoped = await LedgerScopedTablesAsync(conn);
        var captured = await CapturedTablesAsync(conn);

        // v3 capture walks fn_ledger_snapshot_part_names(); the v2 restore still
        // reads documents shaped by fn_ledger_snapshot_payload. A table in one list
        // and not the other is captured by one format and lost by the other.
        var payloadKeys = await PayloadCapturedTablesAsync(conn, ledger.LedgerId);
        Assert.True(payloadKeys.SetEquals(captured),
            "fn_ledger_snapshot_payload and fn_ledger_snapshot_part_names disagree. Only in the payload: "
            + string.Join(", ", payloadKeys.Except(captured).Order())
            + "; only in the part names: " + string.Join(", ", captured.Except(payloadKeys).Order()));

        // The capture list must only claim real ledger-scoped tables.
        var phantom = captured.Except(ledgerScoped).OrderBy(x => x).ToList();
        Assert.True(phantom.Count == 0,
            $"fn_ledger_snapshot_part_names names table(s) that are not ledger-scoped: {string.Join(", ", phantom)}");

        // Allow-lists must not name tables that no longer exist.
        var allowLists = new[] { DerivedRebuiltOnRestore, IntentionallyExcluded };
        var staleAllow = allowLists.SelectMany(s => s).Except(ledgerScoped).OrderBy(x => x).ToList();
        Assert.True(staleAllow.Count == 0,
            $"This test's allow-lists name non-existent table(s): {string.Join(", ", staleAllow)} — remove them.");

        // One class per table: a captured table cannot also be "excluded".
        var overlap = allowLists.Prepend(captured)
            .SelectMany(s => s).GroupBy(t => t).Where(g => g.Count() > 1)
            .Select(g => g.Key).OrderBy(x => x).ToList();
        Assert.True(overlap.Count == 0,
            $"Table(s) classified twice (captured / derived / excluded): {string.Join(", ", overlap)}");

        // THE guard: no ledger-scoped table is left unclassified.
        var classified = allowLists.SelectMany(s => s).Concat(captured).ToHashSet();
        var unclassified = ledgerScoped.Except(classified).OrderBy(x => x).ToList();
        Assert.True(unclassified.Count == 0,
            $"Ledger-scoped table(s) not covered by a snapshot: {string.Join(", ", unclassified)}. " +
            "Add each to the snapshot functions (re-emit them in a migration, as 245 and 247 do), or to " +
            "this test's DerivedRebuiltOnRestore / IntentionallyExcluded allow-list (with a reason).");
    }

    // -----------------------------------------------------------------
    // Guard 1b — "excluded" has to be true: the restore cannot reach in.
    // -----------------------------------------------------------------

    [Fact]
    public async Task No_table_outside_the_snapshot_references_a_row_the_restore_replaces()
    {
        // A restore deletes every captured row and re-inserts it. A table outside
        // the snapshot that references one of those rows therefore loses its rows
        // (CASCADE), loses the link (SET NULL), or blocks the restore (RESTRICT /
        // NO ACTION) — whatever the allow-list says about it surviving a rollback.
        // ledger_operation_promotions was "excluded" while every restore cascaded
        // through it from txn_headers; mig 247 captures it.
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var captured = await CapturedTablesAsync(conn);
        var reachesIn = (await ForeignKeysAsync(conn))
            .Where(f => captured.Contains(f.Parent) && !captured.Contains(f.Child))
            .Where(f => !DerivedRebuiltOnRestore.Contains(f.Child))
            .Select(f => $"{f.Child}.{f.Name} -> {f.Parent}")
            .OrderBy(x => x).ToList();

        Assert.True(reachesIn.Count == 0,
            "Table(s) outside the snapshot reference rows a restore deletes and re-inserts: "
            + string.Join("; ", reachesIn)
            + ". Capture the table, or drop the reference: an excluded table cannot point into the "
            + "snapshot and still survive a restore.");
    }

    // -----------------------------------------------------------------
    // Guard 1c — the restore's insert order satisfies every reference.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Restore_insert_order_satisfies_every_reference_between_captured_tables()
    {
        // Each captured table is inserted after the ones it references — or the FK
        // is deferrable. A SELF-reference is never satisfied by table order: the v3
        // restore inserts a table in chunks keyset-ordered on id, so a row can
        // reference one in a later chunk of its own table. That failed a production
        // restore on txn_headers.is_merged_into (mig 247); no test saw it because a
        // test ledger fits in one chunk. Checked here against the catalog, so it
        // does not depend on a fixture happening to seed a crossing.
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var order = await TableListAsync(conn, "fn_ledger_snapshot_insert_order");
        var position = order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);

        var outOfOrder = (await ForeignKeysAsync(conn))
            .Where(f => position.ContainsKey(f.Child) && position.ContainsKey(f.Parent))
            .Where(f => !f.Deferrable && position[f.Parent] >= position[f.Child])
            .Select(f => f.Child == f.Parent
                ? $"{f.Name} ({f.Child} references itself)"
                : $"{f.Name} ({f.Child} is inserted before {f.Parent})")
            .OrderBy(x => x).ToList();

        Assert.True(outOfOrder.Count == 0,
            "The snapshot restore cannot satisfy these FKs in its insert order: "
            + string.Join("; ", outOfOrder)
            + ". Move the referenced table earlier in fn_ledger_snapshot_insert_order, or make the "
            + "FK DEFERRABLE INITIALLY DEFERRED (mig 124 and 247 did for the cases order cannot fix).");
    }

    // -----------------------------------------------------------------
    // Guard 1d — every reference out of the snapshot is accounted for.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Every_reference_out_of_the_snapshot_is_classified()
    {
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var captured = await CapturedTablesAsync(conn);
        // ledgers is the row the restore writes INTO; it always exists.
        var outward = (await ForeignKeysAsync(conn))
            .Where(f => captured.Contains(f.Child) && !captured.Contains(f.Parent) && f.Parent != "ledgers")
            .ToList();
        var names = outward.Select(f => f.Name).ToHashSet();

        var overlap = OutwardReconciledOnRestore.Intersect(OutwardParentNeverDeleted).Order().ToList();
        Assert.True(overlap.Count == 0, $"FK(s) in both outward lists: {string.Join(", ", overlap)}");

        var stale = OutwardReconciledOnRestore.Concat(OutwardParentNeverDeleted)
            .Except(names).Order().ToList();
        Assert.True(stale.Count == 0,
            $"This test's outward lists name FK(s) that are not references out of the snapshot: {string.Join(", ", stale)}");

        var unclassified = outward
            .Where(f => !OutwardReconciledOnRestore.Contains(f.Name) && !OutwardParentNeverDeleted.Contains(f.Name))
            .Select(f => $"{f.Child}.{f.Name} -> {f.Parent}")
            .Order().ToList();
        Assert.True(unclassified.Count == 0,
            "Captured row(s) reference a table the restore does not bring back: "
            + string.Join("; ", unclassified)
            + ". If an app path can delete that parent, a snapshot taken before the delete cannot "
            + "restore: handle it in fn_ledger_snapshot_reconcile_external_refs and list it in "
            + "OutwardReconciledOnRestore. Otherwise list it in OutwardParentNeverDeleted.");

        var notDeferrable = outward
            .Where(f => OutwardReconciledOnRestore.Contains(f.Name) && !f.Deferrable)
            .Select(f => f.Name).Order().ToList();
        Assert.True(notDeferrable.Count == 0,
            "Reconciled FK(s) must be deferrable so the restored row lands before the fix-up: "
            + string.Join(", ", notDeferrable));
    }

    // -----------------------------------------------------------------
    // Guard 2 — fidelity: restore round-trips every captured table.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Snapshot_restore_round_trips_every_captured_table()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        // EVERY captured table is seeded, and the sanity check below fails on any
        // that is not. A table with no rows passes a before/after comparison at
        // 0 == 0 — green on exactly the omission this test exists to catch. The
        // previous version checked a hand-kept list of seeded tables, and
        // saved_reports and report_pins (migs 244/245) shipped without ever being
        // added to it.
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -30m, Utc(2026, 1, 2), payee: "seed");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");

        // Investment activity through the real endpoint → securities, holdings,
        // lots, realized_gains populated by the recompute interceptor.
        var inv = await ledger.AddInvestmentAccountAsync("IRA");
        var security = await ledger.AddSecurityAsync("Fund", "FND");
        async Task PostInv(object req) => Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = inv.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1) });
        await PostInv(new CreateInvestmentTransactionRequest { BrokerageAccountId = inv.Id, Action = "sell", SecurityId = security, Shares = -4m, Price = 150m, PostedAt = Utc(2021, 1, 1) });
        await ledger.AddSecuritySplitAsync(security, 2m, Utc(2019, 6, 1));
        await ledger.AddSecurityPriceAsync(security, 120m, Utc(2026, 1, 1));
        await ledger.AddSecurityComponentAsync(security, "equity", 60m, region: "us");
        await ledger.AddSecurityComponentAsync(security, "fixed_income", 40m, region: "us");

        // Self-references that CROSS A CHUNK BOUNDARY. The restore below replays a
        // capture taken at one row per chunk, keyset-ordered on id, so a row whose
        // id sorts before the row it references lands in an earlier chunk — the
        // production failure of mig 247. Ids are chosen to force that for each
        // self-reference, rather than left to the 50/50 of random uuids. Postgres
        // orders uuids bytewise, which is the ordinal order of their text form.
        static (Guid Low, Guid High) OrderedPair()
        {
            var tail = Guid.NewGuid().ToString()[2..];
            return (Guid.Parse("00" + tail), Guid.Parse("ff" + tail));
        }
        var (childCategory, parentCategory) = OrderedPair();
        var (brokerageId, holdingsSiblingId) = OrderedPair();

        var (mergedLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -12m, Utc(2026, 1, 4), payee: "duplicate");
        var (winnerLeg, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -12m, Utc(2026, 1, 4), payee: "duplicate");
        var headerA = await ledger.ResolveHeaderIdAsync(mergedLeg);
        var headerB = await ledger.ResolveHeaderIdAsync(winnerLeg);
        var (loser, winner) = string.CompareOrdinal(headerA.ToString(), headerB.ToString()) < 0
            ? (headerA, headerB) : (headerB, headerA);
        await ledger.MarkTransactionMergedAsync(loser, winner);

        // A second member, whose PRIVATE rows the owner's RLS cannot see. A manual
        // snapshot used to run under that RLS, so these were neither captured nor
        // restored (mig 247). Mutated after the snapshot below; the restore must
        // bring them back.
        var memberId = await ledger.AddMemberAsync("editor");

        Guid bankLegId;
        Guid promotionRunId;
        Guid memberReportId;
        await using (var db = _fixture.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = parentCategory, LedgerId = ledger.LedgerId, Name = "Household",
                AccountType = "category", CategoryKind = "expense", CurrencyCode = "USD",
                OpeningBalance = 0m, IsActive = true,
            });
            db.Accounts.Add(new AccountRow
            {
                Id = childCategory, LedgerId = ledger.LedgerId, Name = "Utilities",
                AccountType = "category", CategoryKind = "expense", CurrencyCode = "USD",
                OpeningBalance = 0m, IsActive = true, ParentId = parentCategory,
            });
            db.Accounts.Add(new AccountRow
            {
                Id = holdingsSiblingId, LedgerId = ledger.LedgerId, Name = "Brokerage Holdings",
                AccountType = "investment", CurrencyCode = "USD", OpeningBalance = 0m,
                IsActive = true, IsSystem = true,
            });
            db.Accounts.Add(new AccountRow
            {
                Id = brokerageId, LedgerId = ledger.LedgerId, Name = "Brokerage",
                AccountType = "investment", CurrencyCode = "USD", OpeningBalance = 0m,
                IsActive = true, HoldingsAccountId = holdingsSiblingId,
            });

            bankLegId = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId && l.AccountId == bank.Id)
                .OrderBy(l => l.Id)
                .Select(l => l.Id).FirstAsync();
            db.TxnLegRecon.Add(new TxnLegReconRow
            {
                LegId = bankLegId,
                LedgerId = ledger.LedgerId,
                Status = "cleared",
                ClearedAt = Utc(2026, 1, 3),
                ClearedByUserId = null,
            });
            db.BudgetTargets.Add(new BudgetTargetRow
            {
                LedgerId = ledger.LedgerId,
                CategoryId = dining.Id,
                TargetMonth = new DateOnly(2026, 1, 1),
                Amount = 250m,
                CreatedAt = Utc(2026, 1, 1),
                UpdatedAt = Utc(2026, 1, 1),
            });

            var tagId = Guid.NewGuid();
            db.Tags.Add(new TagRow
            {
                Id = tagId,
                LedgerId = ledger.LedgerId,
                Name = "Property B",
                CreatedAt = Utc(2026, 1, 1),
            });
            db.TxnHeaderTags.Add(new TxnHeaderTagRow
            {
                HeaderId = await db.TxnLegs.AsNoTracking()
                    .Where(l => l.Id == bankLegId).Select(l => l.HeaderId).SingleAsync(),
                TagId = tagId,
                LedgerId = ledger.LedgerId,
            });
            db.TxnLegTags.Add(new TxnLegTagRow
            {
                LegId = bankLegId,
                TagId = tagId,
                LedgerId = ledger.LedgerId,
            });


            var groupId = Guid.NewGuid();
            db.UserAccountGroups.Add(new UserAccountGroupRow
            {
                Id = groupId, UserId = ledger.UserId, LedgerId = ledger.LedgerId,
                Name = "Everyday", SortOrder = 0, CreatedAt = Utc(2026, 1, 1),
            });
            db.UserAccountGroupMembers.Add(new UserAccountGroupMemberRow
            {
                GroupId = groupId, AccountId = bank.Id, LedgerId = ledger.LedgerId,
                AddedAt = Utc(2026, 1, 1),
            });

            db.ProviderSecurityMappings.Add(new ProviderSecurityMappingRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, ProviderKey = "simplefin",
                ProviderSecurityId = "FND", SecurityId = security, CreatedAt = Utc(2026, 1, 1),
            });

            var templateId = Guid.NewGuid();
            var seriesId = Guid.NewGuid();
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = templateId, LedgerId = ledger.LedgerId, Origin = "manual",
                Payee = "Rent", PostedAt = Utc(2026, 1, 1), TransactedAt = Utc(2026, 1, 1),
                IsRecurringTemplate = true,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "manual",
                CreatedAt = Utc(2026, 1, 1),
            });
            db.RecurringOccurrenceExceptions.Add(new RecurringOccurrenceExceptionRow
            {
                Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, RecurringTransactionId = seriesId,
                OccurrenceDate = new DateOnly(2026, 2, 1), CreatedAt = Utc(2026, 1, 1),
            });

            var loanId = Guid.NewGuid();
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = ledger.LedgerId, Name = "Mortgage",
                AccountType = "loan", CurrencyCode = "USD", OpeningBalance = -100000m, IsActive = true,
            });
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 100000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12, EscrowAmount = 0m,
                PaymentIsComputed = true, CreatedAt = Utc(2026, 1, 1),
            });

            // A promote-on-clear record: the sync run stays outside the snapshot,
            // its promotion is captured with the transaction it describes.
            var runId = Guid.NewGuid();
            db.LedgerOperations.Add(new LedgerOperationRow
            {
                Id = runId, LedgerId = ledger.LedgerId, Family = "ingest", ProviderKey = "simplefin",
                TriggeredVia = "manual", Status = "completed", StartedAt = Utc(2026, 1, 5),
                CompletedAt = Utc(2026, 1, 5),
            });
            db.LedgerOperationPromotions.Add(new LedgerOperationPromotionRow
            {
                Id = Guid.NewGuid(), LedgerOperationId = runId, LedgerId = ledger.LedgerId,
                HeaderId = winner, WasAmount = -40m, BecameAmount = -48m, PromotedAt = Utc(2026, 1, 5),
            });
            promotionRunId = runId;

            var reportId = Guid.NewGuid();
            db.SavedReports.Add(new SavedReportRow
            {
                Id = reportId, LedgerId = ledger.LedgerId, OwnerUserId = ledger.UserId,
                Name = "Monthly spend", Spec = """{"kind":"spending"}""",
                CreatedAt = Utc(2026, 1, 1), UpdatedAt = Utc(2026, 1, 1),
            });
            db.ReportPins.Add(new ReportPinRow
            {
                Id = Guid.NewGuid(), UserId = ledger.UserId, LedgerId = ledger.LedgerId,
                SavedReportId = reportId, SortOrder = 0, CreatedAt = Utc(2026, 1, 1),
            });
            db.ReportPins.Add(new ReportPinRow
            {
                Id = Guid.NewGuid(), UserId = ledger.UserId, LedgerId = ledger.LedgerId,
                CatalogKey = "net-worth", SortOrder = 1, CreatedAt = Utc(2026, 1, 1),
            });

            // The member's own: a private report, its pin, an account group.
            memberReportId = Guid.NewGuid();
            db.SavedReports.Add(new SavedReportRow
            {
                Id = memberReportId, LedgerId = ledger.LedgerId, OwnerUserId = memberId,
                Name = "Member's private report", Spec = """{"kind":"spending"}""",
                CreatedAt = Utc(2026, 1, 1), UpdatedAt = Utc(2026, 1, 1),
            });
            db.ReportPins.Add(new ReportPinRow
            {
                Id = Guid.NewGuid(), UserId = memberId, LedgerId = ledger.LedgerId,
                SavedReportId = memberReportId, SortOrder = 0, CreatedAt = Utc(2026, 1, 1),
            });
            db.UserAccountGroups.Add(new UserAccountGroupRow
            {
                Id = Guid.NewGuid(), UserId = memberId, LedgerId = ledger.LedgerId,
                Name = "Member's group", SortOrder = 0, CreatedAt = Utc(2026, 1, 1),
            });
            await db.SaveChangesAsync();

            // account_external_ids has no API writer and no EF entity: the Moneydance
            // importer is its only author, and it writes raw SQL. This is that row.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO account_external_ids (account_id, ledger_id, source, external_id)
                VALUES ({bank.Id}, {ledger.LedgerId}, 'moneydance', 'md-checking')");

            // A row whose id IS the all-zero uuid — what the chunk keyset used to
            // start strictly after, and so skip. The Moneydance importer writes
            // explicit ids in raw SQL, and so does this: EF reads Guid.Empty as
            // "no key yet" and would generate one, leaving the case untested.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO tags (id, ledger_id, name, created_at)
                VALUES ({Guid.Empty}, {ledger.LedgerId}, 'Nil-id tag', {Utc(2026, 1, 1)})");
        }
        await ledger.EditHeaderAsync(bankLegId, memo: "reconciled at bank");

        // Fingerprint every captured table.
        await using var db2 = _fixture.NewDbContext();
        var conn = db2.Database.GetDbConnection();
        await conn.OpenAsync();
        var captured = await CapturedTablesAsync(conn);

        var before = new Dictionary<string, (long Rows, string Digest)>();
        foreach (var t in captured) before[t] = await FingerprintAsync(conn, t, ledger.LedgerId);

        var unseeded = before.Where(kv => kv.Value.Rows == 0).Select(kv => kv.Key).Order().ToList();
        Assert.True(unseeded.Count == 0,
            "Captured table(s) with no seeded rows, so this test cannot tell whether they round-trip: "
            + string.Join(", ", unseeded) + ". Seed them above.");

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("full"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Re-capture the same state at ONE row per chunk: same snapshot, same rows,
        // only the chunking differs. The endpoint's 2000-row chunks put a test ledger
        // in a single chunk per table, which is how the cross-chunk failure reached
        // production unseen. Service role, so RLS does not narrow the capture — for
        // a single-owner ledger it sees exactly what the request did.
        // REPEATABLE READ, which capture requires (mig 247).
        await using (var capture = await db2.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead))
        {
            await db2.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT ledger_snapshot_write({snap.Id}, {ledger.LedgerId}, 1)");
            await capture.CommitAsync();
        }
        var headerChunks = await db2.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM ledger_snapshot_parts WHERE snapshot_id = {snap.Id} AND part_name = 'txn_headers'")
            .SingleAsync();
        Assert.Equal(before["txn_headers"].Rows, headerChunks);

        // Mutate after the snapshot: a stray transaction, a dropped recon mark, the
        // fund's look-through, the saved report (its pin cascades with it), and a
        // later promotion — which the restore rolls back with everything else.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -99m, Utc(2026, 2, 2), payee: "post-snapshot");
        await using (var db3 = _fixture.NewDbContext())
        {
            await db3.TxnLegRecon.Where(r => r.LegId == bankLegId).ExecuteDeleteAsync();
            await db3.SecurityComponents.Where(c => c.SecurityId == security).ExecuteDeleteAsync();
            await db3.SavedReports.Where(r => r.LedgerId == ledger.LedgerId).ExecuteDeleteAsync();
            await db3.UserAccountGroups.Where(g => g.UserId == memberId).ExecuteDeleteAsync();
            db3.LedgerOperationPromotions.Add(new LedgerOperationPromotionRow
            {
                Id = Guid.NewGuid(), LedgerOperationId = promotionRunId, LedgerId = ledger.LedgerId,
                HeaderId = loser, WasAmount = -10m, BecameAmount = -12m, PromotedAt = Utc(2026, 2, 3),
            });
            await db3.SaveChangesAsync();
        }

        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        // Every captured table must return to the snapshot state, row for row.
        var mismatches = new List<string>();
        foreach (var t in captured)
        {
            var after = await FingerprintAsync(conn, t, ledger.LedgerId);
            if (after != before[t])
                mismatches.Add(after.Rows == before[t].Rows
                    ? $"{t}: same {after.Rows} rows, different content"
                    : $"{t}: {before[t].Rows} -> {after.Rows} rows");
        }
        Assert.True(mismatches.Count == 0,
            "Restore did not round-trip these captured tables: " + string.Join("; ", mismatches));
    }

    // -----------------------------------------------------------------
    // Guard 3 — precision: investment share/price columns stay (25,12).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Investment_share_and_price_columns_are_numeric_25_12()
    {
        // Migration 043 bumped the share-quantity + execution-price family from
        // lossy (19,4)/(19,6) to NUMERIC(25,12) but missed lots.unit_cost (fixed
        // mig 180). Pin the whole family so the next precision bump can't miss a
        // column again. Deliberately EXCLUDED:
        //   * money AMOUNT columns (txn_legs.amount) — 2dp by ADR-0073.
        //   * security_prices.price — a market-valuation snapshot, constrained to
        //     NUMERIC(19,4) on purpose by mig 155 (ADR-0070 D8) to match its
        //     high/low columns and reject FLOAT noise. NOT an execution price.
        var family = new (string Table, string Col)[]
        {
            ("txn_legs", "quantity"),
            ("txn_legs", "unit_price"),   // per-share EXECUTION price (distinct from market price)
            ("holdings", "quantity"),
            ("lots", "quantity"),
            ("lots", "unit_cost"),
        };

        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var wrong = new List<string>();
        foreach (var (table, col) in family)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT numeric_precision, numeric_scale
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @t AND column_name = @c";
            var pt = cmd.CreateParameter(); pt.ParameterName = "t"; pt.Value = table; cmd.Parameters.Add(pt);
            var pc = cmd.CreateParameter(); pc.ParameterName = "c"; pc.Value = col; cmd.Parameters.Add(pc);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync(), $"{table}.{col} not found");
            var precision = r.GetInt32(0);
            var scale = r.GetInt32(1);
            if (precision != 25 || scale != 12) wrong.Add($"{table}.{col} is NUMERIC({precision},{scale})");
        }

        Assert.True(wrong.Count == 0,
            "Share/price columns drifted off NUMERIC(25,12): " + string.Join(", ", wrong)
            + " — bump them to match the mig-043 family (see mig 180 for why 4dp drifts).");
    }

    // -----------------------------------------------------------------
    // Guard 4 — no unconstrained numeric in aggregated financial tables.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Aggregated_financial_numeric_columns_are_bounded()
    {
        // The realized_gains failure (mig 182) was an UNCONSTRAINED `numeric` column:
        // the recompute stored cost_basis_sold = fractional_qty(12dp) × unit_cost(12dp)
        // = 24 decimal places, which at a 6-figure magnitude is ~30 significant digits
        // and overflows .NET decimal on read. Any unbounded numeric in an aggregated
        // financial table is the same footgun — pin that every one carries an explicit
        // scale, so a computed value can't grow past decimal's ceiling and blow up the
        // read. (The specific scale is a per-column choice — money 2dp, shares 12dp —
        // this only forbids the no-scale case.)
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var unbounded = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT table_name || '.' || column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND data_type = 'numeric' AND numeric_scale IS NULL
                  AND table_name IN ('realized_gains', 'holdings', 'txn_header_account_balances', 'lots');";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) unbounded.Add(r.GetString(0));
        }

        Assert.True(unbounded.Count == 0,
            "Unconstrained numeric column(s) in aggregated financial tables (overflow risk — a "
            + "large, high-scale computed value overflows .NET decimal on read): "
            + string.Join(", ", unbounded) + ". Constrain each to NUMERIC(precision, scale).");
    }

    // -----------------------------------------------------------------
    // Guard 5 — no ON DELETE SET NULL FK nulls a NOT NULL column.
    // -----------------------------------------------------------------

    [Fact]
    public async Task No_set_null_fk_nulls_a_not_null_column()
    {
        // A composite ON DELETE SET NULL foreign key with no column list nulls
        // EVERY referencing column when the parent is deleted. If any of those
        // columns is NOT NULL the SET NULL can never succeed — deleting a parent
        // row throws 23502. mig 183: this defect on the txn_headers ->
        // recurring_transactions FK (it also nulled the NOT NULL ledger_id) broke
        // snapshot restore for any ledger with a recurring-linked transaction. The
        // fix is the PG15+ column-specific form: ON DELETE SET NULL (nullable_col).
        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var broken = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT c.conrelid::regclass || '.' || c.conname
                FROM pg_constraint c
                WHERE c.contype = 'f'
                  AND c.confdeltype = 'n'                             -- ON DELETE SET NULL
                  AND coalesce(cardinality(c.confdelsetcols), 0) = 0  -- nulls ALL fk columns (no column list)
                  AND EXISTS (
                      SELECT 1 FROM unnest(c.conkey) k
                      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k
                      WHERE a.attnotnull                              -- ...but a referencing column is NOT NULL
                  );";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) broken.Add(r.GetString(0));
        }

        Assert.True(broken.Count == 0,
            "ON DELETE SET NULL foreign key(s) that null a NOT NULL column — deleting the parent "
            + "throws 23502 (broke snapshot restore, mig 183): " + string.Join(", ", broken)
            + ". Use ON DELETE SET NULL (nullable_column) to null only the nullable FK column.");
    }

    // -----------------------------------------------------------------
    // Guard 6 — a migration COMMENT must not assert a stale NUMERIC scale.
    // -----------------------------------------------------------------

    // Claims that are wrong-but-frozen. engineering-standards.md §3.1: once a
    // migration is committed to main it is never edited, so a stale header cannot
    // be corrected in place — it is recorded here, with the doc that carries the
    // real value, instead of being silently tolerated.
    //
    // Key is "<migration number>:<table>.<column>:<claimed precision>,<claimed scale>".
    private static readonly HashSet<string> FrozenScaleClaims = new()
    {
        // mig 113's header documents shares as "NUMERIC(28,8) per holdings.quantity".
        // mig 043 — seventy migrations earlier — had already moved that column to
        // (25,12). database-schema.md carries the correct value.
        "113:holdings.quantity:28,8",

        // mig 155 narrates the BEFORE state deliberately: "security_prices.price was
        // NUMERIC(25,12) while its own high / low columns are NUMERIC(19,4)". The
        // sentence exists to explain why that same migration constrains price to
        // (19,4) — the claim is past tense, not a description of the result.
        "155:security_prices.price:25,12",
    };

    // NUMERIC(p,s) is masked before the line is split on commas, because the
    // literal contains one: splitting first turns "NUMERIC(19,4)" into two
    // fragments and loses the claim.
    private static readonly Regex NumericLiteral =
        new(@"NUMERIC\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaskedClaim = new(@"«(\d+):(\d+)»", RegexOptions.Compiled);
    private static readonly Regex QualifiedColumn =
        new(@"\b([a-z_][a-z0-9_]*)\.([a-z_][a-z0-9_]*)\b", RegexOptions.Compiled);

    [Fact]
    public async Task Migration_comments_do_not_assert_a_stale_numeric_scale()
    {
        // WHY: migration headers are load-bearing documentation here. 205 and 209
        // each rounded money at a scale a stale header asserted, and 209 left
        // disposals off in their last decimal place on a real ledger. The column-side guard above pins
        // the columns; this is its comment-side twin.
        //
        // A claim is one fragment (comma/semicolon-delimited) of a comment line
        // holding exactly one `table.column` and exactly one NUMERIC(p,s). That
        // pairing rule matters: a greedy same-line match reads
        // "holdings.cost_basis NUMERIC(19,4), lots.unit_cost NUMERIC(25,12)" as a
        // claim that lots.unit_cost is (19,4), which is a parser artifact, not drift.
        var dir = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);

        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var actual = new Dictionary<string, (int Precision, int Scale)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT table_name, column_name, numeric_precision, numeric_scale
                FROM information_schema.columns
                WHERE table_schema = 'public' AND data_type = 'numeric'
                  AND numeric_precision IS NOT NULL AND numeric_scale IS NOT NULL";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                actual[$"{r.GetString(0)}.{r.GetString(1)}"] = (r.GetInt32(2), r.GetInt32(3));
        }

        var stale = new List<string>();
        var verified = 0;

        foreach (var path in Directory.EnumerateFiles(dir, "*.sql").OrderBy(p => p))
        {
            var migration = Path.GetFileName(path).Split('_')[0];
            foreach (var raw in await File.ReadAllLinesAsync(path))
            {
                var line = raw.TrimStart();
                if (!line.StartsWith("--", StringComparison.Ordinal)) continue;

                var masked = NumericLiteral.Replace(line, m => $"«{m.Groups[1].Value}:{m.Groups[2].Value}»");
                foreach (var fragment in masked.Split(',', ';'))
                {
                    var claims = MaskedClaim.Matches(fragment);
                    var columns = QualifiedColumn.Matches(fragment);
                    if (claims.Count != 1 || columns.Count != 1) continue;

                    var column = $"{columns[0].Groups[1].Value}.{columns[0].Groups[2].Value}";
                    // A comment may name a table that has since been dropped, or a
                    // non-column dotted token. Unverifiable, not drift.
                    if (!actual.TryGetValue(column, out var live)) continue;

                    var precision = int.Parse(claims[0].Groups[1].Value, CultureInfo.InvariantCulture);
                    var scale = int.Parse(claims[0].Groups[2].Value, CultureInfo.InvariantCulture);
                    verified++;

                    if (precision == live.Precision && scale == live.Scale) continue;
                    if (FrozenScaleClaims.Contains($"{migration}:{column}:{precision},{scale}")) continue;

                    stale.Add($"mig {migration}: says {column} is NUMERIC({precision},{scale}), "
                        + $"schema says ({live.Precision},{live.Scale})");
                }
            }
        }

        // Floor. Every check here is an absence check, and a parser that silently
        // stops matching reports the same clean result as a correct corpus. The
        // known-good claims are the proof it is still reading.
        Assert.True(verified >= 6,
            $"Only {verified} NUMERIC(p,s) claim(s) in migration comments could be checked against "
            + "the live schema. The corpus has several; this guard has stopped parsing them, so a "
            + "clean result from it would mean nothing.");

        Assert.True(stale.Count == 0,
            "Migration comment(s) assert a NUMERIC scale the schema does not have — the failure mode "
            + "that put three disposals a cent out in mig 209: " + string.Join("; ", stale)
            + ". The migration cannot be edited (engineering-standards §3.1), so correct the value in "
            + "database-schema.md and add the claim to FrozenScaleClaims with the reason.");
    }
}
