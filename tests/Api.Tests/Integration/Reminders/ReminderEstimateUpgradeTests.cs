using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Migration 220 rehearsed against an install that already has reminders.
/// </summary>
/// <remarks>
/// <para>
/// <b>220 cannot fail on real data, and that is worth stating rather than assuming.</b>
/// It adds a NULLABLE column, a CHECK that every existing row satisfies vacuously (they
/// are all NULL), a partial index, and a CREATE OR REPLACE FUNCTION. Unlike 218 — which
/// added a UNIQUE index over columns that had never been constrained, and so needed a
/// fail-loud precheck naming the offender — there is no data 220 can be inconsistent
/// with.
/// </para>
/// <para>
/// So this rehearsal proves the thing that CAN go wrong: that nobody's amounts change.
/// If the column arrived with a non-null default, every existing reminder would start
/// estimating on the next agenda load and the next auto-post run — a silent,
/// ledger-wide behaviour change from an upgrade nobody asked to be one. That is the
/// assertion below, and it is cheap.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderEstimateUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public ReminderEstimateUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig220 = "220_reminder_estimated_amounts.sql";

    private static readonly Guid LedgerId = new("77777777-7777-7777-7777-777777777777");
    private static readonly Guid AccountId = new("88888888-8888-8888-8888-888888888888");
    private static readonly Guid SeriesId = new("99999999-9999-9999-9999-999999999999");

    private (string ConnectionString, string WorkDir) FreshInstallBefore220(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig220-").FullName;
        var cs = _fixture.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig220, work));
        return (cs, work);
    }

    /// <summary>An install with a reminder series and two committed occurrences behind it.</summary>
    private static async Task SeedSeriesWithHistoryAsync(string connectionString)
    {
        await ExecAsync(connectionString, $"""
            INSERT INTO ledgers (id, name) VALUES ('{LedgerId}', 'Home');
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code)
            VALUES ('{AccountId}', '{LedgerId}', 'Checking', 'bank', 'USD');
            INSERT INTO recurring_transactions
                (id, ledger_id, source_account_id, start_date, rrule, next_due_date)
            VALUES ('{SeriesId}', '{LedgerId}', '{AccountId}', DATE '2026-01-01',
                    'FREQ=MONTHLY;BYMONTHDAY=1', DATE '2026-03-01');
            """);

        await SeedOccurrenceAsync(connectionString, "2026-01-01", -40m);
        await SeedOccurrenceAsync(connectionString, "2026-02-01", -60m);
    }

    private static Task SeedOccurrenceAsync(string connectionString, string date, decimal sourceNet)
        => ExecAsync(connectionString, $"""
            WITH h AS (
                INSERT INTO txn_headers
                    (ledger_id, origin, payee, posted_at, transacted_at,
                     is_recurring_template, recurring_transaction_id, occurrence_date)
                VALUES ('{LedgerId}', 'manual', 'Electric',
                        TIMESTAMPTZ '{date} 00:00:00+00', TIMESTAMPTZ '{date} 00:00:00+00',
                        FALSE, '{SeriesId}', DATE '{date}')
                RETURNING id
            )
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT gen_random_uuid(), h.id, '{LedgerId}', '{AccountId}', 0, {sourceNet}
              FROM h;
            """);

    /// <summary>
    /// Existing reminders come through with estimation OFF.
    /// </summary>
    /// <remarks>
    /// The assertion that matters. A non-null default on the new column would turn
    /// estimation on for every reminder on every install, changing amounts on the agenda
    /// and in what auto-post commits, with nothing in the release notes to warn anyone.
    /// </remarks>
    [Fact]
    public async Task Existing_reminders_come_through_with_estimation_off()
    {
        var (cs, work) = FreshInstallBefore220("mig220_off_by_default");
        await SeedSeriesWithHistoryAsync(cs);

        Assert.False(
            await ColumnExistsAsync(cs, "recurring_transactions", "estimate_sample_count"),
            "staged short of 220 but the column is already there — the rehearsal is not at "
            + "the version it claims");

        UpgradeRehearsal.Apply(cs, work, Mig220);

        Assert.True(await ColumnExistsAsync(cs, "recurring_transactions", "estimate_sample_count"));

        // Off, for the series that existed before the upgrade.
        Assert.Equal(
            0,
            await ScalarAsync(cs,
                "SELECT count(*) FROM recurring_transactions WHERE estimate_sample_count IS NOT NULL"));
    }

    /// <summary>
    /// The samples function reads history that PRE-DATES it.
    /// </summary>
    /// <remarks>
    /// The occurrences an estimate averages were written by earlier releases, so the
    /// function has to work against rows nothing in this migration created. Asserting the
    /// sum and count rather than an average, because that is what it returns — the
    /// division is C#'s, at the 2dp destination scale.
    /// </remarks>
    [Fact]
    public async Task The_samples_function_reads_pre_existing_occurrences()
    {
        var (cs, work) = FreshInstallBefore220("mig220_reads_history");
        await SeedSeriesWithHistoryAsync(cs);
        UpgradeRehearsal.Apply(cs, work, Mig220);

        // Turn estimation on the way the API would, then ask the function.
        await ExecAsync(cs,
            $"UPDATE recurring_transactions SET estimate_sample_count = 2 WHERE id = '{SeriesId}';");

        Assert.Equal(
            2,
            await ScalarAsync(cs,
                $"SELECT sample_count FROM reminder_estimate_samples('{LedgerId}', '{SeriesId}')"));

        // -40 + -60. The mean is C#'s to compute.
        Assert.Equal(
            -100,
            await ScalarAsync(cs,
                $"SELECT sample_sum FROM reminder_estimate_samples('{LedgerId}', '{SeriesId}')"));
    }

    /// <summary>
    /// The CHECK refuses a sample size outside 1-24 after the upgrade.
    /// </summary>
    /// <remarks>
    /// Without this, 220 could have added the column and skipped the constraint, and the
    /// test above would still pass — it only ever writes a value the constraint permits.
    /// </remarks>
    [Fact]
    public async Task After_the_upgrade_an_out_of_range_sample_size_is_refused()
    {
        var (cs, work) = FreshInstallBefore220("mig220_check_bites");
        await SeedSeriesWithHistoryAsync(cs);
        UpgradeRehearsal.Apply(cs, work, Mig220);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs,
            $"UPDATE recurring_transactions SET estimate_sample_count = 99 WHERE id = '{SeriesId}';"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_recurring_transactions_estimate_sample_count", ex.ConstraintName);
    }

    /// <summary>Applied twice against the database it just changed.</summary>
    [Fact]
    public async Task Applying_220_twice_is_harmless()
    {
        var (cs, work) = FreshInstallBefore220("mig220_rerun");
        await SeedSeriesWithHistoryAsync(cs);

        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig220);

        Assert.True(await ColumnExistsAsync(cs, "recurring_transactions", "estimate_sample_count"));
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns WHERE table_name = @t AND column_name = @c",
            connection);
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("c", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<decimal> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
