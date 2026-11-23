using System.Text.RegularExpressions;

using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Schema;

/// <summary>
/// The memo-and-tags guards against the migrated schema. No SQL in the database reads
/// the raw inputs (a header's own memo, a leg's own memo, the header and leg tag links)
/// but the listed functions: every reader of what a register shows reads the stored
/// presentation, which only the C# rule computes (plan guard 4). And every memo column
/// and transaction tag table is catalogued with its role, agreeing with the write
/// catalog's inputs and migration 264's stored columns (plan guard 5).
/// </summary>
/// <remarks>
/// <para>The catalog makes a new memo column or tag table fail until someone says what
/// it is: an input the write and reader guards must then hold, the stored presentation
/// only the recompute writes, or neither, with the reason.</para>
/// <para>Read from the migrated database's catalog, not from the migration files: every
/// function in <c>public</c> at its current definition, however it was created, and every
/// view's definition as PostgreSQL stores it. A view column that read the header memo
/// was dropped by migration 266; this holds every view and function to that.</para>
/// <para>The match is by name: a function or view whose SQL names <c>txn_header_tags</c>,
/// <c>txn_leg_tags</c>, <c>leg_memo</c> or a bare <c>memo</c> is a finding, whatever table
/// the <c>memo</c> belongs to. Each listed one says why it is no reader that bypasses the
/// rule. It fails on an unlisted finding, on a listed one that no longer matches (stale),
/// and on a scan that found too few functions or views to be reading the catalog.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class AnnotationSqlCatalogTests : DatabaseTest
{
    public AnnotationSqlCatalogTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>Functions and views whose SQL names a raw input, by kind and name, each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // The snapshot: whole rows out and back, the stored presentation with the inputs
        // it was computed from (ADR-0108 D10).
        ["function fn_ledger_snapshot_part_names"] = "names the captured tables; reads no row",
        ["function fn_ledger_snapshot_insert_order"] = "names the captured tables in the order a restore inserts them; reads no row",
        ["function fn_ledger_snapshot_payload"] = "captures the link tables' whole rows for a snapshot",
        ["function fn_ledger_snapshot_restore"] = "copies the captured link rows back verbatim, with the stored presentation computed from them",
        ["function fn_ledger_snapshot_clear"] = "deletes a ledger's link rows before a restore copies the snapshot's back",
        ["function fn_ledger_delete"] = "deletes the whole ledger, its link rows with it",
    };

    [GeneratedRegex(@"\b(?:txn_header_tags|txn_leg_tags|leg_memo|memo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RawInput();

    [Fact]
    public async Task No_function_or_view_reads_the_raw_memo_and_tag_inputs_but_the_listed_ones()
    {
        var functions = await DefinitionsAsync("""
            SELECT 'function ' || p.proname, pg_get_functiondef(p.oid)
              FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace
               AND p.prokind IN ('f', 'p')
               AND NOT EXISTS (SELECT 1 FROM pg_depend d
                                WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            """);
        var views = await DefinitionsAsync("""
            SELECT 'view ' || c.relname, pg_get_viewdef(c.oid)
              FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('v', 'm')
            """);
        // Fail closed: a catalog query that matched nothing would pass.
        Assert.True(functions.Count > 50,$"read only {functions.Count} functions; the scan is not reading the catalog");
        Assert.True(views.Count >= 5, $"read only {views.Count} views; the scan is not reading the catalog");

        var findings = functions.Concat(views)
            .Select(d => (d.Name, Names: RawInput().Matches(d.Sql).Select(m => m.Value.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList()))
            .Where(f => f.Names.Count > 0)
            .ToList();
        var violations = findings
            .Where(f => !Allowed.ContainsKey(f.Name))
            .Select(f => $"{f.Name} names {string.Join(", ", f.Names)}: read the stored presentation, or list it with its reason")
            .Order(StringComparer.Ordinal)
            .ToList();
        violations.AddRange(Allowed.Keys.Where(k => findings.All(f => f.Name != k))
            .Select(k => $"{k}: listed, but its SQL names no raw input; remove the entry"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>What a memo-like column or a transaction tag table is to the guards.</summary>
    private enum Role
    {
        /// <summary>An input of the rule: the write and reader catalogs hold it.</summary>
        Input,
        /// <summary>The stored presentation: only the recompute writes it.</summary>
        Stored,
        /// <summary>Neither: the reason says what it is.</summary>
        Other,
    }

    /// <summary>
    /// Every column whose name holds <c>memo</c>, on every table and view, by
    /// <c>relation.column</c>: its role, and for <see cref="Role.Other"/> why it is no input.
    /// </summary>
    private static readonly Dictionary<string, (Role Role, string Why)> MemoColumns = new(StringComparer.Ordinal)
    {
        ["txn_headers.memo"] = (Role.Input, "the transaction's own memo"),
        ["txn_legs.leg_memo"] = (Role.Input, "a leg's own memo"),
        ["txn_legs.notes_memo"] = (Role.Stored, "the memo the row shows"),
        ["txn_legs.notes_memo_inherited"] = (Role.Stored, "whether the shown memo comes from the other side"),
        ["txn_legs.notes_inherited_memo"] = (Role.Stored, "what the row would show with its own value blank"),
        ["txn_legs.notes_header_memo"] = (Role.Stored, "the transaction's memo, where the header is in play"),
        ["resolved_transactions.notes_memo"] = (Role.Stored, "the row's own leg's stored memo"),
        ["resolved_transactions.notes_memo_inherited"] = (Role.Stored, "the row's own leg's stored flag"),
        ["resolved_transactions.notes_inherited_memo"] = (Role.Stored, "the row's own leg's stored placeholder"),
        ["resolved_transactions.notes_header_memo"] = (Role.Stored, "the row's own leg's stored header memo"),
        ["resolved_transactions.counterparty_notes_memo"] = (Role.Stored, "the posting's other leg's stored memo"),
        ["resolved_transactions.counterparty_notes_memo_inherited"] = (Role.Stored, "the posting's other leg's stored flag"),
        ["resolved_transactions.counterparty_notes_inherited_memo"] = (Role.Stored, "the posting's other leg's stored placeholder"),
        ["resolved_transactions.counterparty_notes_header_memo"] = (Role.Stored, "the posting's other leg's stored header memo"),
        ["txn_header_originals.memo"] = (Role.Other,
            "the feed's memo from before the first edit (migration 230): a record of what the feed said, which the rule never reads"),
    };

    /// <summary>Every <c>txn_*_tags</c> table, with its role.</summary>
    private static readonly Dictionary<string, Role> TagTables = new(StringComparer.Ordinal)
    {
        ["txn_header_tags"] = Role.Input,
        ["txn_leg_tags"] = Role.Input,
        ["txn_leg_note_tags"] = Role.Stored,
    };

    /// <summary>The other objects the memo-and-tags guards are built on, each with the query that finds it.</summary>
    private static readonly Dictionary<string, string> NamedObjects = new(StringComparer.Ordinal)
    {
        ["function tag_name_key"] = "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'tag_name_key'",
        ["function saved_report_spec_tag_id"] = "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'saved_report_spec_tag_id'",
        ["function saved_reports_repoint_tag"] = "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'saved_reports_repoint_tag'",
        ["the unique tag-name key index"] = """
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
             WHERE i.indrelid = 'public.tags'::regclass AND i.indisunique
               AND pg_get_indexdef(i.indexrelid) LIKE '%(ledger_id, tag_name_key(name))%'
            """,
    };

    [Fact]
    public async Task Every_memo_column_and_transaction_tag_table_is_in_the_guarded_catalog()
    {
        var memoColumns = await DefinitionsAsync("""
            SELECT c.relname || '.' || a.attname, c.relkind::text
              FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm')
               AND a.attnum > 0 AND NOT a.attisdropped AND a.attname ~ 'memo'
            """);
        var tagTables = await DefinitionsAsync("""
            SELECT c.relname, c.relkind::text FROM pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p') AND c.relname ~ '^txn_\w*_tags$'
            """);
        Assert.True(memoColumns.Count >= 5, $"found only {memoColumns.Count} memo columns; the scan is not reading the catalog");

        var violations = new List<string>();
        violations.AddRange(memoColumns.Where(c => !MemoColumns.ContainsKey(c.Name))
            .Select(c => $"{c.Name}: a memo column the guards' catalog does not name; give it its role"));
        violations.AddRange(MemoColumns.Keys.Where(k => memoColumns.All(c => c.Name != k))
            .Select(k => $"{k}: catalogued, but no such column exists; remove the entry"));
        violations.AddRange(tagTables.Where(t => !TagTables.ContainsKey(t.Name))
            .Select(t => $"{t.Name}: a transaction tag table the guards' catalog does not name; give it its role"));
        violations.AddRange(TagTables.Keys.Where(k => tagTables.All(t => t.Name != k))
            .Select(k => $"{k}: catalogued, but no such table exists; remove the entry"));

        // The roles agree with the guards that hold them: the inputs are the write
        // catalog's, and the stored ones are what migration 264 adds and the view exposes.
        var inputs = Unit.StoredNotesWriteCatalogTests.InputColumns;
        var catalogInputMemos = inputs.SelectMany(i => i.Value.Where(c => c.Contains("memo", StringComparison.Ordinal)).Select(c => $"{i.Key}.{c}"));
        AssertSame("memo inputs", MemoColumns.Where(c => c.Value.Role == Role.Input).Select(c => c.Key), catalogInputMemos, violations);
        AssertSame("tag table inputs", TagTables.Where(t => t.Value == Role.Input).Select(t => t.Key),
            inputs.Keys.Where(k => Regex.IsMatch(k, @"^txn_\w*_tags$")), violations);
        AssertSame("stored tag table", TagTables.Where(t => t.Value == Role.Stored).Select(t => t.Key),
            [Unit.StoredNotesWriteCatalogTests.StoredTagTable], violations);
        var stored = Unit.StoredNotesWriteCatalogTests.StoredColumns(Unit.StoredNotesWriteCatalogTests.RepoRoot());
        AssertSame("stored memo columns", MemoColumns.Where(c => c.Value.Role == Role.Stored && c.Key.StartsWith("txn_legs.", StringComparison.Ordinal)).Select(c => c.Key),
            stored.Where(c => c.Contains("memo", StringComparison.Ordinal)).Select(c => $"txn_legs.{c}"), violations);
        violations.AddRange(MemoColumns.Where(c => c.Value.Role == Role.Stored && c.Key.StartsWith("resolved_transactions.", StringComparison.Ordinal)
                                                   && !stored.Contains(Regex.Replace(c.Key.Split('.')[1], "^counterparty_", "")))
            .Select(c => $"{c.Key}: catalogued as the view's stored presentation, but 264 stores no such column"));

        // Every column the write catalog names as an input exists (the originating account
        // among them), and so does every other object the guards are built on.
        var columns = await DefinitionsAsync("""
            SELECT c.relname || '.' || a.attname, '' FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
            """);
        violations.AddRange(inputs.SelectMany(i => i.Value.Select(c => $"{i.Key}.{c}"))
            .Where(c => columns.All(x => x.Name != c))
            .Select(c => $"{c}: an input of the write catalog that the schema does not have"));
        foreach (var (name, query) in NamedObjects)
        {
            if (await CountAsync(query) != 1) violations.Add($"{name}: not found exactly once");
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static void AssertSame(string what, IEnumerable<string> catalogued, IEnumerable<string> expected, List<string> violations)
    {
        var a = catalogued.ToHashSet(StringComparer.Ordinal);
        var b = expected.ToHashSet(StringComparer.Ordinal);
        if (!a.SetEquals(b))
            violations.Add($"{what}: catalogued here as [{string.Join(", ", a.Order(StringComparer.Ordinal))}], the guards hold [{string.Join(", ", b.Order(StringComparer.Ordinal))}]");
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Every (name, SQL) row <paramref name="sql"/> returns, read as the superuser so no definition is hidden.</summary>
    private async Task<List<(string Name, string Sql)>> DefinitionsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }
}
