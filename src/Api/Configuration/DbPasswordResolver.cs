using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Coffer.Api.Configuration;

/// <summary>
/// Injects the database role passwords into the connection strings from FILES
/// rather than from the connection strings themselves.
/// </summary>
/// <remarks>
/// <para>
/// Same reasoning ADR-0092 D1 applied to the master KEK: a value passed as an
/// environment variable is readable via <c>docker inspect</c>,
/// <c>/proc/&lt;pid&gt;/environ</c>, any child process's environment, and crash
/// dumps. <c>coffer_app</c> and <c>coffer_service</c> are the credentials that
/// authenticate every query the application makes, so they belong in a file
/// with restricted permissions — a docker/compose secret — not in the process
/// environment of two containers.
/// </para>
/// <para>
/// This runs at the CONFIGURATION layer, before <see cref="ApiOptions"/> is
/// bound, and rewrites the connection-string values in place. There are a dozen
/// consumers of those strings across the API, the backup service and the
/// importer; resolving here means every one of them sees a finished connection
/// string and none of them has to know where the password came from.
/// </para>
/// <para>
/// Precedence is file-first, and an inline password is IGNORED when a file is
/// configured (with a warning). That direction matters: the transition leaves
/// installs with a password in both places for a while, and if the environment
/// won, moving the secret into a file would appear to work while changing
/// nothing — the same silent failure ADR-0092 D6 hit on the KEK's env-var
/// transition, where env-first precedence quietly undid rotations.
/// </para>
/// </remarks>
public static class DbPasswordResolver
{
    /// <summary>Config key for the <c>coffer_app</c> password file.</summary>
    public const string AppPasswordFileKey = "Api:AppPasswordFile";

    /// <summary>Config key for the <c>coffer_service</c> password file.</summary>
    public const string ServicePasswordFileKey = "Api:ServicePasswordFile";

    private const string AppConnectionKey = "Api:ConnectionString";
    private const string ServiceConnectionKey = "Api:ServiceConnectionString";

    /// <summary>What happened for one role, so startup can log it without
    /// touching the secret itself.</summary>
    public sealed record Outcome(string Role, bool FromFile, bool InlinePasswordIgnored);

    /// <summary>
    /// Rewrites both connection strings in <paramref name="config"/>, reading
    /// each password from its configured file when one is set. Returns one
    /// <see cref="Outcome"/> per role so the caller can log the source.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A password file is configured but missing, unreadable or empty. This
    /// fails closed rather than falling back: a connection string that has
    /// quietly lost its password either fails later with a confusing error, or
    /// — against a Postgres still configured with <c>trust</c> — succeeds, and
    /// an install that authenticates by accident is worse than one that won't
    /// start.
    /// </exception>
    public static IReadOnlyList<Outcome> ApplyTo(IConfiguration config)
        => ApplyTo(config, File.ReadAllText);

    /// <summary>Testable seam: <paramref name="readFile"/> stands in for the
    /// filesystem.</summary>
    public static IReadOnlyList<Outcome> ApplyTo(IConfiguration config, Func<string, string> readFile)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(readFile);

        return
        [
            Apply(config, readFile, "coffer_app", AppConnectionKey, AppPasswordFileKey),
            Apply(config, readFile, "coffer_service", ServiceConnectionKey, ServicePasswordFileKey),
        ];
    }

    private static Outcome Apply(
        IConfiguration config,
        Func<string, string> readFile,
        string role,
        string connectionKey,
        string passwordFileKey)
    {
        var path = config[passwordFileKey];
        var connectionString = config[connectionKey];

        // No file configured: leave the connection string exactly as it came in.
        // An install upgrading from the env-var arrangement still works, and a
        // bare-metal operator who puts the password in the connection string by
        // hand is not forced into a file they don't want.
        if (string.IsNullOrWhiteSpace(path))
            return new Outcome(role, FromFile: false, InlinePasswordIgnored: false);

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"{passwordFileKey} is set but {connectionKey} is empty. The password file supplies only the " +
                $"password — host, database and username still have to come from the connection string.");

        string contents;
        try
        {
            contents = readFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The path is safe to name — it locates the secret, it isn't the
            // secret. Naming it is the difference between a one-minute fix and
            // an hour of guessing which of two files is wrong.
            throw new InvalidOperationException(
                $"Could not read the {role} password from '{path}' ({passwordFileKey}): {ex.Message}", ex);
        }

        // Trailing newline only. A file written by `echo` or a text editor ends
        // in one and it is never part of the password, but a full Trim() would
        // silently corrupt a password that legitimately starts or ends with a
        // space. This matches how the Postgres image's own *_FILE handling
        // treats secret files.
        var password = contents.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                $"The {role} password file '{path}' ({passwordFileKey}) is empty.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                $"{connectionKey} is not a valid Npgsql connection string: {ex.Message}", ex);
        }

        var inlineIgnored = !string.IsNullOrEmpty(builder.Password);
        builder.Password = password;
        config[connectionKey] = builder.ConnectionString;

        return new Outcome(role, FromFile: true, InlinePasswordIgnored: inlineIgnored);
    }
}
