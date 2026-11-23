using System.Diagnostics;
using System.Globalization;

using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Migration 301 timed on the stress ledger, and on the sparse long-lived shape, as an
/// install that upgrades would hold them, with the peak memory of the Postgres backend
/// that runs it (ADR-0114 D15, "The upgrade").
/// </summary>
/// <remarks>
/// <para><b>The data.</b> The default stress seed, written at head, optionally with
/// <see cref="LongLivedAccounts"/> and <see cref="LongLivedLedger"/> added, is copied into
/// a database staged just before 301, the way a dump is loaded: rows whole, with the
/// triggers and foreign keys out of the way while they load. The stress seed holds no
/// budget targets and no originals rows, so both are added there in the shape the code
/// before 301 wrote them: a target at 2 places for every category in every month the
/// ledger spans, and an originals row with no amount on a share of the headers. A table
/// with no rows would say nothing of what its rewrite costs.</para>
/// <para><b>The figures.</b> 301's script alone, on one backend, as the runner sends
/// it, wall clock and the peak rise of that backend's private resident memory (RssAnon)
/// over its baseline, sampled from <c>/proc</c> through a superuser connection, beside
/// the row counts of the tables it rewrites, validates or extends. The command timeout
/// is DbUp's per-script one, so a script that would time out at an upgrade fails here.
/// Measure, don't assert; what is asserted is that the result is what 301 promises: the
/// ceiling validated, every stored figure numerically unchanged, and realized_gains_walk
/// in agreement with the stored rows.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class Migration301CostTests : DatabaseTest
{
    private const string Prefix = "mig301-cost:";
    private const string Mig301 = "301_each_currency_keeps_its_minor_units.sql";

    /// <summary>DbUp's per-script timeout (<c>MigrationRunner</c>).</summary>
    private const int RunnerTimeoutSeconds = 600;

    /// <summary>The tables copied, parents first.</summary>
    private static readonly string[] Copied =
    [
        "users", "ledgers", "securities", "accounts", "txn_headers", "txn_legs",
        "holdings", "lots", "realized_gains", "budget_targets", "txn_header_originals",
    ];

    /// <summary>Each stored figure 301 could change, as text a scale does not alter.</summary>
    private static readonly (string Table, string Sql)[] Figures =
    [
        ("txn_legs", "SELECT md5(string_agg(id::text || ':' || trim_scale(amount)::text, ',' ORDER BY id)) FROM txn_legs"),
        ("budget_targets", """
            SELECT md5(string_agg(id::text || ':' || trim_scale(amount)::text || ':' || currency_code, ',' ORDER BY id))
              FROM budget_targets
            """),
        ("realized_gains", """
            SELECT md5(string_agg(concat_ws(':', id, trim_scale(quantity), trim_scale(proceeds), trim_scale(proceeds_lt),
                                            trim_scale(cost_basis_sold), trim_scale(cost_basis_sold_lt),
                                            trim_scale(realized_gain), trim_scale(realized_gain_lt)),
                                  ',' ORDER BY id))
              FROM realized_gains
            """),
        ("txn_header_originals", """
            SELECT md5(string_agg(concat_ws(':', header_id, payee, memo, posted_at, transacted_at, check_number, captured_at),
                                  ',' ORDER BY header_id))
              FROM txn_header_originals
            """),
    ];

    private readonly ITestOutputHelper _output;

    public Migration301CostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private void Print(FormattableString line) => _output.WriteLine($"{Prefix} {line.ToString(CultureInfo.InvariantCulture)}");

    [Theory]
    [InlineData("default")]
    [InlineData("long-lived")]
    public async Task Migration_301_prints_its_time_and_peak_memory(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Default);
        if (shape == "long-lived")
        {
            await LongLivedAccounts.AddAsync(ledger);
            await LongLivedLedger.AddAsync(Db, ledger);
        }
        var ledgerId = ledger.LedgerId;

        var work = Directory.CreateTempSubdirectory("coffer-mig301-cost-").FullName;
        var old = Unpooled(Db.EmptyDatabaseConnectionString(shape == "default" ? "mig301_cost" : "mig301_cost_long"));
        UpgradeRehearsal.RunStaged(old, UpgradeRehearsal.StageBefore(Mig301, work));
        var oldSuperuser = Unpooled(new NpgsqlConnectionStringBuilder(Db.SuperuserConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(old).Database,
        }.ConnectionString);

        var copy = Stopwatch.StartNew();
        await using (var from = new NpgsqlConnection(Unpooled(Db.ServiceConnectionString)))
        await using (var to = new NpgsqlConnection(oldSuperuser))
        {
            await from.OpenAsync();
            await to.OpenAsync();
            // A dump's data loads before its triggers and foreign keys exist; the
            // replica role skips both, so the rows arrive as they were, in any order.
            await ExecAsync(to, "SET session_replication_role = replica;");
            foreach (var table in Copied)
            {
                var where = table switch
                {
                    "users" => $"t.id = '{ledger.UserId}'",
                    "ledgers" => $"t.id = '{ledgerId}'",
                    _ => $"t.ledger_id = '{ledgerId}'",
                };
                await CopyAsync(from, to, table, where, await OutsideReferencesAsync(to, table));
            }
            await ExecAsync(to, OldShapeSql(ledgerId));
            await ExecAsync(to, "SET session_replication_role = origin;");
            // Copied tables have no statistics until analysed, as autovacuum would have them.
            await ExecAsync(to, $"ANALYZE {string.Join(", ", Copied)};");
        }
        copy.Stop();

        var counts = await CountsAsync(old);
        Print($"{shape}: seed={seeded.Elapsed.TotalSeconds:F1}s copy={copy.Elapsed.TotalSeconds:F1}s {counts}");

        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, sql) in Figures) before[table] = await ScalarAsync(old, sql);

        // The script alone, on one backend, as the runner sends it.
        var script = await File.ReadAllTextAsync(Path.Combine(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), Mig301));
        var (seconds, rise) = await MeasureAsync(old, async connection =>
        {
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = RunnerTimeoutSeconds };
            await command.ExecuteNonQueryAsync();
        });
        Print($"{shape}: script={seconds:F1}s peak backend rss-anon rise={rise}kB (runner timeout {RunnerTimeoutSeconds}s)");

        Assert.Equal("true", await ScalarAsync(old, """
            SELECT convalidated::text FROM pg_constraint
             WHERE conrelid = 'txn_legs'::regclass AND conname = 'ck_txn_legs_amount_at_most_3_places'
            """));
        foreach (var (table, sql) in Figures)
            Assert.True(before[table] == await ScalarAsync(old, sql), $"301 changed a stored figure in {table}");

        // The stored rows are what the walk derives, both ways round.
        var walk = Stopwatch.StartNew();
        var disagree = await ScalarAsync(old, """
            WITH stored AS (
                SELECT sell_leg_id, sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                       proceeds_lt, cost_basis_sold_lt, realized_gain_lt
                  FROM realized_gains),
            walked AS (
                SELECT w.* FROM holdings h CROSS JOIN LATERAL realized_gains_walk(h.account_id, h.security_id) w)
            SELECT ((SELECT count(*) FROM (SELECT * FROM stored EXCEPT SELECT * FROM walked) a)
                  + (SELECT count(*) FROM (SELECT * FROM walked EXCEPT SELECT * FROM stored) b))::text
            """);
        walk.Stop();
        Print($"{shape}: realized-gains walk check={walk.Elapsed.TotalSeconds:F1}s");
        Assert.Equal("0", disagree);
        // Not vacuous: some stored figures carry a fraction, so a walk rounding at other
        // places than the writer would disagree with them.
        Assert.NotEqual("0", await ScalarAsync(old,
            "SELECT count(*)::text FROM realized_gains WHERE cost_basis_sold <> round(cost_basis_sold, 0)"));
    }

    /// <summary>
    /// The budget targets and originals rows the code before 301 wrote, over the copied
    /// ledger: a target at 2 places, in the ledger's currency as migration 280 stamped it,
    /// for every category in every month from the ledger's first row to its last; and an
    /// originals row, with no amount, on every fourth header, as a first edit captured it
    /// a day after the row posted.
    /// </summary>
    private static string OldShapeSql(Guid ledgerId) => $"""
        INSERT INTO budget_targets (id, ledger_id, category_id, target_month, amount, currency_code, created_at, updated_at)
        SELECT md5(a.id::text || m.month::text)::uuid, a.ledger_id, a.id, m.month::date,
               (get_byte(decode(md5(a.id::text || m.month::text), 'hex'), 0) * 100
                + get_byte(decode(md5(a.id::text || m.month::text), 'hex'), 1)) / 100.0,
               l.currency_code, m.month, m.month
          FROM accounts a
          JOIN ledgers l ON l.id = a.ledger_id
          CROSS JOIN generate_series(
                (SELECT date_trunc('month', min(posted_at)) FROM txn_headers WHERE ledger_id = '{ledgerId}'),
                (SELECT date_trunc('month', max(posted_at)) FROM txn_headers WHERE ledger_id = '{ledgerId}'),
                interval '1 month') AS m(month)
         WHERE a.ledger_id = '{ledgerId}' AND a.account_type = 'category'
        ON CONFLICT DO NOTHING;

        INSERT INTO txn_header_originals
            (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number, captured_at)
        SELECT h.id, h.ledger_id, h.payee, h.memo, h.posted_at, h.transacted_at, h.check_number,
               h.posted_at + interval '1 day'
          FROM txn_headers h
         WHERE h.ledger_id = '{ledgerId}' AND get_byte(decode(md5(h.id::text), 'hex'), 0) % 4 = 0
        ON CONFLICT DO NOTHING;
        """;

    /// <summary>The nullable columns of <paramref name="table"/> whose foreign key names a
    /// table not copied: a header's import run or series, a row's folder, and the like.</summary>
    private static async Task<string[]> OutsideReferencesAsync(NpgsqlConnection to, string table)
    {
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT a.attname
              FROM pg_constraint c
              JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
             WHERE c.contype = 'f' AND c.conrelid = ('public.' || @t)::regclass
               AND NOT (c.confrelid::regclass::text = ANY (@copied))
               AND NOT a.attnotnull
            """, to);
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("copied", Copied);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return [.. columns];
    }

    private static async Task<string> CountsAsync(string cs)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "txn_headers", "txn_legs", "accounts", "holdings", "lots", "realized_gains", "budget_targets", "txn_header_originals" })
            parts.Add($"{table}={await ScalarAsync(cs, $"SELECT count(*)::text FROM {table}")}");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Run <paramref name="work"/> on a connection of its own and sample, until it ends,
    /// the private resident memory of every backend of the database; the result is the
    /// largest rise of any backend over the first figure seen for it. The measured
    /// backend's first figure is read before the work is sent, so it holds none of the
    /// work's memory; the sampler's own connect would otherwise let the work start first.
    /// </summary>
    private async Task<(double Seconds, long PeakRiseKb)> MeasureAsync(string cs, Func<NpgsqlConnection, Task> work)
    {
        var database = new NpgsqlConnectionStringBuilder(cs).Database!;
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        using var stop = new CancellationTokenSource();
        var baselineRead = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampler = SampleAsync(database, connection.ProcessID, baselineRead, stop.Token);
        long baselineReadBy;
        try { baselineReadBy = await baselineRead.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { await stop.CancelAsync(); throw; }
        var sentAt = Stopwatch.GetTimestamp();
        var watch = Stopwatch.StartNew();
        await work(connection);
        watch.Stop();
        await stop.CancelAsync();
        var rise = await sampler;
        Assert.True(baselineReadBy <= sentAt, "the measured backend's baseline was read after the work was sent");
        return (watch.Elapsed.TotalSeconds, rise);
    }

    /// <summary>Sample until <paramref name="stop"/>; <paramref name="baselineRead"/> gets
    /// the timestamp by which <paramref name="measured"/>'s first figure was in hand.</summary>
    private async Task<long> SampleAsync(
        string database, int measured, TaskCompletionSource<long> baselineRead, CancellationToken stop)
    {
        try
        {
            return await SampleLoopAsync(database, measured, baselineRead, stop);
        }
        catch (Exception e)
        {
            baselineRead.TrySetException(e);
            throw;
        }
    }

    private async Task<long> SampleLoopAsync(
        string database, int measured, TaskCompletionSource<long> baselineRead, CancellationToken stop)
    {
        await using var connection = new NpgsqlConnection(Unpooled(Db.SuperuserConnectionString));
        await connection.OpenAsync(CancellationToken.None);
        var baseline = new Dictionary<int, long>();
        long rise = 0;
        while (!stop.IsCancellationRequested)
        {
            await using (var command = new NpgsqlCommand("""
                SELECT a.pid, pg_read_file('/proc/' || a.pid || '/status')
                  FROM pg_stat_activity a
                 WHERE a.datname = @d AND a.backend_type = 'client backend'
                """, connection))
            {
                command.Parameters.AddWithValue("d", database);
                await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    var pid = reader.GetInt32(0);
                    string status;
                    try { status = reader.GetString(1); }
                    catch (InvalidCastException) { continue; }
                    var anon = Parse(status);
                    if (!baseline.TryGetValue(pid, out var first)) baseline[pid] = first = anon;
                    rise = Math.Max(rise, anon - first);
                }
            }
            if (baseline.ContainsKey(measured)) baselineRead.TrySetResult(Stopwatch.GetTimestamp());
            try { await Task.Delay(5, stop); } catch (OperationCanceledException) { }
        }
        return rise;
    }

    private static long Parse(string status)
    {
        var line = status.Split('\n').FirstOrDefault(l => l.StartsWith("RssAnon:", StringComparison.Ordinal));
        return line is null ? 0 : long.Parse(line["RssAnon:".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
    }

    /// <summary>Rows of <paramref name="table"/> copied whole through their JSON, in batches.
    /// A column the staged schema does not have yet is left behind by the record type.</summary>
    private static async Task CopyAsync(NpgsqlConnection from, NpgsqlConnection to, string table, string where, string[] dropped)
    {
        var strip = string.Concat(dropped.Select(d => $" - '{d}'"));
        await using var read = new NpgsqlCommand(
            $"SELECT to_jsonb(t){strip} FROM {table} t WHERE {where}", from) { CommandTimeout = 1800 };
        await using var reader = await read.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        await reader.DisposeAsync();
        foreach (var batch in rows.Chunk(5000))
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {table} SELECT * FROM jsonb_populate_recordset(NULL::{table}, @j::jsonb)", to) { CommandTimeout = 1800 };
            insert.Parameters.AddWithValue("j", "[" + string.Join(",", batch) + "]");
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 1800 };
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 1800 };
        await command.ExecuteNonQueryAsync();
    }

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, CommandTimeout = 1800 }.ConnectionString;
}
