using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Db;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Migration 264, its data steps and the stored-notes step after it, timed on the stress
/// ledger as an install that upgrades would hold it, with the peak memory of the
/// Postgres backends that run them.
/// </summary>
/// <remarks>
/// <para><b>The data.</b> The default stress seed, written at head, is copied into a
/// database staged just before 264 and put back the way the earlier release stored it:
/// a one-posting transaction's memo and its originating side's tags on the header, an
/// investment event's memo copied onto its investment-side legs as the investment
/// editor wrote it, and a case variant of every tenth tag carrying some of its links.
/// So 264's steps C, D and M do the work they do on an upgrade. The seed has no
/// Moneydance payloads, so steps A and B find no candidates here; their cost is a
/// pass over the Moneydance rows a ledger has.</para>
/// <para><b>The figures.</b> The script alone, then the C# step alone (each as the
/// runner runs them, in that order), wall clock, and for each the peak rise of the
/// private resident memory (RssAnon) of the backends serving it over their baseline,
/// sampled from <c>/proc</c> through a superuser connection. Measure, don't assert;
/// what is asserted is that the result is what the rule computes.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class Migration264CostTests : DatabaseTest
{
    private const string Prefix = "mig264-cost:";
    private const string Mig264 = "264_annotation_data.sql";

    private readonly ITestOutputHelper _output;

    public Migration264CostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private void Print(FormattableString line) => _output.WriteLine($"{Prefix} {line.ToString(CultureInfo.InvariantCulture)}");

    [Fact]
    public async Task Migration_264_on_the_stress_ledger_prints_its_time_and_peak_memory()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Default);
        var ledgerId = ledger.LedgerId;

        var work = Directory.CreateTempSubdirectory("coffer-mig264-cost-").FullName;
        var old = Unpooled(Db.EmptyDatabaseConnectionString("mig264_cost"));
        UpgradeRehearsal.RunStaged(old, UpgradeRehearsal.StageBefore(Mig264, work));

        var copy = Stopwatch.StartNew();
        await using (var from = new NpgsqlConnection(Unpooled(Db.ServiceConnectionString)))
        await using (var to = new NpgsqlConnection(old))
        {
            await from.OpenAsync();
            await to.OpenAsync();
            await CopyAsync(from, to, "users", $"t.id = '{ledger.UserId}'", []);
            await CopyAsync(from, to, "ledgers", $"t.id = '{ledgerId}'", []);
            foreach (var table in new[] { "accounts", "securities", "tags", "txn_headers", "txn_legs", "txn_header_tags", "txn_leg_tags" })
                // A header's import run and series are not copied, so it names neither.
                await CopyAsync(from, to, table, $"t.ledger_id = '{ledgerId}'",
                    table == "txn_headers" ? ["ledger_operation_id", "recurring_transaction_id"] : []);
            // Copied tables have no statistics until analysed, and the rewind plans its joins on them.
            await ExecAsync(to, "ANALYZE users, ledgers, accounts, securities, tags, txn_headers, txn_legs, txn_header_tags, txn_leg_tags;");
            await ExecAsync(to, RewindSql(ledgerId));
        }
        copy.Stop();

        var counts = await CountsAsync(old, ledgerId);
        Print($"seed={seeded.Elapsed.TotalSeconds:F1}s copy+rewind={copy.Elapsed.TotalSeconds:F1}s {counts}");

        // The script alone, on one backend, as the runner sends it.
        var script = await File.ReadAllTextAsync(Path.Combine(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), Mig264));
        var (sqlSeconds, sqlRise) = await MeasureAsync(old, async connection =>
        {
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 1800 };
            await command.ExecuteNonQueryAsync();
        });
        Print($"script: {sqlSeconds:F1}s peak backend rss-anon rise={sqlRise}kB");

        // The step alone, through its own code; it opens its own connections, so every
        // backend of the database is sampled.
        var (stepSeconds, stepRise) = await MeasureAsync(old, _ =>
            LegNotesBackfill.RunAsync(old, ledgerId: null, CancellationToken.None));
        Print($"stored-notes step: {stepSeconds:F1}s peak backend rss-anon rise={stepRise}kB");
        Print($"total: {sqlSeconds + stepSeconds:F1}s");

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(old).Options);
        db.Database.SetCommandTimeout(1800);
        Assert.Empty(await LegNotes.DifferencesAsync(db, ledgerId, CancellationToken.None));
    }

    /// <summary>
    /// The earlier release's placement, over the copied ledger: a one-posting header
    /// holds its memo (its originating side's own) and that side's tags; an event's memo
    /// sits on its investment-side legs too; every tenth tag has a case variant created
    /// a day later, carrying the links of every other header that carried the tag.
    /// </summary>
    private static string RewindSql(Guid ledgerId) => $$"""
        BEGIN;
        CREATE TEMP TABLE one ON COMMIT DROP AS
        SELECT l.header_id, l.id AS leg_id, l.leg_memo
          FROM txn_legs l
          JOIN txn_headers h ON h.id = l.header_id AND l.account_id = h.originating_account_id
         WHERE h.ledger_id = '{{ledgerId}}'
           AND (SELECT count(DISTINCT x.posting_index) FROM txn_legs x WHERE x.header_id = h.id) = 1;
        UPDATE txn_headers h SET memo = o.leg_memo FROM one o WHERE h.id = o.header_id AND o.leg_memo IS NOT NULL;
        UPDATE txn_legs l SET leg_memo = NULL FROM one o WHERE l.id = o.leg_id AND l.leg_memo IS NOT NULL;
        INSERT INTO txn_header_tags (header_id, tag_id, ledger_id, created_at)
        SELECT o.header_id, lt.tag_id, lt.ledger_id, lt.created_at
          FROM one o JOIN txn_leg_tags lt ON lt.leg_id = o.leg_id
        ON CONFLICT DO NOTHING;
        DELETE FROM txn_leg_tags lt USING one o WHERE lt.leg_id = o.leg_id;

        UPDATE txn_legs l SET leg_memo = h.memo
          FROM txn_headers h
          JOIN accounts b ON b.id = h.originating_account_id
         WHERE l.header_id = h.id AND h.ledger_id = '{{ledgerId}}'
           AND h.action IS NOT NULL AND h.action <> 'transfer_shares' AND h.memo IS NOT NULL
           AND (SELECT count(DISTINCT x.posting_index) FROM txn_legs x WHERE x.header_id = h.id) > 1
           AND (l.account_id = b.id OR l.account_id = b.holdings_account_id);

        CREATE TEMP TABLE variant ON COMMIT DROP AS
        SELECT t.id AS tag_id, md5(t.id::text || '/variant')::uuid AS variant_id, upper(t.name) || '' AS name,
               t.created_at + interval '1 day' AS created_at
          FROM (SELECT t.*, row_number() OVER (ORDER BY t.id) AS n FROM tags t WHERE t.ledger_id = '{{ledgerId}}') t
         WHERE t.n % 10 = 0 AND upper(t.name) <> t.name;
        INSERT INTO tags (id, ledger_id, name, created_at)
        SELECT v.variant_id, '{{ledgerId}}', v.name, v.created_at FROM variant v;
        UPDATE txn_header_tags ht SET tag_id = v.variant_id
          FROM variant v
         WHERE ht.tag_id = v.tag_id AND get_byte(decode(md5(ht.header_id::text), 'hex'), 0) % 2 = 0;
        UPDATE txn_leg_tags lt SET tag_id = v.variant_id
          FROM variant v
         WHERE lt.tag_id = v.tag_id AND get_byte(decode(md5(lt.leg_id::text), 'hex'), 0) % 2 = 0;
        COMMIT;
        ANALYZE;
        """;

    private static async Task<string> CountsAsync(string cs, Guid ledgerId)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM txn_headers WHERE ledger_id = '{ledgerId}'),
                   (SELECT count(*) FROM txn_legs WHERE ledger_id = '{ledgerId}'),
                   (SELECT count(*) FROM txn_headers WHERE ledger_id = '{ledgerId}' AND memo IS NOT NULL),
                   (SELECT count(*) FROM txn_header_tags WHERE ledger_id = '{ledgerId}'),
                   (SELECT count(*) FROM txn_leg_tags WHERE ledger_id = '{ledgerId}'),
                   (SELECT count(*) FROM tags WHERE ledger_id = '{ledgerId}')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return string.Create(CultureInfo.InvariantCulture,
            $"headers={reader.GetInt64(0)} legs={reader.GetInt64(1)} header-memos={reader.GetInt64(2)} header-tag-links={reader.GetInt64(3)} leg-tag-links={reader.GetInt64(4)} tags={reader.GetInt64(5)}");
    }

    /// <summary>
    /// Run <paramref name="work"/> on a connection of its own and sample, until it ends,
    /// the private resident memory of every backend of the database; the result is the
    /// largest rise of any backend over the first figure seen for it.
    /// </summary>
    private async Task<(double Seconds, long PeakRiseKb)> MeasureAsync(string cs, Func<NpgsqlConnection, Task> work)
    {
        var database = new NpgsqlConnectionStringBuilder(cs).Database!;
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        using var stop = new CancellationTokenSource();
        var sampler = SampleAsync(database, stop.Token);
        var watch = Stopwatch.StartNew();
        await work(connection);
        watch.Stop();
        await stop.CancelAsync();
        return (watch.Elapsed.TotalSeconds, await sampler);
    }

    private async Task<long> SampleAsync(string database, CancellationToken stop)
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
            try { await Task.Delay(5, stop); } catch (OperationCanceledException) { }
        }
        return rise;
    }

    private static long Parse(string status)
    {
        var line = status.Split('\n').FirstOrDefault(l => l.StartsWith("RssAnon:", StringComparison.Ordinal));
        return line is null ? 0 : long.Parse(line["RssAnon:".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
    }

    /// <summary>Rows of <paramref name="table"/> copied whole through their JSON, in batches.</summary>
    private static async Task CopyAsync(NpgsqlConnection from, NpgsqlConnection to, string table, string where, string[] dropped)
    {
        var strip = string.Concat(dropped.Select(d => $" - '{d}'"));
        await using var read = new NpgsqlCommand(
            $"SELECT to_jsonb(t){strip} FROM {table} t WHERE {where}", from) { CommandTimeout = 1800 };
        await using var reader = await read.ExecuteReaderAsync();
        var batch = new List<string>(5000);
        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {table} SELECT * FROM jsonb_populate_recordset(NULL::{table}, @j::jsonb)", to) { CommandTimeout = 1800 };
            insert.Parameters.AddWithValue("j", "[" + string.Join(",", batch) + "]");
            await insert.ExecuteNonQueryAsync();
            batch.Clear();
        }
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        await reader.DisposeAsync();
        foreach (var row in rows)
        {
            batch.Add(row);
            if (batch.Count == 5000) await FlushAsync();
        }
        await FlushAsync();
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 1800 };
        await command.ExecuteNonQueryAsync();
    }

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, CommandTimeout = 1800 }.ConnectionString;
}
