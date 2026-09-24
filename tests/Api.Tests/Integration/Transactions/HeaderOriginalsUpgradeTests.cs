using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Migration 230 rehearsed against an install that already has override rows.
/// </summary>
/// <remarks>
/// <para><b>Why this needs a rehearsal at all.</b> 230 is not a shape change with
/// nothing to be inconsistent with — it MOVES existing user data between tables.
/// Every assertion here is about rows written by earlier releases, and none of
/// them can be made against a database at head, where the override table no
/// longer exists.</para>
///
/// <para><b>The one line that matters is the fold.</b> Most override rows in the
/// field carry only SOME fields — a payee rename sets payee and leaves posted_at
/// NULL — so <c>SET posted_at = o.posted_at</c> would blank the date on every one
/// of them. The fold must be <c>COALESCE(o.x, h.x)</c>, and the only way to catch
/// the difference is to seed a PARTIAL override row and check the columns it did
/// NOT touch. A rehearsal seeded with fully-populated overrides would pass either
/// way, which is the can-never-fail shape this harness exists to stamp out.</para>
///
/// <para>Verified by hand against clones of the real dev database as well, with
/// 801 manufactured partial overrides; this pins the same property where it runs
/// on every push.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HeaderOriginalsUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public HeaderOriginalsUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig230 = "230_flip_header_overrides_to_originals.sql";

    private static readonly Guid LedgerId = new("30303030-0000-0000-0000-000000000001");
    private static readonly Guid BankId = new("30303030-0000-0000-0000-000000000002");
    private static readonly Guid CategoryId = new("30303030-0000-0000-0000-000000000003");

    /// <summary>Renamed payee only. Every other column of the override is NULL.</summary>
    private static readonly Guid RenamedId = new("30303030-0000-0000-0000-00000000000a");
    /// <summary>Re-dated only.</summary>
    private static readonly Guid RedatedId = new("30303030-0000-0000-0000-00000000000b");
    /// <summary>Never edited — no override row at all.</summary>
    private static readonly Guid UntouchedId = new("30303030-0000-0000-0000-00000000000c");

    private const string FeedPayee = "SQ *COFFEE 0304";
    private const string FeedMemo = "POS PURCHASE";
    private const string FeedPosted = "2026-03-04 12:00:00+00";
    private const string FeedTransacted = "2026-03-02 12:00:00+00";
    private const string FeedCheck = "1207";
    private const string CuratedPayee = "Blue Bottle Coffee";
    private const string CuratedPosted = "2026-03-09 12:00:00+00";

    private (string ConnectionString, string WorkDir) FreshInstallBefore230(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig230-").FullName;
        var cs = _fixture.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig230, work));
        return (cs, work);
    }

    /// <summary>
    /// An install as it stood before 230: feed values on the headers, user edits
    /// in the override table, each override carrying ONE field.
    /// </summary>
    private static async Task SeedPre230Async(string connectionString)
    {
        await ExecAsync(connectionString, $"""
            INSERT INTO ledgers (id, name) VALUES ('{LedgerId}', 'Home');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code)
            VALUES ('{BankId}',     '{LedgerId}', 'Checking', 'bank',     NULL,      'USD'),
                   ('{CategoryId}', '{LedgerId}', 'Coffee',   'category', 'expense', 'USD');
            """);

        foreach (var id in new[] { RenamedId, RedatedId, UntouchedId })
        {
            await ExecAsync(connectionString, $"""
                INSERT INTO txn_headers
                    (id, ledger_id, origin, payee, memo, posted_at, transacted_at, check_number)
                VALUES ('{id}', '{LedgerId}', 'manual', '{FeedPayee}', '{FeedMemo}',
                        TIMESTAMPTZ '{FeedPosted}', TIMESTAMPTZ '{FeedTransacted}', '{FeedCheck}');
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES (gen_random_uuid(), '{id}', '{LedgerId}', '{BankId}',     0, -4.50),
                       (gen_random_uuid(), '{id}', '{LedgerId}', '{CategoryId}', 0,  4.50);
                """);
        }

        // PARTIAL overrides — the shape the fold has to survive.
        await ExecAsync(connectionString, $"""
            INSERT INTO txn_header_overrides (header_id, ledger_id, payee)
            VALUES ('{RenamedId}', '{LedgerId}', '{CuratedPayee}');
            INSERT INTO txn_header_overrides (header_id, ledger_id, posted_at)
            VALUES ('{RedatedId}', '{LedgerId}', TIMESTAMPTZ '{CuratedPosted}');
            """);
    }

    /// <summary>
    /// A one-field override moves its field and leaves the rest of the row alone.
    /// </summary>
    /// <remarks>
    /// The memo / transacted_at / check_number assertions are the point. An
    /// assignment fold would have nulled all three on this row — and
    /// <c>transacted_at</c> is NOT NULL since migration 189, so the upgrade would
    /// have failed loudly there and silently corrupted the other two.
    /// </remarks>
    [Fact]
    public async Task A_partial_override_folds_without_blanking_the_columns_it_never_set()
    {
        var (cs, work) = FreshInstallBefore230("mig230_partial_fold");
        await SeedPre230Async(cs);

        Assert.True(await TableExistsAsync(cs, "txn_header_overrides"),
            "staged short of 230 but the override table is already gone — the rehearsal "
            + "is not at the version it claims");

        UpgradeRehearsal.Apply(cs, work, Mig230);

        Assert.Equal(CuratedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_headers WHERE id = '{RenamedId}'"));
        Assert.Equal(FeedMemo, await TextAsync(cs,
            $"SELECT memo FROM txn_headers WHERE id = '{RenamedId}'"));
        Assert.Equal(FeedCheck, await TextAsync(cs,
            $"SELECT check_number FROM txn_headers WHERE id = '{RenamedId}'"));
        Assert.Equal(FeedPosted, await TimestampAsync(cs,
            $"SELECT posted_at FROM txn_headers WHERE id = '{RenamedId}'"));
        Assert.Equal(FeedTransacted, await TimestampAsync(cs,
            $"SELECT transacted_at FROM txn_headers WHERE id = '{RenamedId}'"));

        // The other direction: a date-only override moves the date and nothing else.
        Assert.Equal(CuratedPosted, await TimestampAsync(cs,
            $"SELECT posted_at FROM txn_headers WHERE id = '{RedatedId}'"));
        Assert.Equal(FeedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_headers WHERE id = '{RedatedId}'"));
    }

    /// <summary>
    /// The feed's values land in the sidecar, for exactly the edited rows.
    /// </summary>
    /// <remarks>
    /// Captured from the headers BEFORE the fold, which is the only moment they
    /// are still on the canonical row. Capturing after would record the curated
    /// values as the "originals" and lose the feed's permanently — recoverable
    /// from nothing, since the override table is dropped in the same migration.
    ///
    /// <para>The untouched row is half the assertion: an originals row for every
    /// header would make "has been edited" mean nothing, and that predicate is
    /// what <c>resolved_transactions.has_overrides</c> and the undo-import warning
    /// both read.</para>
    /// </remarks>
    [Fact]
    public async Task The_feed_values_are_captured_for_the_edited_rows_only()
    {
        var (cs, work) = FreshInstallBefore230("mig230_capture");
        await SeedPre230Async(cs);
        UpgradeRehearsal.Apply(cs, work, Mig230);

        Assert.Equal(2, await CountAsync(cs, "SELECT count(*) FROM txn_header_originals"));
        Assert.Equal(0, await CountAsync(cs,
            $"SELECT count(*) FROM txn_header_originals WHERE header_id = '{UntouchedId}'"));

        // The feed's payee is recoverable even though the header now shows the
        // curated one — which is what payee recall reads.
        Assert.Equal(FeedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_header_originals WHERE header_id = '{RenamedId}'"));
        Assert.Equal(FeedPosted, await TimestampAsync(cs,
            $"SELECT posted_at FROM txn_header_originals WHERE header_id = '{RedatedId}'"));

        // Denormalized ledger_id, which the RLS policy gates on directly.
        Assert.Equal(2, await CountAsync(cs,
            $"SELECT count(*) FROM txn_header_originals WHERE ledger_id = '{LedgerId}'"));
    }

    /// <summary>
    /// An unedited row comes through the upgrade byte-identical.
    /// </summary>
    /// <remarks>
    /// The overwhelming majority of rows on any install have no override at all
    /// (1.7% of prod's headers had one). A fold that reached them — a join written
    /// as a cross product, say — would rewrite the whole table, and the two tests
    /// above would not notice.
    /// </remarks>
    [Fact]
    public async Task An_unedited_row_is_untouched_by_the_upgrade()
    {
        var (cs, work) = FreshInstallBefore230("mig230_untouched");
        await SeedPre230Async(cs);
        UpgradeRehearsal.Apply(cs, work, Mig230);

        Assert.Equal(FeedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_headers WHERE id = '{UntouchedId}'"));
        Assert.Equal(FeedMemo, await TextAsync(cs,
            $"SELECT memo FROM txn_headers WHERE id = '{UntouchedId}'"));
        Assert.Equal(FeedCheck, await TextAsync(cs,
            $"SELECT check_number FROM txn_headers WHERE id = '{UntouchedId}'"));
        Assert.Equal(FeedPosted, await TimestampAsync(cs,
            $"SELECT posted_at FROM txn_headers WHERE id = '{UntouchedId}'"));
        Assert.Equal(FeedTransacted, await TimestampAsync(cs,
            $"SELECT transacted_at FROM txn_headers WHERE id = '{UntouchedId}'"));
    }

    /// <summary>
    /// The register reads the curated values, and reports the rows as edited.
    /// </summary>
    /// <remarks>
    /// Asserted through <c>resolved_transactions</c> because that view is what the
    /// register renders. It is also where the upgrade could go wrong invisibly:
    /// the migration rewrites the view with <c>CREATE OR REPLACE</c>, so a column
    /// dropped or reordered would break the EF entity rather than the SQL.
    /// </remarks>
    [Fact]
    public async Task After_the_upgrade_the_view_shows_the_curated_values_and_flags_the_edits()
    {
        var (cs, work) = FreshInstallBefore230("mig230_view");
        await SeedPre230Async(cs);
        UpgradeRehearsal.Apply(cs, work, Mig230);

        Assert.Equal(CuratedPayee, await TextAsync(cs, $"""
            SELECT DISTINCT payee FROM resolved_transactions
             WHERE header_id = '{RenamedId}'
            """));
        Assert.Equal(CuratedPosted, await TimestampAsync(cs, $"""
            SELECT DISTINCT posted_at FROM resolved_transactions
             WHERE header_id = '{RedatedId}'
            """));

        // has_overrides keeps its name and changes meaning: has an original on file.
        Assert.Equal(2, await CountAsync(cs, """
            SELECT count(DISTINCT header_id) FROM resolved_transactions
             WHERE has_overrides
            """));
        Assert.Equal(0, await CountAsync(cs, $"""
            SELECT count(*) FROM resolved_transactions
             WHERE header_id = '{UntouchedId}' AND has_overrides
            """));
    }

    /// <summary>
    /// The view is still <c>security_invoker</c>, so RLS still evaluates as the caller.
    /// </summary>
    /// <remarks>
    /// <c>CREATE OR REPLACE VIEW</c> DROPS this option silently and
    /// <c>pg_get_viewdef</c> does not emit it, so a migration that rewrites the
    /// view and forgets the <c>ALTER VIEW</c> turns the hottest read path in the
    /// app into a cross-ledger leak with nothing on screen to say so. Migration
    /// 228 exists because that happened. Asserted on the catalog rather than by
    /// querying as another user, because the option is the mechanism and a
    /// behavioural test would pass on a single-ledger fixture.
    /// </remarks>
    [Fact]
    public async Task The_resolved_view_keeps_security_invoker_through_the_rewrite()
    {
        var (cs, work) = FreshInstallBefore230("mig230_invoker");
        await SeedPre230Async(cs);
        UpgradeRehearsal.Apply(cs, work, Mig230);

        foreach (var view in new[] { "resolved_transactions", "account_current_balances" })
        {
            Assert.Equal(1, await CountAsync(cs, $"""
                SELECT count(*) FROM pg_class
                 WHERE relname = '{view}' AND relkind = 'v'
                   AND 'security_invoker=true' = ANY (reloptions)
                """));
        }
    }

    /// <summary>Both override tables are gone.</summary>
    [Fact]
    public async Task Both_override_tables_are_dropped()
    {
        var (cs, work) = FreshInstallBefore230("mig230_dropped");
        await SeedPre230Async(cs);
        UpgradeRehearsal.Apply(cs, work, Mig230);

        Assert.False(await TableExistsAsync(cs, "txn_header_overrides"));
        Assert.False(await TableExistsAsync(cs, "txn_leg_overrides"));
    }

    /// <summary>Applied twice against the database it just changed.</summary>
    /// <remarks>
    /// The re-run is not hypothetical for this one: the operator's next restart
    /// re-runs nothing, but a restore onto a database that already carries the
    /// journal row does, and so does anyone applying the script by hand. The
    /// second pass runs against a database with no override tables at all, so
    /// every statement that references them has to tolerate their absence.
    /// </remarks>
    [Fact]
    public async Task Applying_230_twice_is_harmless()
    {
        var (cs, work) = FreshInstallBefore230("mig230_rerun");
        await SeedPre230Async(cs);

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig230);

        // ...and the fold did not run a second time over its own output.
        Assert.Equal(CuratedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_headers WHERE id = '{RenamedId}'"));
        Assert.Equal(FeedPayee, await TextAsync(cs,
            $"SELECT payee FROM txn_header_originals WHERE header_id = '{RenamedId}'"));
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND table_name = @t",
            connection);
        command.Parameters.AddWithValue("t", table);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>A timestamptz rendered as the UTC literal the seeds are written in.</summary>
    private static async Task<string?> TimestampAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT to_char(({sql}) AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') || '+00'",
            connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
