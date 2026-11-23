using System.Globalization;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;
using Xunit.Sdk;

using Coffer.Api.Migrations;
using Coffer.Api.Notifications;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The guarantees a database per test rests on, each checked where it can fail.
/// </summary>
/// <remarks>
/// A test's database is a clone of a locked template, checked and dropped when the test
/// ends. These hold that machinery to what it promises: the template cannot be opened
/// or unlocked by any role a test runs as, nothing can be kept in the maintenance
/// database or in template1, the end-of-test checks catch what a test can leave behind
/// (a session elsewhere, a transaction open in its own database, a connection nobody
/// returned, a connection into an earlier test's released database, a large object, an
/// unaccounted database, a change to the roles or the locked databases), a failed setup
/// leaves nothing behind, a clone is the settled template, and a host booted on one
/// writes nothing at boot.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TestDatabaseIsolationTests : DatabaseTest
{
    /// <summary>Shorter than <see cref="TestDatabaseCluster.SettleBound"/>, for the
    /// checks planted here to fail on: the sessions they plant never go.</summary>
    private static readonly TimeSpan PlantedSettle = TimeSpan.FromMilliseconds(200);

    private readonly PostgresFixture _fixture;

    public TestDatabaseIsolationTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output) => _fixture = fixture;

    [Fact]
    public async Task The_template_refuses_connections_from_every_role()
    {
        foreach (var (role, connectionString) in new[]
                 {
                     ("superuser", Db.SuperuserConnectionString),
                     ("coffer_service", Db.ServiceConnectionString),
                     ("coffer_app", Db.AppConnectionString),
                 })
        {
            var template = TestDatabaseCluster.For(connectionString, TestDatabaseCluster.TemplateName);
            try
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var connection = new NpgsqlConnection(template);
                    await connection.OpenAsync();
                });
                Assert.True(ex.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState,
                    $"{role}: expected 'not currently accepting connections', got {ex.SqlState} {ex.MessageText}");
            }
            finally
            {
                TestDatabaseCluster.ClearPool(template);
            }
        }

        var facts = await ScalarsAsync(Db.SuperuserConnectionString,
            "SELECT datistemplate::text, datallowconn::text, pg_get_userbyid(datdba) FROM pg_database WHERE datname = @t",
            ("t", TestDatabaseCluster.TemplateName));
        Assert.True(facts[0] == "true", "the template is not marked IS_TEMPLATE");
        Assert.True(facts[1] == "false", "the template allows connections");
        Assert.Equal(TestDatabaseCluster.FixtureRole, facts[2]);
    }

    [Fact]
    public async Task No_role_a_test_runs_as_can_unlock_the_template()
    {
        // Unlocking takes the template's owner (or a superuser), and the fixture's
        // superuser role owns it, so the service role's CREATEDB is not enough.
        await using var service = new NpgsqlConnection(Db.ServiceConnectionString);
        await service.OpenAsync();
        try
        {
            foreach (var ddl in new[]
                     {
                         $"ALTER DATABASE {TestDatabaseCluster.TemplateName} ALLOW_CONNECTIONS true",
                         $"ALTER DATABASE {TestDatabaseCluster.TemplateName} IS_TEMPLATE false",
                         $"ALTER DATABASE {TestDatabaseCluster.TemplateName} OWNER TO coffer_service",
                     })
            {
                await using var command = new NpgsqlCommand(ddl, service);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege,
                    $"'{ddl}' as coffer_service: expected a privilege refusal, got {ex.SqlState} {ex.MessageText}");
            }
        }
        finally
        {
            // Put it back should a refusal above not have happened.
            await RelockTemplateAsync();
        }
    }

    [Fact]
    public async Task A_change_to_the_template_or_the_roles_fails_the_cluster_check()
    {
        var cluster = _fixture.Cluster;
        // Anchor: nothing has changed.
        await cluster.CheckClusterStateAsync();

        foreach (var (change, expected) in new[]
                 {
                     ($"ALTER DATABASE {TestDatabaseCluster.TemplateName} ALLOW_CONNECTIONS true",
                      $"database {TestDatabaseCluster.TemplateName} owner={TestDatabaseCluster.FixtureRole} istemplate=true allowconn=true"),
                     ($"ALTER DATABASE {TestDatabaseCluster.TemplateName} OWNER TO coffer_service",
                      $"database {TestDatabaseCluster.TemplateName} owner=coffer_service"),
                     ("GRANT CONNECT ON DATABASE template1 TO coffer_app",
                      "database template1"),
                     ("ALTER ROLE coffer_service SET work_mem = '7MB'",
                      "setting coffer_service in *: work_mem=7MB"),
                     ("ALTER ROLE coffer_app NOINHERIT",
                      "role coffer_app super=false inherit=false"),
                 })
        {
            try
            {
                await ExecuteAsSuperuserAsync(change);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => cluster.CheckClusterStateAsync());
                Assert.True(ex.Message.Contains("changed to: " + expected, StringComparison.Ordinal),
                    $"'{change}' should report 'changed to: {expected}'; got: {ex.Message}");
            }
            finally
            {
                await ExecuteAsSuperuserAsync(
                    "ALTER ROLE coffer_service RESET work_mem; ALTER ROLE coffer_app INHERIT; "
                    + "REVOKE CONNECT ON DATABASE template1 FROM coffer_app");
                await RelockTemplateAsync();
            }
            await cluster.CheckClusterStateAsync();
        }
    }

    [Fact]
    public async Task The_maintenance_database_holds_nothing_a_test_could_share()
    {
        // The request role cannot open it.
        await AssertConnectRefusedAsync(Db.AppConnectionString, TestDatabaseCluster.MaintenanceDatabase);

        // The service role connects (production's migration runner does).
        var serviceMaintenance = TestDatabaseCluster.For(Db.ServiceConnectionString, TestDatabaseCluster.MaintenanceDatabase);
        await using var service = new NpgsqlConnection(serviceMaintenance);
        await service.OpenAsync();

        // Refused by the server's own default (Postgres 15+ grants no CREATE on public,
        // and only a database's owner may create a schema); these pin that default.
        // The fixture's REVOKEs are what the next two probes test: TEMPORARY, and the
        // large-object functions, which need no privilege on any schema.
        foreach (var sql in new[]
                 {
                     "CREATE TABLE public.shared_between_tests (id int)",
                     "CREATE SCHEMA shared_between_tests",
                     "CREATE TEMP TABLE shared_between_tests (id int)",
                     "SELECT lo_create(0)",
                     "SELECT lo_from_bytea(0, 'shared'::bytea)",
                 })
        {
            await using var command = new NpgsqlCommand(sql, service);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege,
                $"'{sql}': expected a privilege refusal, got {ex.SqlState} {ex.MessageText}");
        }

        // Before the pools are cleared: idle there is the migration runner's pool;
        // working there is caught.
        await Db.CheckSessionsAsync();
        await using (var transaction = await service.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand("SELECT 1", service, transaction))
                await command.ExecuteScalarAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.CheckSessionsAsync());
            Assert.Contains($"still working in the maintenance database: pid {service.ProcessID} coffer_service@postgres", ex.Message);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task A_connection_nobody_returned_to_the_maintenance_database_fails_once_the_pools_are_cleared()
    {
        var cluster = _fixture.Cluster;
        var serviceMaintenance = TestDatabaseCluster.For(Db.ServiceConnectionString, TestDatabaseCluster.MaintenanceDatabase);

        // Anchor: a connection returned to its pool is closed by clearing the pools.
        await using (var pooled = new NpgsqlConnection(serviceMaintenance))
            await pooled.OpenAsync();
        NpgsqlConnection.ClearAllPools();
        await cluster.CheckMaintenanceDatabaseAsync();

        // One still checked out survives the clear, idle or not, and so does whatever
        // it holds. Claiming the fixture's application name does not hide it.
        var spoofed = new NpgsqlConnectionStringBuilder(serviceMaintenance)
        {
            ApplicationName = TestDatabaseCluster.FixtureApplicationName,
        }.ConnectionString;
        foreach (var connectionString in new[] { serviceMaintenance, spoofed })
        {
            var held = new NpgsqlConnection(connectionString);
            await held.OpenAsync();
            var pid = held.ProcessID;
            try
            {
                NpgsqlConnection.ClearAllPools();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => cluster.CheckMaintenanceDatabaseAsync(PlantedSettle));
                Assert.Contains("still in the maintenance database after every pool was cleared", ex.Message);
                Assert.Contains($"pid {pid} coffer_service@postgres", ex.Message);
            }
            finally
            {
                await held.DisposeAsync();
                NpgsqlConnection.ClearAllPools();
            }
            await WaitForBackendToExitAsync(pid);
        }
        await cluster.CheckMaintenanceDatabaseAsync();
    }

    [Fact]
    public async Task A_large_object_in_the_maintenance_database_fails_the_check()
    {
        var cluster = _fixture.Cluster;
        await cluster.CheckMaintenanceDatabaseAsync();

        // A superuser is not stopped by the REVOKE; the check is what catches it.
        var superMaintenance = TestDatabaseCluster.For(Db.SuperuserConnectionString, TestDatabaseCluster.MaintenanceDatabase);
        uint oid;
        await using (var super = new NpgsqlConnection(superMaintenance))
        {
            await super.OpenAsync();
            await using var create = new NpgsqlCommand("SELECT lo_create(0)", super);
            oid = (uint)(await create.ExecuteScalarAsync())!;
        }
        try
        {
            TestDatabaseCluster.ClearPool(superMaintenance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => cluster.CheckMaintenanceDatabaseAsync());
            Assert.Contains($"large object in the maintenance database: {oid} owned by", ex.Message);
        }
        finally
        {
            await using (var super = new NpgsqlConnection(superMaintenance))
            {
                await super.OpenAsync();
                await using var unlink = new NpgsqlCommand($"SELECT lo_unlink({oid})", super);
                await unlink.ExecuteNonQueryAsync();
            }
            TestDatabaseCluster.ClearPool(superMaintenance);
        }
        await cluster.CheckMaintenanceDatabaseAsync();
    }

    [Fact]
    public async Task Template1_is_closed_to_every_role_a_test_runs_as()
    {
        // What a CREATE DATABASE without TEMPLATE copies; nothing may be left in it.
        await AssertConnectRefusedAsync(Db.AppConnectionString, "template1");
        await AssertConnectRefusedAsync(Db.ServiceConnectionString, "template1");
    }

    [Fact]
    public async Task An_empty_database_is_made_as_a_fresh_install_s_database_is()
    {
        var empty = Db.EmptyDatabaseConnectionString("fresh_install");
        var facts = await ScalarsAsync(empty, """
            SELECT pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname = current_database())),
                   (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = 'public')::text,
                   (SELECT string_agg(extname, ',' ORDER BY extname) FROM pg_extension)
            """);
        Assert.Equal("coffer_service", facts[0]);
        Assert.Equal("0", facts[1]);
        Assert.Equal("pg_trgm,pgcrypto,plpgsql", facts[2]);
    }

    [Fact]
    public async Task A_connection_into_an_earlier_test_s_released_database_fails_the_check_and_names_it()
    {
        var cluster = _fixture.Cluster;
        var earlier = TestDatabaseCluster.NewName();
        const string earlierTest = "an earlier test (planted)";
        var earlierService = TestDatabaseCluster.For(Db.ServiceConnectionString, earlier);
        int latePid;
        using (cluster.HoldDrops())
        {
            // The earlier test as it ends: a pooled connection, its pools cleared, its
            // database released and waiting to be dropped.
            await cluster.CloneTemplateAsync(earlier, earlierTest);
            await using (var own = new NpgsqlConnection(earlierService))
                await own.OpenAsync();
            TestDatabaseCluster.ClearPool(earlierService);
            await cluster.ReleaseAsync(earlier);

            // Anchor: waiting to be dropped is not a leak.
            await Db.CheckSessionsAsync();

            // This test connects into it.
            var late = new NpgsqlConnection(earlierService);
            await late.OpenAsync();
            latePid = late.ProcessID;
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.CheckSessionsAsync());
                Assert.Contains($"connected to {earlier} after its test ({earlierTest}) released it", ex.Message);
                Assert.Contains($"pid {latePid} coffer_service@{earlier}", ex.Message);
            }
            finally
            {
                await late.DisposeAsync();
                TestDatabaseCluster.ClearPool(earlierService);
            }
        }
        await WaitForBackendToExitAsync(latePid);
        await Db.CheckSessionsAsync();
    }

    [Fact]
    public async Task A_connection_an_earlier_test_never_returned_fails_the_check_once_the_settle_runs_out()
    {
        var cluster = _fixture.Cluster;
        var earlier = TestDatabaseCluster.NewName();
        const string earlierTest = "an earlier test (planted)";
        var earlierService = TestDatabaseCluster.For(Db.ServiceConnectionString, earlier);
        int heldPid;
        using (cluster.HoldDrops())
        {
            await cluster.CloneTemplateAsync(earlier, earlierTest);
            var held = new NpgsqlConnection(earlierService);
            await held.OpenAsync();
            heldPid = held.ProcessID;
            try
            {
                TestDatabaseCluster.ClearPool(earlierService);
                await cluster.ReleaseAsync(earlier);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.CheckSessionsAsync(PlantedSettle));
                Assert.Contains($"still connected to {earlier}", ex.Message);
                Assert.Contains($"from before its test ({earlierTest}) released it", ex.Message);
                Assert.Contains($"pid {heldPid} coffer_service@{earlier}", ex.Message);
            }
            finally
            {
                await held.DisposeAsync();
                TestDatabaseCluster.ClearPool(earlierService);
            }
        }
        await WaitForBackendToExitAsync(heldPid);
        await Db.CheckSessionsAsync();
    }

    [Fact]
    public async Task A_session_left_in_a_database_no_test_owns_fails_the_check_and_names_it()
    {
        var stray = "stray_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsServiceAsync($"CREATE DATABASE {stray}");
        var strayService = TestDatabaseCluster.For(Db.ServiceConnectionString, stray);
        try
        {
            await using (var held = new NpgsqlConnection(strayService))
            {
                await held.OpenAsync();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.CheckSessionsAsync());
                Assert.Contains($"in a database that is not this test's: pid {held.ProcessID} coffer_service@{stray}", ex.Message);
            }
        }
        finally
        {
            TestDatabaseCluster.ClearPool(strayService);
            await ExecuteAsServiceAsync($"DROP DATABASE {stray} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task A_transaction_left_open_in_its_own_database_fails_the_check_and_names_it()
    {
        await using var held = new NpgsqlConnection(Db.ServiceConnectionString);
        await held.OpenAsync();
        // Anchor: an idle connection in its own database is expected (pools hold them).
        await Db.CheckSessionsAsync();

        await using var transaction = await held.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT 1", held, transaction))
            await command.ExecuteScalarAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Db.CheckSessionsAsync());
        Assert.Contains("still working in this test's own database", ex.Message);
        Assert.Contains($"pid {held.ProcessID} coffer_service@{Db.Name}", ex.Message);
        Assert.Contains("idle in transaction", ex.Message);

        await transaction.RollbackAsync();
        await Db.CheckSessionsAsync();
    }

    [Fact]
    public async Task A_database_nobody_accounts_for_is_reported_whatever_its_name()
    {
        var cluster = _fixture.Cluster;
        // Anchor: nothing is left over while this test runs.
        Assert.Empty(await cluster.FindLeftoversAsync());

        // The service role holds CREATEDB (restore needs it), so code under test can make
        // a database of any name, not only one shaped like a per-test database.
        var stray = "stray_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsServiceAsync($"CREATE DATABASE {stray}");
        try
        {
            Assert.Contains($"{stray} (from (no test recorded))", await cluster.FindLeftoversAsync());

            // At the end of the run a test that never released its database counts too,
            // named by the test that owned it.
            var atEnd = await cluster.FindLeftoversAsync(includeLive: true);
            Assert.Contains(atEnd, l => l.StartsWith(Db.Name + " (from ", StringComparison.Ordinal)
                                        && l.Contains(nameof(A_database_nobody_accounts_for_is_reported_whatever_its_name),
                                            StringComparison.Ordinal));
        }
        finally
        {
            await ExecuteAsServiceAsync($"DROP DATABASE {stray}");
        }

        Assert.Empty(await cluster.FindLeftoversAsync());
    }

    [Fact]
    public async Task A_failed_setup_releases_its_database_and_leaves_the_next_test_clean()
    {
        // Nothing of this test's own is connected yet, so the failing setup's end
        // check sees only its own sessions.
        var failing = new SetupThrows(_fixture, Output);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(failing.InitializeAsync);
        var passed = false;
        try
        {
            Assert.Equal(SetupThrows.Failure, ex.Message);
            Assert.NotNull(failing.DatabaseName);

            // Released: not reported even counting running tests, and its pooled
            // connection is gone, so this test's own check passes.
            Assert.DoesNotContain(await _fixture.Cluster.FindLeftoversAsync(includeLive: true),
                l => l.StartsWith(failing.DatabaseName + " ", StringComparison.Ordinal));
            await Db.CheckSessionsAsync();
            passed = true;
        }
        finally
        {
            // What xunit would not do; a no-op once the setup's failure released it.
            // When an assertion above failed, its message is the one to report.
            try
            {
                await failing.DisposeAsync();
            }
            catch (InvalidOperationException) when (!passed)
            {
            }
        }
    }

    [Fact]
    public async Task A_clone_is_the_settled_template_owned_and_bound_as_production()
    {
        await using (var super = new NpgsqlConnection(Db.SuperuserConnectionString))
        {
            await super.OpenAsync();
            Assert.Equal(_fixture.TemplateFingerprint, await TestDatabaseCluster.FingerprintAsync(super));
        }

        var facts = await ScalarsAsync(Db.SuperuserConnectionString, """
            SELECT current_database(),
                   pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname = current_database())),
                   (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','S')
                       AND pg_get_userbyid(c.relowner) <> 'coffer_service')::text,
                   (SELECT count(*) FROM public.__schema_migrations)::text,
                   (SELECT count(*) FROM public.oidc_server_keys)::text,
                   (SELECT count(*) FROM public.system_events WHERE event_key = 'backup.none')::text,
                   (SELECT count(*) > 0 FROM pg_stats WHERE schemaname = 'public')::text
            """);
        // The journal holds a row for every script and for every C# step between them.
        var migrations = Directory.EnumerateFiles(
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), "*.sql").Count()
            + MigrationRunner.CSharpSteps.Count;

        Assert.Equal(Db.Name, facts[0]);
        Assert.Equal("coffer_service", facts[1]);
        Assert.Equal("0", facts[2]);
        Assert.Equal(migrations.ToString(CultureInfo.InvariantCulture), facts[3]);
        Assert.Equal("1", facts[4]);
        Assert.Equal("1", facts[5]);
        Assert.Equal("true", facts[6]);

        // The request role on a clone: RLS binds it, and it runs with the role's settings.
        var appFacts = await ScalarsAsync(Db.AppConnectionString,
            "SELECT current_setting('jit'), (SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user)::text");
        Assert.Equal("off", appFacts[0]);
        Assert.Equal("false", appFacts[1]);
    }

    [Fact]
    public async Task A_host_booted_on_a_clone_writes_nothing_at_boot()
    {
        var before = await FingerprintAsync();
        var backups = Directory.CreateTempSubdirectory("coffer-boot-writes-").FullName;
        try
        {
            await using (var factory = new ApiFactory(Db).WithConfig("Api:Backup:Directory", backups))
            {
                using var client = factory.CreateClient();
                using var ok = await client.GetAsync("/api/ledgers");
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

                // The scheduler's first tick runs the backup-age monitor at boot, on a
                // background thread. Run it here too, so the assertion does not depend on
                // whether that thread got there before the host stopped.
                using var scope = factory.Services.CreateScope();
                Assert.Null(await scope.ServiceProvider.GetRequiredService<BackupAgeMonitor>().CheckAsync());
            }
        }
        finally
        {
            try { Directory.Delete(backups, recursive: true); } catch (IOException) { }
        }

        var after = await FingerprintAsync();
        var changed = before.Keys.Where(t => before[t] != after.GetValueOrDefault(t)).ToList();
        Assert.True(changed.Count == 0, "boot wrote to: " + string.Join(", ", changed));
    }

    [Fact]
    public void Clones_are_refused_once_the_template_s_first_backup_alert_stops_throttling()
    {
        var at = _fixture.FirstBootAlertAt;
        var limit = BackupAgeMonitor.RepeatAfter - PostgresFixture.FirstBootAlertMargin;

        // Anchors: the fixture's own alert is current, and so is one just inside the bound.
        PostgresFixture.EnsureFirstBootAlertStillThrottles(at, TimeProvider.System.GetUtcNow().UtcDateTime);
        PostgresFixture.EnsureFirstBootAlertStillThrottles(at, at + limit - TimeSpan.FromMinutes(1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresFixture.EnsureFirstBootAlertStillThrottles(at, at + limit));
        Assert.Contains("a host booted on a clone would now write a new alert at boot", ex.Message);
    }

    /// <summary>A test class whose setup fails after its database exists and holds a
    /// pooled connection. Not a test: it has no test methods, and is driven by
    /// <see cref="A_failed_setup_releases_its_database_and_leaves_the_next_test_clean"/>.</summary>
    private sealed class SetupThrows : DatabaseTest
    {
        public const string Failure = "setup failed (planted)";

        public SetupThrows(PostgresFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

        public string? DatabaseName { get; private set; }

        protected override async Task OnInitializedAsync()
        {
            DatabaseName = Db.Name;
            await using (var connection = new NpgsqlConnection(Db.ServiceConnectionString))
                await connection.OpenAsync();
            throw new InvalidOperationException(Failure);
        }
    }

    private async Task<Dictionary<string, string>> FingerprintAsync()
    {
        await using var super = new NpgsqlConnection(Db.SuperuserConnectionString);
        await super.OpenAsync();
        return await TestDatabaseCluster.FingerprintAsync(super);
    }

    private Task ExecuteAsSuperuserAsync(string sql) => ExecuteAsync(Db.SuperuserConnectionString, sql);

    private Task ExecuteAsServiceAsync(string sql) => ExecuteAsync(Db.ServiceConnectionString, sql);

    private Task RelockTemplateAsync() => ExecuteAsSuperuserAsync(
        $"ALTER DATABASE {TestDatabaseCluster.TemplateName} OWNER TO {TestDatabaseCluster.FixtureRole}; "
        + $"ALTER DATABASE {TestDatabaseCluster.TemplateName} IS_TEMPLATE true ALLOW_CONNECTIONS false");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertConnectRefusedAsync(string connectionString, string database)
    {
        var target = TestDatabaseCluster.For(connectionString, database);
        try
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var connection = new NpgsqlConnection(target);
                await connection.OpenAsync();
            });
            Assert.True(refused.SqlState == PostgresErrorCodes.InsufficientPrivilege,
                $"{new NpgsqlConnectionStringBuilder(target).Username}@{database}: expected a CONNECT refusal, "
                + $"got {refused.SqlState} {refused.MessageText}");
        }
        finally
        {
            TestDatabaseCluster.ClearPool(target);
        }
    }

    /// <summary>Wait until a backend this test closed has left the server, so the
    /// checks that follow see the state the test means them to.</summary>
    private async Task WaitForBackendToExitAsync(int pid)
    {
        await using var super = new NpgsqlConnection(Db.SuperuserConnectionString);
        await super.OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = @p", super);
            command.Parameters.AddWithValue("p", pid);
            if ((long)(await command.ExecuteScalarAsync())! == 0) return;
            await Task.Delay(25);
        }
        throw new XunitException($"backend {pid} did not exit");
    }

    private static async Task<string[]> ScalarsAsync(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = new string[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
        return values;
    }
}


