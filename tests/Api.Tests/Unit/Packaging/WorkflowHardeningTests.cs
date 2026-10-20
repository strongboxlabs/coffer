using System.Text.RegularExpressions;

using YamlDotNet.RepresentationModel;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// What a release is built and installed from is fixed, not whatever a tag points at
/// today (FU-094). Every workflow in <c>.github/workflows</c> is read, and the canonical
/// copies kept beside them where a checkout has those: a new one inherits these rules
/// without anyone remembering to add it.
/// </summary>
public sealed partial class WorkflowHardeningTests
{
    public static TheoryData<string> Workflows()
    {
        var root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (var dir in WorkflowDirectories(root))
            foreach (var file in Directory.EnumerateFiles(dir, "*.yml").Order(StringComparer.Ordinal))
                data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        return data;
    }

    /// <summary>The workflows directory, and the canonical copies only where a checkout
    /// carries them; a published snapshot does not.</summary>
    private static IEnumerable<string> WorkflowDirectories(string root)
    {
        yield return Path.Combine(root, ".github", "workflows");
        var copies = Path.Combine(root, "docs", "maintainer", "public-ci");
        if (Directory.Exists(copies)) yield return copies;
    }

    private static string Text(string workflow) => File.ReadAllText(Path.Combine(RepoRoot(), workflow));

    private static YamlMappingNode Parse(string workflow)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(Text(workflow)));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<(string Job, YamlMappingNode Spec)> Jobs(YamlMappingNode root) =>
        ((YamlMappingNode)root.Children[new YamlScalarNode("jobs")]).Children
            .Select(kv => (((YamlScalarNode)kv.Key).Value!, (YamlMappingNode)kv.Value));

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)
            ? ((YamlSequenceNode)steps).Children.Cast<YamlMappingNode>()
            : [];

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s+(\S+)(.*)$", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$")]
    private static partial Regex CommitPin();

    [GeneratedRegex(@"^\s+#\s+v\d+\.\d+\.\d+\s*$")]
    private static partial Regex ReleaseComment();

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_action_is_pinned_to_a_commit_with_its_release_named(string workflow)
    {
        // A tag can be moved after it was reviewed, under a job holding packages:write.
        // The comment names the release a pin is at, which is what moves it on.
        var uses = UsesLine().Matches(Text(workflow)).ToList();
        Assert.NotEmpty(uses);
        foreach (var use in uses)
        {
            var action = use.Groups[1].Value;
            Assert.True(CommitPin().IsMatch(action), $"{workflow}: `{action}` is not pinned to a commit");
            Assert.True(ReleaseComment().IsMatch(use.Groups[2].Value.TrimEnd('\r')),
                $"{workflow}: `{action}` does not name its release in a `# vX.Y.Z` comment");
        }
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_checkout_leaves_no_credential_in_the_clone(string workflow)
    {
        // persist-credentials writes the job's token into .git/config, where every later
        // step, and anything a build script runs, can read it.
        var checkouts = Jobs(Parse(workflow))
            .SelectMany(j => Steps(j.Spec).Select(s => (j.Job, Step: s)))
            .Where(s => Scalar(s.Step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(checkouts);
        foreach (var (job, step) in checkouts)
        {
            var with = step.Children.TryGetValue(new YamlScalarNode("with"), out var w) ? (YamlMappingNode)w : null;
            Assert.True(with is not null && Scalar(with, "persist-credentials") == "false",
                $"{workflow}, job {job}: a checkout without persist-credentials: false");
        }
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_workflow_states_its_permissions(string workflow)
    {
        // Unstated, a job gets the repository's default token scope, which nothing here
        // pins down. Stated at the top, a job can only widen it on purpose.
        Assert.True(Parse(workflow).Children.ContainsKey(new YamlScalarNode("permissions")),
            $"{workflow} has no top-level permissions block");
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_job_runs_a_service_container(string workflow)
    {
        // A service is published on every interface of the runner, and its password has
        // to be written into the workflow: nothing a step generates reaches its env.
        // ci.yml starts its Postgres in a step instead, on loopback, under a password
        // made for the job.
        foreach (var (job, spec) in Jobs(Parse(workflow)))
            Assert.False(spec.Children.ContainsKey(new YamlScalarNode("services")),
                $"{workflow}, job {job}: a services: container");
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_workflow_that_pushes_the_image_records_its_digest(string workflow)
    {
        // install.sh pulls a release's image by the digest recorded on the release, and
        // from 0.122.0 refuses a release without one.
        var steps = Jobs(Parse(workflow)).SelectMany(j => Steps(j.Spec)).ToList();
        var build = steps.SingleOrDefault(s => Scalar(s, "uses")?.StartsWith("docker/build-push-action@", StringComparison.Ordinal) == true);
        if (build is null) return;   // pushes nothing
        Assert.Equal("build", Scalar(build, "id"));

        var record = steps.Skip(steps.IndexOf(build) + 1)
            .SingleOrDefault(s => Scalar(s, "run")?.Contains("image-digest", StringComparison.Ordinal) == true);
        Assert.NotNull(record);
        Assert.Contains("steps.build.outputs.digest", record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_compose_file_runs_the_recorded_digest()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        Assert.Contains("${COFFER_IMAGE_TAG:-latest}${COFFER_IMAGE_DIGEST:+@${COFFER_IMAGE_DIGEST}}", compose);
    }

    [Theory]
    [InlineData("secrets/")]
    [InlineData(".env.*")]
    [InlineData("**/.env.*")]
    [InlineData("src/Api/data/")]
    public void A_build_context_carries_no_secret_a_checkout_holds(string pattern)
    {
        var ignore = File.ReadAllLines(Path.Combine(RepoRoot(), ".dockerignore")).Select(l => l.Trim());
        Assert.Contains(pattern, ignore);
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
