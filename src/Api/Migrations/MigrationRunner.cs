using DbUp;
using DbUp.Engine.Output;
using DbUp.ScriptProviders;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Migrations;

/// <summary>
/// Applies the SQL files under <c>db/migrations/</c> at API startup, using
/// DbUp to track which scripts have already run in a per-database
/// <c>__schema_migrations</c> table. Forward-only migrations per
/// <c>docs/engineering-standards.md §3.1</c>.
/// </summary>
/// <remarks>
/// The importer's <c>PostgresFixture</c> applies the same files differently
/// (re-runs every script against a fresh container). DbUp's tracked-runs
/// model is the right shape for a long-lived API but wrong for the
/// per-test-class fixture, so the two stay separate. Both read the same
/// directory, so the source of truth is one set of <c>NNN_*.sql</c> files.
/// </remarks>
public static class MigrationRunner
{
    /// <summary>
    /// Filename of the table DbUp uses to track applied migrations. Matches
    /// the convention used by other tools in the ecosystem (Flyway:
    /// <c>flyway_schema_history</c>, Liquibase: <c>databasechangelog</c>) so
    /// it's discoverable in <c>\dt</c>.
    /// </summary>
    private const string JournalTable = "__schema_migrations";

    /// <summary>
    /// Apply every <c>NNN_*.sql</c> migration that hasn't run yet.
    /// </summary>
    /// <param name="connectionString">DDL-owner connection string.</param>
    /// <param name="migrationsDirectory">Absolute path to <c>db/migrations</c>.</param>
    /// <param name="logger">Used for both the "applied X" summary and any
    /// per-script failure trace; DbUp's own log output is routed in.</param>
    /// <exception cref="InvalidOperationException">When DbUp reports any
    /// script failed to apply. The exception message names the failing
    /// script so the error is locatable without grepping logs.</exception>
    public static void Run(string connectionString, string migrationsDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Directory.Exists(migrationsDirectory))
            throw new DirectoryNotFoundException(
                $"Migrations directory not found: {migrationsDirectory}");

        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        // 10-minute per-script command timeout. The default 30s ceiling is
        // fine for the small test-container DBs but trips on the dev / prod
        // path where some migrations touch the full transactions table
        // (e.g. migration 030's column-add + CHECK constraint validation on
        // a populated txn_headers). Schema changes are off-line by design;
        // an upper bound large enough to fit any reasonable migration is
        // safer than a tight default that masquerades as a network failure.
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithExecutionTimeout(TimeSpan.FromMinutes(10))
            .WithScriptsFromFileSystem(migrationsDirectory, new FileSystemScriptOptions
            {
                IncludeSubDirectories = false,
                Filter = path => path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase),
            })
            // We pass DbUp no variables, so its `$name$` substitution is unused —
            // and it actively collides with PostgreSQL dollar-quoting: a function
            // body delimited by a NAMED tag (e.g. `$func$ … $func$`, needed to
            // nest dollar-quoted strings) reads to DbUp as an undefined variable
            // and fails the migration. Disabling substitution lets legitimate
            // Postgres SQL through untouched. (Bare `$$` and `$tag$` inside string
            // literals were already tolerated; this covers named delimiters too.)
            .WithVariablesDisabled()
            .JournalToPostgresqlTable("public", JournalTable)
            .LogTo(new DbUpLoggerAdapter(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            var failingScript = result.ErrorScript?.Name ?? "(unknown)";
            throw new InvalidOperationException(
                $"Migration failed at {failingScript}: {result.Error?.Message}", result.Error);
        }

        logger.LogInformation(
            "Applied {Count} migration script(s) from {Directory}.",
            result.Scripts.Count(), migrationsDirectory);
    }
}

/// <summary>
/// Bridges DbUp's <see cref="IUpgradeLog"/> to a Microsoft.Extensions.Logging
/// logger so migration progress shows up in the same structured-logging
/// pipeline as the rest of the API.
/// </summary>
internal sealed class DbUpLoggerAdapter : IUpgradeLog
{
    private readonly ILogger _logger;

    public DbUpLoggerAdapter(ILogger logger)
    {
        _logger = logger;
    }

#pragma warning disable CA2254 // The format strings come from DbUp at runtime.
    public void LogTrace(string format, params object[] args) =>
        _logger.LogTrace(format, args);
    public void LogDebug(string format, params object[] args) =>
        _logger.LogDebug(format, args);
    public void LogInformation(string format, params object[] args) =>
        _logger.LogInformation(format, args);
    public void LogWarning(string format, params object[] args) =>
        _logger.LogWarning(format, args);
    public void LogError(string format, params object[] args) =>
        _logger.LogError(format, args);
    public void LogError(Exception ex, string format, params object[] args) =>
        _logger.LogError(ex, format, args);
#pragma warning restore CA2254
}
