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
        // still reading corrupts it. An upgrade fetches into its stage, a fresh install into
        // its directory, and either way the move into place is a rename.
        Assert.Matches(@"fetch scripts/upgrade\.sh ""\$STAGE/upgrade\.sh\.new""", install);
        Assert.Matches(@"mv -f ""\$INSTALL_DIR/upgrade\.sh\.new"" ""\$INSTALL_DIR/upgrade\.sh""", install);
        Assert.DoesNotMatch(@"fetch scripts/upgrade\.sh ""\$INSTALL_DIR/upgrade\.sh""", install);
        Assert.DoesNotMatch(@"fetch scripts/upgrade\.sh ""\$STAGE/upgrade\.sh""", install);
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

    [Fact]
    public void An_upgrade_is_not_told_where_to_find_a_setup_token()
    {
        // An upgraded install has no setup screen, so the hint would read as a step.
        var install = Read("scripts", "install.sh").Split('\n');

        var hint = Array.FindIndex(install, l => l.Contains("If it asks for a one-time setup token", StringComparison.Ordinal));
        Assert.True(hint >= 0, "install.sh no longer prints the setup-token hint");
        var guard = Array.FindLastIndex(install, hint, l => l.TrimStart().StartsWith("if ", StringComparison.Ordinal));
        Assert.Equal(@"if [ ""$MODE"" = fresh ]; then", install[guard].Trim());
        // The hint and its command are the guard's whole body.
        Assert.Equal("fi", install[hint + 2].Trim());
    }

    /// <summary>
    /// The host updater's contract with the two scripts (ADR-0110). scripts/maintainer/
    /// check-install-contract.sh runs it against a fake docker; these hold the lines it
    /// leans on, so a rewrite that drops one fails here with a name.
    /// </summary>
    [Fact]
    public void Install_carries_the_contract_marker_that_upgrade_looks_for()
    {
        var install = Read("scripts", "install.sh");
        var upgrade = Read("scripts", "upgrade.sh");

        Assert.Matches(@"(?m)^INSTALL_CONTRACT=1$", install);
        // Exactly that line, and the run stops before anything is changed without it.
        Assert.Contains("grep -qx 'INSTALL_CONTRACT=1' \"$f\"", upgrade);
        Assert.Contains("DIE_CODE=13 die", upgrade);
    }

    [Fact]
    public void An_unattended_run_never_prompts_and_never_uses_sudo()
    {
        var install = Read("scripts", "install.sh");

        Assert.Contains(@"[ ""$UNATTENDED"" != 1 ] || SUDO=""""", install);
        // The two prompt helpers refuse before they read, and the terminal check is skipped.
        // (Within the function: up to the line that closes it.)
        Assert.Matches(@"(?s)ask\(\) \{(?:(?!\n\}\n).)*?\[ ""\$UNATTENDED"" != 1 \] \|\| die", install);
        Assert.Matches(@"(?s)yesno\(\) \{(?:(?!\n\}\n).)*?\[ ""\$UNATTENDED"" != 1 \] \|\| die", install);
        Assert.Contains(@"[ ""$UNATTENDED"" = 1 ] || [ -e ""$TTY_DEV"" ]", install);
        // An unattended run is an in-place upgrade of a named release, nothing else.
        Assert.Contains("COFFER_UNATTENDED=1 is for an in-place upgrade only", install);
        Assert.Contains("COFFER_UNATTENDED=1 needs COFFER_VERSION=<x.y.z>", install);
    }

    [Fact]
    public void The_exit_status_says_where_a_run_failed()
    {
        var install = Read("scripts", "install.sh");
        var upgrade = Read("scripts", "upgrade.sh");

        // 10 before the preflight passes, 11 between it and the first change, 12 after it.
        Assert.Matches(@"preflight\) echo 10 ;; prechange\) echo 11 ;; \*\) echo 12", install);
        // 14: the readiness wait timed out, which is not a failed upgrade.
        Assert.Contains("DIE_CODE=14 die", install);
        Assert.Contains("DIE_CODE=14 die", upgrade);
        // Before it hands over, upgrade.sh has changed nothing.
        Assert.Contains(@"exit ""${DIE_CODE:-10}""", upgrade);
    }

    [Fact]
    public void An_upgrade_changes_nothing_until_its_preflight_has_passed()
    {
        var install = Read("scripts", "install.sh");
        var upgrade = Read("scripts", "upgrade.sh");

        // The first change is begin_changes, and it comes straight after the preflight.
        Assert.Matches(@"(?s)preflight_upgrade\s+begin_changes\s+fi\s+commit_files", install);
        // The .bak files are install.sh's to write, at that first change; upgrade.sh no longer
        // overwrites .env.bak before it has run anything.
        Assert.DoesNotContain(@"cp -a ""$DIR/.env"" ""$DIR/.env.bak""", upgrade);
        Assert.Contains(@"cp -a ""$INSTALL_DIR/.env"" ""$INSTALL_DIR/.env.bak""", install);
        // A run that did not finish leaves them as they are.
        Assert.Contains(@"""$prev"" = started", install);
    }

    /// <summary>
    /// The API answers nothing until its migrations have run, and the first start of a
    /// release runs them over the whole database. Both scripts wait for it, bounded by the
    /// same overridable timeout, and fail with where to look rather than probing once.
    /// </summary>
    [Theory]
    [InlineData("install.sh")]
    [InlineData("upgrade.sh")]
    public void The_scripts_wait_for_readiness_while_the_first_start_migrates(string script)
    {
        var text = Read("scripts", script);

        Assert.Contains(@"READY_TIMEOUT=""${COFFER_READY_TIMEOUT_SECONDS:-600}""", text);
        // Refused before anything is changed, not discovered by the wait.
        Assert.Matches(@"case ""\$READY_TIMEOUT"" in\s*\n\s*''\|\*\[!0-9\]\*\|0\*\) die ", text);
        // Polls until ready or the timeout, with progress, rather than once.
        Assert.Matches(@"localhost:\$\{?port[^/""]*/readyz", text);
        Assert.Matches(@"curl -fsS --max-time 5 -o /dev/null ""[^""]+""", text);
        Assert.Matches(@"-lt ""\$READY_TIMEOUT""", text);
        Assert.Contains("still starting after", text);
        Assert.DoesNotContain("%{http_code}' \"http://localhost:${port:-8080}/readyz\"", text);

        // The timeout fails the run, says it may still be migrating, and where to look.
        var timeout = Regex.Match(text, @"die ""[^""]*did not answer /readyz within \$\{READY_TIMEOUT\}s(?<rest>[^""]*)""");
        Assert.True(timeout.Success, $"{script} does not fail on a readiness timeout");
        Assert.Contains("migrations", timeout.Groups["rest"].Value);
        Assert.Contains("compose logs -f api", timeout.Groups["rest"].Value);
        Assert.Contains("COFFER_READY_TIMEOUT_SECONDS", timeout.Groups["rest"].Value);
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
