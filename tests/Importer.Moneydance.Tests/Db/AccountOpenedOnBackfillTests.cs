using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Migration 196 — backfill <c>accounts.opened_on</c> from the MD payload already
/// stored on <c>provider_raw_payload</c> (mig 110 / ADR-0035 §3).
///
/// Teaching the importer to read MD's creation stamp only helps ledgers imported
/// AFTER that change: MD import is a one-shot bootstrap of a new ledger, with no
/// re-import path onto an existing one. The migration is what reaches an
/// already-imported ledger, so its behaviour is worth pinning — it is a one-way
/// data fix that cannot be re-run against production to correct a mistake.
///
/// The fixture applies every migration at startup, against an empty table. These
/// tests seed rows and re-execute the same file, which is exactly the idempotent
/// re-run the migration is written to tolerate.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class AccountOpenedOnBackfillTests
{
    private readonly PostgresFixture _fixture;

    public AccountOpenedOnBackfillTests(PostgresFixture fixture) => _fixture = fixture;

    private static string MigrationSql()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "db", "migrations",
                "196_backfill_account_opened_on_from_md_payload.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate migration 196.");
    }

    private static async Task RunBackfillAsync(NpgsqlConnection connection) =>
        await connection.ExecuteAsync(MigrationSql());

    /// <summary>Insert a bare account row carrying the given MD payload.</summary>
    private static async Task<Guid> SeedAsync(
        NpgsqlConnection connection,
        string? payloadJson,
        string accountType = "bank",
        DateOnly? existingOpenedOn = null)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active,
                                  provider_raw_payload, opened_on)
            VALUES (@id, @ledgerId, 'seeded', @accountType,
                    CASE WHEN @accountType = 'category' THEN 'expense' END,
                    'USD', 0, TRUE, @payload::jsonb, @openedOn);
            """,
            new
            {
                id,
                ledgerId = TestLedger.Id,
                accountType,
                payload = payloadJson,
                openedOn = existingOpenedOn,
            });
        return id;
    }

    private static async Task<DateOnly?> OpenedOnAsync(NpgsqlConnection connection, Guid id) =>
        await connection.ExecuteScalarAsync<DateOnly?>(
            "SELECT opened_on FROM accounts WHERE id = @id;", new { id });

    [Fact]
    public async Task Backfills_from_the_yyyymmdd_field()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection, """{"date_created":"20180314"}""");
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2018, 3, 14), await OpenedOnAsync(connection, id));
    }

    [Fact]
    public async Task Backfills_from_epoch_millis_when_the_int_field_is_absent()
    {
        // The common case in a real export: creation_date covers 181 of 781
        // accounts, date_created only 64.
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        // 1767286800000 = 2026-01-01T17:00Z — MD's local-noon stamp.
        var id = await SeedAsync(connection, """{"creation_date":"1767286800000"}""");
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2026, 1, 1), await OpenedOnAsync(connection, id));
    }

    [Fact]
    public async Task Prefers_the_int_field_when_both_are_present()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection,
            """{"date_created":"20260101","creation_date":"1767286800000"}""");
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2026, 1, 1), await OpenedOnAsync(connection, id));
    }

    [Theory]
    // to_date() is lenient — 20261301 silently becomes 2027-01-01 and 20260230
    // becomes 2026-03-02. The round-trip guard must reject both rather than
    // writing a plausible-looking wrong date into every affected account.
    [InlineData("""{"date_created":"20261301"}""")]
    [InlineData("""{"date_created":"20260230"}""")]
    [InlineData("""{"date_created":"0"}""")]
    [InlineData("""{"date_created":"not-a-date"}""")]
    [InlineData("""{"creation_date":"0"}""")]
    [InlineData("""{"creation_date":"not-a-number"}""")]
    [InlineData("""{"name":"no stamp at all"}""")]
    [InlineData("""{}""")]
    public async Task Leaves_null_when_the_payload_has_no_usable_stamp(string payload)
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection, payload);
        await RunBackfillAsync(connection);

        Assert.Null(await OpenedOnAsync(connection, id));
    }

    [Fact]
    public async Task A_malformed_int_field_still_falls_back_to_the_epoch_field()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection,
            """{"date_created":"20261301","creation_date":"1767286800000"}""");
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2026, 1, 1), await OpenedOnAsync(connection, id));
    }

    [Fact]
    public async Task Never_overwrites_a_start_date_already_set()
    {
        // The editor owns the field (ADR-0050). A backfill that clobbered a
        // user's correction would be unrecoverable — there is no re-import.
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection, """{"date_created":"20180314"}""",
            existingOpenedOn: new DateOnly(2011, 7, 1));
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2011, 7, 1), await OpenedOnAsync(connection, id));
    }

    [Fact]
    public async Task Skips_categories_and_coffer_native_accounts()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        // A category: its opening balance is forced to 0, so the as-of date of
        // that balance is meaningless even when MD recorded one.
        var category = await SeedAsync(connection, """{"date_created":"20180314"}""",
            accountType: "category");
        // Created in Coffer, never imported — no payload to mine.
        var native = await SeedAsync(connection, payloadJson: null);

        await RunBackfillAsync(connection);

        Assert.Null(await OpenedOnAsync(connection, category));
        Assert.Null(await OpenedOnAsync(connection, native));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        await using var connection = _fixture.OpenConnection();
        await connection.ExecuteAsync("TRUNCATE accounts CASCADE;");

        var id = await SeedAsync(connection, """{"date_created":"20180314"}""");
        await RunBackfillAsync(connection);
        await RunBackfillAsync(connection);
        await RunBackfillAsync(connection);

        Assert.Equal(new DateOnly(2018, 3, 14), await OpenedOnAsync(connection, id));
    }
}
