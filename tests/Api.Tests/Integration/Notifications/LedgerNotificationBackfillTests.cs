using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// Migration 214's backfill — the upgrade half of retiring <c>inherit</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the one part of the change that cannot be tested against the shared fixture,
/// because the state it acts on no longer exists once every migration has run: the
/// <c>notification_mode</c> column is gone. So each case builds a database at migration
/// 213, seeds the pre-upgrade state by hand, and then runs 214 alone.
/// </para>
/// <para>
/// Worth the trouble because this is the highest-risk piece of the whole change. Every
/// ledger in the field is <c>inherit</c> and almost none have targets of their own;
/// retiring the mode without moving anything would mute ledger scope on every existing
/// install — reproducing ADR-0096's founding incident inside the subsystem built to end
/// it. A green suite against a fresh database would say nothing about that.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerNotificationBackfillTests
{
    private readonly PostgresFixture _fixture;

    public LedgerNotificationBackfillTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig214 = "214_ledgers_own_their_notifications.sql";

    /// <summary>
    /// A database migrated to 213 — i.e. an install as it exists in the field today.
    /// </summary>
    private static string StageAt213(string sourceDir, string workDir)
    {
        Directory.CreateDirectory(workDir);
        foreach (var script in Directory.GetFiles(sourceDir, "*.sql"))
        {
            // Everything up to and including 213. Ordinal compare on the numeric
            // prefix, which every script in this directory carries.
            var name = Path.GetFileName(script);
            if (string.CompareOrdinal(name, Mig214) >= 0) continue;
            File.Copy(script, Path.Combine(workDir, name), overwrite: true);
        }
        return workDir;
    }

    private async Task<string> FreshDatabaseAt213Async(string dbName, string workDir)
    {
        var connectionString = _fixture.EmptyDatabaseConnectionString(dbName);
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        MigrationRunner.Run(connectionString, StageAt213(source, workDir), NullLogger.Instance);
        await Task.CompletedTask;
        return connectionString;
    }

    private static void ApplyMig214(string connectionString, string workDir)
    {
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        File.Copy(Path.Combine(source, Mig214), Path.Combine(workDir, Mig214), overwrite: true);
        MigrationRunner.Run(connectionString, workDir, NullLogger.Instance);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    [Fact]
    public async Task An_inheriting_ledger_keeps_reporting_where_it_was_reporting()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig214-a").FullName;
        var cs = await FreshDatabaseAt213Async("coffer_mig214_inherit", work);

        // The install as it exists in the field: a deployment webhook, and a ledger on
        // the default mode with nothing of its own.
        await ExecAsync(cs, """
            INSERT INTO notification_subscribers
                (subscriber_key, display_name, min_severity, config_ciphertext)
            VALUES ('webhook', 'Household Discord', 'warning', '\x0102030405'::BYTEA);
            INSERT INTO ledgers (id, name)
            VALUES ('11111111-1111-1111-1111-111111111111', 'Home');
            """);

        Assert.Equal(0, await ScalarAsync(cs, "SELECT count(*) FROM ledger_notification_subscribers"));

        ApplyMig214(cs, work);

        // It reports to the same URL it reported to before, from its own row now.
        Assert.Equal(1, await ScalarAsync(cs, "SELECT count(*) FROM ledger_notification_subscribers"));
        Assert.Equal("Household Discord", await TextAsync(cs,
            "SELECT display_name FROM ledger_notification_subscribers"));

        // The ciphertext is carried BYTE FOR BYTE. Both tables are sealed with the same
        // master KEK by the same primitive, which is what lets this be pure SQL with no
        // key material at migration time.
        Assert.Equal(1, await ScalarAsync(cs, """
            SELECT count(*) FROM ledger_notification_subscribers l
              JOIN notification_subscribers s ON s.config_ciphertext = l.config_ciphertext
            """));

        // And the deployment row stays put: it still serves deployment events.
        Assert.Equal(1, await ScalarAsync(cs, "SELECT count(*) FROM notification_subscribers"));
    }

    [Fact]
    public async Task A_heartbeat_target_is_not_copied_into_ledgers()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig214-b").FullName;
        var cs = await FreshDatabaseAt213Async("coffer_mig214_heartbeat", work);

        await ExecAsync(cs, """
            INSERT INTO notification_subscribers
                (subscriber_key, display_name, min_severity, monitors, config_ciphertext)
            VALUES ('healthchecks', 'Backup switch', 'warning', 'backup', '\x0a0b0c'::BYTEA);
            INSERT INTO ledgers (id, name) VALUES (gen_random_uuid(), 'A');
            INSERT INTO ledgers (id, name) VALUES (gen_random_uuid(), 'B');
            """);

        ApplyMig214(cs, work);

        // Copying it would give two ledgers one healthchecks check. One URL is ONE check,
        // so whichever ledger's job ran would hold it green and mask the other's dead
        // one — the exact false-coverage failure the monitor binding exists to remove.
        // It also watches a deployment job that no ledger runs.
        Assert.Equal(0, await ScalarAsync(cs, "SELECT count(*) FROM ledger_notification_subscribers"));
    }

    [Fact]
    public async Task A_ledger_that_already_owned_its_targets_is_left_alone()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig214-c").FullName;
        var cs = await FreshDatabaseAt213Async("coffer_mig214_own", work);

        await ExecAsync(cs, """
            INSERT INTO notification_subscribers
                (subscriber_key, display_name, min_severity, config_ciphertext)
            VALUES ('webhook', 'Deployment', 'warning', '\x01'::BYTEA);
            INSERT INTO ledgers (id, name, notification_mode)
            VALUES ('22222222-2222-2222-2222-222222222222', 'Opted out', 'own');
            INSERT INTO ledger_notification_subscribers
                (ledger_id, subscriber_key, display_name, min_severity, config_ciphertext)
            VALUES ('22222222-2222-2222-2222-222222222222', 'webhook', 'Mine', 'warning',
                    '\xff'::BYTEA);
            """);

        ApplyMig214(cs, work);

        // It opted out explicitly. Adding the deployment's target would re-create, per
        // ledger, exactly the leak this migration removes.
        Assert.Equal(1, await ScalarAsync(cs, "SELECT count(*) FROM ledger_notification_subscribers"));
        Assert.Equal("Mine", await TextAsync(cs,
            "SELECT display_name FROM ledger_notification_subscribers"));
    }

    [Fact]
    public async Task A_disabled_deployment_target_does_not_come_back_as_copies()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig214-d").FullName;
        var cs = await FreshDatabaseAt213Async("coffer_mig214_disabled", work);

        await ExecAsync(cs, """
            INSERT INTO notification_subscribers
                (subscriber_key, display_name, is_enabled, min_severity, config_ciphertext)
            VALUES ('webhook', 'Switched off', FALSE, 'warning', '\x01'::BYTEA);
            INSERT INTO ledgers (id, name) VALUES (gen_random_uuid(), 'A');
            """);

        ApplyMig214(cs, work);

        // An admin turned it off. Coming back as one enabled copy per ledger would be
        // the migration overruling them, N times.
        Assert.Equal(0, await ScalarAsync(cs, "SELECT count(*) FROM ledger_notification_subscribers"));
    }

    [Fact]
    public async Task The_mode_column_is_gone_afterwards()
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig214-e").FullName;
        var cs = await FreshDatabaseAt213Async("coffer_mig214_column", work);

        Assert.Equal(1, await ScalarAsync(cs, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_name = 'ledgers' AND column_name = 'notification_mode'
            """));

        ApplyMig214(cs, work);

        // Dropped rather than pinned to a single value: a one-valued column reads as
        // configurable and is not, and leaving it would let a future call site
        // reintroduce the branch.
        Assert.Equal(0, await ScalarAsync(cs, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_name = 'ledgers' AND column_name = 'notification_mode'
            """));
    }
}
