namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The CI .NET job skips its tests for a pull request that changes nothing but the web
/// package manifest and its lock file (the "Decide whether the tests can tell" step of
/// <c>.github/workflows/ci.yml</c>). That is sound only while nothing the .NET job builds or
/// runs reads either file. This keeps it true: a source, test, project or CI script file
/// that names one has to come with a change to that step.
/// </summary>
public sealed class DotnetTestsIgnoreWebManifestTests
{
    // Written in pieces so this file does not name them itself.
    private static readonly string[] Needles = ["package" + ".json", "package" + "-lock"];

    private static readonly string[] Roots = ["src", "tests"];

    private static readonly string[] Files =
    [
        "Coffer.slnx", "global.json", "Directory.Build.props", "Directory.Build.targets",
        "Directory.Packages.props", "scripts/ci-dotnet-shards.sh", "scripts/dotnet-shard-filters.sh",
        "scripts/test-class-timings.sh",
    ];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".sh", ".slnx",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "wwwroot", "coverage", "test-results",
    };

    [Fact]
    public void Nothing_the_dotnet_job_runs_reads_the_web_manifest()
    {
        var repo = RepoRoot();
        var candidates = Roots
            .Select(r => Path.Combine(repo, r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetRelativePath(repo, f).Replace('\\', '/').Split('/')
                .Any(SkippedDirectories.Contains))
            // The web tree is the one the manifest belongs to.
            .Where(f => !Path.GetRelativePath(repo, f).Replace('\\', '/').StartsWith("src/Web/", StringComparison.Ordinal))
            .Concat(Files.Select(f => Path.Combine(repo, f)).Where(File.Exists))
            .ToList();

        // A walk that found nothing would pass for the wrong reason.
        Assert.Contains(candidates, f => f.EndsWith("Api.Tests.csproj", StringComparison.Ordinal));
        Assert.Contains(candidates, f => f.EndsWith("ci-dotnet-shards.sh", StringComparison.Ordinal));
        Assert.True(candidates.Count > 500, $"only {candidates.Count} files were scanned");

        var offenders = candidates
            .Where(f => Needles.Any(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(repo, f).Replace('\\', '/'))
            .Order()
            .ToList();
        Assert.True(offenders.Count == 0,
            "These files name the web manifest or its lock file, so a pull request that changes only those "
            + "can change a .NET result. Change the \"Decide whether the tests can tell\" step of "
            + "ci.yml, which skips the .NET tests for such a pull request, together with them: "
            + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