/// <summary>
/// Two tests that each require the template's exact state and then write, run in name
/// order. Whichever runs second would see the first one's rows if the two shared a
/// database.
/// </summary>
[Collection(ApiCollection.Name)]
[TestCaseOrderer(OrderIndependence.ForwardOrderer, OrderIndependence.Assembly)]
public sealed class StartsFromTemplateForwardTests : DatabaseTest
{
    private readonly PostgresFixture _fixture;

    public StartsFromTemplateForwardTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output) => _fixture = fixture;

    [Fact]
    public Task A_runs_first() => OrderIndependence.StartsFromTemplateThenWritesAsync(Db, _fixture);

    [Fact]
    public Task B_runs_second() => OrderIndependence.StartsFromTemplateThenWritesAsync(Db, _fixture);
}

/// <summary>The same pair as <see cref="StartsFromTemplateForwardTests"/>, in the other
/// order.</summary>
[Collection(ApiCollection.Name)]
[TestCaseOrderer(OrderIndependence.ReverseOrderer, OrderIndependence.Assembly)]
public sealed class StartsFromTemplateReverseTests : DatabaseTest
{
    private readonly PostgresFixture _fixture;

    public StartsFromTemplateReverseTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output) => _fixture = fixture;

    [Fact]
    public Task A_runs_second() => OrderIndependence.StartsFromTemplateThenWritesAsync(Db, _fixture);

    [Fact]
    public Task B_runs_first() => OrderIndependence.StartsFromTemplateThenWritesAsync(Db, _fixture);
}

