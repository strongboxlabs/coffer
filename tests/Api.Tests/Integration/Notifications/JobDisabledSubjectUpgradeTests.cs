using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// Migration 267 rehearsed against an install that already holds job-disabled events.
/// </summary>
/// <remarks>
/// <para>
/// Every disable used to be published as <c>scheduler.job-disabled</c>, a subject nothing
/// ever clears. 267 moves each such row to its job's own subject, read out of the row's
/// summary, so the job's next <c>&lt;job type&gt;.succeeded</c> clears it like any other
/// job warning. A fresh database has no such rows, so only a rehearsal on seeded data
/// exercises the rewrite.
/// </para>
/// <para>
/// Pinned: both scopes are rewritten, a row whose summary is not the scheduler's is left
/// alone, every other column and every other row is untouched, and a second run changes
/// nothing.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class JobDisabledSubjectUpgradeTests : DatabaseTest
{
    public JobDisabledSubjectUpgradeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Mig267 = "267_job_disabled_per_job_subject.sql";

    private static readonly Guid LedgerId = new("26726726-7267-2672-6726-726726726701");

    private const string LedgerEventsSql = """
        SELECT event_key, severity, topic, summary, occurred_at
          FROM ledger_events
         ORDER BY occurred_at
        """;

    private const string SystemEventsSql = """
        SELECT event_key, severity, topic, summary, occurred_at
          FROM system_events
         ORDER BY occurred_at
        """;

    private (string ConnectionString, string WorkDir) FreshInstallBefore267(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig267-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig267, work));
        return (cs, work);
    }

    /// <summary>
    /// A disabled feed sync and a disabled backup as the scheduler wrote them, a row
    /// that shares the key but not the summary, and unrelated events in both scopes.
    /// </summary>
    private static Task SeedAsync(string connectionString) => ExecAsync(connectionString, $"""
        INSERT INTO ledgers (id, name) VALUES ('{LedgerId}', 'Home');

        INSERT INTO ledger_events (ledger_id, occurred_at, severity, topic, event_key, summary)
        VALUES
          ('{LedgerId}', TIMESTAMPTZ '2026-09-01 06:00:00+00', 'warning', 'consistency',
           'consistency.drift', 'Balances disagree.'),
          ('{LedgerId}', TIMESTAMPTZ '2026-09-02 06:00:00+00', 'critical', 'scheduler',
           'scheduler.job-disabled',
           'Scheduled feed-sync has been switched OFF after 5 consecutive failures. It will not run again until it is re-enabled. See the server log.'),
          ('{LedgerId}', TIMESTAMPTZ '2026-09-03 06:00:00+00', 'critical', 'scheduler',
           'scheduler.job-disabled', 'Not the scheduler''s wording.');

        INSERT INTO system_events (occurred_at, severity, topic, event_key, summary)
        VALUES
          (TIMESTAMPTZ '2026-09-01 03:00:00+00', 'info', 'backup',
           'backup.succeeded', 'Backup completed.'),
          (TIMESTAMPTZ '2026-09-02 03:00:00+00', 'critical', 'scheduler',
           'scheduler.job-disabled',
           'Scheduled backup has been switched OFF after 5 consecutive failures. It will not run again until it is re-enabled. See the server log.');
        """);

    [Fact]
    public async Task Each_disable_moves_to_its_jobs_subject_and_nothing_else_changes()
    {
        var (cs, work) = FreshInstallBefore267("mig267_rewrite");
        await SeedAsync(cs);

        var ledgerBefore = await KeysAsync(cs, LedgerEventsSql);
        var systemBefore = await KeysAsync(cs, SystemEventsSql);
        Assert.Equal(
            ["consistency.drift", "scheduler.job-disabled", "scheduler.job-disabled"],
            ledgerBefore);
        Assert.Equal(["backup.succeeded", "scheduler.job-disabled"], systemBefore);
        var ledgerRest = await UpgradeRehearsal.DigestAsync(cs, RestOf(LedgerEventsSql));
        var systemRest = await UpgradeRehearsal.DigestAsync(cs, RestOf(SystemEventsSql));

        UpgradeRehearsal.Apply(cs, work, Mig267);

        Assert.Equal(
            ["consistency.drift", "feed-sync.disabled", "scheduler.job-disabled"],
            await KeysAsync(cs, LedgerEventsSql));
        Assert.Equal(["backup.succeeded", "backup.disabled"], await KeysAsync(cs, SystemEventsSql));

        // Only the key moved: severity, topic, summary and time come through as they were.
        Assert.Equal(ledgerRest, await UpgradeRehearsal.DigestAsync(cs, RestOf(LedgerEventsSql)));
        Assert.Equal(systemRest, await UpgradeRehearsal.DigestAsync(cs, RestOf(SystemEventsSql)));
    }

    [Fact]
    public async Task Applying_267_twice_changes_nothing_the_first_run_did_not()
    {
        var (cs, work) = FreshInstallBefore267("mig267_rerun");
        await SeedAsync(cs);

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig267);

        Assert.Equal(
            ["consistency.drift", "feed-sync.disabled", "scheduler.job-disabled"],
            await KeysAsync(cs, LedgerEventsSql));
        Assert.Equal(["backup.succeeded", "backup.disabled"], await KeysAsync(cs, SystemEventsSql));
    }

    /// <summary>The same rows without their key, for the "nothing else changed" digest.</summary>
    private static string RestOf(string sql) =>
        sql.Replace("SELECT event_key, ", "SELECT ", StringComparison.Ordinal);

    private static async Task<List<string>> KeysAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var keys = new List<string>();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
