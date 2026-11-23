namespace Coffer.Api.Tests.Unit;

/// <summary>
/// No migration ships with a placeholder where its data steps belong.
/// </summary>
/// <remarks>
/// <para>A migration whose data steps are still to be written stands a guard in their
/// place, marked <c>coffer:migration-placeholder</c>, that refuses every database holding
/// data. On an empty test database the guard passes, so every other test is green
/// while the tree would stop each existing install at that migration on its next start.
/// This test is what fails on such a tree; <c>release.sh</c> refuses it too.</para>
/// </remarks>
public sealed class MigrationPlaceholderTests
{
    internal const string Marker = "coffer:migration-placeholder";

    [Fact]
    public void No_migration_carries_the_data_step_placeholder()
    {
        var directory = Path.Combine(RepoRoot(), "db", "migrations");
        var scripts = Directory.GetFiles(directory, "*.sql");
        // Fail closed: a wrong directory would find no marker in nothing.
        Assert.True(scripts.Length > 250, $"found only {scripts.Length} migrations under {directory}");

        var carrying = scripts
            .Where(s => File.ReadAllText(s).Contains(Marker, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(carrying.Count == 0,
            "These migrations still stand a placeholder where their data steps belong, which refuses every "
            + "database holding data; write the steps before this tree can ship: " + string.Join(", ", carrying));
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
