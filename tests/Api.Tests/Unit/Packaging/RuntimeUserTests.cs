using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// The API container runs unprivileged (FU-084), and every path that replaces a root-run
/// container hands its volumes over first. Each of these was a separate edit; any one of
/// them reverting would leave the image root again, or an upgrade unable to read its key.
/// </summary>
public sealed partial class RuntimeUserTests
{
    [GeneratedRegex(@"^\s*USER\s+(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex UserLine();

    [Fact]
    public void The_runtime_stage_ends_as_an_unprivileged_user()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        var runtime = dockerfile[dockerfile.LastIndexOf("\nFROM ", StringComparison.Ordinal)..];

        var users = UserLine().Matches(runtime).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(users);
        Assert.DoesNotContain(users[^1], new[] { "root", "0", "0:0", "root:root" });
        // Created owned by that user, so Docker seeds a new volume with it.
        Assert.Contains("install -d -o \"$APP_UID\" -g \"$APP_UID\"", runtime);
        Assert.Contains("/app/data /app/keys", runtime);
    }

    [Fact]
    public void Compose_does_not_put_the_api_back_to_root()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        Assert.DoesNotMatch(@"(?m)^\s*user:\s*""?(0|root)", compose);
    }

    [Theory]
    [InlineData("scripts/install.sh")]
    [InlineData("scripts/dev-up-docker.sh")]
    public void Every_path_that_replaces_the_api_container_hands_its_volumes_over_first(string script)
    {
        // The hand-over takes the volumes from the container that is about to be replaced;
        // after `up` it is gone, and the new one cannot read its own master key.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), script));
        // The run, not the installer's fetch of the script.
        var run = Regex.Match(text, @"bash ""\$[A-Za-z_]+/scripts/own-api-volumes\.sh""");
        var handOver = run.Success ? run.Index : -1;
        var up = Regex.Match(text, @"(?m)^\s*(\$DOCKER |docker )compose up -d");

        Assert.True(handOver >= 0, $"{script} never runs own-api-volumes.sh");
        Assert.True(up.Success, $"{script}: no `up` found to order against");
        Assert.True(handOver < up.Index, $"{script} starts the new container before handing the volumes over");
        // The restore drill's upgrade leg runs it too, and is held by running: it starts a
        // root-run release on real volumes, then this build, which refuses them otherwise.
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
        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
