using System.Text.RegularExpressions;

using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// The rotation gate (FU-078): seals in flight finish before a re-wrap starts, new ones are
/// refused while it runs, and none at all after it commits — and every sealing path in the
/// API holds a lease, which a source scan enforces so a new one cannot quietly skip it.
/// </summary>
public sealed class MasterKeyWriteGateTests
{
    [Fact]
    public void An_open_gate_grants_leases()
    {
        var gate = new MasterKeyWriteGate();
        using var a = gate.Enter();
        using var b = gate.Enter();
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public async Task Closing_waits_for_the_leases_in_flight()
    {
        var gate = new MasterKeyWriteGate();
        var lease = gate.Enter();

        var closing = gate.CloseAndDrainAsync(TimeSpan.FromSeconds(5), TimeProvider.System, default);
        await Task.Delay(100);
        Assert.False(closing.IsCompleted);
        Assert.Throws<MasterKeyRotatingException>(() => gate.Enter());   // refused while draining

        lease.Dispose();
        Assert.True(await closing);
    }

    [Fact]
    public async Task A_drain_that_times_out_reopens_the_gate()
    {
        var gate = new MasterKeyWriteGate();
        using var stuck = gate.Enter();

        Assert.False(await gate.CloseAndDrainAsync(TimeSpan.FromMilliseconds(100), TimeProvider.System, default));
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void A_retired_gate_stays_shut_and_reopen_does_not_undo_it()
    {
        var gate = new MasterKeyWriteGate();
        gate.Retire();
        gate.Reopen();

        Assert.True(gate.IsClosed);
        Assert.Throws<MasterKeyRotatingException>(() => gate.Enter());
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_it_once()
    {
        var gate = new MasterKeyWriteGate();
        var lease = gate.Enter();
        lease.Dispose();
        lease.Dispose();

        // Had the second dispose released again, the count would be negative and a later
        // drain would return while a real lease was still out.
        using var real = gate.Enter();
        Assert.False(await gate.CloseAndDrainAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, default));
    }

    // --- every sealing path holds a lease ----------------------------------

    private static readonly Regex Seals = new(@"\b(SealWithMasterKey|CreateWrappedLek)\s*\(", RegexOptions.Compiled);

    /// <summary>Files that seal under the master key without a lease, and why that is right.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["src/Api/Crypto/LedgerKeyService.cs"] = "defines the seal methods (and refuses while the gate is closed)",
        ["src/Api/Crypto/KekRotationService.cs"] = "IS the rotation: it seals under the NEW key, behind the closed gate",
    };

    [Fact]
    public void Every_file_that_seals_under_the_master_key_takes_a_lease()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 300, $"Scanned only {files.Count} files; the guard is not looking at the API.");

        var sealing = new List<string>();
        var violations = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var code = File.ReadAllText(file);
            if (!Seals.IsMatch(code)) continue;
            sealing.Add(relative);
            if (Exempt.ContainsKey(relative)) continue;
            if (!code.Contains("EnterSealing(", StringComparison.Ordinal))
                violations.Add(relative);
        }

        // Fail closed: the paths known to seal must be found, or the scan proves nothing.
        Assert.Contains("src/Api/Db/Repositories/LedgersRepository.cs", sealing);
        Assert.Contains("src/Api/Backup/BackupManager.cs", sealing);
        Assert.Contains("src/Api/Backup/Drive/DriveSyncService.cs", sealing);
        Assert.True(violations.Count == 0,
            "These seal under the master key without holding a rotation lease (FU-078): "
            + string.Join(", ", violations)
            + ". Take `using var sealing = keys.EnterSealing();` before sealing and hold it until the "
            + "sealed value is saved, or the next rotation can strand it.");
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
