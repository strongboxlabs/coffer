using System.Text.RegularExpressions;

using Coffer.Api.Migrations;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Only the migration runner marks a session as a migration's, and only the ledger write
/// lock marks a transaction as holding it.
/// </summary>
/// <remarks>
/// <para><c>fn_ledger_write_lock</c> (migration 275) does nothing on a session whose
/// <c>coffer.migration_session</c> is <c>on</c>: a data migration writes before it
/// recomputes, and nothing runs beside it. A request or a job that carried the setting
/// would skip the lock and its refusals, so the setting is named in exactly two places
/// outside the tests and the docs: the runner, which sets it at connect, and migration 275,
/// which reads it. No SQL sets it.</para>
/// <para>It also does nothing in a transaction whose <c>coffer.ledger_write_held</c> lists
/// the ledger: the function sets that transaction-local marker once it holds the lock, so a
/// second call returns without reading <c>pg_locks</c>. Anything else that set the marker
/// would skip the lock the same way, so only migration 275, which defines the function,
/// names it outside the tests and the docs.</para>
/// </remarks>
public sealed partial class LedgerWriteLockCatalogTests
{
    /// <summary>The transaction-local marker <c>fn_ledger_write_lock</c> keeps of the
    /// ledgers it holds (migration 275).</summary>
    private const string HeldMarker = "coffer.ledger_write_held";

    private static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "node_modules", "dist", "tests", "docs", ".vs", ".idea"];

    private static readonly string[] TextExtensions =
        [".cs", ".sql", ".sh", ".ps1", ".yml", ".yaml", ".json", ".ts", ".tsx", ".js", ".mjs", ".env", ".example", ".conf", ".toml", ".csproj", ".props", ".targets"];

    [Fact]
    public void Only_the_runner_sets_the_migration_session_setting_and_only_migration_275_reads_it()
    {
        var root = RepoRoot();
        var setting = MigrationRunner.MigrationSessionSetting;
        var files = Files(root).ToList();
        // Fail closed: a wrong root would find the setting nowhere and pass.
        Assert.True(files.Count > 500, $"scanned only {files.Count} files under {root}");

        var naming = files
            .Where(f => File.ReadAllText(f).Contains(setting, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["db/migrations/275_one_write_lock_per_ledger.sql", "src/Api/Migrations/MigrationRunner.cs"],
            naming);

        var setters = Directory.EnumerateFiles(Path.Combine(root, "db"), "*.sql", SearchOption.AllDirectories)
            .Where(f => SetsTheSetting().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(setters.Count == 0, "SQL that sets the migration session setting: " + string.Join(", ", setters));
    }

    [Fact]
    public void Only_migration_275_names_the_ledger_write_held_marker()
    {
        var root = RepoRoot();
        var files = Files(root).ToList();
        // Fail closed: a wrong root would find the marker nowhere and pass.
        Assert.True(files.Count > 500, $"scanned only {files.Count} files under {root}");

        var naming = files
            .Where(f => File.ReadAllText(f).Contains(HeldMarker, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["db/migrations/275_one_write_lock_per_ledger.sql"], naming);
    }

    [GeneratedRegex(@"set_config\s*\(\s*'coffer\.migration_session'|\bSET\s+(?:LOCAL\s+|SESSION\s+)?coffer\.migration_session\b|\bALTER\s+(?:ROLE|DATABASE|SYSTEM)\b[^;]*coffer\.migration_session",
        RegexOptions.IgnoreCase)]
    private static partial Regex SetsTheSetting();

    private static IEnumerable<string> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                || name.StartsWith("Dockerfile", StringComparison.Ordinal))
                yield return file;
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.Ordinal)) continue;
            foreach (var file in Files(sub)) yield return file;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
