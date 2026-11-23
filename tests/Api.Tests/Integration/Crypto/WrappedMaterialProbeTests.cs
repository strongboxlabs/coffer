using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Auth;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Scheduling;
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
public sealed class WrappedMaterialProbeTests : DatabaseTest
{
    public WrappedMaterialProbeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public void A_fresh_clone_reports_wrapped_material_because_the_template_sealed_an_oauth_pair()
    {
        // The template is settled the way a first boot leaves a database, and a first
        // boot seals the OAuth server's key pair under the master key. So every install
        // that has booted once holds wrapped material, and a key-less boot of it refuses.
        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(Db.ServiceConnectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.True(result);
    }

    [Fact]
    public async Task Reports_wrapped_material_when_a_ledger_carries_a_wrapped_lek()
    {
        // The clone's OAuth pair would answer first, so it goes, and the probe must then
        // read the clone as holding nothing: the answer below turns on the ledger alone.
        await RemoveTheTemplatesOauthPairAsync();
        Assert.False(Probe(Db.ServiceConnectionString));

        await using var db = Db.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            WrappedLek = new LedgerKeyService(new MasterKey(new byte[32], "v1")).CreateWrappedLek(),
            LekKekId = "v1",
        });
        await db.SaveChangesAsync();

        Assert.True(Probe(Db.ServiceConnectionString));
    }

    [Fact]
    public async Task Reports_wrapped_material_when_the_oauth_pair_is_the_only_wrapped_value()
    {
        // The install this check exists for: set up, booted, and nothing else configured.
        // Its only sealed bytes are the OAuth server's key pair. Read as virgin, a key-less
        // boot would mint a fresh master key and MCP sign-in could no longer open its keys.
        await RemoveTheTemplatesOauthPairAsync();
        Assert.False(Probe(Db.ServiceConnectionString));

        // Sealed by the loader boot calls, with no legacy files to move in, as on a first boot.
        var legacy = Directory.CreateTempSubdirectory("coffer-probe-oauth-").FullName;
        try
        {
            var material = new OpenIddictKeyMaterial(
                Db.NewServiceFactory(), TimeProvider.System, NullLogger<OpenIddictKeyMaterial>.Instance);
            Assert.True(await material.LoadAsync(Db.NewLedgerKeyService(), legacy));
        }
        finally
        {
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
        }

        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(Db.ServiceConnectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.True(result);
    }

    /// <summary>The probe's answer, asserting it ran: a probe that could not reach the
    /// database also answers true, and that must not pass for a finding.</summary>
    internal static bool Probe(string connectionString)
    {
        Exception? failure = null;
        var result = WrappedMaterialProbe.Exists(connectionString, ex => failure = ex);
        Assert.Null(failure);
        return result;
    }

    [Fact]
    public async Task An_empty_passphrase_or_drive_token_reads_as_absence_as_it_does_to_rotation()
    {
        // Rotation and reconciliation treat an empty value as nothing sealed (FU-095): neither
        // opens it, so a key minted over it orphans nothing. The probe used to count it, so a
        // start without a key refused over bytes that are not there.
        await RemoveTheTemplatesOauthPairAsync();
        await using (var db = Db.NewDbContext())
        {
            db.GlobalScheduledJobs.Add(new GlobalScheduledJobRow
            {
                JobType = GlobalJobTypes.Backup, PassphraseCiphertext = [],
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.DriveSync.Add(new DriveSyncRow
            {
                OauthCiphertext = [], CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.False(Probe(Db.ServiceConnectionString));
    }

    /// <summary>Delete the OAuth pair every clone carries from the template.</summary>
    private async Task RemoveTheTemplatesOauthPairAsync()
    {
        await using var db = Db.NewDbContext();
        Assert.Equal(1, await db.OidcServerKeys.ExecuteDeleteAsync());
    }

    [Theory]
    [InlineData("notification_subscribers", "coffer_probe_deployment_target")]
    [InlineData("ledger_notification_subscribers", "coffer_probe_ledger_target")]
    public async Task Reports_wrapped_material_when_only_a_notification_target_carries_it(
        string table, string databaseName)
    {
        // An admin completes setup and configures a delivery target BEFORE creating
        // any ledger, scheduling a backup or connecting Drive. Its only KEK-wrapped
        // bytes sit in config_ciphertext. A probe that lists fewer columns than
        // rotation covers would, after the key file is lost and the API restarted, let
        // ADR-0092 D3 read a virgin install, mint a fresh KEK and orphan the sealed URL — the
        // precise outcome ADR-0092 D3 exists to prevent, produced by the gate meant to prevent
        // it. Nothing fails loudly; the target just stops delivering.
        //
        // Against a database migrated here from empty, holding only what this test
        // arranges, so none of the columns the probe reads is set except the one row
        // below and the assertion turns on that row alone. A clone would not do: it
        // carries the template's sealed OAuth pair, which the probe counts, so the
        // assertion would pass whatever this row held.
        var connectionString = Db.EmptyDatabaseConnectionString(databaseName);
        MigrationRunner.Run(
            TestDatabaseCluster.ForMigrationRunner(connectionString),
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
            Db.EmptyDatabaseConnectionString("coffer_emptied_ciphertext_probe");
        MigrationRunner.Run(
            TestDatabaseCluster.ForMigrationRunner(connectionString),
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
        // first boot probes BEFORE migrations, so none of the probed tables exist.
        // Every check therefore raises 42P01, and that has to read as "absent",
        // not as a probe failure — the fail-closed branch would refuse the boot
        // and brick every fresh install.
        Exception? captured = null;

        var result = WrappedMaterialProbe.Exists(
            Db.EmptyDatabaseConnectionString(), ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Reports_false_on_a_migrated_database_with_no_wrapped_values()
    {
        // Distinct path from the one above: the tables DO exist and hold no wrapped
        // value. A boot that ran the migrations but stopped before it sealed the OAuth
        // pair leaves this — still virgin for ADR-0092 D3's purposes, so a key may be minted.
        // Migrated here from empty rather than cloned: a clone carries the template's
        // sealed OAuth pair.
        var connectionString = Db.EmptyDatabaseConnectionString("coffer_migrated_probe");
        MigrationRunner.Run(
            TestDatabaseCluster.ForMigrationRunner(connectionString),
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
