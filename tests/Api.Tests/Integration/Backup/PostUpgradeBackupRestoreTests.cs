using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Backup;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Schema;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// A whole-database backup taken by this build, after its upgrade, restores on this
/// build: its journal carries the C# migration steps as well as the SQL files, and a
/// restore that knew only the SQL files would refuse it as coming from a newer build. And
/// a full backup taken before migration 301 is brought through it by the same step,
/// keeping every figure, the same whenever it runs (ADR-0114 D15, "The upgrade").
/// </summary>
/// <remarks>
/// The database here is what a restore has once the dump is loaded: migrated by this
/// build and holding data. What the restore does next with it is
/// <see cref="BackupService.BringRestoredDatabaseUpToDate"/>. The dump, the load and the
/// swap need the Postgres client tools and rename the live database, so they are not run
/// here; restoring a backup taken after the upgrade end to
/// end is the restore drill's, on the release candidate (the upgrade drill restores one).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PostUpgradeBackupRestoreTests : DatabaseTest
{
    public PostUpgradeBackupRestoreTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_backup_taken_after_the_upgrade_restores_and_one_from_a_newer_build_is_refused()
    {
        var work = Directory.CreateTempSubdirectory("coffer-post-upgrade-").FullName;
        var cs = Db.EmptyDatabaseConnectionString("post_upgrade_backup");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageAll(work));
        await ExecAsync(cs, "INSERT INTO ledgers (id, name) VALUES ('26400000-0000-0000-0000-000000000901', 'Ledger A');");

        var journal = await JournalAsync(cs);
        Assert.All(MigrationRunner.CSharpSteps, s => Assert.Contains(s.Name, journal));
        // The set a restore used to know: the SQL files alone call the steps unknown.
        Assert.NotEmpty(BackupService.UnknownMigrations(
            journal, Directory.GetFiles(work, "*.sql").Select(f => Path.GetFileName(f)!)));

        // The runner's database-exists check pools where clearing the pools does not reach,
        // so it gets the unpooled string every migration run in these tests gets.
        var runner = TestDatabaseCluster.ForMigrationRunner(cs);
        BackupService.BringRestoredDatabaseUpToDate(journal, runner, work, NullLogger.Instance);
        Assert.Equal(journal, await JournalAsync(cs));

        var refused = Assert.Throws<BackupException>(() =>
            BackupService.BringRestoredDatabaseUpToDate([.. journal, "999_from_a_newer_build.sql"], runner, work, NullLogger.Instance));
        Assert.Contains("999_from_a_newer_build.sql", refused.Message, StringComparison.Ordinal);
    }

    private const string Mig300 = "300_securities_quote_currency_confirmed_at.sql";
    private const string Mig301 = "301_each_currency_keeps_its_minor_units.sql";

    private static readonly Guid FundUnitBank = new("30100000-0000-4000-8000-000000000014");
    private static readonly Guid NonIsoBank = new("30100000-0000-4000-8000-000000000015");
    private static readonly Guid FundUnitHeader = new("30100000-0000-4000-8000-000000000045");
    private static readonly Guid NonIsoHeader = new("30100000-0000-4000-8000-000000000046");

    /// <summary>Every figure 301 could touch, and the codes it reads, by value: a decimal
    /// compares by value, so the scale the money columns widen to does not count.</summary>
    private static readonly string[] SeededValues =
    [
        "SELECT id, amount FROM txn_legs ORDER BY id",
        "SELECT id, ledger_id, category_id, target_month, amount, currency_code, created_at, updated_at FROM budget_targets ORDER BY id",
        """
        SELECT id, sell_leg_id, sold_at, quantity, proceeds, proceeds_lt, cost_basis_sold, cost_basis_sold_lt,
               realized_gain, realized_gain_lt, created_at
          FROM realized_gains ORDER BY id
        """,
        """
        SELECT header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number, captured_at
          FROM txn_header_originals ORDER BY header_id
        """,
        "SELECT id, currency_code, opening_balance FROM accounts ORDER BY id",
        "SELECT id, currency_code FROM ledgers ORDER BY id",
    ];

    /// <summary>
    /// A full backup taken before 301 by the code of its day, with accounts in a 0-, a 2-
    /// and a 3-place currency and in codes an account can no longer take, is brought
    /// through 301 by the restore's own step and keeps every figure it holds.
    /// </summary>
    /// <remarks>The ADR-0114 D15 upgrade says an old full backup migrates forward at
    /// restore, so 301 must apply to any historic backup. A validated per-currency CHECK on
    /// budget_targets fails here on the 2-place target in the 0-place ledger, and a
    /// rounding UPDATE changes a figure this compares.</remarks>
    [Fact]
    public async Task An_old_backup_with_zero_and_three_place_currencies_migrates_forward_through_301_and_keeps_every_figure()
    {
        var (cs, work, journal) = await OldBackupAsync("pre301_restore");

        // ---- The backup, as it was taken -------------------------------------
        Assert.Equal(Mig300, journal.Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => Path.GetFileName(n)).Order(StringComparer.Ordinal).Last());
        Assert.DoesNotContain(journal, n => n.EndsWith(Mig301, StringComparison.Ordinal));
        Assert.Equal(["BHD", "CLF", "JPY", "USD", "XAU", "ZZZ"], await ListAsync(cs,
            "SELECT DISTINCT currency_code FROM accounts ORDER BY 1"));
        // A 2-place target in the 0-place ledger, and a gain stored at 2 places on a
        // 0-place account: both are figures a per-currency rule would refuse or round.
        Assert.Equal(["JPY|12.50"], await ListAsync(cs,
            "SELECT b.currency_code || '|' || b.amount FROM budget_targets b"));
        Assert.Contains("1000.50", await ListAsync(cs, "SELECT cost_basis_sold::text FROM realized_gains"));
        var before = new List<List<object?[]>>();
        foreach (var sql in SeededValues) before.Add(await RowsAsync(cs, sql));
        Assert.All(before, rows => Assert.NotEmpty(rows));

        // ---- The restore's step ----------------------------------------------
        UpgradeRehearsal.StageAll(work);
        BackupService.BringRestoredDatabaseUpToDate(
            journal, TestDatabaseCluster.ForMigrationRunner(cs), work, NullLogger.Instance);

        Assert.Contains(await JournalAsync(cs), n => n.EndsWith(Mig301, StringComparison.Ordinal));
        for (var i = 0; i < SeededValues.Length; i++)
            Assert.Equal(before[i], await RowsAsync(cs, SeededValues[i]));
    }

    /// <summary>
    /// The same old backup restored forward twice, at two moments, comes out the same:
    /// 301 reads no clock, so nothing it writes depends on when the restore ran.
    /// </summary>
    /// <remarks>Each digest is over whole rows, so a column 301 added with a clock
    /// default counts as well as a stamp written into an existing one. A realized_gains
    /// row is compared without its key and stamp, which the recompute gave it when the
    /// backup was seeded; the fact above compares them within one database.</remarks>
    [Fact]
    public async Task An_old_backup_restored_forward_twice_at_two_moments_comes_out_the_same()
    {
        var (first, firstWork, firstJournal) = await OldBackupAsync("pre301_first");
        var (second, secondWork, secondJournal) = await OldBackupAsync("pre301_second");
        string[] digests =
        [
            "SELECT to_jsonb(t)::text FROM txn_legs t ORDER BY id",
            "SELECT to_jsonb(t)::text FROM budget_targets t ORDER BY id",
            "SELECT (to_jsonb(t) - 'id' - 'created_at')::text FROM realized_gains t ORDER BY sell_leg_id",
            "SELECT to_jsonb(t)::text FROM txn_header_originals t ORDER BY header_id",
        ];
        foreach (var sql in digests)
        {
            Assert.NotEmpty(await ListAsync(first, sql));
            Assert.Equal(await UpgradeRehearsal.DigestAsync(first, sql), await UpgradeRehearsal.DigestAsync(second, sql));
        }

        // The second restore starts once the first has committed, so any clock 301
        // read would read a later instant there.
        UpgradeRehearsal.StageAll(firstWork);
        BackupService.BringRestoredDatabaseUpToDate(
            firstJournal, TestDatabaseCluster.ForMigrationRunner(first), firstWork, NullLogger.Instance);
        UpgradeRehearsal.StageAll(secondWork);
        BackupService.BringRestoredDatabaseUpToDate(
            secondJournal, TestDatabaseCluster.ForMigrationRunner(second), secondWork, NullLogger.Instance);

        foreach (var cs in new[] { first, second })
            Assert.Contains(await JournalAsync(cs), n => n.EndsWith(Mig301, StringComparison.Ordinal));
        foreach (var sql in digests)
            Assert.Equal(await UpgradeRehearsal.DigestAsync(first, sql), await UpgradeRehearsal.DigestAsync(second, sql));
    }

    /// <summary>
    /// A database as a restore holds it once an old dump is loaded: migrated to 300 by
    /// the code before 301 and seeded the way that code wrote, with
    /// <c>Migration301UpgradeTests</c>' install plus two codes an account can no longer
    /// take beside its N.A. one: a fund unit with more places than the ceiling, and a
    /// code List One does not hold. Free text, as accounts.currency_code has no CHECK.
    /// </summary>
    private async Task<(string ConnectionString, string WorkDir, List<string> Journal)> OldBackupAsync(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-old-backup-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig301, work));
        await Migration301UpgradeTests.SeedAsync(cs);
        var ledger = Migration301UpgradeTests.Ledger;
        await ExecAsync(cs, $"""
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code) VALUES
                ('{FundUnitBank}', '{ledger}', 'Fund-unit bank', 'bank', 'CLF'),
                ('{NonIsoBank}',   '{ledger}', 'Non-ISO bank',   'bank', 'ZZZ');
            """);
        await Migration301UpgradeTests.BankPostingAsync(cs, FundUnitHeader, FundUnitBank, -7.89m, "2026-01-06");
        await Migration301UpgradeTests.BankPostingAsync(cs, NonIsoHeader, NonIsoBank, -0.01m, "2026-01-07");
        return (cs, work, await JournalAsync(cs));
    }

    /// <summary>Every row <paramref name="sql"/> returns, each column as its .NET value.</summary>
    private static async Task<List<object?[]>> RowsAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<List<string>> ListAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<List<string>> JournalAsync(string cs)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT scriptname FROM public.__schema_migrations ORDER BY schemaversionsid", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
