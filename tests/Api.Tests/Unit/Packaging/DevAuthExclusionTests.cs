using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// The published image has no dev auth in it (FU-089): any request with
/// <c>X-Dev-Auth: 1</c> is the admin, so it must not merely be switched off by
/// environment and configuration on a host. The tests here run against a build that HAS
/// it, so they hold the files that decide what the image gets instead.
/// </summary>
public sealed class DevAuthExclusionTests
{
    [Fact]
    public void The_image_is_published_in_Release_without_asking_for_dev_auth()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        var publish = Regex.Matches(dockerfile, @"^RUN dotnet publish .*$", RegexOptions.Multiline);

        var line = Assert.Single(publish).Value;
        Assert.Contains("src/Api/Api.csproj", line);
        Assert.Contains("-c Release", line);
        Assert.DoesNotContain("CofferDevAuth", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COFFER_DEV_AUTH", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Release_build_leaves_dev_auth_out_unless_asked()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Api.csproj"));

        // On by default only in Debug; the define and the handler both follow the switch.
        Assert.Contains(
            "<CofferDevAuth Condition=\"'$(CofferDevAuth)' == '' And '$(Configuration)' == 'Debug'\">true</CofferDevAuth>",
            project);
        Assert.Contains("<DefineConstants Condition=\"'$(CofferDevAuth)' == 'true'\">", project);
        Assert.Matches(new Regex(
            @"<ItemGroup Condition=""'\$\(CofferDevAuth\)' != 'true'"">\s*<Compile Remove=""Auth/DevAuthHandler.cs"" />"),
            project);
    }

    [Fact]
    public void Every_registration_of_dev_auth_is_behind_the_compile_time_switch()
    {
        // The handler type is removed from a Release build, so a use outside the #if would
        // not compile there — but a use of the scheme NAME would, and could register some
        // other handler under it. Every file naming the handler must carry the switch.
        var api = Path.Combine(RepoRoot(), "src", "Api");
        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !f.EndsWith("DevAuthHandler.cs", StringComparison.Ordinal)))
        {
            var code = File.ReadAllText(file);
            if (!code.Contains("DevAuthHandler", StringComparison.Ordinal)) continue;
            var withoutComments = Regex.Replace(code, @"//.*$", "", RegexOptions.Multiline);
            if (!withoutComments.Contains("DevAuthHandler", StringComparison.Ordinal)) continue;
            Assert.True(code.Contains("#if COFFER_DEV_AUTH", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} uses DevAuthHandler outside #if COFFER_DEV_AUTH");
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
        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
