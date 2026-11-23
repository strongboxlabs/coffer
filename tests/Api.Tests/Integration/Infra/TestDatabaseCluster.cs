using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Npgsql;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The per-test databases of one test process: each cloned from a locked template,
/// checked when its test ends, and dropped in the background.
/// </summary>
/// <remarks>
/// <para><b>The issue.</b> Tests that share one database see whatever earlier tests
/// left in it, so a result depends on what ran before, and a per-test timing measures
/// the accumulated rows of the whole run. Unique ids per test separate the rows
/// logically; they do not isolate global tables, planner statistics, or anything a
/// test counts.</para>
/// <para><b>The handling.</b> Each test gets its own database, created with
/// <c>CREATE DATABASE ... TEMPLATE</c> from a template the fixture migrated and settled
/// once, and dropped after the test. A clone is a file-level copy, so it starts with
/// exactly the template's schema, rows and statistics.</para>
/// <para><b>What a test's end checks</b> (<see cref="EndTestAsync"/>), failing the
/// test: a session of its in a database that is not its own; a session in its own
/// still working (running a statement or holding a transaction open); any session
/// left in the maintenance database once every pool is cleared (a connection nobody
/// returned, which can hold an advisory lock or a LISTEN into the next test); a
/// connection into an earlier test's database after that test released it; a large
/// object in the maintenance database; a database nobody accounts for; a change to the
/// roles, their settings, or the locked databases; and a failed drop of an earlier
/// test's database. The drop itself runs in the background, and
/// <see cref="DisposeAsync"/> waits for every drop and runs the same checks once more,
/// plus the template's content, failing the run on any of them.</para>
/// <para><b>The fixture's own sessions</b> run as <see cref="FixtureRole"/>, a
/// superuser whose password exists only inside this class, and the session checks
/// leave out that role and nothing else. No member hands that role's connection out;
/// a test that needs a superuser gets the container's, whose sessions are checked
/// like any other.</para>
/// <para>Depends on Npgsql and xunit only: the importer test project links this file
/// rather than carrying a copy that could drift.</para>
/// </remarks>
public sealed partial class TestDatabaseCluster : IAsyncDisposable
{
    /// <summary>The migrated, settled database every test database is cloned from.</summary>
    public const string TemplateName = "coffer_template";

    /// <summary>The server's maintenance database, where database-level DDL is issued.
    /// Nothing a test could share may live there.</summary>
    public const string MaintenanceDatabase = "postgres";

    /// <summary>The <c>application_name</c> of the fixture's own sessions, for reading
    /// <c>pg_stat_activity</c>. The checks key on <see cref="FixtureRole"/>, not on
    /// this, since any session can claim an application name.</summary>
    public const string FixtureApplicationName = "coffer-fixture";

    /// <summary>The role the fixture's own sessions run as: a superuser created at
    /// start with a password only this class holds. The session checks leave out its
    /// sessions and no others.</summary>
    public const string FixtureRole = "coffer_fixture";

    /// <summary>Every per-test database's name starts with this.</summary>
    public const string ClonePrefix = "t_";

    /// <summary>The role that owns the schema and, as in production, the database.</summary>
    public const string ServiceRole = "coffer_service";

    /// <summary>The request role (RLS applies).</summary>
    public const string AppRole = "coffer_app";

    /// <summary>Owner of every clone and every empty database, as production's database
    /// is owned by the service role (db/init: <c>ALTER DATABASE ... OWNER TO
    /// coffer_service</c>).</summary>
    public const string CloneOwner = ServiceRole;

    /// <summary>
    /// Schema privileges and the two extensions, per database, as production's
    /// db/init applies them. Run as the fixture on a database that has just been
    /// created: the template, and every empty database a test asks for.
    /// </summary>
    /// <remarks>Extensions need a superuser (or the database's owner), so they are
    /// created here and migration 001's CREATE EXTENSION IF NOT EXISTS no-ops, as it
    /// does in production, where they are install-managed.</remarks>
    public const string PerDatabaseGrantsSql = """
        GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
        GRANT USAGE          ON SCHEMA public TO coffer_app;
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        """;

