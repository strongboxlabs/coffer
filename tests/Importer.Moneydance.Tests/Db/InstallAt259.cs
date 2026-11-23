using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// A database staged at 259 holding one ledger the current importer wrote into a test's
/// own database, for the upgrade tests that run 260 on from there.
/// </summary>
internal static class InstallAt259
{
    /// <summary>
    /// A database at 259 holding the ledger's rows as the import wrote them, minus what
    /// 259 has no column for. Rows are copied whole through their JSON, so every column
    /// both schemas share keeps its value. With <paramref name="derived"/>, the
    /// derived holdings, lots, realized gains and running balances come too, so a
    /// recompute there starts from what the import left. Returns its connection string and the directory its migrations
    /// are staged in.
    /// </summary>
    public static async Task<(string ConnectionString, string Work)> CreateAsync(
        TestDatabase db, string suffix, Guid ledgerId, bool derived = false)
    {
        var old = db.EmptyDatabaseConnectionString(suffix);
        var work = Directory.CreateTempSubdirectory($"coffer-{suffix}-").FullName;
        foreach (var script in Directory.GetFiles(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), "*.sql"))
            if (string.CompareOrdinal(Path.GetFileName(script), "260_") < 0)
                File.Copy(script, Path.Combine(work, Path.GetFileName(script)));
        MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(old), work, NullLogger.Instance);

        await using var from = db.OpenConnection();
        await using var to = new NpgsqlConnection(old);
        await to.OpenAsync();
        await CopyAsync(from, to, "ledgers", "t.id = @L", ledgerId);
        await CopyAsync(from, to, "accounts", "t.ledger_id = @L", ledgerId);
        await CopyAsync(from, to, "securities", "t.ledger_id = @L", ledgerId);
        await CopyAsync(from, to, "security_splits", "t.ledger_id = @L", ledgerId);
        // The import's run, which the old database does not hold.
        await CopyAsync(from, to, "txn_headers", "t.ledger_id = @L", ledgerId, "ledger_operation_id");
        await CopyAsync(from, to, "txn_legs", "t.ledger_id = @L", ledgerId);
        await CopyAsync(from, to, "recurring_transactions", "t.ledger_id = @L", ledgerId);
        if (derived)
        {
            await CopyAsync(from, to, "holdings", "t.ledger_id = @L", ledgerId);
            await CopyAsync(from, to, "lots", "t.ledger_id = @L", ledgerId);
            await CopyAsync(from, to, "realized_gains", "t.ledger_id = @L", ledgerId);
            await CopyAsync(from, to, "txn_header_account_balances", "t.ledger_id = @L", ledgerId);
        }
        // The copied headers carry their insertion order; a header inserted here after
        // them takes the next one.
        await to.ExecuteAsync("SELECT setval('txn_headers_seq', (SELECT max(seq) FROM txn_headers))");
        return (old, work);
    }

    /// <summary>Every migration from 260 on (before <paramref name="before"/>, when given),
    /// as an install's next start runs them.</summary>
    public static void UpgradeFrom259(string cs, string work, string? before = null)
    {
        foreach (var script in Directory.GetFiles(MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory), "*.sql"))
        {
            var name = Path.GetFileName(script);
            if (string.CompareOrdinal(name, "260_") >= 0
                && (before is null || string.CompareOrdinal(name, before) < 0))
                File.Copy(script, Path.Combine(work, name), overwrite: true);
        }
        MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(cs), work, NullLogger.Instance);
    }

    private static async Task CopyAsync(
        NpgsqlConnection from, NpgsqlConnection to, string table, string where, Guid ledgerId, params string[] dropped)
    {
        var strip = string.Concat(dropped.Select(d => $" - '{d}'"));
        var json = await from.ExecuteScalarAsync<string>(
            $"SELECT COALESCE(jsonb_agg(to_jsonb(t){strip}), '[]'::jsonb)::text FROM {table} t WHERE {where}",
            new { L = ledgerId });
        await to.ExecuteAsync(
            $"INSERT INTO {table} SELECT * FROM jsonb_populate_recordset(NULL::{table}, @J::jsonb)", new { J = json });
    }
}
