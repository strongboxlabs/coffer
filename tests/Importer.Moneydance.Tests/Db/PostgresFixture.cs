using Coffer.Importer.Moneydance.Db;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Spins up a Postgres 16 container for the duration of the test session,
/// applies every migration in <c>db/migrations/</c> in filename order, and
/// exposes an open connection to each test that requests it. The fixture is
/// shared across the <see cref="DbCollection"/> via xUnit's
/// <see cref="ICollectionFixture{TFixture}"/> so the container is started
/// exactly once per test run.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("coffer_test")
        .WithUsername("coffer")
        .WithPassword("coffer_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        DapperDateOnlyHandler.Register();
        await _container.StartAsync().ConfigureAwait(false);
        // PR 3.8 requires coffer_service + coffer_app roles before
        // migration 017 will run. Importer integration tests use the
        // superuser connection (Dapper, BYPASSRLS by default), so
        // they only need the roles to exist — they don't need to use
        // them.
        await ProvisionRolesAsync(ConnectionString).ConfigureAwait(false);
        await ApplyMigrationsAsync(ConnectionString).ConfigureAwait(false);
        await SeedTestLedgerAsync(ConnectionString).ConfigureAwait(false);
    }

    /// <summary>
    /// Seed <see cref="TestLedger"/> — the ledger DB-backed tests write into.
    /// </summary>
    /// <remarks>
    /// Migrations no longer ship a ledger (ADR-0088 / migration 186), so the
    /// fixture provides one explicitly rather than tests inheriting a seeded row
    /// by luck. Runs after migrations for that reason. Tests that TRUNCATE
    /// ... CASCADE their working tables leave `ledgers` alone, so one seed per
    /// container is enough.
    /// </remarks>
    private static async Task SeedTestLedgerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO ledgers (id, name) VALUES (@id, @name) ON CONFLICT (id) DO NOTHING;";
        cmd.Parameters.AddWithValue("id", TestLedger.Id);
        cmd.Parameters.AddWithValue("name", TestLedger.Name);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ProvisionRolesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
DO $Init$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_service') THEN
        CREATE ROLE coffer_service LOGIN BYPASSRLS PASSWORD 'test-service-password';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_app') THEN
        CREATE ROLE coffer_app LOGIN NOBYPASSRLS PASSWORD 'test-app-password';
    END IF;
END $Init$;
GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
GRANT USAGE          ON SCHEMA public TO coffer_app;
";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    public NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Apply every <c>NNN_*.sql</c> file under <c>db/migrations/</c> in
    /// lexicographic order. The migrations directory is located by walking
    /// up from the test binary until a <c>db</c> sibling is found — this
    /// keeps tests working from any working directory (Visual Studio, dotnet
    /// test in CI, etc.).
    /// </summary>
    private static async Task ApplyMigrationsAsync(string connectionString)
    {
        var migrationsDir = LocateMigrationsDirectory();
        var sqlFiles = Directory.EnumerateFiles(migrationsDir, "*.sql")
                                .OrderBy(path => path, StringComparer.Ordinal)
                                .ToList();
        if (sqlFiles.Count == 0)
            throw new InvalidOperationException(
                $"No migration files found under {migrationsDir}.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var path in sqlFiles)
        {
            var sql = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static string LocateMigrationsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate db/migrations/ from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// xUnit collection that shares a single <see cref="PostgresFixture"/> across
/// every integration test class in the <c>Db</c> folder.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