    /// <summary>How long a session that ended at the server is given to leave
    /// <c>pg_stat_activity</c>: a cleared pool closes its sockets at once, and the
    /// backend exits a moment later.</summary>
    internal static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(2);

    private static int _sequence;

    private readonly string _admin;
    private readonly Channel<string> _drops =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _dropGate = new(1, 1);
    private readonly Task _dropLoop;
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _dropFailures = new();
    private IReadOnlyList<string>? _settledState;
    private readonly IMessageSink? _diagnostics;

    private TestDatabaseCluster(string admin, IMessageSink? diagnostics)
    {
        _admin = admin;
        _diagnostics = diagnostics;
        _dropLoop = Task.Run(DrainDropsAsync);
    }

    /// <summary>
    /// Create <see cref="FixtureRole"/> with a fresh random password and return a
    /// cluster whose own sessions all run as it.
    /// </summary>
    /// <param name="containerSuperuserConnectionString">The container superuser, any
    /// database. Used once, unpooled, to create the role.</param>
    /// <param name="diagnostics">The collection fixture's message sink, which the
    /// end-of-run failure is also written to (see <see cref="DisposeAsync"/>).</param>
    public static async Task<TestDatabaseCluster> StartAsync(
        string containerSuperuserConnectionString, IMessageSink? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerSuperuserConnectionString);
        var password = Guid.NewGuid().ToString("N");
        var bootstrap = new NpgsqlConnectionStringBuilder(containerSuperuserConnectionString)
        {
            Database = MaintenanceDatabase,
            Pooling = false,
        }.ConnectionString;
        await using (var connection = new NpgsqlConnection(bootstrap))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var create = new NpgsqlCommand(
                $"CREATE ROLE {FixtureRole} LOGIN SUPERUSER PASSWORD '{password}'", connection);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new TestDatabaseCluster(new NpgsqlConnectionStringBuilder(containerSuperuserConnectionString)
        {
            Database = MaintenanceDatabase,
            Username = FixtureRole,
            Password = password,
            ApplicationName = FixtureApplicationName,
        }.ConnectionString, diagnostics);
    }

    // ------------------------------------------------------------------------------
    // setup, once per process
    // ------------------------------------------------------------------------------

    /// <summary>
    /// The two roles production's <c>db/init/00-init-roles.sh</c> creates, with its
    /// cluster-wide settings, and the lockdown of the databases no test owns.
    /// </summary>
    public Task ProvisionRolesAsync(string servicePassword, string appPassword) =>
        ExecuteAdminAsync($@"
DO $Init$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ServiceRole}') THEN
        CREATE ROLE {ServiceRole} LOGIN BYPASSRLS PASSWORD '{servicePassword}';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
        CREATE ROLE {AppRole} LOGIN NOBYPASSRLS PASSWORD '{appPassword}';
    END IF;
END $Init$;

-- The request role's planner settings, as db/init sets them. Cluster-wide, as there:
-- a setting scoped to one database would not reach the clones, and every
-- request-path test and every stress timing would pay a JIT compile production never
-- pays. RoleSettingsMirrorInitScriptTests holds the two equal.
ALTER ROLE {AppRole} SET jit = off;

-- Restore-by-swap privilege (migration 246), as db/init grants it. The other half,
-- owning the live database, is every clone's OWNER clause.
ALTER ROLE {ServiceRole} CREATEDB;

-- Nothing a test could share may live in the maintenance database. The request role
-- cannot open it at all. The service role keeps CONNECT because production's
-- migration runner checks from there that its database exists (and a restore issues
-- its database-level statements from there), but it cannot create anything: no
-- CREATE on public (the Postgres 15+ default), no TEMPORARY, since a temp table in a
-- pooled session would outlive the test that made it, and no large objects, which
-- need no privilege on any schema and would outlive every test.
REVOKE CONNECT, TEMPORARY ON DATABASE {MaintenanceDatabase} FROM PUBLIC;
GRANT CONNECT ON DATABASE {MaintenanceDatabase} TO {ServiceRole};
REVOKE EXECUTE ON FUNCTION lo_create(oid), lo_creat(integer), lo_from_bytea(oid, bytea),
                           lo_import(text), lo_import(text, oid) FROM PUBLIC;

