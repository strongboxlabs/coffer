using Npgsql;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Migration 248 rehearsed against installs as they exist in the field (ADR-0105).
/// </summary>
/// <remarks>
/// <para>
/// 248 acts on rows a fresh database does not have: it adds a NOT NULL column to every
/// existing session, re-checks every pending challenge against a replaced constraint, and
/// writes the setup marker only on an install that already has a user. The suite's
/// fixtures start at head, where all of that has already happened, so without this a
/// green run says nothing about the upgrade.
/// </para>
/// <para>
/// The marker is the claim with teeth. Absent on an install that has users, the API
/// would offer pre-auth setup, and its whole-database restore, on a live deployment the
/// moment its last passkey went. Present on a fresh install, setup could never run.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SessionAuthMethodUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public SessionAuthMethodUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig248 = "248_session_auth_method_and_disabled_users.sql";
    private const string Mig249 = "249_setup_marker_deterministic.sql";

    /// <summary>When the seeded install's first user was created: when its setup completed.</summary>
    private static readonly DateTime FirstUserCreated = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly Guid UserId = new("24824824-8248-2482-4824-824824824801");
    private static readonly Guid SessionId = new("24824824-8248-2482-4824-824824824802");
    private static readonly Guid ChallengeId = new("24824824-8248-2482-4824-824824824803");

    /// <summary>Every column a session carries state in, apart from the new one.</summary>
    private const string SessionStateSql = """
        SELECT id, user_id, session_hash, user_agent, created_at, last_seen_at,
               expires_at, revoked_at
          FROM auth_sessions
         ORDER BY id
        """;

    private (string ConnectionString, string WorkDir) InstallBefore248(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig248-").FullName;
        var cs = _fixture.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig248, work));
        return (cs, work);
    }

    /// <summary>A signed-in user mid-way through signing in on a second device.</summary>
    private static Task SeedLiveInstallAsync(string connectionString) => ExecAsync(connectionString, $"""
        INSERT INTO users (id, display_name, username, created_by, created_at)
        VALUES ('{UserId}', 'op', 'op-mig248', 'integration-test',
                TIMESTAMPTZ '{FirstUserCreated:yyyy-MM-dd HH:mm:ss}+00');

        INSERT INTO auth_sessions (id, user_id, session_hash, user_agent, expires_at)
        VALUES ('{SessionId}', '{UserId}', decode(repeat('ab', 32), 'hex'), 'browser',
                TIMESTAMPTZ '2031-01-01 00:00:00+00');

        INSERT INTO webauthn_pending_challenges (id, flow, user_id, options_json, expires_at)
        VALUES ('{ChallengeId}', 'login', '{UserId}', '[]',
                TIMESTAMPTZ '2031-01-01 00:00:00+00');
        """);

    [Fact]
    public async Task An_install_with_a_user_keeps_its_sessions_and_gets_the_setup_marker()
    {
        var (cs, work) = InstallBefore248("mig248_live");
        await SeedLiveInstallAsync(cs);

        // Absent before, or the assertions after the apply prove nothing about it.
        Assert.False(await SetupMarkedAsync(cs),
            "staged short of 248 but the setup marker already exists");
        var before = await UpgradeRehearsal.DigestAsync(cs, SessionStateSql);

        UpgradeRehearsal.Apply(cs, work, Mig248);

        // Nobody is signed out by the upgrade, and nothing about a session changes
        // except that it now says how it began. 'passkey' grants nothing extra.
        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(cs, SessionStateSql));
        Assert.Equal("passkey", await ScalarAsync<string>(cs,
            $"SELECT auth_method FROM auth_sessions WHERE id = '{SessionId}'"));
        // The pending sign-in survives the replaced constraint.
        Assert.Equal(1L, await ScalarAsync<long>(cs,
            $"SELECT count(*) FROM webauthn_pending_challenges WHERE id = '{ChallengeId}'"));
        Assert.True(await SetupMarkedAsync(cs),
            "an install with a user upgraded without the setup marker: losing the last "
            + "passkey would reopen pre-auth setup and restore");
    }

    [Fact]
    public async Task The_marker_says_when_setup_completed_however_late_the_upgrade_runs()
    {
        // 248 alone stamped the marker with now(), so the same data migrated at two
        // moments gave two different rows; the restore drill caught it, comparing a
        // backup migrated at restore time with a database migrated at upgrade time.
        // 249 makes the result a function of the data: when the first user arrived.
        var (cs, work) = InstallBefore248("mig248_marker_time");
        await SeedLiveInstallAsync(cs);

        UpgradeRehearsal.Apply(cs, work, Mig248);
        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig249);

        Assert.Equal(FirstUserCreated, await ScalarAsync<DateTime>(cs,
            "SELECT updated_at FROM system_settings WHERE key = 'setup.completed'"));
    }

    [Fact]
    public async Task A_fresh_install_gets_no_setup_marker()
    {
        // Only the system user, as a new install has before anyone completes setup.
        var (cs, work) = InstallBefore248("mig248_fresh");

        UpgradeRehearsal.Apply(cs, work, Mig248);

        Assert.False(await SetupMarkedAsync(cs),
            "a fresh install was marked as set up, so setup could never run");
    }

    [Fact]
    public async Task Every_step_up_action_has_its_flow_and_an_unknown_one_is_refused()
    {
        var (cs, work) = InstallBefore248("mig248_flows");
        await SeedLiveInstallAsync(cs);
        UpgradeRehearsal.Apply(cs, work, Mig248);

        // Driven from the code's own list, so an action added without widening the
        // constraint fails here rather than as a 500 on its first tap.
        foreach (var action in StepUpActions.All)
            await InsertChallengeAsync(cs, StepUpActions.FlowFor(action));

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertChallengeAsync(cs, StepUpActions.FlowFor("not-a-real-action")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("webauthn_pending_challenges_flow_check", ex.ConstraintName);
    }

    private static async Task<bool> SetupMarkedAsync(string connectionString) =>
        await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM system_settings WHERE key = 'setup.completed' AND value = 'true'::jsonb") == 1;

    /// <summary>Insert a challenge for <paramref name="flow"/> and roll it back.</summary>
    private static async Task InsertChallengeAsync(string connectionString, string flow)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO webauthn_pending_challenges (id, flow, user_id, options_json, expires_at)
            VALUES (gen_random_uuid(), @flow, @user, '{}', TIMESTAMPTZ '2031-01-01 00:00:00+00')
            """, connection, tx))
        {
            command.Parameters.AddWithValue("flow", flow);
            command.Parameters.AddWithValue("user", UserId);
            await command.ExecuteNonQueryAsync();
        }
        await tx.RollbackAsync();
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
