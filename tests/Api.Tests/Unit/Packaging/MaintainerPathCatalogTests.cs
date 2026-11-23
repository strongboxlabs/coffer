using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// Every test file that names a maintainer path the public snapshot does not carry
/// (<c>scripts/maintainer</c>, <c>docs/maintainer</c>, <c>.github</c>) is recorded here with
/// how it holds up without it, so a new one cannot reach the snapshot unexamined.
/// </summary>
/// <remarks>
/// The public snapshot leaves the maintainer tree out, and keeps only its own
/// <c>.github/workflows</c>. A test that reads a maintainer file fails there on a file that is
/// not there, so it is marked <c>[MaintainerFact]</c> or <c>[MaintainerTheory]</c> and skipped
/// (<c>MaintainerTree</c>); this test checks a file recorded as marked uses one. A file that
/// only names such a path, or reads one only where present, is recorded with the reason it
/// holds up. The record is per file: a marked file may also hold tests that read nothing of it.
/// </remarks>
public sealed class MaintainerPathCatalogTests
{
    private const string Marked = "marked";

    private static readonly Regex MaintainerPath = new(
        @"scripts/maintainer|docs/maintainer|""scripts"",\s*""maintainer""|""docs"",\s*""maintainer""|\.github/|""\.github""",
        RegexOptions.Compiled);

    private static readonly Regex MaintainerAttribute = new(@"\[Maintainer(?:Fact|Theory)\b", RegexOptions.Compiled);

    /// <summary>Each test file naming a maintainer path: <see cref="Marked"/>, or the reason it
    /// holds up without the maintainer tree as it is.</summary>
    private static readonly Dictionary<string, string> Catalog = new(StringComparer.Ordinal)
    {
        ["tests/Api.Tests/Unit/DecisionCitationCatalogTests.cs"] = Marked,
        ["tests/Api.Tests/Unit/InKindRebuildPredicateTests.cs"] = Marked,
        ["tests/Api.Tests/Unit/Packaging/InstallContractTests.cs"] = Marked,
        ["tests/Importer.Moneydance.Tests/Db/Migration263InKindSaleTests.cs"] = Marked,
        ["tests/Api.Tests/Integration/Infra/MaintainerTree.cs"] =
            "the skip rule itself: it names the tree to test for it",
        ["tests/Api.Tests/Unit/Packaging/WorkflowHardeningTests.cs"] =
            "reads .github/workflows, which the public snapshot has too, and docs/maintainer/public-ci only where present",
        ["tests/Api.Tests/Unit/Packaging/ImageIdentityTests.cs"] =
            "reads .github/workflows/release.yml, which the public snapshot has too, and only where present",
        ["tests/Api.Tests/Unit/Packaging/UpgradeScriptTests.cs"] =
            "names scripts/maintainer in a comment only",
        ["tests/Api.Tests/Unit/DotnetTestsIgnoreWebManifestTests.cs"] =
            "names .github in a comment only, and reads only the roots that exist",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "TestResults",
    };

    [Fact]
    public void Every_test_file_naming_a_maintainer_path_is_recorded()
    {
        var repo = RepoRoot();
        var self = $"tests/Api.Tests/Unit/Packaging/{nameof(MaintainerPathCatalogTests)}.cs";
        var files = Directory.EnumerateFiles(Path.Combine(repo, "tests"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repo, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(SkippedDirectories.Contains))
            .ToList();

        // Fail closed: a wrong root would scan nothing and pass.
        Assert.True(files.Count > 500, $"Scanned only {files.Count} test files.");
        Assert.Contains(self, files);

        var naming = files
            .Where(f => f != self && MaintainerPath.IsMatch(File.ReadAllText(Path.Combine(repo, f))))
            .ToHashSet(StringComparer.Ordinal);

        var unrecorded = naming.Where(f => !Catalog.ContainsKey(f)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unrecorded.Count == 0,
            "These test files name a maintainer path the public snapshot does not carry. Mark each test that "
            + "reads one [MaintainerFact] or [MaintainerTheory] and record the file here as marked, or record why "
            + "it holds up as it is:\n  " + string.Join("\n  ", unrecorded));

        var stale = Catalog.Keys.Where(f => !naming.Contains(f)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These recorded files no longer name a maintainer path; remove them:\n  " + string.Join("\n  ", stale));

        var unmarked = Catalog
            .Where(e => e.Value == Marked && !MaintainerAttribute.IsMatch(File.ReadAllText(Path.Combine(repo, e.Key))))
            .Select(e => e.Key)
            .ToList();
        Assert.True(unmarked.Count == 0,
            "These files are recorded as marked but use no [MaintainerFact] or [MaintainerTheory]:\n  "
            + string.Join("\n  ", unmarked));

        Assert.All(Catalog.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
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
        throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
