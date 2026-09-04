// Namespace/directory is Packaging, not Build: .gitignore carries `[Bb]uild/`, so a
// test placed under Unit/Build/ is silently excluded from the commit — it compiles
// and passes on the author's disk and does not exist for anyone else.
namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// Both layers of the image must be stamped with the commit they were built from.
/// </summary>
/// <remarks>
/// The About panel offers a cross-layer check — matching UI and API build numbers
/// confirm both came from one commit — and for every published image that check
/// was impossible to satisfy. The web stage ran <c>npm run build</c> before the
/// identity args were declared, so <c>vite.config.ts</c> fell back to shelling out
/// to git, found no <c>.git</c> (it is dockerignored), and stamped the bundle
/// "build 0 / dev". The API row was stamped correctly, so the panel showed a
/// mismatch on every deployment and agreement only in a local dev build — the one
/// place nobody needs it.
///
/// <para>This is a source scan rather than a behavioural test because the failure
/// is one of ORDER inside a Dockerfile, which nothing at runtime can observe: the
/// image builds fine, ships fine, and quietly reports the wrong thing. A future
/// reshuffle of those stages would reintroduce it in silence.</para>
/// </remarks>
public sealed class ImageIdentityTests
{
    private static readonly string[] IdentityArgs =
        ["SOURCE_COMMIT_SHA", "SOURCE_COMMIT_COUNT", "SOURCE_COMMIT_DATE"];

    [Fact]
    public void The_web_stage_receives_the_commit_identity_before_it_builds()
    {
        var lines = File.ReadAllLines(LocateDockerfile());

        var webStage = Array.FindIndex(lines,
            l => l.StartsWith("FROM", StringComparison.Ordinal)
                 && l.Contains(" AS web", StringComparison.Ordinal));
        Assert.True(webStage >= 0, "no `FROM ... AS web` stage in the Dockerfile");

        var viteBuild = Array.FindIndex(lines, webStage,
            l => l.Contains("npm run build", StringComparison.Ordinal));
        Assert.True(viteBuild > webStage, "the web stage does not run `npm run build`");

        // Only what the web stage itself declares counts. An ARG in a later stage
        // is invisible here — which is exactly how this broke.
        var stageBody = string.Join('\n', lines[webStage..viteBuild]);

        foreach (var arg in IdentityArgs)
        {
            Assert.Contains($"ARG {arg}", stageBody, StringComparison.Ordinal);

            // ARG alone is not enough: Vite reads process.env, so the value has to
            // be promoted to an ENV before the build runs.
            Assert.Contains($"{arg}=${arg}", stageBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_release_workflow_supplies_every_arg_the_image_expects()
    {
        var workflow = Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");

        // Skipped rather than failed when absent: .github is deny-listed from the
        // public snapshot, so a consumer running this suite has no workflow to check
        // and should not see a red test for a file they are not meant to have.
        if (!File.Exists(workflow)) return;

        var text = File.ReadAllText(workflow);
        foreach (var arg in IdentityArgs)
        {
            Assert.Contains($"{arg}=", text, StringComparison.Ordinal);
        }
    }

    private static string LocateDockerfile()
    {
        var path = Path.Combine(RepoRoot(), "Dockerfile");
        Assert.True(File.Exists(path), $"no Dockerfile at {path}");
        return path;
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
