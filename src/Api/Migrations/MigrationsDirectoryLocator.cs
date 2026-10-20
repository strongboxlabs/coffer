namespace Coffer.Api.Migrations;

/// <summary>
/// Walks up from the running assembly's directory to find <c>db/migrations</c>.
/// Mirrors the helper in the importer's test fixture so dev runs, tests, and
/// container deployments (where migrations are copied alongside the binary)
/// all land on the same lookup rule.
/// </summary>
public static class MigrationsDirectoryLocator
{
    private static readonly string Subpath = Path.Combine("db", "migrations");

    /// <summary>
    /// Locate <c>db/migrations</c> by walking up from
    /// <paramref name="startDirectory"/>. Returns the absolute path on
    /// success; throws when no ancestor contains the subpath.
    /// </summary>
    public static string Locate(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, Subpath);
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate '{Subpath}' from {startDirectory}.");
    }
}
