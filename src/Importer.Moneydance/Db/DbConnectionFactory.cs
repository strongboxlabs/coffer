using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Resolves the Postgres connection string from CLI options or the
/// <c>COFFER_DB_CONNECTION</c> environment variable, and opens an
/// <see cref="NpgsqlConnection"/>. Centralized so PR 2.3 onward has a single
/// place to evolve the configuration story (env file loading, Phase 4 secrets
/// management, etc.).
/// </summary>
public sealed class DbConnectionFactory
{
    /// <summary>
    /// Dapper cannot bind <see cref="DateOnly"/> parameters on its own — it
    /// throws <c>NotSupportedException: The member … of type System.DateOnly
    /// cannot be used as a parameter value</c> — so
    /// <see cref="DapperDateOnlyHandler"/> must be registered before any
    /// importer command runs. Registering it here, at the one gateway every
    /// importer DB path opens its connection through, means no host can forget.
    ///
    /// A host that registered it only in its own entry point (the CLI, the test
    /// fixture) would leave every other host — the API — without it, and fail on
    /// the first DateOnly parameter: <c>accounts.opened_on</c> on every account,
    /// <c>recurring_transactions.start_date</c> on every reminder.
    ///
    /// Register() is idempotent, so the CLI and test fixture calling it too is
    /// harmless.
    /// </summary>
    static DbConnectionFactory() => DapperDateOnlyHandler.Register();

    public const string EnvVarName = "COFFER_DB_CONNECTION";

    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "Connection string is empty.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <summary>
    /// Build a factory using the CLI-supplied connection string (preferred)
    /// or the <c>COFFER_DB_CONNECTION</c> environment variable as a fallback.
    /// Throws if neither is set, so callers can present a clear error before
    /// attempting any DB work.
    /// </summary>
    public static DbConnectionFactory FromCliOrEnvironment(string? cliValue)
    {
        var resolved = !string.IsNullOrWhiteSpace(cliValue)
            ? cliValue
            : Environment.GetEnvironmentVariable(EnvVarName);

        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException(
                $"No database connection string. Pass --db <CONNECTION_STRING> " +
                $"or set the {EnvVarName} environment variable.");

        return new DbConnectionFactory(resolved);
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        // Importer transactions accumulate every INSERT of the import in a single commit
        // (paired transactions + price snapshots). The deferred symmetric-
        // pairing constraint trigger fires per-row at COMMIT, so its cost grows
        // with the export and a large one can run past Npgsql's default 30-second per-command
        // timeout — and the timeout fires on COMMIT itself, rolling everything
        // back. Force a generous timeout via the connection-string builder
        // (parsing the user's value first so explicit overrides win).
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        if (builder.CommandTimeout == 30)        // Npgsql default
            builder.CommandTimeout = 600;
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
