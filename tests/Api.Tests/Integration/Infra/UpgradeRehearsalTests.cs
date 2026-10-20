using Npgsql;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The rehearsal harness testing itself.
/// </summary>
/// <remarks>
/// A drill that cannot fail is worse than no drill, because it is cited as
/// evidence. This project has shipped that mistake twice — an upgrade script
/// whose no-rotation assertion had no failing branch, and idempotency tests that
/// asserted only a status code — so the harness four migrations are about to be
/// verified with does not get to be taken on trust. Each case below breaks
/// something on purpose and requires the harness to notice.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class UpgradeRehearsalTests
{
    private readonly PostgresFixture _fixture;

    public UpgradeRehearsalTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig214 = "214_ledgers_own_their_notifications.sql";

    private static string NewWorkDir()
        => Directory.CreateTempSubdirectory("coffer-rehearsal-").FullName;

    private static async Task<bool> ColumnExistsAsync(
        string connectionString, string table, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns "
            + "WHERE table_name = @t AND column_name = @c", connection);
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("c", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Staging really stops short: the column migration 214 removes is still
    /// present, which is what makes the database "an install in the field".
    /// </summary>
    [Fact]
    public async Task Staging_before_a_migration_leaves_the_state_that_migration_acts_on()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_stage_probe");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageBefore(Mig214, work));

        Assert.True(
            await ColumnExistsAsync(connectionString, "ledgers", "notification_mode"),
            "staged short of 214 but ledgers.notification_mode is already gone — "
            + "the database is not at the version the rehearsal claims");

        UpgradeRehearsal.Apply(connectionString, work, Mig214);

        Assert.False(
            await ColumnExistsAsync(connectionString, "ledgers", "notification_mode"),
            "214 ran but ledgers.notification_mode survived");
    }

    /// <summary>
    /// A mistyped migration name is refused. Without this it would stage every
    /// script, the rehearsal would silently become an ordinary fresh-database
    /// test, and it would pass.
    /// </summary>
    [Fact]
    public void A_migration_name_that_does_not_exist_is_refused()
    {
        var work = NewWorkDir();
        var ex = Assert.Throws<ArgumentException>(
            () => UpgradeRehearsal.StageBefore("214_ledgers_own_their_notification.sql", work));
        Assert.Contains("would otherwise silently include every migration", ex.Message);
    }

    /// <summary>
    /// THE self-test: data that exists before the migration must be able to make
    /// the migration fail. If this ever goes green the harness is worthless.
    /// </summary>
    /// <remarks>
    /// The shape is not hypothetical — it is the scheduled <c>SET NOT NULL</c> on
    /// the ledger key columns, whose backfill has to reach every pre-existing row
    /// or the constraint cannot be added. Here the backfill is deliberately
    /// omitted, and the rehearsal has to report that.
    /// </remarks>
    [Fact]
    public async Task A_constraint_that_existing_rows_violate_fails_the_rehearsal()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_selftest");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageAll(work));

        // A row as an older install left it: no wrapped key material.
        await ExecAsync(connectionString,
            "INSERT INTO ledgers (id, name) VALUES "
            + "('11111111-1111-1111-1111-111111111111', 'Pre-upgrade ledger')");

        var boom = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            UpgradeRehearsal.ApplyInlineScript(
                connectionString, work, "999_selftest_constraint_without_backfill.sql",
                "ALTER TABLE ledgers ALTER COLUMN wrapped_lek SET NOT NULL;")));

        // Not just "something threw" — it has to be the constraint, on that column.
        Assert.Contains("wrapped_lek", boom.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A migration that only works once is caught, rather than surviving to fail
    /// on the operator's next restart.
    /// </summary>
    [Fact]
    public async Task A_script_that_cannot_run_twice_is_caught()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_rerun");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageAll(work));

        const string script = "998_selftest_not_rerunnable.sql";
        const string sql = "CREATE TABLE rehearsal_once (id INT PRIMARY KEY);";

        UpgradeRehearsal.ApplyInlineScript(connectionString, work, script, sql);

        // Second execution of the same DDL: no IF NOT EXISTS, so it must fail.
        await ExecAsync(connectionString,
            "DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%" + script + "'");

        var boom = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            UpgradeRehearsal.ApplyInlineScript(connectionString, work, script, sql)));
        Assert.Contains("rehearsal_once", boom.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The digest notices a value changing, and does not notice row order.
    /// A digest that changed for no reason would be turned off within a week.
    /// </summary>
    [Fact]
    public async Task The_digest_catches_a_changed_value_and_ignores_nothing_else()
    {
        var work = NewWorkDir();
        var connectionString = _fixture.EmptyDatabaseConnectionString("rehearsal_digest");
        UpgradeRehearsal.RunStaged(connectionString, UpgradeRehearsal.StageAll(work));

        await ExecAsync(connectionString,
            "INSERT INTO ledgers (id, name) VALUES "
            + "('22222222-2222-2222-2222-222222222222', 'Alpha'), "
            + "('33333333-3333-3333-3333-333333333333', 'Beta')");

        const string projection = "SELECT id, name FROM ledgers ORDER BY id";
        var before = await UpgradeRehearsal.DigestAsync(connectionString, projection);
        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(connectionString, projection));

        await ExecAsync(connectionString,
            "UPDATE ledgers SET name = 'Alpha!' "
            + "WHERE id = '22222222-2222-2222-2222-222222222222'");

        Assert.NotEqual(before, await UpgradeRehearsal.DigestAsync(connectionString, projection));
    }
}
