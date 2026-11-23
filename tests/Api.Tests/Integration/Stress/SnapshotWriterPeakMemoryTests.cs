using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The peak memory of the Postgres backend that writes a snapshot, on the default
/// stress seed, with the snapshot writer that chunks every table on its primary key
/// (migration 264) against the one it replaces, which chunked only a table with a
/// column named <c>id</c> and wrote every other table as one aggregate. Measured for the
/// whole capture and for the two largest tables the old writer wrote in one piece:
/// <c>txn_leg_note_tags</c> and <c>txn_leg_recon</c>.
/// </summary>
/// <remarks>
/// <para>Measure, don't assert. Both writers run over the same snapshot id, each on a
/// fresh backend inside a REPEATABLE READ transaction that is rolled back: "one piece"
/// re-creates the earlier writer (262's definition, read from its migration) for the
/// transaction only, so nothing outlives it. The rounds alternate the order.</para>
/// <para>Memory is the backend's private resident memory (RssAnon), sampled from
/// <c>/proc</c> through a superuser connection while the write runs; the rise is the peak
/// over the backend's baseline. The writes run as the service role.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SnapshotWriterPeakMemoryTests : DatabaseTest
{
    private const string Prefix = "snapshot-peak:";
    private const int Rounds = 3;

    private readonly ITestOutputHelper _output;

    public SnapshotWriterPeakMemoryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private void Print(FormattableString line) => _output.WriteLine($"{Prefix} {line.ToString(CultureInfo.InvariantCulture)}");

    private enum Writer { OnePiece, Chunked }

    private sealed record Run(Writer Writer, string Scope, int Round, long BaselineAnonKb, long PeakAnonKb, int Parts, long Rows, double Seconds);

    [Fact]
    public async Task Snapshot_writer_prints_its_backend_peak_one_piece_and_chunked()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var seeded = await StressLedger.SeedLedgerAsync(Db, ledger, StressLedger.Scale.Default);
        var ledgerId = ledger.LedgerId;

        // The seed reconciles nothing, so txn_leg_recon would be empty: every leg on an
        // account that is not a category is marked cleared, one row per reconciled leg,
        // the table's shape on a ledger whose registers are kept reconciled. Not an input
        // of anything the seed's checks read.
        await using (var connection = new NpgsqlConnection(Unpooled(Db.ServiceConnectionString)))
        {
            await connection.OpenAsync();
            await using var reconcile = new NpgsqlCommand("""
                INSERT INTO txn_leg_recon (leg_id, ledger_id, status, cleared_at)
                SELECT l.id, l.ledger_id, 'cleared', h.posted_at
                  FROM txn_legs l
                  JOIN txn_headers h ON h.id = l.header_id
                  JOIN accounts a ON a.id = l.account_id
                 WHERE l.ledger_id = @l AND a.account_type <> 'category'
                ON CONFLICT (leg_id) DO NOTHING
                """, connection);
            reconcile.Parameters.AddWithValue("l", ledgerId);
            await reconcile.ExecuteNonQueryAsync();
        }

        await using (var db = Db.NewServiceDbContext())
        {
            var rows = new Dictionary<string, long>
            {
                ["txn_legs"] = await db.TxnLegs.LongCountAsync(l => l.LedgerId == ledgerId),
                ["txn_leg_note_tags"] = await db.TxnLegNoteTags.LongCountAsync(t => t.LedgerId == ledgerId),
                ["txn_leg_recon"] = await db.TxnLegRecon.LongCountAsync(r => r.LedgerId == ledgerId),
            };
            Print($"seed {string.Join(" ", rows.Select(r => $"{r.Key}={r.Value}"))} seed={seeded.Elapsed.TotalSeconds:F1}s");
        }

        var snapshotId = Guid.NewGuid();
        await using (var db = Db.NewServiceDbContext())
        {
            db.LedgerSnapshots.Add(new Coffer.Api.Db.Entities.LedgerSnapshotRow
            {
                Id = snapshotId, LedgerId = ledgerId, CreatedAt = DateTime.UtcNow, CreatedByUserId = ledger.UserId, Kind = "manual",
                Description = "peak memory", SchemaVersion = "stress", Content = [], ContentSizeUncompressed = 0,
            });
            await db.SaveChangesAsync();
        }

        var onePiece = OnePieceWriter();
        var runs = new List<Run>();
        foreach (var scope in new[] { "whole capture", "txn_leg_note_tags", "txn_leg_recon" })
            for (var round = 0; round < Rounds; round++)
                foreach (var writer in round % 2 == 0 ? new[] { Writer.OnePiece, Writer.Chunked } : [Writer.Chunked, Writer.OnePiece])
                    runs.Add(await MeasureAsync(writer, scope, round, snapshotId, ledgerId, onePiece));

        foreach (var run in runs)
            Print($"run {run.Scope} {run.Writer} round={run.Round} write={run.Seconds:F1}s parts={run.Parts} rows={run.Rows} rss-anon baseline={run.BaselineAnonKb}kB peak={run.PeakAnonKb}kB rise={run.PeakAnonKb - run.BaselineAnonKb}kB");
        foreach (var scope in runs.Select(r => r.Scope).Distinct())
        {
            double Median(Writer w) => AnnotationApiTiming.Median(
                runs.Where(r => r.Scope == scope && r.Writer == w).Select(r => (double)(r.PeakAnonKb - r.BaselineAnonKb)).ToList());
            Print($"median rise {scope}: one-piece={Median(Writer.OnePiece):F0}kB chunked={Median(Writer.Chunked):F0}kB");
        }

        // The two writers capture the same rows.
        foreach (var scope in runs.Select(r => r.Scope).Distinct())
            Assert.Single(runs.Where(r => r.Scope == scope).Select(r => r.Rows).Distinct());

    }

    /// <summary>The writer as migration 262 last defined it, read from that migration.</summary>
    private static string OnePieceWriter()
    {
        var dir = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        var text = File.ReadAllText(Directory.GetFiles(dir, "262_*.sql").Single());
        var match = Regex.Match(text, @"CREATE OR REPLACE FUNCTION fn_snapshot_write_part\([\s\S]*?\$\$[\s\S]*?\$\$;");
        Assert.True(match.Success, "262's fn_snapshot_write_part not found");
        Assert.Contains("c_unchunked_max", match.Value, StringComparison.Ordinal);
        return match.Value;
    }

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, CommandTimeout = 900 }.ConnectionString;

    private async Task<Run> MeasureAsync(Writer writer, string scope, int round, Guid snapshotId, Guid ledgerId, string onePiece)
    {
        await using var connection = new NpgsqlConnection(Unpooled(Db.ServiceConnectionString));
        await connection.OpenAsync();
        var pid = await ScalarAsync<int>(connection, null, "SELECT pg_backend_pid()");
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        if (writer == Writer.OnePiece)
        {
            await using var replace = new NpgsqlCommand(onePiece, connection, tx);
            await replace.ExecuteNonQueryAsync();
        }

        var baseline = await ReadAnonKbAsync(pid);
        using var stop = new CancellationTokenSource();
        var sampler = SampleAsync(pid, stop.Token);
        var watch = Stopwatch.StartNew();
        await using (var cmd = new NpgsqlCommand(scope == "whole capture"
                         ? "SELECT content_size_uncompressed FROM ledger_snapshot_write(@s, @l)"
                         : "SELECT fn_snapshot_write_part(@s, @l, @t, 2000)", connection, tx))
        {
            cmd.Parameters.AddWithValue("s", snapshotId);
            cmd.Parameters.AddWithValue("l", ledgerId);
            if (scope != "whole capture") cmd.Parameters.AddWithValue("t", scope);
            await cmd.ExecuteScalarAsync();
        }
        watch.Stop();
        await stop.CancelAsync();
        var peak = Math.Max(await sampler, await ReadAnonKbAsync(pid));

        await using var count = new NpgsqlCommand(
            "SELECT count(*)::int, COALESCE(sum(jsonb_array_length(content)), 0)::bigint FROM ledger_snapshot_parts WHERE snapshot_id = @s",
            connection, tx);
        count.Parameters.AddWithValue("s", snapshotId);
        int parts;
        long rows;
        await using (var reader = await count.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            parts = reader.GetInt32(0);
            rows = reader.GetInt64(1);
        }
        await tx.RollbackAsync();
        return new Run(writer, scope, round, baseline, peak, parts, rows, watch.Elapsed.TotalSeconds);
    }

    private async Task<long> ReadAnonKbAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(Unpooled(Db.SuperuserConnectionString));
        await connection.OpenAsync();
        return Parse(await ScalarAsync<string>(connection, null, $"SELECT pg_read_file('/proc/{pid}/status')"));
    }

    private async Task<long> SampleAsync(int pid, CancellationToken stop)
    {
        await using var connection = new NpgsqlConnection(Unpooled(Db.SuperuserConnectionString));
        await connection.OpenAsync(CancellationToken.None);
        await using var cmd = new NpgsqlCommand($"SELECT pg_read_file('/proc/{pid}/status')", connection);
        long anon = 0;
        while (!stop.IsCancellationRequested)
        {
            anon = Math.Max(anon, Parse((string)(await cmd.ExecuteScalarAsync(CancellationToken.None))!));
            try { await Task.Delay(5, stop); } catch (OperationCanceledException) { }
        }
        return anon;
    }

    private static long Parse(string status)
    {
        var line = status.Split('\n').FirstOrDefault(l => l.StartsWith("RssAnon:", StringComparison.Ordinal));
        Assert.True(line is not null, "/proc status has no RssAnon");
        return long.Parse(line!["RssAnon:".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
