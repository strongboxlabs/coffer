using Coffer.Api.Migrations;
using Dapper;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The importer tests' databases are what production's import writes into, reached as
/// the role it writes as, and a test whose setup fails leaves nothing for the next one.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class FixtureShapeTests : DatabaseTest
{
    private readonly PostgresFixture _fixture;

    public FixtureShapeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output) => _fixture = fixture;

    [Fact]
    public async Task Tests_run_as_the_service_role_on_a_database_the_production_runner_migrated()
    {
        await using var conn = Db.OpenConnection();
        var facts = await conn.QuerySingleAsync<(string User, bool Super, bool BypassRls, long Foreign, long Applied)>(
            """
            SELECT current_user::text, r.rolsuper, r.rolbypassrls,
                   (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','S')
                       AND pg_get_userbyid(c.relowner) <> 'coffer_service'),
                   (SELECT count(*) FROM public.__schema_migrations)
              FROM pg_roles r WHERE r.rolname = current_user
            """);
        // The journal holds a row for every script and for every C# step between them.
        var migrations = Directory.EnumerateFiles(
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), "*.sql").LongCount()
            + MigrationRunner.CSharpSteps.Count;

        Assert.Equal("coffer_service", facts.User);
        Assert.False(facts.Super, "importer tests run as a superuser, which no install's import does");
        Assert.True(facts.BypassRls);
        Assert.Equal(0, facts.Foreign);
        Assert.Equal(migrations, facts.Applied);
    }

    [Fact]
    public async Task A_failed_setup_releases_its_database_and_leaves_no_session_behind()
    {
        var failing = new SetupThrows(_fixture, Output);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(failing.InitializeAsync);
        var passed = false;
        try
        {
            Assert.Equal(SetupThrows.Failure, ex.Message);
            Assert.NotNull(failing.DatabaseName);

            // Released: its pools were cleared, so the connection its setup returned to
            // the pool is gone. Left registered, that idle connection would sit in a
            // database that is not the next test's and fail that test's check.
            await using var conn = Db.OpenConnection();
            long left = -1;
            for (var i = 0; i < 80 && left != 0; i++)
            {
                if (i > 0) await Task.Delay(25);
                left = await conn.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = @n", new { n = failing.DatabaseName });
            }
            Assert.True(left == 0, $"{left} session(s) still in {failing.DatabaseName} after its setup failed");
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

    /// <summary>A test class whose setup fails after its database exists and holds a
    /// pooled connection. Not a test: it has no test methods.</summary>
    private sealed class SetupThrows : DatabaseTest
    {
        public const string Failure = "setup failed (planted)";

        public SetupThrows(PostgresFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

        public string? DatabaseName { get; private set; }

        protected override async Task OnInitializedAsync()
        {
            DatabaseName = Db.Name;
            await using (var connection = Db.OpenConnection())
                await connection.ExecuteScalarAsync<int>("SELECT 1");
            throw new InvalidOperationException(Failure);
        }
    }
}
