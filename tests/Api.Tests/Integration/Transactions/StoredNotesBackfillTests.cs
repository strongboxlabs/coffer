using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Migration 264 and the upgrade step that fills the stored memo-and-tags presentation
/// for existing data: the step is journaled right after its script and before anything
/// later, its registration fails closed, 264 runs clean on an empty database and again
/// over its own result, and the step stores exactly what the rule computes, converging
/// on a rerun after a partial run.
/// </summary>
/// <remarks>
/// 264's data steps on an install's data are <c>Migration264UpgradeTests</c>' and the
/// importer's vintage tests'. Here the backfill is driven the way the seeds drive it:
/// on a database migrated while empty, seeded after, then
/// <see cref="LegNotesBackfill.RunAsync"/> (the step's own code). The seeds are SQL,
/// standing in for rows an earlier release wrote, in shapes the rule tells apart: a
/// one-split with each side's own notes, a split with transaction and line notes, an
/// event with a fee, an in-kind transfer, and a header without legs.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesBackfillTests : DatabaseTest
{
    public StoredNotesBackfillTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static Guid Id(int n) => new($"26400000-0000-0000-0000-{n:D12}");

    private static readonly Guid Ledger = Id(1);
    private static readonly Guid Security = Id(2);
    private static readonly Guid BankA = Id(10);
    private static readonly Guid BankB = Id(11);
    private static readonly Guid CatA = Id(20);
    private static readonly Guid CatB = Id(21);
    private static readonly Guid Fee = Id(22);
    private static readonly Guid BrokP = Id(30);
    private static readonly Guid HoldP = Id(31);
    private static readonly Guid BrokQ = Id(32);
    private static readonly Guid HoldQ = Id(33);
    private static readonly Guid TagA = Id(40);
    private static readonly Guid TagB = Id(41);
    private static readonly Guid TagC = Id(42);

    /// <summary>A migration after 264, staged only in this rehearsal's directory.</summary>
    private const string LaterScript = "265_later_migration_probe.sql";

    [Fact]
    public async Task The_step_is_journaled_directly_after_its_script_and_before_any_later_one()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig264-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig264_step_order");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(LegNotesBackfill.AfterScript, work));
        // Staged before 264, the step is not part of the run: its columns do not exist yet.
        Assert.DoesNotContain(LegNotesBackfill.Name, await JournalAsync(cs));

        // A later migration staged beside 264, which fails unless the step has already
        // run: it must sit between its script and anything after it.
        await File.WriteAllTextAsync(Path.Combine(work, LaterScript), $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM public.__schema_migrations WHERE scriptname = '{LegNotesBackfill.Name}') THEN
                    RAISE EXCEPTION 'a later migration ran before the stored-notes backfill';
                END IF;
            END $$;
            """);
        UpgradeRehearsal.Apply(cs, work, LegNotesBackfill.AfterScript);

        var journal = await JournalAsync(cs);
        Assert.Equal(LegNotesBackfill.Name, journal[journal.IndexOf(LegNotesBackfill.AfterScript) + 1]);
        Assert.Equal(LaterScript, journal[journal.IndexOf(LegNotesBackfill.Name) + 1]);
    }

    [Fact]
    public void A_directory_that_goes_past_264_without_it_is_refused()
    {
        // The step registers only where its script is staged, so a directory that lost
        // 264 but holds a later script would migrate past it without the step, silently.
        var work = Directory.CreateTempSubdirectory("coffer-mig264-").FullName;
        UpgradeRehearsal.StageBefore(LegNotesBackfill.AfterScript, work);
        File.WriteAllText(Path.Combine(work, LaterScript), "SELECT 1;");
        var cs = Db.EmptyDatabaseConnectionString("mig264_missing_script");

        var refused = Assert.Throws<InvalidOperationException>(() => UpgradeRehearsal.RunStaged(cs, work));
        Assert.Contains(LegNotesBackfill.AfterScript, refused.Message, StringComparison.Ordinal);
        Assert.Contains(LegNotesBackfill.Name, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_264_runs_clean_on_an_empty_database()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig264-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig264_empty");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(LegNotesBackfill.AfterScript, work));
        UpgradeRehearsal.Apply(cs, work, LegNotesBackfill.AfterScript);

        var journal = await JournalAsync(cs);
        Assert.Contains(LegNotesBackfill.AfterScript, journal);
        Assert.Contains(LegNotesBackfill.Name, journal);
    }

    [Fact]
    public async Task Migration_264_runs_again_over_its_own_committed_changes_when_its_journal_row_is_missing()
    {
        // The script commits, then the runner journals it in a statement of its own. A
        // stop between the two leaves 264 applied but unjournaled, and the next start runs
        // it again (the step after it too, which journals with it): every object it creates
        // must already be accepted as there.
        var work = Directory.CreateTempSubdirectory("coffer-mig264-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig264_rerun");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(LegNotesBackfill.AfterScript, work));
        UpgradeRehearsal.Apply(cs, work, LegNotesBackfill.AfterScript);
        const string objects = """
            SELECT 'col ' || attname FROM pg_attribute
             WHERE attrelid = 'txn_legs'::regclass AND attname LIKE 'notes\_%' AND NOT attisdropped
            UNION ALL SELECT 'con ' || conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('txn_legs'::regclass, 'txn_leg_note_tags'::regclass)
            UNION ALL SELECT 'idx ' || indexrelid::regclass::text FROM pg_index WHERE indrelid = 'txn_leg_note_tags'::regclass
            UNION ALL SELECT 'pol ' || polname || ' ' || pg_get_expr(polqual, polrelid) || ' '
                             || COALESCE(pg_get_expr(polwithcheck, polrelid), '')
                        FROM pg_policy WHERE polrelid = 'txn_leg_note_tags'::regclass
            UNION ALL SELECT 'rls ' || relrowsecurity FROM pg_class WHERE oid = 'txn_leg_note_tags'::regclass
            UNION ALL SELECT 'acl ' || grantee || ' ' || privilege_type FROM information_schema.role_table_grants
             WHERE table_name = 'txn_leg_note_tags'
            ORDER BY 1
            """;
        var before = await UpgradeRehearsal.DigestAsync(cs, objects);

        await ExecAsync(cs, $"""
            DELETE FROM public.__schema_migrations
             WHERE scriptname LIKE '%{LegNotesBackfill.AfterScript}' OR scriptname = '{LegNotesBackfill.Name}';
            """);
        Assert.DoesNotContain(LegNotesBackfill.AfterScript, await JournalAsync(cs));
        UpgradeRehearsal.RunStaged(cs, work);

        var journal = await JournalAsync(cs);
        Assert.Contains(LegNotesBackfill.AfterScript, journal);
        Assert.Contains(LegNotesBackfill.Name, journal);
        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(cs, objects));
    }

    [Fact]
    public async Task Every_cascade_into_the_tag_role_table_has_an_index_to_find_its_rows()
    {
        // Deleting a leg or a tag cascades into txn_leg_note_tags, matching on the foreign
        // key's columns. Without an index those columns lead, each deleted tag scans the
        // whole table, which is about as large as txn_legs. A partial index does not
        // serve a cascade, which matches every row.
        await using var connection = Db.OpenServiceConnection();
        await using var command = new NpgsqlCommand("""
            SELECT c.conname
              FROM pg_constraint c
             WHERE c.conrelid = 'txn_leg_note_tags'::regclass
               AND c.contype = 'f'
               AND NOT EXISTS (
                     SELECT 1 FROM pg_index i
                      WHERE i.indrelid = c.conrelid
                        AND i.indpred IS NULL
                        AND (i.indkey[0] = c.conkey[1]
                             OR (SELECT array_agg(k ORDER BY k)
                                   FROM unnest((i.indkey::int2[])[0:cardinality(c.conkey) - 1]) k)
                                = (SELECT array_agg(k ORDER BY k) FROM unnest(c.conkey) k)))
             ORDER BY c.conname
            """, connection);
        var unserved = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) unserved.Add(reader.GetString(0));
        Assert.True(unserved.Count == 0, $"no index serves the cascade of [{string.Join(", ", unserved)}]");
        Assert.Equal(2L, await ScalarAsync(Db.ServiceConnectionString,
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'txn_leg_note_tags'::regclass AND contype = 'f'"));
    }

    [Fact]
    public void DbUp_asks_a_step_for_its_script_only_when_it_runs_the_step()
    {
        // The runner lists the scripts to run before deciding which recompute step
        // computes, and a step does its work when asked for its text. Were the text asked
        // for while listing, the backfill would run before 264 had added its columns, on
        // every upgrade. An empty database cannot show the difference through the real
        // step (it has nothing to compute), so a probe registered the way the runner
        // registers its steps records when it is asked.
        var cs = Db.EmptyDatabaseConnectionString("mig264_lazy_step");
        var probe = new ProbeStep();
        var upgrader = DbUp.DeployChanges.To
            .PostgresqlDatabase(cs)
            .WithScript("999_probe_step", probe)
            .JournalToPostgresqlTable("public", "__probe_migrations")
            .LogToNowhere()
            .Build();

        var listed = upgrader.GetScriptsToExecute().Select(s => s.Name).ToList();
        Assert.Contains("999_probe_step", listed);
        Assert.Equal(0, probe.Asked);

        Assert.True(upgrader.PerformUpgrade().Successful);
        Assert.Equal(1, probe.Asked);
    }

    private sealed class ProbeStep : DbUp.Engine.IScript
    {
        public int Asked { get; private set; }

        public string ProvideScript(Func<System.Data.IDbCommand> dbCommandFactory)
        {
            Asked++;
            return "SELECT 1;";
        }
    }

    [Fact]
    public void Only_the_last_recompute_step_of_a_run_computes()
    {
        // Several recompute steps in one upgrade: the values are computed once, by the
        // last, under the final schema and rule; the earlier ones journal without computing.
        static HashSet<string> Run(params string[] names) => new(names, StringComparer.Ordinal);
        Assert.Equal("270_b", MigrationRunner.ComputingStep(["264_a", "270_b"], Run("264_a", "270_b", "265_x.sql")));
        // A run that reaches only the earlier step computes in it.
        Assert.Equal("264_a", MigrationRunner.ComputingStep(["264_a", "270_b"], Run("264_a")));
        Assert.Null(MigrationRunner.ComputingStep(["264_a", "270_b"], Run("265_x.sql")));
    }

    [Fact]
    public async Task The_backfill_stores_what_the_rule_computes_and_converges_after_a_partial_run()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig264-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("mig264_backfill");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageAll(work));
        await SeedAsync(cs);
        await LegNotesBackfill.RunAsync(cs, ledgerId: null, CancellationToken.None);

        await using (var db = Context(cs))
        {
            Assert.Empty(await LegNotes.DifferencesAsync(db, Ledger, CancellationToken.None));
            var legs = await db.TxnLegs.CountAsync(l => l.LedgerId == Ledger);
            Assert.Equal(legs, await StoredNotesEquivalence.PresentedLegsAsync(db, Ledger));
            Assert.True(await db.TxnLegNoteTags.CountAsync(t => t.LedgerId == Ledger) > 0);
            // Read through the readers, the backfilled rows are the rule's from every perspective.
            await StoredNotesEquivalence.AssertEquivalentAsync(db, Ledger);
            await StoredNotesEquivalence.AssertFiltersEquivalentAsync(db, Ledger, StoredNotesEquivalence.FilterSample.All);
        }

        // Rerun the step's work, as the next start does after a run that journaled nothing.
        const string everything =
            "SELECT n.id, n.notes_kind, n.notes_is_read_only, n.notes_groups_by_header, n.notes_memo, n.notes_memo_inherited, "
            + "n.notes_inherited_memo, n.notes_inherit_from_account_id, n.notes_tags_inherited, n.notes_header_memo, n.notes_header_in_play, "
            + "(SELECT string_agg(t.tag_id::text || t.header_id::text || t.shown || t.in_play || t.inherited || t.transaction_level, ',' ORDER BY t.tag_id) "
            + "FROM txn_leg_note_tags t WHERE t.leg_id = n.id) FROM txn_legs n ORDER BY n.id";
        var before = await UpgradeRehearsal.DigestAsync(cs, everything);
        // As a run that stopped part way leaves it: the later headers' batches never
        // committed, so their legs are unpresented and their tag rows absent. A rerun that
        // wrote nothing would leave them so; the digest only matches if it converges.
        await ExecAsync(cs, $"""
            DELETE FROM txn_leg_note_tags WHERE header_id IN ('{Id(102)}', '{Id(103)}', '{Id(104)}');
            UPDATE txn_legs SET notes_kind = NULL, notes_is_read_only = NULL, notes_groups_by_header = NULL,
                   notes_memo = NULL, notes_memo_inherited = NULL, notes_inherited_memo = NULL,
                   notes_inherit_from_account_id = NULL, notes_tags_inherited = NULL,
                   notes_header_memo = NULL, notes_header_in_play = NULL
             WHERE header_id IN ('{Id(102)}', '{Id(103)}', '{Id(104)}');
            """);
        Assert.NotEqual(before, await UpgradeRehearsal.DigestAsync(cs, everything));
        await LegNotesBackfill.RunAsync(cs, ledgerId: null, CancellationToken.None);
        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(cs, everything));

        // And a rerun over values already right writes nothing.
        await using (var db = Context(cs))
        {
            var headers = await db.TxnHeaders.Where(h => h.LedgerId == Ledger).Select(h => h.Id).ToListAsync();
            var writes = await LegNotes.RecomputeAsync(db, headers, CancellationToken.None);
            Assert.Equal(LegNotes.Writes.None, writes);
        }
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AppDbContext Context(string cs) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static async Task<List<string>> JournalAsync(string cs)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT scriptname FROM public.__schema_migrations ORDER BY schemaversionsid", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            names.Add(name[(name.LastIndexOfAny(['/', '\\']) + 1)..]);
        }
        return names;
    }

    private static async Task SeedAsync(string cs)
    {
        await ExecAsync(cs, $"""
            INSERT INTO ledgers (id, name) VALUES ('{Ledger}', 'Ledger A');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, is_system)
            VALUES ('{BankA}', '{Ledger}', 'Account A',  'bank',       NULL,      'USD', FALSE),
                   ('{BankB}', '{Ledger}', 'Account B',  'bank',       NULL,      'USD', FALSE),
                   ('{CatA}',  '{Ledger}', 'Category A', 'category',   'expense', 'USD', FALSE),
                   ('{CatB}',  '{Ledger}', 'Category B', 'category',   'expense', 'USD', FALSE),
                   ('{Fee}',   '{Ledger}', 'Category C', 'category',   'expense', 'USD', FALSE),
                   ('{HoldP}', '{Ledger}', 'Holdings P', 'investment', NULL,      'USD', TRUE),
                   ('{HoldQ}', '{Ledger}', 'Holdings Q', 'investment', NULL,      'USD', TRUE);
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id)
            VALUES ('{BrokP}', '{Ledger}', 'Brokerage P', 'investment', 'USD', '{HoldP}'),
                   ('{BrokQ}', '{Ledger}', 'Brokerage Q', 'investment', 'USD', '{HoldQ}');
            INSERT INTO securities (id, ledger_id, name) VALUES ('{Security}', '{Ledger}', 'Security A');
            INSERT INTO tags (id, ledger_id, name)
            VALUES ('{TagA}', '{Ledger}', 'tag-a'), ('{TagB}', '{Ledger}', 'tag-b'), ('{TagC}', '{Ledger}', 'tag-c');
            """);

        // A one-split, each side with its own notes, and one with notes on one side.
        await HeaderAsync(cs, Id(100), BankA);
        var a0 = await LegAsync(cs, Id(100), BankA, 0, -10m, memo: "entered side");
        var c0 = await LegAsync(cs, Id(100), CatA, 0, 10m, memo: "other side");
        await LegTagAsync(cs, a0, TagA);
        await LegTagAsync(cs, c0, TagB);
        await HeaderAsync(cs, Id(101), BankA);
        var a1 = await LegAsync(cs, Id(101), BankA, 0, -11m, memo: "only side");
        await LegAsync(cs, Id(101), BankB, 0, 11m);
        await LegTagAsync(cs, a1, TagC);

        // A split: the transaction's memo and tags, one line's own.
        await HeaderAsync(cs, Id(102), BankA, memo: "whole");
        await HeaderTagAsync(cs, Id(102), TagA);
        var line = await LegAsync(cs, Id(102), BankA, 0, -12m, memo: "line");
        await LegAsync(cs, Id(102), CatA, 0, 12m);
        await LegAsync(cs, Id(102), BankA, 1, -13m);
        await LegAsync(cs, Id(102), CatB, 1, 13m);
        await LegTagAsync(cs, line, TagB);

        // A buy with a fee, its notes on the header.
        await HeaderAsync(cs, Id(103), BrokP, memo: "buy", action: "buy");
        await HeaderTagAsync(cs, Id(103), TagC);
        await LegAsync(cs, Id(103), HoldP, 0, 100m, role: "security", security: Security, quantity: 2m);
        await LegAsync(cs, Id(103), BrokP, 0, -100m, role: "security");
        await LegAsync(cs, Id(103), Fee, 1, 5m, role: "fee");
        await LegAsync(cs, Id(103), BrokP, 1, -5m, role: "fee");

        // An in-kind transfer, Q to P.
        await HeaderAsync(cs, Id(104), BrokQ, memo: "in kind", action: "transfer_shares");
        await LegAsync(cs, Id(104), HoldQ, 0, -100m, role: "security", security: Security, quantity: -2m);
        await LegAsync(cs, Id(104), BrokQ, 0, 0m, role: "security");
        await LegAsync(cs, Id(104), HoldP, 1, 100m, role: "security", security: Security, quantity: 2m);
        await LegAsync(cs, Id(104), BrokP, 1, 0m, role: "security");

        // A header with no legs: nothing to store.
        await HeaderAsync(cs, Id(105), originating: null);

        // The register's posting counts, as the writers of that release left them.
        for (var h = 100; h <= 105; h++)
            await ExecAsync(cs, $"SELECT fn_recompute_posting_counts_for_header('{Id(h)}');");
    }

    private static Task HeaderAsync(string cs, Guid id, Guid? originating, string? memo = null, string? action = null) =>
        ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, action, payee, memo, posted_at, transacted_at, created_at,
                                     originating_account_id)
            VALUES ('{id}', '{Ledger}', 'manual', {Lit(action)}, 'Payee {id.ToString()[^3..]}', {Lit(memo)},
                    '2026-03-01', '2026-03-01', '2026-03-01', {Lit(originating?.ToString())});
            """);

    private static async Task<Guid> LegAsync(
        string cs, Guid header, Guid account, int posting, decimal amount,
        string? memo = null, string? role = null, Guid? security = null, decimal? quantity = null)
    {
        var id = Guid.NewGuid();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await ExecAsync(cs, $"""
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, leg_memo,
                                  posting_role, security_id, quantity, unit_price)
            VALUES ('{id}', '{header}', '{Ledger}', '{account}', {posting}, {amount.ToString(inv)}, {Lit(memo)},
                    {Lit(role)}, {Lit(security?.ToString())}, {quantity?.ToString(inv) ?? "NULL"},
                    {(quantity is null ? "NULL" : "50")});
            """);
        return id;
    }

    private static Task LegTagAsync(string cs, Guid leg, Guid tag) =>
        ExecAsync(cs, $"INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id) VALUES ('{leg}', '{tag}', '{Ledger}');");

    private static Task HeaderTagAsync(string cs, Guid header, Guid tag) =>
        ExecAsync(cs, $"INSERT INTO txn_header_tags (header_id, tag_id, ledger_id) VALUES ('{header}', '{tag}', '{Ledger}');");

    private static string Lit(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
