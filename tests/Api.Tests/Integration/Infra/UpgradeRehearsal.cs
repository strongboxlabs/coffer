using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Migrations;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Builds a database at a chosen migration, lets a test seed it the way a real
/// install would already be seeded, and then applies the migration under test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> New-code correctness and upgrade correctness are
/// different properties, and the suite only ever tested the first: every fixture
/// starts from a database at head, where the state a backfill acts on has already
/// been migrated away. A green suite against a fresh database says nothing about
/// what happens to rows that were written years ago.
/// </para>
/// <para>
/// The pattern was hand-rolled once, for migration 214's backfill, with a note
/// saying it was worth the trouble because "a green suite against a fresh database
/// would say nothing about that". Four scheduled migrations need the same thing —
/// a realized-gains backfill, a SET NOT NULL whose backfill needs real key
/// material, a nullable column on two scheduler tables, and two txn_headers
/// columns backfilled out of a JSON payload — so it is infrastructure now rather
/// than a copied block.
/// </para>
/// <para>
/// <b>What it does NOT do.</b> It rehearses SQL against data, not a deployment.
/// The container image, the compose topology and the credential plumbing are the
/// other half of an upgrade and belong to <c>scripts/maintainer/upgrade-drill.sh</c>;
/// this class would happily pass while the image fails to start. Neither half is
/// sufficient alone, which is worth remembering before either is cited as proof
/// that a release upgrades cleanly.
/// </para>
/// </remarks>
public static class UpgradeRehearsal
{
    /// <summary>
    /// Copy every migration that sorts BEFORE <paramref name="migrationFileName"/>
    /// into <paramref name="workDir"/> — i.e. an install as it exists in the field
    /// on the release before that migration shipped.
    /// </summary>
    /// <remarks>
    /// Ordinal compare on the file name, which works because every script in
    /// db/migrations carries a zero-padded numeric prefix and DbUp orders by the
    /// same rule. A name that does not exist is caught rather than silently
    /// staging everything: passing a typo'd migration would stage the whole
    /// directory and turn the rehearsal into an ordinary fresh-database test,
    /// green and meaningless.
    /// </remarks>
    public static string StageBefore(string migrationFileName, string workDir)
    {
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        var all = Directory.GetFiles(source, "*.sql").Select(Path.GetFileName).ToList();
        if (!all.Contains(migrationFileName, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"No migration named '{migrationFileName}' in {source}. "
                + "Staging would otherwise silently include every migration and the "
                + "rehearsal would prove nothing.",
                nameof(migrationFileName));
        }

        Directory.CreateDirectory(workDir);
        foreach (var script in Directory.GetFiles(source, "*.sql"))
        {
            var name = Path.GetFileName(script);
            if (string.CompareOrdinal(name, migrationFileName) >= 0) continue;
            File.Copy(script, Path.Combine(workDir, name), overwrite: true);
        }
        return workDir;
    }

    /// <summary>
    /// Run every migration currently staged in <paramref name="workDir"/> against
    /// <paramref name="connectionString"/>.
    /// </summary>
    public static void RunStaged(string connectionString, string workDir)
        => MigrationRunner.Run(connectionString, workDir, NullLogger.Instance);

    /// <summary>
    /// Add one migration to the staged set and run it.
    /// </summary>
    public static void Apply(string connectionString, string workDir, string migrationFileName)
    {
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        var script = Path.Combine(source, migrationFileName);
        if (!File.Exists(script))
            throw new ArgumentException($"No migration named '{migrationFileName}'.", nameof(migrationFileName));

        File.Copy(script, Path.Combine(workDir, migrationFileName), overwrite: true);
        MigrationRunner.Run(connectionString, workDir, NullLogger.Instance);
    }

    /// <summary>
    /// Apply a migration, then force it to run a SECOND time against the database
    /// it just changed. Throws if the script is not re-runnable.
    /// </summary>
    /// <remarks>
    /// The failure this catches does not appear on the upgrade — it appears on the
    /// operator's NEXT restart, or on a restore onto a database that already has
    /// the journal row, or when someone runs the script by hand.
    ///
    /// <para><b>Deleting the journal row is the whole method.</b> The obvious
    /// implementation — call the runner twice — asserts nothing at all: DbUp
    /// records every executed script in <c>__schema_migrations</c> and skips what
    /// it has already run, so the second pass is a no-op that passes for a script
    /// which would explode if it ever actually re-ran. That is the same
    /// can-never-fail shape this class exists to stamp out, so it is worth being
    /// explicit: the row is removed so the SQL genuinely executes twice.</para>
    /// </remarks>
    public static async Task ApplyAndProveRerunnableAsync(
        string connectionString, string workDir, string migrationFileName)
    {
        Apply(connectionString, workDir, migrationFileName);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var forget = new NpgsqlCommand(
                "DELETE FROM public.__schema_migrations WHERE scriptname LIKE @n", connection);
            forget.Parameters.AddWithValue("n", "%" + migrationFileName);
            var removed = await forget.ExecuteNonQueryAsync();
            if (removed == 0)
            {
                throw new InvalidOperationException(
                    $"'{migrationFileName}' left no journal row, so the re-run below would "
                    + "not re-execute it and this check would pass without testing anything.");
            }
        }

        MigrationRunner.Run(connectionString, workDir, NullLogger.Instance);
    }

    /// <summary>
    /// Stage the whole migration directory — a database at head.
    /// </summary>
    public static string StageAll(string workDir)
    {
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        Directory.CreateDirectory(workDir);
        foreach (var script in Directory.GetFiles(source, "*.sql"))
            File.Copy(script, Path.Combine(workDir, Path.GetFileName(script)), overwrite: true);
        return workDir;
    }

    /// <summary>
    /// Write an ad-hoc script into the staged set and run it. For a rehearsal's
    /// own self-test only — the script does not come from db/migrations and is
    /// never shipped.
    /// </summary>
    public static void ApplyInlineScript(
        string connectionString, string workDir, string fileName, string sql)
    {
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, fileName), sql);
        MigrationRunner.Run(connectionString, workDir, NullLogger.Instance);
    }

    /// <summary>
    /// A stable digest of whatever <paramref name="sql"/> returns, for asserting
    /// that rows a migration was not supposed to touch are byte-for-byte the same
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// The assertion a backfill actually needs is not "did it write something" —
    /// that passes for a backfill which also clobbers every row that was already
    /// correct. It is "the rows that already had a value still have the SAME
    /// value". Take a digest over those rows before the migration and compare it
    /// after; a mutation anywhere in the projected columns changes the hash.
    ///
    /// <para>ORDER BY inside the query is the caller's job. Postgres does not
    /// promise row order without one, so an unordered query would produce a digest
    /// that changes for no reason and a test that fails at random.</para>
    /// </remarks>
    // ASCII unit / record separators, and a marker no column value can produce.
    // The separators matter: without them "ab" + "c" and "a" + "bc" hash the same,
    // so a migration that shifted a value from one column to the next would be
    // invisible to this digest.
    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';
    private const string NullMarker = "\u0000NULL";

    public static async Task<string> DigestAsync(string connectionString, string sql)
    {
        var sb = new StringBuilder();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i)
                    ? NullMarker
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                sb.Append(value).Append(FieldSeparator);
            }
            sb.Append(RecordSeparator);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
