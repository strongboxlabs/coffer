using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Coffer.Api.Tests.Unit.Packaging;

/// <summary>
/// scripts/install.sh and scripts/upgrade.sh as the host updater runs them (ADR-0110): an
/// unattended upgrade changes nothing until its preflight has passed, never prompts, never uses
/// sudo, and says in its exit status where it failed. The behaviour is checked by
/// check-install-contract.sh (a maintainer script) against a fake docker, a fake curl and a fake
/// GitHub; this runs it, so the check is part of the suite on every PR and cannot be skipped by
/// not remembering to run it. The script also runs broken copies of install.sh and fails unless
/// it catches them, so a gate that stopped checking would fail here.
/// </summary>
public sealed class InstallContractTests
{
    [Fact]
    public async Task The_install_and_upgrade_scripts_keep_the_updater_contract()
    {
        // The scripts read /proc/version and use GNU tools; the other script tests skip the same way.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(Path.Combine("scripts", "maintainer", "check-install-contract.sh"));

        using var process = Process.Start(info) ?? throw new InvalidOperationException("bash did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout + await stderr;

        // Exit 3 means the script could not run here (root, or a missing tool): not a pass.
        Assert.True(process.ExitCode == 0, $"check-install-contract.sh exited {process.ExitCode}:\n{output}");
        Assert.Contains("install contract: all checks passed", output);
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
