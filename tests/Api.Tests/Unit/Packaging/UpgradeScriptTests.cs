using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// The upgrade command, <c>bash ~/coffer/upgrade.sh</c> (0.123.0): install.sh puts it on every
/// host, and it runs the release's own install.sh, never an old copy and never main's.
/// The documents an operator reads first say so.
/// </summary>
public sealed class UpgradeScriptTests
{
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepoRoot(), .. path]));

    [Fact]
    public void Install_puts_the_current_upgrade_script_on_the_host_by_rename()
    {
        var install = Read("scripts", "install.sh");

        // By rename: this run may be upgrade.sh's child, and overwriting a script bash is
        // still reading corrupts it.
        Assert.Matches(@"fetch scripts/upgrade\.sh ""\$INSTALL_DIR/upgrade\.sh\.new""", install);
        Assert.Matches(@"mv -f ""\$INSTALL_DIR/upgrade\.sh\.new"" ""\$INSTALL_DIR/upgrade\.sh""", install);
        Assert.DoesNotMatch(@"fetch scripts/upgrade\.sh ""\$INSTALL_DIR/upgrade\.sh""", install);
        // Where its releases come from, for the next upgrade.
        Assert.Contains(@"set_env COFFER_REPO ""$REPO""", install);
    }

    [Fact]
    public void Install_upgrades_in_place_without_asking_when_the_upgrade_script_runs_it()
    {
        var install = Read("scripts", "install.sh");

        var block = Regex.Match(install, @"if \[ ""\$\{COFFER_UPGRADE:-\}"" = 1 \]; then(?<body>.*?)\nelif", RegexOptions.Singleline);
        Assert.True(block.Success, "install.sh has no COFFER_UPGRADE branch");
        Assert.Contains("MODE=upgrade", block.Groups["body"].Value);
        // Never a fresh install by accident.
        Assert.Contains("die", block.Groups["body"].Value);
        Assert.DoesNotContain("MODE=fresh", block.Groups["body"].Value);
    }

    [Fact]
    public void The_upgrade_script_runs_the_release_s_own_installer()
    {
        var upgrade = Read("scripts", "upgrade.sh");

        Assert.Contains(@"contents/scripts/install.sh?ref=v$V", upgrade);
        Assert.DoesNotContain("ref=main", upgrade);
        Assert.DoesNotContain("bash <(", upgrade);
        Assert.Matches(@"COFFER_VERSION=""\$V"" COFFER_UPGRADE=1", upgrade);
        // A token reaches curl on stdin, never its argv.
        Assert.DoesNotMatch(@"-H\s+""Authorization", upgrade);
        Assert.Contains("curl -fsSL -H @-", upgrade);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs", "upgrading.md")]
    [InlineData("docs", "operations.md")]
    public void The_operator_documents_give_the_upgrade_command(params string[] path) =>
        Assert.Contains("bash ~/coffer/upgrade.sh", Read(path));

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
