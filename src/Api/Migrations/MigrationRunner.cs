using DbUp;
using DbUp.Engine.Output;
using DbUp.ScriptProviders;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Coffer.Api.Migrations;

/// <summary>
/// Applies the SQL files under <c>db/migrations/</c> at API startup, using
/// DbUp to track which scripts have already run in a per-database
/// <c>__schema_migrations</c> table. Forward-only migrations per
/// <c>docs/engineering-standards.md §3.1</c>.
/// </summary>
/// <remarks>
/// Both test projects migrate their template database through this runner, as
/// <c>coffer_service</c>, so tests run on a schema built the way an install's is.
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
    /// The session setting every connection the runner opens carries, set to <c>on</c> at
    /// connect (<see cref="ForMigrationSession"/>).
    /// </summary>
    /// <remarks>
    /// <para><c>fn_ledger_write_lock</c> (migration 275) does nothing on such a session. It
    /// otherwise refuses a transaction that has written before taking the ledger write lock,
    /// and a data migration writes (and may run DDL) before it recomputes balances or
    /// holdings, all in one transaction. Migrations run before the API serves, so there is
    /// nothing for the lock to order them against.</para>
    /// <para>Only the runner sets it: the API's own connection strings never carry it, and
    /// no SQL function sets it (<c>LedgerWriteLockCatalogTests</c>). The marked connection
    /// string differs from the plain one, so its connections sit in a pool of their own and
    /// are never handed to a request.</para>
    /// </remarks>
    internal const string MigrationSessionSetting = "coffer.migration_session";

    /// <summary><paramref name="connectionString"/> with <see cref="MigrationSessionSetting"/>
    /// set to <c>on</c> for every session it opens, alongside any options it already
    /// carries.</summary>
    internal static string ForMigrationSession(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var mark = $"-c {MigrationSessionSetting}=on";
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? mark : builder.Options + " " + mark;
        return builder.ConnectionString;
    }

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

        // Every script and C# step runs on a session marked as a migration's.
        var session = ForMigrationSession(connectionString);

        // 10-minute per-script command timeout. The default 30s ceiling is
        // fine for the small test-container DBs but trips on a populated
        // database, where some migrations touch the full transactions table
        // (e.g. migration 030's column-add + CHECK constraint validation on
        // a populated txn_headers). Schema changes are off-line by design;
        // an upper bound large enough to fit any reasonable migration is
        // safer than a tight default that masquerades as a network failure.
        var builder = DeployChanges.To
            .PostgresqlDatabase(session)
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
            .LogTo(new DbUpLoggerAdapter(logger));

        // The C# steps among the SQL files, each ordered by its name like them, directly
        // after the script it belongs to (Registered says which apply here).
        var steps = Registered(migrationsDirectory)
            .Select(s => (Step: s, Script: s.Create(session, logger)))
            .ToList();
        foreach (var (step, script) in steps)
            builder = builder.WithScript(step.Name, script);

        var upgrader = builder.Build();

        // When one upgrade runs several recompute steps, only the last computes: the
        // values are then computed once, under the final schema and rule, and the earlier
        // steps journal without computing (each would compute what the last recomputes).
        var last = ComputingStep(
            steps.Select(s => s.Step.Name).ToList(),
            upgrader.GetScriptsToExecute().Select(s => s.Name).ToHashSet(StringComparer.Ordinal));
        foreach (var (step, script) in steps)
            script.Computes = step.Name == last;

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

    /// <summary>
    /// A migration step written in C#, journaled under <see cref="Name"/> directly after
    /// the SQL script <see cref="AfterScript"/>.
    /// </summary>
    /// <remarks>Each one recomputes the stored memo-and-tags presentation: the rule it
    /// stores is defined in C# only, so no SQL script may compute it.</remarks>
    public sealed record CSharpStep(string Name, string AfterScript)
    {
        internal Func<string, ILogger, RecomputeStep> Create { get; init; } = null!;
    }

    /// <summary>A C# step's script: DbUp runs it in name order like a SQL file.</summary>
    internal abstract class RecomputeStep : DbUp.Engine.IScript
    {
        /// <summary>False for every recompute step of a run but the last: it journals
        /// without computing, because the last one recomputes everything anyway.</summary>
        public bool Computes { get; set; } = true;

        public abstract string ProvideScript(Func<System.Data.IDbCommand> dbCommandFactory);
    }

    /// <summary>
    /// Every C# step this build ships, in journal order. The one list: the runner
    /// registers from it, a whole-database restore counts its names as known migrations
    /// (<see cref="KnownScriptNames"/>), and the test harnesses that apply scripts one by
    /// one run it after the script it follows.
    /// </summary>
    public static IReadOnlyList<CSharpStep> CSharpSteps { get; } =
    [
        new(LegNotesBackfill.Name, LegNotesBackfill.AfterScript)
        {
            Create = (connectionString, logger) => new LegNotesBackfill(connectionString, logger),
        },
    ];

    /// <summary>
    /// Every journal name this build can have written to a database migrated from
    /// <paramref name="migrationsDirectory"/>: its SQL files and its C# steps. A journal
    /// row outside this set comes from a newer build.
    /// </summary>
    public static IReadOnlySet<string> KnownScriptNames(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);
        var names = Directory.EnumerateFiles(migrationsDirectory, "*.sql")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        names.UnionWith(CSharpSteps.Select(s => s.Name));
        return names;
    }

    /// <summary>
    /// Of the recompute steps <paramref name="steps"/>, the one that computes in a run
    /// that executes <paramref name="toRun"/>: the last by name among those it runs. Null
    /// when it runs none.
    /// </summary>
    internal static string? ComputingStep(IReadOnlyCollection<string> steps, IReadOnlySet<string> toRun) =>
        steps.Where(toRun.Contains).Order(StringComparer.Ordinal).LastOrDefault();

    /// <summary>
    /// Run one C# step by name, outside DbUp and without journaling it: for a harness that
    /// applies the SQL files itself, after it has applied the step's script.
    /// </summary>
    public static void RunCSharpStep(string name, string connectionString, ILogger logger)
    {
        var step = CSharpSteps.SingleOrDefault(s => s.Name == name)
            ?? throw new ArgumentException($"No C# migration step named '{name}'.", nameof(name));
        step.Create(ForMigrationSession(connectionString), logger).ProvideScript(() => throw new InvalidOperationException(
            "A C# migration step does its work itself and runs no command of DbUp's."));
    }

    /// <summary>
    /// The steps that belong to this run: those whose script is in the directory. A
    /// directory staged before a step's script (an upgrade rehearsal) leaves the step
    /// out, so it never runs against tables it lacks. A directory that holds a script
    /// sorting after the step's script but not the script itself is refused: the step
    /// would be silently skipped on a database that goes past it.
    /// </summary>
    private static IEnumerable<CSharpStep> Registered(string migrationsDirectory)
    {
        var scripts = Directory.EnumerateFiles(migrationsDirectory, "*.sql")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();
        foreach (var step in CSharpSteps)
        {
            if (scripts.Contains(step.AfterScript, StringComparer.Ordinal))
            {
                yield return step;
                continue;
            }
            var later = scripts.Where(s => string.CompareOrdinal(s, step.AfterScript) > 0)
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (later is not null)
                throw new InvalidOperationException(
                    $"The migrations directory holds {later}, which sorts after {step.AfterScript}, "
                    + $"but not {step.AfterScript} itself: the C# step {step.Name} that follows it "
                    + "could not run. Restore the missing script.");
        }
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
