using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// The startup gate that decides whether a key-less boot is legal (ADR-0092 D3).
/// The stakes are asymmetric and that asymmetry is what these tests pin: a false
/// "virgin" mints a fresh KEK over live wrapped material and orphans it, while a
/// false "not virgin" merely refuses to boot, which the operator resolves by
/// supplying the key or passing <c>--adopt-new-kek</c>. So every uncertain
/// outcome must report true.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WrappedMaterialProbeTests
{
    private readonly PostgresFixture _fixture;

    public WrappedMaterialProbeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reports_wrapped_material_when_a_ledger_carries_a_wrapped_lek()
    {
        // Seeded here rather than assumed: relying on other tests in the shared
        // collection to have left a wrapped LEK behind makes this order-dependent
        // and it silently passed for the wrong reason the first time.
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            WrappedLek = new LedgerKeyService(new MasterKey(new byte[32], "v1")).CreateWrappedLek(),
            LekKekId = "v1",
        });
        await db.SaveChangesAsync();

        Assert.True(WrappedMaterialProbe.Exists(_fixture.ServiceConnectionString));
    }

    [Theory]
    [InlineData("notification_subscribers", "coffer_probe_deployment_target")]
    [InlineData("ledger_notification_subscribers", "coffer_probe_ledger_target")]
    public async Task Reports_wrapped_material_when_only_a_notification_target_carries_it(
        string table, string databaseName)
    {
        // The install this exists for: an admin completes setup and configures a
        // delivery target BEFORE creating any ledger, scheduling a backup or connecting
        // Drive. Its only KEK-wrapped bytes sit in config_ciphertext. The probe listed
        // three columns while rotation covered five, so lose the key file, restart, and
        // D3 reads a virgin install, mints a fresh KEK and orphans the sealed URL — the
        // precise outcome D3 exists to prevent, produced by the gate meant to prevent
        // it. Nothing fails loudly; the target just stops delivering.
        //
        // Against an ISOLATED database, and that is the whole point. The first version
        // of this test seeded the shared one and asserted Exists() — which was already
        // true because of other tests' wrapped LEKs, so it passed with the fix reverted.
        // It pinned nothing. Exactly the trap the LEK test above documents ("silently
        // passed for the wrong reason the first time"), walked into a second time.
        var connectionString = _fixture.EmptyDatabaseConnectionString(databaseName);
        MigrationRunner.Run(
            connectionString,
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            NullLogger.Instance);

        var sealedConfig = new LedgerKeyService(new MasterKey(new byte[32], "v1"))
            .SealWithMasterKey("{}"u8.ToArray());

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            if (table == "ledger_notification_subscribers")
            {
                // The ledger-scope row needs a ledger to hang off, and that ledger must
                // carry NO wrapped_lek — otherwise the ledger check answers first and
                // this test passes for the wrong reason all over again.
                await using var ledger = new NpgsqlCommand(
                    "INSERT INTO ledgers (id, name) VALUES (@id, 'probe')", connection);
                ledger.Parameters.AddWithValue("id", LedgerProbeId);
                await ledger.ExecuteNonQueryAsync();
            }

            await using var insert = new NpgsqlCommand(
                table == "ledger_notification_subscribers"
                    ? "INSERT INTO ledger_notification_subscribers "
                      + "(ledger_id, subscriber_key, display_name, config_ciphertext) "
                      + "VALUES (@ledger, 'webhook', 'probe', @blob)"
                    : "INSERT INTO notification_subscribers "
                      + "(subscriber_key, display_name, config_ciphertext) "
                      + "VALUES ('webhook', 'probe', @blob)",
                connection);
            insert.Parameters.AddWithValue("ledger", LedgerProbeId);
            insert.Parameters.AddWithValue("blob", sealedConfig);
            await insert.ExecuteNonQueryAsync();
        }

        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(connectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.True(result);
    }

    private static readonly Guid LedgerProbeId = new("0f6a1d4e-1f2b-4c3d-8e9a-0b1c2d3e4f50");

    [Fact]
    public async Task An_emptied_ciphertext_reads_as_absence_not_presence()
    {
        // Reconciliation EMPTIES config_ciphertext to retire a blob this install cannot
        // open — the column is NOT NULL, so [] is how a row says "nothing sealed here".
        // A probe that counted length-zero as material would mean an install whose
        // targets had all been retired by a cross-KEK restore could never legally boot
        // key-less again: refused over bytes that are not there. Hence Length > 0 rather
        // than a bare Any().
        var connectionString =
            _fixture.EmptyDatabaseConnectionString("coffer_emptied_ciphertext_probe");
        MigrationRunner.Run(
            connectionString,
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            NullLogger.Instance);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO notification_subscribers "
                + "(subscriber_key, display_name, config_ciphertext) "
                + "VALUES ('webhook', 'retired', ''::BYTEA)",
                connection);
            await insert.ExecuteNonQueryAsync();
        }

        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(connectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Reports_true_and_surfaces_the_reason_when_the_database_is_unreachable()
    {
        // Fails CLOSED. An unreachable database is not evidence of a virgin
        // install, and treating it as one is the expensive mistake.
        Exception? captured = null;
        var unreachable = "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;"
            + "Password=nobody;Timeout=1;Command Timeout=1";

        var result = WrappedMaterialProbe.Exists(unreachable, ex => captured = ex);

        Assert.True(result);
        Assert.NotNull(captured);
    }

    [Fact]
    public void Reports_false_on_a_reachable_database_with_no_schema()
    {
        // The genuinely virgin case, and the one that matters most: the API's
        // first boot probes BEFORE migrations, so none of the three tables exist.
        // Every check therefore raises 42P01, and that has to read as "absent",
        // not as a probe failure — the fail-closed branch would refuse the boot
        // and brick every fresh install.
        Exception? captured = null;

        var result = WrappedMaterialProbe.Exists(
            _fixture.EmptyDatabaseConnectionString(), ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Reports_false_on_a_migrated_database_with_no_wrapped_values()
    {
        // Distinct path from the one above: the tables DO exist and every wrapped
        // column is null. That's an install which migrated but never created a
        // ledger — still virgin for D3's purposes, so a key may be minted.
        var connectionString = _fixture.EmptyDatabaseConnectionString("coffer_migrated_probe");
        MigrationRunner.Run(
            connectionString,
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            NullLogger.Instance);

        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(connectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Rejects_a_blank_connection_string()
        => Assert.Throws<ArgumentException>(() => WrappedMaterialProbe.Exists("  "));
}
