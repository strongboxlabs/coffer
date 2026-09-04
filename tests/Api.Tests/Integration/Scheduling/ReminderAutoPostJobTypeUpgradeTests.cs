using Npgsql;

using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// Migration 219 rehearsed against an install that already has scheduled jobs.
/// </summary>
/// <remarks>
/// <para>
/// 219 replaces <c>ck_scheduled_jobs_type</c> — <c>DROP CONSTRAINT IF EXISTS</c> then a
/// validating <c>ADD</c> — so it re-checks every existing row on the way through. That
/// is a real upgrade action on real data, and the fresh-database schema stage cannot
/// exercise it because there are no rows there to re-check.
/// </para>
/// <para>
/// <b>Why 219 needs no fail-loud precheck, unlike 218.</b> 218 added a UNIQUE index over
/// columns that had NEVER been constrained, so the data could already violate it and the
/// migration had to stop and name the offender. 219's new value set is a strict SUPERSET
/// of the old one, and the old constraint was in force the whole time — so any row that
/// satisfied it satisfies the new one, and the validating ADD cannot fail on data the
/// database itself permitted. The only way to break it is to hand-drop the constraint
/// first and insert something arbitrary, which is not a state a migration owes a
/// courtesy message to.
/// </para>
/// <para>
/// So this rehearsal pins the three things that ARE claimed: the upgrade succeeds with
/// live rows present, those rows come through untouched, and the replaced constraint
/// actually bites afterwards.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderAutoPostJobTypeUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public ReminderAutoPostJobTypeUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig219 = "219_reminder_auto_post_job.sql";

    private static readonly Guid LedgerId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid UserId = new("66666666-6666-6666-6666-666666666666");

    /// <summary>Every column a scheduled job carries state in, ordered for a stable digest.</summary>
    private const string JobStateSql = """
        SELECT job_type, enabled, hour_local, minute_local, timezone,
               next_run_at, last_run_at, consecutive_failures, last_error, disabled_reason
          FROM scheduled_jobs
         ORDER BY job_type
        """;

    private (string ConnectionString, string WorkDir) FreshInstallBefore219(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig219-").FullName;
        var cs = _fixture.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig219, work));
        return (cs, work);
    }

    /// <summary>
    /// An install running all three pre-219 job types, with the state a real one carries:
    /// one healthy, one mid-failure-streak, one auto-disabled with a reason.
    /// </summary>
    private static async Task SeedLiveJobsAsync(string connectionString)
    {
        await ExecAsync(connectionString, $"""
            INSERT INTO ledgers (id, name) VALUES ('{LedgerId}', 'Home');
            INSERT INTO users (id, display_name, username, created_by)
            VALUES ('{UserId}', 'op', 'op-mig219', 'integration-test');
            INSERT INTO user_ledger_grants (user_id, ledger_id, role)
            VALUES ('{UserId}', '{LedgerId}', 'owner');

            INSERT INTO scheduled_jobs
                (ledger_id, job_type, enabled, hour_local, minute_local, timezone,
                 next_run_at, configured_by_user_id)
            VALUES ('{LedgerId}', 'quote-refresh', TRUE, 19, 0, 'America/New_York',
                    TIMESTAMPTZ '2026-09-04 23:00:00+00', '{UserId}');

            INSERT INTO scheduled_jobs
                (ledger_id, job_type, enabled, hour_local, minute_local, timezone,
                 next_run_at, consecutive_failures, last_error, configured_by_user_id)
            VALUES ('{LedgerId}', 'feed-sync', TRUE, 6, 30, 'UTC',
                    TIMESTAMPTZ '2026-09-04 06:30:00+00', 3, 'bank timed out', '{UserId}');

            INSERT INTO scheduled_jobs
                (ledger_id, job_type, enabled, hour_local, minute_local, timezone,
                 consecutive_failures, last_error, disabled_reason, configured_by_user_id)
            VALUES ('{LedgerId}', 'snapshot', FALSE, 3, 0, 'UTC',
                    5, 'disk full', 'consecutive-failures', '{UserId}');
            """);
    }

    /// <summary>
    /// The upgrade runs with live rows present, and leaves every one of them alone.
    /// </summary>
    /// <remarks>
    /// The digest is the assertion that matters. "The migration succeeded" would pass for
    /// one that dropped the constraint and rewrote or deleted rows on the way — and this
    /// table holds the failure streaks and the auto-disable reasons that decide whether a
    /// job ever runs again, so silently resetting them is a plausible and expensive bug.
    /// </remarks>
    [Fact]
    public async Task An_install_with_live_scheduled_jobs_upgrades_without_touching_them()
    {
        var (cs, work) = FreshInstallBefore219("mig219_live_jobs");
        await SeedLiveJobsAsync(cs);

        // Absent before, or the assertion after the apply proves nothing about the apply.
        Assert.False(
            await ConstraintAllowsAsync(cs, JobTypes.ReminderAutoPost),
            "staged short of 219 but the constraint already admits reminder-auto-post — "
            + "the rehearsal is not at the version it claims");

        var before = await UpgradeRehearsal.DigestAsync(cs, JobStateSql);

        UpgradeRehearsal.Apply(cs, work, Mig219);

        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(cs, JobStateSql));
        Assert.True(
            await ConstraintAllowsAsync(cs, JobTypes.ReminderAutoPost),
            "219 ran but the constraint still refuses reminder-auto-post");
    }

    /// <summary>
    /// The replaced constraint still REFUSES an unknown job type.
    /// </summary>
    /// <remarks>
    /// Without this, 219 could have dropped the constraint and never added it back — or
    /// added one admitting anything — and the test above would pass on both counts, since
    /// it only ever asks whether a value it wants is accepted.
    /// </remarks>
    [Fact]
    public async Task After_the_upgrade_an_unknown_job_type_is_still_refused()
    {
        var (cs, work) = FreshInstallBefore219("mig219_still_bites");
        await SeedLiveJobsAsync(cs);
        UpgradeRehearsal.Apply(cs, work, Mig219);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertJobAsync(cs, "not-a-real-job-type"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_scheduled_jobs_type", ex.ConstraintName);
    }

    /// <summary>
    /// Applied twice against the database it just changed.
    /// </summary>
    /// <remarks>
    /// <c>DROP CONSTRAINT IF EXISTS</c> then <c>ADD</c> is re-runnable by construction,
    /// but only while the <c>IF EXISTS</c> stays — drop that guard and the script becomes
    /// a one-shot that fails on the operator's next restore, long after the upgrade it was
    /// tested on.
    /// </remarks>
    [Fact]
    public async Task Applying_219_twice_leaves_the_constraint_intact()
    {
        var (cs, work) = FreshInstallBefore219("mig219_rerun");
        await SeedLiveJobsAsync(cs);

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig219);

        Assert.True(await ConstraintAllowsAsync(cs, JobTypes.ReminderAutoPost));
    }

    /// <summary>Can a row with this job_type be inserted, i.e. does the CHECK admit it?</summary>
    private static async Task<bool> ConstraintAllowsAsync(string connectionString, string jobType)
    {
        try
        {
            await InsertJobAsync(connectionString, jobType, probe: true);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return false;
        }
    }

    /// <summary>
    /// Insert a job row. <paramref name="probe"/> rolls it back, so asking whether the
    /// constraint admits a value does not leave a row behind that the digest would see.
    /// </summary>
    private static async Task InsertJobAsync(
        string connectionString, string jobType, bool probe = false)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO scheduled_jobs
                (ledger_id, job_type, enabled, hour_local, minute_local, configured_by_user_id)
            VALUES (@ledger, @jobType, FALSE, 5, 0, @user)
            """, connection, tx))
        {
            command.Parameters.AddWithValue("ledger", LedgerId);
            command.Parameters.AddWithValue("jobType", jobType);
            command.Parameters.AddWithValue("user", UserId);
            await command.ExecuteNonQueryAsync();
        }

        if (probe) await tx.RollbackAsync();
        else await tx.CommitAsync();
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
