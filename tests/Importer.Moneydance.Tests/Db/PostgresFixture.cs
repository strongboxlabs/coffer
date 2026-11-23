using System.Globalization;

using Coffer.Api.Configuration;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// One Postgres 16 container per test run, and a migrated template every DB-backed
/// importer test's own database is cloned from.
/// </summary>
/// <remarks>
/// <para>Once, in <see cref="InitializeAsync"/>, the same way the API test project does
/// it: provision production's two roles and lock down the databases no test owns
/// (<see cref="TestDatabaseCluster.ProvisionRolesAsync"/>), create the template, migrate
/// it as <c>coffer_service</c> through the production <see cref="MigrationRunner"/> so
/// the service role owns every table, seed <see cref="TestLedger"/>, ANALYZE, and lock
/// it.</para>
/// <para>Per test, <see cref="CreateDatabaseAsync"/> clones it (<see cref="DatabaseTest"/>
/// calls it), so no test sees another's rows. The clone, end-of-test checks and drops
/// are the API test project's (<see cref="TestDatabaseCluster"/>, linked), so the two
/// projects cannot drift.</para>
/// <para>Tests run as <c>coffer_service</c>, the role production's import writes as: the
/// import runs inside the API (<c>ImportJobRunner</c>) on the API's service connection
/// string. The fixture exposes no connection string: the only way to a database is a
/// test's own <see cref="TestDatabase"/>, on that database alone.</para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ServicePassword = "test-service-password";
    private const string AppPassword = "test-app-password";

    private readonly IMessageSink _diagnostics;

    /// <param name="diagnostics">xunit's message sink, which the end-of-run cleanup
    /// failure is written to in full (<see cref="TestDatabaseCluster.DisposeAsync"/>).</param>
    public PostgresFixture(IMessageSink diagnostics) => _diagnostics = diagnostics;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase(TestDatabaseCluster.MaintenanceDatabase)
        .WithUsername("coffer")
        .WithPassword("coffer_test")
        .Build();

    private TestDatabaseCluster? _cluster;
    private string _service = string.Empty;

    private TestDatabaseCluster Cluster =>
        _cluster ?? throw new InvalidOperationException("The fixture has not started.");

    public async Task InitializeAsync()
    {
        DapperDateOnlyHandler.Register();
        await _container.StartAsync().ConfigureAwait(false);
        var superuser = _container.GetConnectionString();
        _cluster = await TestDatabaseCluster.StartAsync(superuser, _diagnostics).ConfigureAwait(false);
        _service = new NpgsqlConnectionStringBuilder(superuser)
        {
            Username = TestDatabaseCluster.ServiceRole,
            Password = ServicePassword,
            // As production pins it on the API's connection strings (DbSessionTimeZone),
            // by the same mechanism.
            Timezone = DbSessionTimeZone.Zone,
        }.ConnectionString;

        await _cluster.ProvisionRolesAsync(ServicePassword, AppPassword).ConfigureAwait(false);
        await BuildTemplateAsync(_cluster).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_cluster is not null) await _cluster.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Clone the template for the test <paramref name="output"/> belongs to,
    /// and say so in that test's output.</summary>
    internal async Task<TestDatabase> CreateDatabaseAsync(ITestOutputHelper output)
    {
        var cluster = Cluster;
        var owner = TestDatabaseCluster.TestNameOf(output);
        var name = TestDatabaseCluster.NewName();
        var ms = await cluster.CloneTemplateAsync(name, owner).ConfigureAwait(false);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"db {name} clone {ms:F0} ms"));
        return new TestDatabase(cluster, name, TestDatabaseCluster.For(_service, name), owner);
    }

    private async Task BuildTemplateAsync(TestDatabaseCluster cluster)
    {
        await cluster.CreateTemplateAsync().ConfigureAwait(false);
        var serviceTemplate = TestDatabaseCluster.For(_service, TestDatabaseCluster.TemplateName);
        try
        {
            MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(serviceTemplate),
                MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), NullLogger.Instance);
            await SeedTestLedgerAsync(serviceTemplate).ConfigureAwait(false);
            await cluster.SealTemplateAsync().ConfigureAwait(false);
        }
        finally
        {
            await cluster.LockTemplateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Seed <see cref="TestLedger"/> — the ledger DB-backed tests write into.
    /// </summary>
    /// <remarks>
    /// Migrations no longer ship a ledger (ADR-0088 / migration 186), so the fixture
    /// provides one explicitly rather than tests inheriting a seeded row by luck. In the
    /// template, so every test's clone starts with it.
    /// </remarks>
    private static async Task SeedTestLedgerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ledgers (id, name) VALUES (@id, @name);";
        cmd.Parameters.AddWithValue("id", TestLedger.Id);
        cmd.Parameters.AddWithValue("name", TestLedger.Name);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// xUnit collection that shares a single <see cref="PostgresFixture"/> (one container,
/// one template) across every DB-backed importer test class. Its members run one at a
/// time; each test still gets its own database (<see cref="DatabaseTest"/>).
/// </summary>
[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// One importer test's own database: a clone of the template, reached as
/// <c>coffer_service</c> (BYPASSRLS, owner of every table), the role production's
/// import writes as, on this database alone.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly TestDatabaseCluster _cluster;
    private readonly string _owningTest;
    private readonly HashSet<string> _empties = new(StringComparer.Ordinal);
    private bool _disposed;

    internal TestDatabase(TestDatabaseCluster cluster, string name, string connectionString, string owningTest)
    {
        _cluster = cluster;
        _owningTest = owningTest;
        Name = name;
        ConnectionString = connectionString;
    }

    /// <summary>
    /// <c>coffer_service</c> on an EMPTY database of this test's own, named under this
    /// one and dropped with it: for a test that migrates a database itself, such as an
    /// install staged at an earlier migration. Created on first use.
    /// </summary>
    public string EmptyDatabaseConnectionString(string suffix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        var name = $"{Name}_{suffix}";
        if (_empties.Add(name))
            _cluster.CreateEmptyAsync(name, _owningTest).GetAwaiter().GetResult();
        return TestDatabaseCluster.For(ConnectionString, name);
    }

    /// <summary>This database's name.</summary>
    public string Name { get; }

    /// <summary><c>coffer_service</c> on this database.</summary>
    public string ConnectionString { get; }

    /// <summary>An open connection to this database.</summary>
    public NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>End the test: check, clear every pool, queue the drop (whatever the
    /// checks found), check again (<see cref="TestDatabaseCluster.EndTestAsync"/>).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cluster.EndTestAsync(Name).ConfigureAwait(false);
    }
}

/// <summary>
/// Base class of every DB-backed importer test: each test gets its own database,
/// cloned before it runs and dropped after it. Setup that needs <see cref="Db"/> goes in
/// <see cref="OnInitializedAsync"/>, since a constructor runs before it exists.
/// </summary>
public abstract class DatabaseTest : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private TestDatabase? _db;

    protected DatabaseTest(PostgresFixture fixture, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(output);
        _fixture = fixture;
        Output = output;
    }

    /// <summary>This test's output.</summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>This test's own database.</summary>
    protected TestDatabase Db => _db ?? throw new InvalidOperationException(
        "The test's database is created in InitializeAsync, after the constructor; "
        + "move setup that needs it into OnInitializedAsync.");

    /// <summary>Create the database, then run <see cref="OnInitializedAsync"/>. If that
    /// throws, the database is ended and released before the exception propagates:
    /// xunit 2 does not call <see cref="DisposeAsync"/> after a failed
    /// <c>InitializeAsync</c> (see <see cref="TestDatabaseCluster.SetUpOrTearDownAsync"/>).</summary>
    public async Task InitializeAsync()
    {
        _db = await _fixture.CreateDatabaseAsync(Output).ConfigureAwait(false);
        await TestDatabaseCluster.SetUpOrTearDownAsync(
            OnInitializedAsync,
            async () =>
            {
                _released = true;
                await _db.DisposeAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>The database was ended by a failed setup, so there is nothing left to
    /// check at disposal.</summary>
    private bool _released;

    /// <summary>Setup that needs <see cref="Db"/>.</summary>
    protected virtual Task OnInitializedAsync() => Task.CompletedTask;

    /// <summary>
    /// Check that every leg the test left holds the stored memo-and-tags presentation the
    /// rule computes (<see cref="StoredNotesDatabaseCheck"/>, the check the API's tests end
    /// with), then end the test's database whatever the check found.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_db is null) return;
        if (_released)
        {
            await _db.DisposeAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            await using var db = new Coffer.Api.Db.AppDbContext(
                new DbContextOptionsBuilder<Coffer.Api.Db.AppDbContext>()
                    .UseNpgsql(_db.ConnectionString).Options);
            await StoredNotesDatabaseCheck.AssertComputedAsync(db, "At the end of the test").ConfigureAwait(false);
        }
        finally
        {
            await _db.DisposeAsync().ConfigureAwait(false);
        }
    }
}
