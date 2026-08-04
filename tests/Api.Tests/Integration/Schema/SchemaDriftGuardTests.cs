using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
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

    // Derived tables: NOT in the snapshot payload; restore rebuilds them from the
    // graph — realized_gains + holdings/lots via recompute_holdings_cost_basis,
    // txn_header_account_balances via fn_recompute_balances_for_account (mig 181).
    private static readonly HashSet<string> DerivedRebuiltOnRestore = new()
    {
        "realized_gains",
        "txn_header_account_balances",
    };

    // Ledger-scoped tables intentionally NOT snapshotted, each with its reason.
    private static readonly HashSet<string> IntentionallyExcluded = new()
    {
        "ledger_snapshots",          // self-referential: a snapshot cannot contain itself
        "mcp_tool_invocations",      // MCP write audit log (ADR-0081/0086) — not ledger data
        "scheduled_jobs",            // scheduler infra (snapshot/backup cadence) — not ledger data
        "feed_connections",          // bank/SimpleFin links (ADR-0006): survive a data rollback
        "feed_connection_accounts",  //   "     "
        "ledger_operations",             // feed-import run history/audit
        "ledger_operation_errors",       //   "     "
        "ledger_operation_promotions",   // feed-import provenance; ledger_operations parent is out of scope
        "invites",                   // access (ADR-0083): not wiped by a data rollback
        "user_ledger_grants",        // access (ADR-0083): who can see the ledger
        "user_preferences",          // per-user UI settings
    };

    private static readonly Regex SafeIdent = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    private static async Task<HashSet<string>> LedgerScopedTablesAsync(DbConnection conn)
    {
        var set = new HashSet<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.relkind = 'r' AND n.nspname = 'public'
              AND a.attname = 'ledger_id' AND a.attnum > 0 AND NOT a.attisdropped;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

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

    private static async Task<long> CountAsync(DbConnection conn, string table, Guid ledgerId)
    {
        if (!SafeIdent.IsMatch(table)) throw new ArgumentException($"unsafe table ident: {table}");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE ledger_id = @l";
        var p = cmd.CreateParameter();
        p.ParameterName = "l";
        p.Value = ledgerId;
        cmd.Parameters.Add(p);
        return (long)(await cmd.ExecuteScalarAsync())!;
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
        var captured = await PayloadCapturedTablesAsync(conn, ledger.LedgerId);

        // The payload must only claim real ledger-scoped tables.
        var phantom = captured.Except(ledgerScoped).OrderBy(x => x).ToList();
        Assert.True(phantom.Count == 0,
            $"fn_ledger_snapshot_payload emits key(s) that are not ledger-scoped tables: {string.Join(", ", phantom)}");

        // Allow-lists must not name tables that no longer exist.
        var staleAllow = DerivedRebuiltOnRestore.Concat(IntentionallyExcluded)
            .Except(ledgerScoped).OrderBy(x => x).ToList();
        Assert.True(staleAllow.Count == 0,
            $"This test's allow-lists name non-existent table(s): {string.Join(", ", staleAllow)} — remove them.");

        // THE guard: no ledger-scoped table is left unclassified.
        var classified = captured.Concat(DerivedRebuiltOnRestore).Concat(IntentionallyExcluded).ToHashSet();
        var unclassified = ledgerScoped.Except(classified).OrderBy(x => x).ToList();
        Assert.True(unclassified.Count == 0,
            $"Ledger-scoped table(s) not covered by a snapshot: {string.Join(", ", unclassified)}. " +
            "Add each to fn_ledger_snapshot_payload + fn_ledger_snapshot_restore, or to this test's " +
            "DerivedRebuiltOnRestore / IntentionallyExcluded allow-list (with a reason).");
    }

    // -----------------------------------------------------------------
    // Guard 2 — fidelity: restore round-trips every captured table.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Snapshot_restore_round_trips_every_captured_table()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        // Seed a broad spread of captured tables. (Tables not seeded here are
        // still checked below at count 0==0; guard 1 proves they're classified.)
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -30m, Utc(2026, 1, 2), payee: "seed");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

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

        Guid bankLegId;
        await using (var db = _fixture.NewDbContext())
        {
            bankLegId = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId && l.AccountId == bank.Id)
                .Select(l => l.Id).FirstAsync();
            db.TxnLegRecon.Add(new TxnLegReconRow
            {
                LegId = bankLegId,
                LedgerId = ledger.LedgerId,
                Status = "cleared",
                ClearedAt = Utc(2026, 1, 3),
                ClearedByUserId = null,
            });
            await db.SaveChangesAsync();
        }
        await ledger.SetHeaderOverrideAsync(bankLegId, memo: "reconciled at bank");

        // Snapshot the seeded state; capture per-table counts.
        await using var db2 = _fixture.NewDbContext();
        var conn = db2.Database.GetDbConnection();
        await conn.OpenAsync();
        var captured = await PayloadCapturedTablesAsync(conn, ledger.LedgerId);

        var before = new Dictionary<string, long>();
        foreach (var t in captured) before[t] = await CountAsync(conn, t, ledger.LedgerId);

        // Sanity: the spread we seeded really has rows (so parity means something).
        foreach (var t in new[] { "txn_headers", "txn_legs", "txn_leg_recon", "txn_header_overrides",
                                  "securities", "holdings", "lots", "security_splits", "security_prices" })
            Assert.True(before[t] > 0, $"expected seeded rows in {t}");

        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots", new CreateSnapshotRequest("full"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // Mutate after the snapshot: add a stray transaction, drop the recon mark.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -99m, Utc(2026, 2, 2), payee: "post-snapshot");
        await using (var db3 = _fixture.NewDbContext())
            await db3.TxnLegRecon.Where(r => r.LegId == bankLegId).ExecuteDeleteAsync();

        // Restore.
        var restoreResp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        // Every captured table's row count must return to the snapshot state.
        var mismatches = new List<string>();
        foreach (var t in captured)
        {
            var after = await CountAsync(conn, t, ledger.LedgerId);
            if (after != before[t]) mismatches.Add($"{t}: {before[t]} -> {after}");
        }
        Assert.True(mismatches.Count == 0,
            "Restore did not round-trip these captured tables (payload/restore INSERT drift): "
            + string.Join("; ", mismatches));
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
}
