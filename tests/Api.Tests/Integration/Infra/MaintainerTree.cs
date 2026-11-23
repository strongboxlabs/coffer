namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The maintainer tree: <c>scripts/maintainer</c>, <c>docs/maintainer</c> and the maintainer's
/// <c>.github</c> files. The public snapshot carries none of it (it keeps its own
/// <c>.github/workflows</c> and nothing else there). A test that reads any of it checks the
/// maintainer's own tooling, and in a checkout without that tooling it has nothing to check.
/// </summary>
/// <remarks>
/// Such a test is marked <see cref="MaintainerFactAttribute"/> or
/// <see cref="MaintainerTheoryAttribute"/>, so where the tree is absent it is skipped and the
/// run says so, rather than failing on a missing file or passing over nothing. A checkout that
/// carries <c>scripts/maintainer</c> runs these tests every time.
/// <c>MaintainerPathCatalogTests</c> holds every test file that names a maintainer path to a
/// recorded handling.
/// </remarks>
internal static class MaintainerTree
{
    public const string SkipReason =
        "Reads the maintainer tree (scripts/maintainer, docs/maintainer, .github), which the public snapshot does not carry.";

    /// <summary>Whether this checkout carries the maintainer tree. A test that cannot find the
    /// checkout's root at all is not skipped: it runs and fails on whatever it cannot find.</summary>
    public static bool Present { get; } =
        RepoRoot() is not { } root || Directory.Exists(Path.Combine(root, "scripts", "maintainer"));

    /// <summary>The checkout's root: the first directory above the test binaries holding both
    /// the Dockerfile and <c>src/Api</c>, as every test that reads the checkout finds it.</summary>
    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
        }
        return null;
    }
}

/// <summary>A <see cref="FactAttribute"/> skipped where the maintainer tree is absent
/// (<see cref="MaintainerTree"/>).</summary>
public sealed class MaintainerFactAttribute : FactAttribute
{
    public MaintainerFactAttribute()
    {
        if (!MaintainerTree.Present) Skip = MaintainerTree.SkipReason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> skipped where the maintainer tree is absent
/// (<see cref="MaintainerTree"/>).</summary>
public sealed class MaintainerTheoryAttribute : TheoryAttribute
{
    public MaintainerTheoryAttribute()
    {
        if (!MaintainerTree.Present) Skip = MaintainerTree.SkipReason;
    }
}
