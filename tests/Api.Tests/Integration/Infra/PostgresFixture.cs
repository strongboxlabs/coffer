using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

using Coffer.Api.Configuration;
using Coffer.Api.Crypto;
using Coffer.Api.Db;
using Coffer.Api.Migrations;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Spins up a Postgres 16 container shared across the API integration
/// tests, mints the same two roles the production docker-init script
/// creates (<c>coffer_service</c>, <c>coffer_app</c>), then applies every
/// <c>db/migrations/*.sql</c> file once via the same DbUp runner that
/// <c>Program.cs</c> uses in production. Repository tests get a
/// migrated DB without paying the cost of booting an HTTP host;
/// endpoint tests boot via <see cref="ApiFactory"/> and DbUp's
/// <c>__schema_migrations</c> journal makes the second run a no-op.
/// </summary>
/// <remarks>
/// Three role identities at play:
/// <list type="bullet">
///   <item><description><b>The container superuser</b> — what
///   Testcontainers configures via <see cref="PostgreSqlBuilder.WithUsername"/>.
///   Used only to bootstrap the two application roles; not exposed to
///   tests.</description></item>
///   <item><description><b>coffer_service</b> — BYPASSRLS, owns the
///   tables that DbUp creates (a prereq for ENABLE ROW LEVEL SECURITY
///   in migration 017). <see cref="ServiceConnectionString"/>
///   surfaces this for <see cref="SyntheticLedger"/> and other
///   cross-cutting test setup.</description></item>
///   <item><description><b>coffer_app</b> — no BYPASSRLS. The
///   <see cref="ApiFactory"/> wires this into <c>Api:ConnectionString</c>
///   so the API under test exercises the same RLS path production
///   does.</description></item>
/// </list>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ServicePassword = "test-service-password";
    private const string AppPassword     = "test-app-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("coffer_api_test")
        .WithUsername("coffer")
        .WithPassword("coffer_api_test")
        // Bump max_connections from the Postgres default 100 to 400 as
        // headroom. The PRIMARY bound on connection use is the per-pool
        // MaxPoolSize cap in BuildConnectionString (the default-100 pools
        // multiplied across ApiFactory hosts + the fixture's global pools
        // were what exhausted the server); this ceiling is just slack above
        // that bounded footprint so a transient disposal overlap can't trip
        // the "53300: remaining connection slots ... SUPERUSER" error.
        .WithCommand("-c", "max_connections=400")
        .Build();

    /// <summary>
    /// Connection string for <c>coffer_service</c> (BYPASSRLS). Used by
    /// <see cref="SyntheticLedger"/> for cross-cutting setup writes
    /// and by the API's <c>ServiceDbContextFactory</c> in tests.
    /// </summary>
    public string ServiceConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Connection string for <c>coffer_app</c> (no BYPASSRLS). Used by
    /// the API's runtime <c>AppDbContext</c>. Issuing raw SQL on this
    /// connection won't see anything until <c>SET app.user_id</c>
    /// runs — that's the production RLS path.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        // Bootstrap the two application roles before running
        // migrations. Mirrors the production db/init/00-init-roles.sh
        // step — the same passwords, the same GRANTs, the same
        // CREATE/USAGE schema privileges.
        var superuserConnectionString = _container.GetConnectionString();
        await ProvisionRolesAsync(superuserConnectionString).ConfigureAwait(false);

        ServiceConnectionString = BuildConnectionString(
            superuserConnectionString, username: "coffer_service", password: ServicePassword);
        AppConnectionString = BuildConnectionString(
            superuserConnectionString, username: "coffer_app", password: AppPassword);

        // Apply migrations as coffer_service so DbUp-created tables are
        // owned by it, which is the precondition for ENABLE ROW LEVEL
        // SECURITY in migration 017.
        var migrationsDirectory = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        MigrationRunner.Run(ServiceConnectionString, migrationsDirectory, NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A connection string for a database on this container that has **no Coffer
    /// schema** — created on demand and left empty. Models the state the API
    /// actually boots into on a fresh install: Postgres reachable, migrations not
    /// yet applied, so none of the app's tables exist.
    /// </summary>
    /// <remarks>
    /// Needed because <see cref="ServiceConnectionString"/> points at the migrated
    /// shared database, which can never represent "virgin". Uses the same
    /// service-role credentials so callers exercise the production connection
    /// shape. Idempotent — repeated calls reuse the same empty database.
    /// </remarks>
    public string EmptyDatabaseConnectionString(string databaseName = "coffer_empty_probe")
    {
        using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            admin.Open();
            using var exists = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @n", admin);
            exists.Parameters.AddWithValue("n", databaseName);
            if (exists.ExecuteScalar() is null)
            {
                // CREATE DATABASE can't be parameterized and won't run inside a
                // transaction; the name is a hard-coded default or test-supplied,
                // never user input, but quote it anyway.
                using var create = new NpgsqlCommand(
                    $"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"")}\"", admin);
                create.ExecuteNonQuery();
            }
        }

        // Schema privileges are PER DATABASE, so the GRANTs ProvisionRolesAsync
        // applied to the main database don't reach this one — without these, the
        // service role gets "42501: permission denied for schema public" the
        // moment a caller tries to migrate it. Same grants as production's
        // db/init/00-init-roles.sh.
        var adminToNewDb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;
        using (var grant = new NpgsqlConnection(adminToNewDb))
        {
            grant.Open();
            using var cmd = new NpgsqlCommand(
                """
                GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
                GRANT USAGE          ON SCHEMA public TO coffer_app;
                -- Extensions need superuser, so create them here rather than
                -- letting 001_extensions.sql try as coffer_service. This mirrors
                -- production, where they are install-managed and outlive a
                -- restore (see BackupService.WipeServiceOwnedObjectsAsync); the
                -- migration's IF NOT EXISTS then no-ops.
                CREATE EXTENSION IF NOT EXISTS pg_trgm;
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                """, grant);
            cmd.ExecuteNonQuery();
        }

        return new NpgsqlConnectionStringBuilder(ServiceConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
    }

    /// <summary>
    /// Open a raw <see cref="NpgsqlConnection"/> bound to the
    /// service-role connection string. Kept for tests that need
    /// direct SQL without an EF context (e.g. TRUNCATE between
    /// fixture tests, raw <c>SET app.user_id</c> dance, …).
    /// </summary>
    public NpgsqlConnection OpenServiceConnection()
    {
        var connection = new NpgsqlConnection(ServiceConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Construct a fresh <see cref="AppDbContext"/> bound to the
    /// service-role connection string. Used by
    /// <see cref="SyntheticLedger"/> for cross-cutting setup writes
    /// (creating ledgers for multiple users, seeding transactions
    /// across ledgers, …).
    /// </summary>
    public AppDbContext NewServiceDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ServiceConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Backward-compatibility alias for <see cref="NewServiceDbContext"/>.
    /// Pre-PR-3.8 tests called this name; they ran against the single
    /// pre-split role which had de facto BYPASSRLS. The service role
    /// is the right successor — RLS doesn't apply, so the tests'
    /// existing assertions keep working.
    /// </summary>
    public AppDbContext NewDbContext() => NewServiceDbContext();

    /// <summary>
    /// Build a <see cref="ServiceDbContextFactory"/> bound to this
    /// fixture's service-role connection string. Repository unit /
    /// integration tests that construct a repo directly (without the
    /// full DI graph) inject this so the repo's internal
    /// <c>_serviceFactory.Create()</c> resolves to a context against
    /// the test container.
    /// </summary>
    public ServiceDbContextFactory NewServiceFactory()
    {
        var options = Options.Create(new ApiOptions
        {
            ConnectionString = AppConnectionString,
            ServiceConnectionString = ServiceConnectionString,
        });
        return new ServiceDbContextFactory(options);
    }

    /// <summary>
    /// Test-side master KEK + LedgerKeyService for repository unit
    /// tests that construct a <see cref="LedgersRepository"/>
    /// directly. The key bytes are deterministic across tests (32
    /// zero bytes) — non-secret by intent; we just need a stable
    /// MasterKey value so wrapped LEKs round-trip across the same
    /// process. ADR-0026.
    /// </summary>
    public Coffer.Api.Crypto.LedgerKeyService NewLedgerKeyService()
    {
        var key = new Coffer.Api.Crypto.MasterKey(new byte[32], id: "test");
        return new Coffer.Api.Crypto.LedgerKeyService(key);
    }

    /// <summary>
    /// Construct a fresh <see cref="AppDbContext"/> bound to the
    /// app-role connection string, with an explicit <c>app.user_id</c>
    /// pre-set on the connection via a custom interceptor that
    /// applies the supplied value once at open. Use this when a test
    /// needs to exercise the RLS path directly (without going through
    /// the HTTP pipeline).
    /// </summary>
    public AppDbContext NewAppDbContextAsUser(Guid userId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(AppConnectionString)
            .AddInterceptors(new FixedUserConnectionInterceptor(userId))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task ProvisionRolesAsync(string superuserConnectionString)
    {
        await using var connection = new NpgsqlConnection(superuserConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
-- Match the production db/init/00-init-roles.sh bootstrap: install
-- the non-trusted extensions as the superuser up-front so the
-- migration runner (running as coffer_service, a non-superuser) can
-- run its CREATE EXTENSION IF NOT EXISTS as a no-op.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

DO $Init$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_service') THEN
        CREATE ROLE coffer_service LOGIN BYPASSRLS PASSWORD '{ServicePassword}';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_app') THEN
        CREATE ROLE coffer_app LOGIN NOBYPASSRLS PASSWORD '{AppPassword}';
    END IF;
END $Init$;
GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
GRANT USAGE          ON SCHEMA public TO coffer_app;
";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string BuildConnectionString(string superuserConnectionString, string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(superuserConnectionString)
        {
            Username = username,
            Password = password,
            // Bound each Npgsql pool so total server connections stay
            // well under the container's max_connections REGARDLESS of how
            // many test classes the suite grows to. Without a cap, every
            // ApiFactory host's data source AND the fixture's process-global
            // pools (NewServiceDbContext / NewAppDbContextAsUser reuse the
            // global pool keyed on this string) each default to 100, and
            // Npgsql holds idle connections for 300s — longer than the whole
            // suite runs — so connections accumulate and never prune mid-run.
            // The symptom was a non-deterministic "53300: remaining
            // connection slots are reserved for roles with the SUPERUSER
            // attribute" from a later test's startup (coffer_service is a
            // non-superuser) once the suite crossed a connection-count
            // threshold. Capping the pool + a short idle lifetime keeps the
            // footprint flat as the suite scales; the tests are serialized in
            // one collection, so a modest ceiling is ample.
            MaxPoolSize = 40,
            ConnectionIdleLifetime = 10,
        };
        return builder.ConnectionString;
    }
}

/// <summary>
/// xUnit collection that shares a single <see cref="PostgresFixture"/>
/// across every API integration test class. The migrations run once
/// when the first test in the collection asks <see cref="ApiFactory"/>
/// for an <c>HttpClient</c>; subsequent tests reuse the now-migrated
/// database.
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
