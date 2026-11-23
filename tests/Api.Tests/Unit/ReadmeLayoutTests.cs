using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The README's Layout tree says what each project under <c>src/</c> and <c>tests/</c> holds.
/// A project added without its line leaves a reader no way to find it there, and one removed
/// while its line stays points them at nothing. This keeps the tree and the directories in step
/// both ways.
/// </summary>
public sealed partial class ReadmeLayoutTests
{
    private static readonly string[] Parents = ["src", "tests"];

    private static readonly HashSet<string> BuildOutput = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "coverage", "test-results",
    };

    [Fact]
    public void The_layout_tree_lists_every_project_directory_and_nothing_else()
    {
        var repo = RepoRoot();
        var listed = ListedChildren(File.ReadAllText(Path.Combine(repo, "README.md")));

        // A parse that found nothing would pass for the wrong reason.
        Assert.Contains("Api", listed["src"]);
        Assert.Contains("Api.Tests", listed["tests"]);

        var problems = new List<string>();
        foreach (var parent in Parents)
        {
            var onDisk = Directory.EnumerateDirectories(Path.Combine(repo, parent))
                .Where(HoldsSource)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var inTree = listed.TryGetValue(parent, out var names) ? names : [];

            problems.AddRange(onDisk.Except(inTree).Order()
                .Select(n => $"{parent}/{n}/ is not in the README layout tree"));
            problems.AddRange(inTree.Except(onDisk).Order()
                .Select(n => $"the README layout tree lists {parent}/{n}/, which does not exist"));
        }

        Assert.True(problems.Count == 0,
            "Update the Layout section of README.md, one line per project saying what it holds: "
            + string.Join("; ", problems));
    }

    /// <summary>
    /// The first fenced block after "## Layout", read as a tree: a root entry such as
    /// <c>├── src/</c>, then its children one level in (<c>│   ├── Api/</c>).
    /// </summary>
    private static Dictionary<string, HashSet<string>> ListedChildren(string readme)
    {
        var heading = readme.IndexOf("\n## Layout", StringComparison.Ordinal);
        Assert.True(heading >= 0, "README.md has no Layout section");
        var open = readme.IndexOf("```", heading, StringComparison.Ordinal);
        var close = readme.IndexOf("```", open + 3, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "the Layout section has no fenced tree");

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? root = null;
        foreach (var line in readme[(open + 3)..close].Split('\n'))
        {
            var top = RootEntry().Match(line);
            if (top.Success)
            {
                root = top.Groups["name"].Value;
                continue;
            }
            var child = ChildEntry().Match(line);
            if (child.Success && root is not null)
            {
                if (!result.TryGetValue(root, out var set))
                {
                    result[root] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.Add(child.Groups["name"].Value);
            }
        }
        return result;
    }

    // A directory left holding only build output (a project renamed or removed locally) is
    // not a project.
    private static bool HoldsSource(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(f => !Path.GetRelativePath(directory, f).Replace('\\', '/').Split('/')
                .Any(BuildOutput.Contains));

    [GeneratedRegex(@"^[├└]── (?<name>[^\s/]+)/")]
    private static partial Regex RootEntry();

    [GeneratedRegex(@"^(?:│|\s)   [├└]── (?<name>[^\s/]+)/")]
    private static partial Regex ChildEntry();

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
