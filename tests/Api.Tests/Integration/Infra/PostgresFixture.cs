using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Backup;
using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Migrations;
using Coffer.Api.Notifications;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// One Postgres 16 container per test process, the two roles production's
/// <c>db/init/00-init-roles.sh</c> creates, and a migrated, settled template that every
/// test's own database is cloned from.
/// </summary>
/// <remarks>
/// <para><b>Once per process</b>, in <see cref="InitializeAsync"/>:</para>
/// <list type="number">
///   <item><description>Provision the roles cluster-wide
///   (<see cref="TestDatabaseCluster.ProvisionRolesAsync"/>): <c>coffer_service</c>
///   (BYPASSRLS, CREATEDB), <c>coffer_app</c> (no BYPASSRLS, <c>jit = off</c>, as
///   db/init sets it). Role settings are cluster-wide so every clone has them;
///   <c>RoleSettingsMirrorInitScriptTests</c> holds them equal to the script.</description></item>
///   <item><description>Leave nothing in the maintenance database (<c>postgres</c>) or
///   in <c>template1</c> a test could share: the request role cannot connect to either,
///   and the service role (which production's migration runner connects to
///   <c>postgres</c> with) can create nothing there.</description></item>
///   <item><description>Create <see cref="TestDatabaseCluster.TemplateName"/>, owned by
///   the fixture's superuser so no role a test runs as can unlock it, and migrate it as
///   <c>coffer_service</c> through the same DbUp runner production uses, so the service
///   role owns every table.</description></item>
///   <item><description>Settle the writes a host makes on its first boot, through the
///   app's own code, so a host booted on a clone writes nothing at boot: the OAuth key
///   pair (sealed under the fixture key every host runs with) and the backup-age
///   monitor's first alert, which throttles its repeats for
///   <see cref="BackupAgeMonitor.RepeatAfter"/> (see
///   <see cref="EnsureFirstBootAlertStillThrottles"/>).</description></item>
///   <item><description>ANALYZE, clear every pool, and lock it:
///   <c>IS_TEMPLATE true ALLOW_CONNECTIONS false</c>.</description></item>
/// </list>
/// <para><b>Once per test</b>, <see cref="CreateDatabaseAsync"/> clones the template
/// (<see cref="DatabaseTest"/> calls it). The fixture exposes no connection string and
/// no context: the only way to a database is a test's own <see cref="TestDatabase"/>.
/// </para>
/// <para><b>What stays shared</b> between the tests of one process: the container, the
/// roles and their settings, process environment variables, and anything a test changes
/// cluster-wide (which it must put back).</para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ServicePassword = "test-service-password";
    private const string AppPassword     = "test-app-password";

    /// <summary>Slack under <see cref="BackupAgeMonitor.RepeatAfter"/> for the length of
    /// one test and for the container's clock against the host's.</summary>
    internal static readonly TimeSpan FirstBootAlertMargin = TimeSpan.FromHours(1);

    private readonly IMessageSink _diagnostics;

    /// <param name="diagnostics">xunit's message sink, which the end-of-run cleanup
    /// failure is written to in full (<see cref="TestDatabaseCluster.DisposeAsync"/>).</param>
    public PostgresFixture(IMessageSink diagnostics) => _diagnostics = diagnostics;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase(TestDatabaseCluster.MaintenanceDatabase)
        .WithUsername("coffer")
        .WithPassword("coffer_api_test")
        // Headroom over the Postgres default of 100. The primary bound on connection
        // use is the per-pool MaxPoolSize in BuildConnectionString; this is slack
        // above that bounded footprint so a transient disposal overlap cannot trip
        // "53300: remaining connection slots are reserved for ... SUPERUSER".
        .WithCommand("-c", "max_connections=400")
        .Build();

    private TestDatabaseCluster? _cluster;
    private TestDatabaseCredentials? _credentials;

    /// <summary>The clone, check and drop machinery, for the tests that test it. It
    /// hands out no connection: its own sessions run as a role only it can log in as.</summary>
    internal TestDatabaseCluster Cluster =>
        _cluster ?? throw new InvalidOperationException("The fixture has not started.");

    /// <summary>
    /// The template's content, table by table, as it was locked: what every clone
    /// starts from. See <see cref="TestDatabaseCluster.FingerprintAsync"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> TemplateFingerprint => Cluster.TemplateFingerprint;

    /// <summary>When the template's first backup alert (<c>backup.none</c>) was written,
    /// by the server's clock.</summary>
    internal DateTime FirstBootAlertAt { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        var superuser = _container.GetConnectionString();
        _cluster = await TestDatabaseCluster.StartAsync(superuser, _diagnostics).ConfigureAwait(false);
        _credentials = new TestDatabaseCredentials(
            service: BuildConnectionString(superuser, TestDatabaseCluster.ServiceRole, ServicePassword),
            app: BuildConnectionString(superuser, TestDatabaseCluster.AppRole, AppPassword),
            superuser: BuildConnectionString(superuser, username: null, password: null));

        await _cluster.ProvisionRolesAsync(ServicePassword, AppPassword).ConfigureAwait(false);
        await BuildTemplateAsync(_cluster, _credentials).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        // Both end-of-run checks report, whichever fails.
        var failures = new List<Exception>();
        try
        {
            try { AssertEveryHostWasDisposed(); }
            catch (Exception e) { failures.Add(e); }
            try
            {
                if (_cluster is not null) await _cluster.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) { failures.Add(e); }
        }
        finally
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    /// <summary>
    /// Every <see cref="ApiFactory"/> the run built was disposed: one that was not keeps its
    /// host for the rest of the run.
    /// </summary>
    private static void AssertEveryHostWasDisposed()
    {
        var left = ApiFactory.NotDisposed;
        if (left.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"At the end of the run, {left.Count} test host(s) were never disposed; each keeps its "
                + "host for the rest of the run. Built by:\n  "
                + string.Join("\n  ", left.GroupBy(t => t).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} (x{g.Count()})")));
    }

    /// <summary>
    /// Clone the template into a new database for the test <paramref name="output"/>
    /// belongs to, and say so in that test's output.
    /// </summary>
    internal Task<TestDatabase> CreateDatabaseAsync(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return CreateDatabaseAsync(TestDatabaseCluster.TestNameOf(output), output.WriteLine);
    }

    /// <summary>
    /// Clone the template into a new database owned by <paramref name="owner"/>, for a class
    /// fixture that holds one database for every test of its class, and say so through
    /// <paramref name="log"/>.
    /// </summary>
    /// <remarks>The caller disposes it when its class ends, which runs the same end-of-test
    /// checks a test's own database gets. No test of that class may also be a
    /// <see cref="DatabaseTest"/>: its end would find the fixture's sessions in a database
    /// that is not its own.</remarks>
    internal async Task<TestDatabase> CreateDatabaseAsync(string owner, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var cluster = Cluster;
        EnsureFirstBootAlertStillThrottles(FirstBootAlertAt, TimeProvider.System.GetUtcNow().UtcDateTime);
        var name = TestDatabaseCluster.NewName();
        var ms = await cluster.CloneTemplateAsync(name, owner).ConfigureAwait(false);
        log(string.Create(CultureInfo.InvariantCulture, $"db {name} clone {ms:F0} ms"));
        return new TestDatabase(cluster, _credentials!, name, owner);
    }

    /// <summary>
    /// A <see cref="ServiceDbContextFactory"/> wired the way PRODUCTION wires it.
    /// </summary>
    /// <remarks>
    /// One place, so the test shape cannot drift from Program.cs. The four
    /// recompute interceptors are passed because production passes them: a factory
    /// built without them hands back a context that saves without recomputing, and
    /// a test asserting a recomputed balance over that is asserting the bug rather
    /// than the fix. Interceptors are cheap and stateless — each scans the
    /// ChangeTracker for its own surface and does nothing when it finds none — so
    /// callers that never touch legs pay an in-memory loop over their own entries.
    /// </remarks>
    public static ServiceDbContextFactory ServiceFactoryFor(
        IOptions<ApiOptions> options, params IInterceptor[] extra) =>
        new(options,
            new LegDerivedRecomputeInterceptor(
                NullLogger<LegDerivedRecomputeInterceptor>.Instance),
            new HoldingsRecomputeInterceptor(),
            new TradePriceFromLegInterceptor(),
            new LegNotesRecomputeInterceptor(),
            extra);

    /// <summary>
    /// The fixture's master key: 32 zero bytes, the same key <see cref="ApiFactory"/>
    /// pins for every host, non-secret by intent, so material wrapped in one place opens
    /// in another (ADR-0026).
    /// </summary>
    internal static LedgerKeyService FixtureKeys() => new(new MasterKey(new byte[32], id: "test"));

    /// <summary>
    /// Fail with a clear message when the template's first backup alert has aged past
    /// the monitor's throttle window, rather than let every host from then on write a
    /// new one at boot.
    /// </summary>
    /// <remarks>
    /// <para><b>The issue.</b> A host booted on a clone writes nothing at boot only
    /// because the template carries the backup-age monitor's <c>backup.none</c> alert,
    /// and the monitor stays quiet for <see cref="BackupAgeMonitor.RepeatAfter"/> after
    /// it. The row's time is the server's <c>now()</c> at template build, and the
    /// throttle compares it with the host's clock, so the guarantee holds for a bounded
    /// time after the process starts. The host's clock cannot be pinned for the monitor
    /// alone: it is the app's one <see cref="TimeProvider"/>.</para>
    /// <para><b>The handling.</b> Every clone checks the bound first, with
    /// <see cref="FirstBootAlertMargin"/> of slack, and refuses past it.</para>
    /// </remarks>
    internal static void EnsureFirstBootAlertStillThrottles(DateTime alertAtUtc, DateTime nowUtc)
    {
        var age = nowUtc - alertAtUtc;
        if (age >= BackupAgeMonitor.RepeatAfter - FirstBootAlertMargin)
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "The template's first backup alert is {0:F1} h old, within {1:F0} h of BackupAgeMonitor.RepeatAfter "
                + "({2:F0} h): a host booted on a clone would now write a new alert at boot. Start a new test "
                + "process, which builds a new template.",
                age.TotalHours, FirstBootAlertMargin.TotalHours, BackupAgeMonitor.RepeatAfter.TotalHours));
    }

    private async Task BuildTemplateAsync(TestDatabaseCluster cluster, TestDatabaseCredentials credentials)
    {
        var template = TestDatabaseCluster.TemplateName;
        await cluster.CreateTemplateAsync().ConfigureAwait(false);

        var serviceTemplate = credentials.Service(template);
        var appTemplate = credentials.App(template);
        try
        {
            // As coffer_service, so DbUp-created tables are owned by it: the precondition
            // for ENABLE ROW LEVEL SECURITY in migration 017.
            MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(serviceTemplate),
                MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), NullLogger.Instance);

            await SettleFirstBootAsync(serviceTemplate, appTemplate).ConfigureAwait(false);

            await using (var connection = new NpgsqlConnection(serviceTemplate))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await using var alert = new NpgsqlCommand(
                    "SELECT max(occurred_at) FROM system_events WHERE event_key = 'backup.none'", connection);
                FirstBootAlertAt = (DateTime)(await alert.ExecuteScalarAsync().ConfigureAwait(false))!;
            }

            await cluster.SealTemplateAsync().ConfigureAwait(false);
        }
        finally
        {
            await cluster.LockTemplateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The writes a host makes the first time it boots on a database, made once here
    /// through the app's own code, so no clone's host makes them.
    /// </summary>
    private static async Task SettleFirstBootAsync(string service, string app)
    {
        var factory = ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = app,
            ServiceConnectionString = service,
        }));

        // The OAuth server key pair, sealed under the fixture key every host runs with,
        // through the loader boot calls. A legacy directory of its own, empty, so it
        // generates the pair rather than importing anything.
        var legacy = Directory.CreateTempSubdirectory("coffer-template-oauth-").FullName;
        var backups = Directory.CreateTempSubdirectory("coffer-template-backups-").FullName;
        try
        {
            var material = new OpenIddictKeyMaterial(
                factory, TimeProvider.System, NullLogger<OpenIddictKeyMaterial>.Instance);
            if (!await material.LoadAsync(FixtureKeys(), legacy).ConfigureAwait(false))
                throw new InvalidOperationException("The OAuth key pair did not load into the template.");

            // The backup-age monitor's first alert ("no backup exists"). The scheduler
            // runs the monitor on every tick, the first one at boot, and the alert
            // throttles its own repeats on this row; without it every host would write
            // one.
            var publisher = new NotificationPublisher(
                factory, FixtureKeys(), Array.Empty<INotificationSubscriber>(),
                NullLogger<NotificationPublisher>.Instance, TimeProvider.System);
            var store = new BackupStore(backups, NullLogger<BackupStore>.Instance,
                partSizeBytes: 1024 * 1024, TimeProvider.System);
            await new BackupAgeMonitor(store, publisher, TimeProvider.System).CheckAsync().ConfigureAwait(false);
        }
        finally
        {
            TestDatabaseCluster.ClearPool(service);
            TestDatabaseCluster.ClearPool(app);
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
            try { Directory.Delete(backups, recursive: true); } catch (IOException) { }
        }
    }

    private static string BuildConnectionString(string superuserConnectionString, string? username, string? password)
    {
        var builder = new NpgsqlConnectionStringBuilder(superuserConnectionString)
        {
            // Bound each Npgsql pool so total server connections stay well under the
            // container's max_connections however many tests a process runs. Npgsql
            // pools are process-global, keyed on the connection string, and default to
            // 100; a short idle lifetime keeps the footprint flat. Tests run one at a
            // time in one collection, so a modest ceiling is ample.
            MaxPoolSize = 40,
            ConnectionIdleLifetime = 10,

            // Mirrors production's DbSessionTimeZone, which pins this on the connection
            // string rather than issuing a SET. It has to be the same MECHANISM and not
            // merely the same value: a fixture that reached UTC another way would let a
            // regression in the real pin pass the whole suite. Testcontainers' postgres
            // image defaults to UTC, so without this the tests would agree by luck.
            Timezone = Coffer.Api.Configuration.DbSessionTimeZone.Zone,
        };
        if (username is not null)
        {
            builder.Username = username;
            builder.Password = password;
        }
        return builder.ConnectionString;
    }
}

/// <summary>
/// xUnit collection that shares a single <see cref="PostgresFixture"/> (one container,
/// one template) across every API integration test class of a process. Its members run
/// one at a time; each test still gets its own database (<see cref="DatabaseTest"/>).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "api-postgres";
}

/// <summary>
/// Test-only <see cref="Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor"/>
/// that pins <c>app.user_id</c> to a fixed value on connection open.
/// Mirrors the production <c>AppUserDbConnectionInterceptor</c> but
/// without an <c>ICurrentUserAccessor</c> dependency — handy for
/// tests that need to drive RLS directly.
/// </summary>
internal sealed class FixedUserConnectionInterceptor :
    Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    private readonly Guid _userId;

    public FixedUserConnectionInterceptor(Guid userId)
    {
        _userId = userId;
    }

    public override async Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = (NpgsqlCommand)connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.user_id', $1, false)";
        cmd.Parameters.AddWithValue(_userId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