-- template1 is what a CREATE DATABASE without TEMPLATE copies. Closed the same way,
-- so nothing a session leaves in it reaches a database made later.
REVOKE CONNECT, TEMPORARY ON DATABASE template1 FROM PUBLIC;
");

    /// <summary>Create the (empty) template, owned by the fixture's superuser role so
    /// no role a test runs as can unlock it, with <see cref="PerDatabaseGrantsSql"/>
    /// applied.</summary>
    public async Task CreateTemplateAsync()
    {
        await ExecuteAdminAsync($"CREATE DATABASE {TemplateName}").ConfigureAwait(false);
        await ExecuteAdminOnAsync(TemplateName, PerDatabaseGrantsSql).ConfigureAwait(false);
    }

    /// <summary>The template's content, table by table, as it was sealed: what every
    /// clone starts from. See <see cref="FingerprintAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> TemplateFingerprint { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>ANALYZE the template and record its <see cref="TemplateFingerprint"/>.
    /// Call once its content is final, before <see cref="LockTemplateAsync"/>.</summary>
    public async Task SealTemplateAsync()
    {
        await using var connection = new NpgsqlConnection(For(_admin, TemplateName));
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var analyze = new NpgsqlCommand("ANALYZE", connection) { CommandTimeout = 300 })
            await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
        TemplateFingerprint = await FingerprintAsync(connection).ConfigureAwait(false);
    }

    /// <summary>
    /// Close the template to connections and mark it a template, then record the state
    /// of the roles and the locked databases that every test's end compares against.
    /// Every pool in the process is cleared first: an idle pooled connection to a
    /// template makes every clone fail ("source database is being accessed by other
    /// users"), and no test is running yet.
    /// </summary>
    public async Task LockTemplateAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(
            $"ALTER DATABASE {TemplateName} IS_TEMPLATE true ALLOW_CONNECTIONS false").ConfigureAwait(false);
        _settledState = await SnapshotClusterStateAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------
    // per-test databases
    // ------------------------------------------------------------------------------

    /// <summary><paramref name="connectionString"/> pointed at <paramref name="database"/>.</summary>
    public static string For(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// <paramref name="connectionString"/> without pooling, for a test that runs the
    /// production migration runner.
    /// </summary>
    /// <remarks>
    /// <para><b>The issue.</b> DbUp's database-exists check connects to the maintenance
    /// database through a pool of its own (an <c>NpgsqlDataSource</c>) that
    /// <c>NpgsqlConnection.ClearAllPools</c> does not reach, so after a pooled run its
    /// connection sits idle in <c>postgres</c> until <c>ConnectionIdleLifetime</c> prunes
    /// it. At the server that is indistinguishable from a connection a test never
    /// returned, which the end-of-test check fails on.</para>
    /// <para><b>The handling.</b> Tests hand the runner an unpooled connection string,
    /// so each connection it opens closes when it is closed. Pooling changes nothing a
    /// migration does.</para>
    /// </remarks>
    public static string ForMigrationRunner(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>Close every idle pooled connection opened with
    /// <paramref name="connectionString"/>.</summary>
    public static void ClearPool(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    /// <summary>A fresh per-test database name: <c>t_&lt;seq5&gt;_&lt;rand8&gt;</c>.</summary>
    public static string NewName() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ClonePrefix}{Interlocked.Increment(ref _sequence):D5}_{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>
    /// Clone the template as <paramref name="name"/>, owned by <see cref="CloneOwner"/>,
    /// and record <paramref name="owningTest"/> as its owner.
    /// </summary>
    /// <returns>How long the clone took, in milliseconds.</returns>
    public async Task<double> CloneTemplateAsync(string name, string owningTest)
    {
        Register(name, owningTest);
        var sw = Stopwatch.StartNew();
        await ExecuteAdminAsync(
            $"CREATE DATABASE {Quote(name)} TEMPLATE {TemplateName} OWNER {CloneOwner} STRATEGY WAL_LOG")
            .ConfigureAwait(false);
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Create an EMPTY database as <paramref name="name"/>, recorded as
    /// <paramref name="owningTest"/>'s: copied from <c>template0</c>, owned by
    /// <see cref="CloneOwner"/> as a fresh install's database is, with
    /// <see cref="PerDatabaseGrantsSql"/> applied as db/init applies it.</summary>
    public async Task CreateEmptyAsync(string name, string owningTest)
    {
        Register(name, owningTest);
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(name)} TEMPLATE template0 OWNER {CloneOwner}")
            .ConfigureAwait(false);
        await ExecuteAdminOnAsync(name, PerDatabaseGrantsSql).ConfigureAwait(false);
    }

    /// <summary>
    /// End the test that owns <paramref name="ownName"/>: check its sessions, clear
    /// every pool in the process, hand its databases to the background drop, then check
    /// what clearing the pools should have emptied and what the test must not leave
    /// behind. The release happens whatever the checks find.
    /// </summary>
    /// <remarks>Every pool, not only the connection strings the test was handed: code
    /// under test builds connection strings of its own (the migration runner's
    /// maintenance-database check among them), and tests run one at a time, so no
    /// other test has a pool to lose.</remarks>
    /// <exception cref="InvalidOperationException">Any check failed; the message names
    /// each problem and the test it belongs to.</exception>
    public async Task EndTestAsync(string ownName)
    {
        ValidateName(ownName);
        var problems = new List<string>();
        try
        {
            problems.AddRange(await SessionProblemsAsync(ownName, SettleBound).ConfigureAwait(false));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ReleaseAsync(ownName).ConfigureAwait(false);
        }

        problems.AddRange(await MaintenanceProblemsAsync(SettleBound).ConfigureAwait(false));
        problems.AddRange((await FindLeftoversAsync().ConfigureAwait(false)).Select(l => "left behind: " + l));
        problems.AddRange(await StateDriftAsync(perTest: true).ConfigureAwait(false));
        while (_dropFailures.TryDequeue(out var failure)) problems.Add("drop failed: " + failure);

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Test database {ownName} ({OwnerOf(ownName)}) ended with problems:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>
    /// The session checks a test's end runs before the pools are cleared, over
    /// <paramref name="ownName"/> and every database named under it (<c>ownName_*</c>:
    /// siblings, empty databases, and what code under test made from it, such as a
    /// restore's swapped-out copy).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>Its own databases: idle is a pooled connection, expected;
    ///   working is a problem.</description></item>
    ///   <item><description>The maintenance database: working is a problem. Idle is the
    ///   migration runner's pool until the pools are cleared, so
    ///   <see cref="CheckMaintenanceDatabaseAsync"/> runs after that.</description></item>
    ///   <item><description>A database an earlier test released: a session that started
    ///   after the release is this test's leak. One that started before is the earlier
    ///   test's, killed by the drop; it is given <paramref name="settle"/> to go, and
    ///   fails if it does not.</description></item>
    ///   <item><description>Anywhere else: a problem.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A check failed; the message names
    /// each session.</exception>
    public async Task CheckSessionsAsync(string ownName, TimeSpan? settle = null)
    {
        ValidateName(ownName);
        var problems = await SessionProblemsAsync(ownName, settle ?? SettleBound).ConfigureAwait(false);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Test database {ownName} ({OwnerOf(ownName)}) ended with sessions it does not own or did not finish:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>
    /// The maintenance-database checks a test's end runs AFTER every pool is cleared:
    /// no session but the fixture's (a connection still there was never returned to its
    /// pool, and can hold an advisory lock or a LISTEN into the next test), and no large
    /// object.
    /// </summary>
    /// <exception cref="InvalidOperationException">A check failed.</exception>
    public async Task CheckMaintenanceDatabaseAsync(TimeSpan? settle = null)
    {
        var problems = await MaintenanceProblemsAsync(settle ?? SettleBound).ConfigureAwait(false);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "The maintenance database holds what a test left:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>
    /// Compare the roles (attributes, memberships, settings) and the databases no test
    /// owns (owner, template and connection flags, ACL) with their state when the
    /// template was locked.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something changed; the message lists
    /// each line added or removed.</exception>
    public async Task CheckClusterStateAsync()
    {
        var drift = await StateDriftAsync(perTest: true).ConfigureAwait(false);
        if (drift.Count > 0)
            throw new InvalidOperationException(
                "Cluster state changed since the template was locked:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", drift));
    }

    /// <summary>
    /// Hand <paramref name="ownName"/>, and every database named under it, to the
    /// background drop, recording the server's time of release. The caller clears its
    /// pools first.
    /// </summary>
    public async Task ReleaseAsync(string ownName)
    {
        ValidateName(ownName);
        var names = new List<string>();
        DateTime releasedAt;
        await using (var connection = new NpgsqlConnection(_admin))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using (var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection))
                releasedAt = (DateTime)(await clock.ExecuteScalarAsync().ConfigureAwait(false))!;
            await using var command = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datname = @n OR datname LIKE @p ORDER BY datname DESC",
                connection);
            command.Parameters.AddWithValue("n", ownName);
            command.Parameters.AddWithValue("p", LikeEscape(ownName) + "\\_%");
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) names.Add(reader.GetString(0));
        }

        _live.TryRemove(ownName, out _);
        foreach (var name in names)
        {
            _owners.TryAdd(name, OwnerOf(ownName));
            _pending[name] = releasedAt;
            if (!_drops.Writer.TryWrite(name))
                _dropFailures.Enqueue($"{name} ({OwnerOf(name)}): released after the fixture stopped dropping");
        }
    }

    /// <summary>Hold the background drop until the result is disposed, so a released
    /// database stays pending. For the tests of the checks that treat pending
    /// databases.</summary>
    internal IDisposable HoldDrops()
    {
        _dropGate.Wait();
        return new Releaser(_dropGate);
    }

    /// <summary>
    /// Databases that exist but are none of: the server's own (<c>postgres</c>,
    /// <c>template0</c>, <c>template1</c>), the template, a running test's (or one named
    /// under it), or one waiting to be dropped. Each with the test that owned it, where
    /// one is recorded. An explicit allowlist, since the service role can create a
    /// database of any name.
    /// </summary>
    /// <param name="includeLive">Also report databases of tests that are still
    /// registered as running — at the end of the run there should be none.</param>
    /// <param name="includePending">Also report databases still marked as waiting to be
    /// dropped — once the drop loop has finished there should be none, and one that
    /// is still there was never dropped.</param>
    public async Task<IReadOnlyList<string>> FindLeftoversAsync(bool includeLive = false, bool includePending = false)
    {
        var leftovers = new List<string>();
        // Read before the catalog: a drop that finishes while the catalog is read takes
        // its name out of the pending set, and would otherwise read as a leftover.
        var pending = PendingSnapshot();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT datname FROM pg_database ORDER BY datname", connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (name is MaintenanceDatabase or "template0" or "template1" or TemplateName) continue;
            if (!includePending && pending.ContainsKey(name)) continue;
            if (!includeLive && _live.Keys.Any(l => IsUnder(name, l))) continue;
            leftovers.Add($"{name} (from {OwnerOf(name)})");
        }
        return leftovers;
    }

    /// <summary>Wait for every queued drop, then fail if a drop failed, any database
    /// is left, the maintenance database holds anything, the roles or locked databases
    /// changed, or the template's content changed.</summary>
    public async ValueTask DisposeAsync()
    {
        _drops.Writer.TryComplete();
        await _dropLoop.ConfigureAwait(false);

        var problems = new List<string>(_dropFailures.Select(f => "drop failed: " + f));
        problems.AddRange((await FindLeftoversAsync(includeLive: true, includePending: true).ConfigureAwait(false))
            .Select(l => "left behind: " + l));
        NpgsqlConnection.ClearAllPools();
        problems.AddRange(await MaintenanceProblemsAsync(SettleBound).ConfigureAwait(false));
        problems.AddRange(await StateDriftAsync(perTest: false).ConfigureAwait(false));
        problems.AddRange(await TemplateContentDriftAsync().ConfigureAwait(false));
        ClearPool(_admin);

        if (problems.Count > 0)
        {
            var message = "The test databases' cluster did not end clean:" + Environment.NewLine + "  "
                          + string.Join(Environment.NewLine + "  ", problems);
            // A collection fixture's dispose failure reaches the console at minimal
            // verbosity (what CI runs) as its exception type alone; a diagnostic
            // message is printed in full. See the test projects' xunit.runner.json.
            _diagnostics?.OnMessage(new DiagnosticMessage(message));
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Every public table's content, as <c>table → md5 of its rows</c>. Two databases
    /// with equal fingerprints hold the same rows.
    /// </summary>
    public static async Task<Dictionary<string, string>> FingerprintAsync(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection))
        await using (var reader = await list.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false)) tables.Add(reader.GetString(0));
        }

        var fingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tables.Count == 0) return fingerprint;
        var sql = string.Join("\nUNION ALL\n", tables.Select(t =>
            $"SELECT '{t}', coalesce(md5(string_agg(x::text, E'\\n' ORDER BY x::text)), '-') FROM public.\"{t}\" x"));
        await using var command = new NpgsqlCommand(sql, connection);
        await using var rows = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rows.ReadAsync().ConfigureAwait(false))
            fingerprint[rows.GetString(0)] = rows.GetString(1);
        return fingerprint;
    }

    /// <summary>
    /// Run <paramref name="setup"/>; if it throws, run <paramref name="teardown"/>
    /// before rethrowing.
    /// </summary>
    /// <remarks>xunit 2 does not call <c>IAsyncLifetime.DisposeAsync</c> when
    /// <c>InitializeAsync</c> throws, so a test whose setup fails would otherwise leave
    /// its database registered as running, unchecked and with its pools full, and the
    /// next test's session check would fail on its connections.</remarks>
    public static async Task SetUpOrTearDownAsync(Func<Task> setup, Func<Task> teardown)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(teardown);
        try
        {
            await setup().ConfigureAwait(false);
        }
        catch (Exception failed)
        {
            try
            {
                await teardown().ConfigureAwait(false);
            }
            catch (Exception alsoFailed)
            {
                throw new AggregateException(failed, alsoFailed);
            }
            throw;
        }
    }

    /// <summary>The display name of the test <paramref name="output"/> belongs to.</summary>
    /// <remarks>xunit 2 keeps the test on a private field of its output helper and
    /// offers no public way to it; the name is only for messages, so a miss degrades to
    /// a placeholder rather than failing.</remarks>
    public static string TestNameOf(ITestOutputHelper? output)
    {
        var field = output?.GetType().GetField("test", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(output) is ITest test ? test.DisplayName : "(unnamed test)";
    }

    /// <summary>Quote a per-test database name as an identifier, after checking it has
    /// the shape this class generates.</summary>
    public static string Quote(string name)
    {
        ValidateName(name);
        return "\"" + name + "\"";
    }

    /// <summary>Whether <paramref name="database"/> is <paramref name="ownName"/> or is
    /// named under it.</summary>
    public static bool IsUnder(string database, string ownName) =>
        database == ownName || database.StartsWith(ownName + "_", StringComparison.Ordinal);

    // ------------------------------------------------------------------------------
    // internals
    // ------------------------------------------------------------------------------

    private async Task ExecuteAdminAsync(string sql) => await ExecuteAdminOnAsync(MaintenanceDatabase, sql).ConfigureAwait(false);

    /// <summary>One statement per call where the statement refuses a transaction block
    /// (<c>CREATE DATABASE</c>, <c>DROP DATABASE</c>).</summary>
    private async Task ExecuteAdminOnAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(For(_admin, database));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record Session(int Pid, string Database, string User, string Description, bool Idle, DateTime Started);

    private async Task<List<Session>> SessionsAsync()
    {
        var sessions = new List<Session>();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT pid, coalesce(datname, ''), coalesce(usename, ''), coalesce(application_name, ''),
                   coalesce(state, ''), left(coalesce(query, ''), 160), backend_start
              FROM pg_stat_activity
             WHERE backend_type = 'client backend'
               AND pid <> pg_backend_pid()
               AND coalesce(usename, '') <> @fixture
             ORDER BY datname, pid
            """, connection);
        command.Parameters.AddWithValue("fixture", FixtureRole);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var pid = reader.GetInt32(0);
            var database = reader.GetString(1);
            var user = reader.GetString(2);
            var state = reader.GetString(4);
            sessions.Add(new Session(pid, database, user,
                $"pid {pid} {user}@{database} app '{reader.GetString(3)}' {state}: {reader.GetString(5)}",
                state == "idle", reader.GetDateTime(6)));
        }
        return sessions;
    }

    private async Task<List<string>> SessionProblemsAsync(string ownName, TimeSpan settle)
    {
        var problems = new List<string>();
        var lingering = new Dictionary<int, string>();
        // Read before the sessions, for the reason FindLeftoversAsync gives.
        var pending = PendingSnapshot();
        foreach (var s in await SessionsAsync().ConfigureAwait(false))
        {
            if (IsUnder(s.Database, ownName))
            {
                if (!s.Idle) problems.Add("still working in this test's own database: " + s.Description);
            }
            else if (s.Database == MaintenanceDatabase)
            {
                if (!s.Idle) problems.Add("still working in the maintenance database: " + s.Description);
            }
            else if (pending.TryGetValue(s.Database, out var releasedAt))
            {
                if (s.Started > releasedAt)
                    problems.Add($"connected to {s.Database} after its test ({OwnerOf(s.Database)}) released it: "
                                 + s.Description);
                else
                    lingering[s.Pid] = $"still connected to {s.Database} {settle.TotalMilliseconds:F0} ms after"
                                       + $" waiting, from before its test ({OwnerOf(s.Database)}) released it: "
                                       + s.Description;
            }
            else
            {
                problems.Add("in a database that is not this test's: " + s.Description);
            }
        }
        problems.AddRange(await StillThereAfterAsync(lingering, settle).ConfigureAwait(false));
        return problems;
    }

    private async Task<List<string>> MaintenanceProblemsAsync(TimeSpan settle)
    {
        var present = (await SessionsAsync().ConfigureAwait(false))
            .Where(s => s.Database == MaintenanceDatabase)
            .ToDictionary(s => s.Pid, s =>
                "still in the maintenance database after every pool was cleared (a connection nobody returned): "
                + s.Description);
        var problems = await StillThereAfterAsync(present, settle).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var objects = new NpgsqlCommand(
            "SELECT oid::text || ' owned by ' || pg_get_userbyid(lomowner) FROM pg_largeobject_metadata ORDER BY oid",
            connection);
        await using var reader = await objects.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            problems.Add("large object in the maintenance database: " + reader.GetString(0));
        return problems;
    }

    /// <summary>The descriptions of the <paramref name="sessions"/> still present once
    /// <paramref name="bound"/> has passed, re-querying until they are gone.</summary>
    private async Task<List<string>> StillThereAfterAsync(Dictionary<int, string> sessions, TimeSpan bound)
    {
        if (sessions.Count == 0) return [];
        var remaining = new HashSet<int>(sessions.Keys);
        var deadline = Stopwatch.StartNew();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync().ConfigureAwait(false);
        while (true)
        {
            await using (var command = new NpgsqlCommand(
                "SELECT pid FROM pg_stat_activity WHERE pid = ANY(@p)", connection))
            {
                command.Parameters.AddWithValue("p", remaining.ToArray());
                var still = new HashSet<int>();
                await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) still.Add(reader.GetInt32(0));
                remaining = still;
            }
            if (remaining.Count == 0 || deadline.Elapsed >= bound) break;
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
        return remaining.Select(pid => sessions[pid]).ToList();
    }

    /// <summary>The roles and the databases no test owns, one line per fact.</summary>
    private async Task<IReadOnlyList<string>> SnapshotClusterStateAsync()
    {
        var lines = new List<string>();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT format('role %s super=%s inherit=%s createrole=%s createdb=%s login=%s replication=%s bypassrls=%s connlimit=%s',
                          rolname, rolsuper::text, rolinherit::text, rolcreaterole::text, rolcreatedb::text,
                          rolcanlogin::text, rolreplication::text, rolbypassrls::text, rolconnlimit)
              FROM pg_roles WHERE rolname IN ('{ServiceRole}', '{AppRole}')
            UNION ALL
            SELECT format('member %s of %s', r.rolname, g.rolname)
              FROM pg_auth_members m
              JOIN pg_roles r ON r.oid = m.member
              JOIN pg_roles g ON g.oid = m.roleid
             WHERE r.rolname IN ('{ServiceRole}', '{AppRole}')
            UNION ALL
            SELECT format('setting %s in %s: %s', r.rolname, coalesce(d.datname, '*'), array_to_string(s.setconfig, ','))
              FROM pg_db_role_setting s
              JOIN pg_roles r ON r.oid = s.setrole
              LEFT JOIN pg_database d ON d.oid = s.setdatabase
             WHERE r.rolname IN ('{ServiceRole}', '{AppRole}')
            UNION ALL
            SELECT format('database %s owner=%s istemplate=%s allowconn=%s acl=%s',
                          datname, pg_get_userbyid(datdba), datistemplate::text, datallowconn::text,
                          coalesce(datacl::text, '-'))
              FROM pg_database
             WHERE datname IN ('{MaintenanceDatabase}', 'template0', 'template1', '{TemplateName}')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) lines.Add(reader.GetString(0));
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private async Task<List<string>> StateDriftAsync(bool perTest)
    {
        if (_settledState is null) return ["the cluster state was never recorded (the template was not locked)"];
        var now = await SnapshotClusterStateAsync().ConfigureAwait(false);
        // A setting scoped to a per-test database goes with its drop; between tests it
        // is the database's own business.
        bool Counts(string line) =>
            !perTest || !line.StartsWith("setting ", StringComparison.Ordinal)
            || !line.Contains(" in " + ClonePrefix, StringComparison.Ordinal);
        var drift = now.Except(_settledState).Where(Counts).Select(l => "changed to: " + l).ToList();
        drift.AddRange(_settledState.Except(now).Where(Counts).Select(l => "changed from: " + l));
        return drift;
    }

    /// <summary>Clone the template once more and compare its content with
    /// <see cref="TemplateFingerprint"/>. The template refuses connections, so the copy
    /// is what can be read.</summary>
    private async Task<List<string>> TemplateContentDriftAsync()
    {
        var probe = NewName();
        var problems = new List<string>();
        try
        {
            await ExecuteAdminAsync($"CREATE DATABASE {Quote(probe)} TEMPLATE {TemplateName}").ConfigureAwait(false);
            Dictionary<string, string> now;
            var probeAdmin = For(_admin, probe);
            await using (var connection = new NpgsqlConnection(probeAdmin))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                now = await FingerprintAsync(connection).ConfigureAwait(false);
            }
            ClearPool(probeAdmin);
            problems.AddRange(TemplateFingerprint.Keys.Union(now.Keys)
                .Where(t => TemplateFingerprint.GetValueOrDefault(t) != now.GetValueOrDefault(t))
                .Select(t => "template content changed in table " + t));
        }
        catch (Exception ex)
        {
            problems.Add("could not check the template's content: " + ex.Message);
        }
        finally
        {
            try
            {
                await ExecuteAdminAsync($"DROP DATABASE IF EXISTS {Quote(probe)} WITH (FORCE)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                problems.Add($"drop failed: {probe} (the template content check): {ex.Message}");
            }
        }
        return problems;
    }

    private void Register(string name, string owningTest)
    {
        ValidateName(name);
        _owners[name] = owningTest;
        _live[name] = 0;
    }

    private Dictionary<string, DateTime> PendingSnapshot() => new(_pending, StringComparer.Ordinal);

    private string OwnerOf(string name)
    {
        if (_owners.TryGetValue(name, out var owner)) return owner;
        var best = _owners.Keys.Where(k => IsUnder(name, k)).OrderByDescending(k => k.Length).FirstOrDefault();
        return best is null ? "(no test recorded)" : _owners[best];
    }

    private async Task DrainDropsAsync()
    {
        await foreach (var name in _drops.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _dropGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ExecuteAdminAsync($"DROP DATABASE IF EXISTS {Quote(name)} WITH (FORCE)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _dropFailures.Enqueue($"{name} ({OwnerOf(name)}): {ex.Message}");
            }
            finally
            {
                _pending.TryRemove(name, out _);
                _dropGate.Release();
            }
        }
    }

    private static void ValidateName(string name)
    {
        if (!SafeName().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a per-test database name.", nameof(name));
    }

    private static string LikeEscape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '_' or '%' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