internal static class OrderIndependence
{
    public const string Assembly = "Api.Tests";
    public const string ForwardOrderer = "Coffer.Api.Tests.Integration.Infra.ByMethodNameOrderer";
    public const string ReverseOrderer = "Coffer.Api.Tests.Integration.Infra.ByMethodNameDescendingOrderer";

    /// <summary>Require the template's exact content, then leave rows behind — a
    /// ledger, a user and a deployment-scope event, the kinds a shared database would
    /// carry from one test into the next.</summary>
    public static async Task StartsFromTemplateThenWritesAsync(TestDatabase db, PostgresFixture fixture)
    {
        await using var super = new NpgsqlConnection(db.SuperuserConnectionString);
        await super.OpenAsync();
        var fingerprint = await TestDatabaseCluster.FingerprintAsync(super);
        var differs = fixture.TemplateFingerprint.Keys
            .Where(t => fixture.TemplateFingerprint[t] != fingerprint.GetValueOrDefault(t)).ToList();
        Assert.True(differs.Count == 0, "starts with rows the template does not have, in: " + string.Join(", ", differs));

        await using var write = new NpgsqlCommand(
            """
            INSERT INTO ledgers (id, name) VALUES (gen_random_uuid(), 'order-independence');
            INSERT INTO users (id, display_name, username, created_by)
                 VALUES (gen_random_uuid(), 'order-independence', 'order-independence-' || md5(random()::text), 'integration-test');
            INSERT INTO system_events (severity, topic, event_key, summary)
                 VALUES ('info', 'backup', 'order.independence', 'left behind');
            """, super);
        await write.ExecuteNonQueryAsync();
    }
}

/// <summary>Runs a class's tests in method-name order.</summary>
public sealed class ByMethodNameOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}

/// <summary>Runs a class's tests in reverse method-name order.</summary>
public sealed class ByMethodNameDescendingOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderByDescending(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}
