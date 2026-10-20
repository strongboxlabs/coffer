using Microsoft.Extensions.Configuration;

using Npgsql;

namespace Coffer.Api.Configuration;

/// <summary>
/// Pins the PostgreSQL <c>TimeZone</c> session parameter to UTC on every
/// connection string the API uses.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. Postgres evaluates date-part extraction on a
/// <c>timestamptz</c> in the SESSION timezone. Every reporting aggregation
/// groups by <c>EXTRACT(YEAR/MONTH/DAY FROM posted_at)</c>, so the session
/// timezone silently decides which month — and, once a daily bucket exists,
/// which DAY — a transaction is counted in. Nothing set it. The app agreed with
/// its own UTC convention only because <c>postgres:16</c> happens to default to
/// UTC, and that is a coincidence the deployment could change without anyone
/// noticing: a container with <c>TZ</c> set, a managed Postgres with a regional
/// default, an <c>ALTER ROLE … SET TimeZone</c>, or a future base image. The
/// symptom would be a month's spending totals quietly shifted by the
/// transactions near each boundary, with nothing red anywhere.
/// </para>
/// <para>
/// WHY THE CONNECTION STRING AND NOT A <c>SET</c>. The first cut of this issued
/// <c>set_config('TimeZone', 'UTC', false)</c> from the connection interceptor,
/// which meant raw SQL in <c>src/Api</c> — banned by ADR-0005, and the audit
/// correctly refused it. Npgsql's <c>Timezone</c> keyword expresses the same
/// intent declaratively: it travels in the startup packet, so the parameter is
/// set before the connection can run anything at all. That is strictly stronger
/// than an interceptor hook, which fires per logical open and therefore has to
/// be re-issued and kept in step with the test fixture's own interceptor. Here
/// there is one mechanism and nothing to mirror.
/// </para>
/// <para>
/// This runs at the CONFIGURATION layer alongside
/// <see cref="DbPasswordResolver"/>, before <see cref="ApiOptions"/> binds, so
/// every consumer of either string — EF, the backup service, the importer, the
/// migration runner — inherits the pin without knowing it exists.
/// </para>
/// <para>
/// UNCONDITIONAL, deliberately. <see cref="DbPasswordResolver"/> returns early
/// when no password file is configured, which is a legitimate arrangement; a
/// correctness guarantee that held only for file-secret installs would not be
/// one. On a UTC-defaulting server this is a behavioural no-op, and that is the
/// point — it converts an accident into a promise, and the timezone test fails
/// closed if the promise is ever broken.
/// </para>
/// </remarks>
public static class DbSessionTimeZone
{
    /// <summary>
    /// The pinned zone, in the Olson/IANA form Npgsql's <c>Timezone</c> keyword
    /// expects. UTC because every instant the app stores is UTC and every
    /// calendar boundary it reports is computed in C# as UTC; the database
    /// agreeing is what makes the two consistent.
    /// </summary>
    public const string Zone = "UTC";

    /// <summary>
    /// What happened for one connection string, so startup can log it. A
    /// non-null <see cref="OverriddenZone"/> means the configured string named
    /// a DIFFERENT zone and we replaced it — worth saying out loud, because
    /// someone set it on purpose and is about to find it ignored.
    /// </summary>
    public sealed record Outcome(string Key, string? OverriddenZone);

    /// <summary>
    /// Rewrites both connection strings in <paramref name="config"/> to carry
    /// <c>Timezone=UTC</c>. Returns one <see cref="Outcome"/> per string that
    /// was present; a string that is not configured is skipped rather than
    /// invented.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A configured connection string is not parseable. Failing here is better
    /// than at first query: this runs at startup, names the offending key, and
    /// cannot be mistaken for a database outage.
    /// </exception>
    public static IReadOnlyList<Outcome> ApplyTo(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var outcomes = new List<Outcome>(2);
        foreach (var key in new[]
                 {
                     DbPasswordResolver.AppConnectionKey,
                     DbPasswordResolver.ServiceConnectionKey,
                 })
        {
            if (Pin(config, key) is { } outcome) outcomes.Add(outcome);
        }

        return outcomes;
    }

    private static Outcome? Pin(IConfiguration config, string key)
    {
        var connectionString = config[key];
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                $"{key} is not a valid Npgsql connection string: {ex.Message}", ex);
        }

        var existing = builder.Timezone;
        var overridden = !string.IsNullOrEmpty(existing) && existing != Zone ? existing : null;

        builder.Timezone = Zone;
        config[key] = builder.ConnectionString;

        return new Outcome(key, overridden);
    }
}
