using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Migration 218 rehearsed against installs that already exist — including the one it
/// is designed to refuse.
/// </summary>
/// <remarks>
/// <para>
/// 218 adds a UNIQUE index over data that has never been constrained, so unlike a
/// column addition it can FAIL on real data. It carries a DO block that is supposed to
/// stop first and name the offending series and date, because Postgres's own
/// "could not create unique index" reports an opaque tuple partway through an upgrade.
/// </para>
/// <para>
/// <b>That block has a failing branch, which makes it the interesting half.</b> A
/// precheck that never fires is indistinguishable from one that is broken: get the
/// GROUP BY or the partial-index predicates subtly wrong and it silently finds nothing,
/// the index build throws its own error instead, and the careful message is never seen.
/// The fresh-database schema stage cannot detect that — there is no data there to
/// duplicate. So the second test below deliberately builds the install 218 must reject.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderOccurrenceUniquenessUpgradeTests
{
    private readonly PostgresFixture _fixture;

    public ReminderOccurrenceUniquenessUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Mig218 = "218_reminder_occurrence_is_unique.sql";

    private static readonly Guid LedgerId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SeriesId = new("44444444-4444-4444-4444-444444444444");

    private async Task<(string ConnectionString, string WorkDir)> FreshInstallBefore218Async(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig218-").FullName;
        var cs = _fixture.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig218, work));
        return (cs, work);
    }

    /// <summary>
    /// A ledger and one reminder series — the shape an occurrence hangs off.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. Migration 124 slimmed recurring_transactions to recurrence
    /// metadata plus a template pointer, dropping description, amount and frequency, so a
    /// seed written from the ORIGINAL table definition fails with "column does not exist"
    /// rather than silently seeding something else.
    /// <para>
    /// No account is needed, but NOT because source_account_id is gone — 124 dropped it
    /// and 125 put it back, 126 again idempotently. It is nullable, which is why a seed
    /// can omit it. (An earlier version of this comment said it had been dropped, which
    /// reached the right conclusion for the wrong reason.)
    /// </para>
    /// </remarks>
    private static async Task SeedSeriesAsync(string connectionString)
    {
        await ExecAsync(connectionString, $"""
            INSERT INTO ledgers (id, name) VALUES ('{LedgerId}', 'Home');
            INSERT INTO recurring_transactions
                (id, ledger_id, start_date, rrule, next_due_date)
            VALUES ('{SeriesId}', '{LedgerId}', DATE '2026-01-01',
                    'FREQ=MONTHLY;BYMONTHDAY=1', DATE '2026-01-01');
            """);
    }

    private static Task SeedOccurrenceAsync(string connectionString, Guid headerId, string occurrenceDate)
        => ExecAsync(connectionString, $"""
            INSERT INTO txn_headers
                (id, ledger_id, origin, payee, posted_at, transacted_at,
                 is_recurring_template, recurring_transaction_id, occurrence_date)
            VALUES ('{headerId}', '{LedgerId}', 'manual', 'Rent',
                    TIMESTAMPTZ '{occurrenceDate} 00:00:00+00', TIMESTAMPTZ '{occurrenceDate} 00:00:00+00',
                    FALSE, '{SeriesId}', DATE '{occurrenceDate}');
            """);

    /// <summary>
    /// The ordinary upgrade: an install with real fired occurrences gains the index.
    /// </summary>
    [Fact]
    public async Task An_install_with_fired_occurrences_upgrades_and_gains_the_index()
    {
        var (cs, work) = await FreshInstallBefore218Async("mig218_clean");
        await SeedSeriesAsync(cs);
        await SeedOccurrenceAsync(cs, Guid.NewGuid(), "2026-01-01");
        await SeedOccurrenceAsync(cs, Guid.NewGuid(), "2026-02-01");

        // Absent first, or the assertion after the apply proves nothing about the apply.
        Assert.False(
            await IndexExistsAsync(cs),
            "staged short of 218 but its index is already present — the rehearsal is not "
            + "at the version it claims");

        UpgradeRehearsal.Apply(cs, work, Mig218);

        Assert.True(await IndexExistsAsync(cs), "218 ran but its unique index is not there");

        // And it now bites on this upgraded database, not merely exists.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => SeedOccurrenceAsync(cs, Guid.NewGuid(), "2026-01-01"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    /// <summary>
    /// The install 218 must refuse: two committed transactions already share one
    /// occurrence slot.
    /// </summary>
    /// <remarks>
    /// This is the DO block's only reason to exist, and the only test that can tell a
    /// working precheck from a broken one. The assertion is on the MESSAGE, not just on
    /// the throw: an index-build failure also throws, so "it threw" would pass with the
    /// precheck deleted and the operator would get the opaque error 218 was written to
    /// replace.
    /// </remarks>
    [Fact]
    public async Task An_install_that_already_double_posted_one_occurrence_is_refused_by_name()
    {
        var (cs, work) = await FreshInstallBefore218Async("mig218_duplicate");
        await SeedSeriesAsync(cs);
        await SeedOccurrenceAsync(cs, Guid.NewGuid(), "2026-01-01");
        await SeedOccurrenceAsync(cs, Guid.NewGuid(), "2026-01-01");   // the same slot, twice

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => Task.Run(() => UpgradeRehearsal.Apply(cs, work, Mig218)));

        var message = Flatten(ex);
        Assert.Contains(SeriesId.ToString(), message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-01-01", message, StringComparison.Ordinal);
        Assert.Contains("re-run the upgrade", message, StringComparison.OrdinalIgnoreCase);

        // Nothing half-applied: the migration is inside one BEGIN/COMMIT, so the index
        // must not exist after the failure.
        Assert.False(
            await IndexExistsAsync(cs),
            "218 failed but left its index behind — the script is not atomic");
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" || ", parts);
    }

    private static async Task<bool> IndexExistsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE indexname = 'uq_txn_headers_recurring_occurrence'",
            connection);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
