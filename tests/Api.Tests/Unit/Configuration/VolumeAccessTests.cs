using System.Runtime.InteropServices;

using Coffer.Api.Configuration;

namespace Coffer.Api.Tests.Unit.Configuration;

/// <summary>
/// The boot check that what the API keeps on its volumes is its own (FU-084): a volume a
/// root-run version wrote stops the boot naming the fix, and nothing else does.
/// </summary>
public sealed class VolumeAccessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("coffer-volume-access-").FullName;

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Prepend(_dir))
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, Directory.Exists(path)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(_dir, recursive: true);
    }

    private string Data => Path.Combine(_dir, "data");
    private string Keys => Path.Combine(_dir, "keys");
    private string Key => Path.Combine(Keys, "master.key");

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Data, "backups"));
        File.WriteAllText(Path.Combine(Data, "backups", "coffer-x.cofferbak"), "archive");
        Directory.CreateDirectory(Keys);
        File.WriteAllText(Key, "key");
    }

    /// <summary>Whether this environment can observe a denial at all: not on Windows, and
    /// not as root, which the mode does not bind.</summary>
    private static bool Denies(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        var probe = Path.Combine(directory, ".probe");
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Volumes_the_api_owns_pass_and_what_does_not_exist_yet_is_skipped()
    {
        Seed();
        VolumeAccess.EnsureOwned(Data, Key);

        // A fresh install: neither exists until the API creates them.
        VolumeAccess.EnsureOwned(Path.Combine(_dir, "nothing"), Path.Combine(_dir, "none", "master.key"));
        // The probe leaves nothing behind.
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_dir, ".coffer-write-probe-*", SearchOption.AllDirectories), _ => true);
    }

    [Fact]
    public void A_directory_root_still_owns_stops_the_boot_naming_the_fix()
    {
        Seed();
        if (OperatingSystem.IsWindows() || !Denies(_dir)) return;
        var backups = Path.Combine(Data, "backups");
        File.SetUnixFileMode(backups, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var ex = Assert.Throws<InvalidOperationException>(() => VolumeAccess.EnsureOwned(Data, Key, "8978f8997f51"));

        Assert.Contains(backups, ex.Message);
        // The fix is a command aimed at this container, not a script: the hosts that see this
        // were set up by a pre-0.122.0 install.sh, which never put one on them.
        Assert.Contains(VolumeAccess.HandOverCommand("8978f8997f51", [Data, Keys]), ex.Message);
        Assert.DoesNotContain("own-api-volumes.sh", ex.Message);
        Assert.DoesNotContain("COFFER_CONTAINER_PREFIX", ex.Message);
    }

    [Theory]
    [InlineData("8978f8997f51", "8978f8997f51", true)]   // Docker's default hostname: the short id
    [InlineData("coffer-api", "coffer-api", false)]
    [InlineData("build-host", "coffer-api", false)]
    [InlineData("8978F8997F51", "coffer-api", false)]   // Docker writes ids in lower case
    [InlineData("8978f8997f5", "coffer-api", false)]
    public void The_command_names_this_container_by_the_hostname_Docker_gave_it(string host, string name, bool exact) =>
        Assert.Equal((name, exact), VolumeAccess.ContainerOf(host));

    [Fact]
    public void The_upgrade_guide_prints_the_same_command_for_the_default_container()
    {
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "upgrading.md"));

        Assert.Contains(VolumeAccess.HandOverCommand("coffer-api", ["/app/data", "/app/keys"]), guide);
        Assert.DoesNotContain("bash scripts/own-api-volumes.sh", guide);
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

    [Fact]
    public void A_file_it_cannot_read_stops_the_boot()
    {
        Seed();
        if (OperatingSystem.IsWindows() || !Denies(_dir)) return;
        var archive = Path.Combine(Data, "backups", "coffer-x.cofferbak");
        File.SetUnixFileMode(archive, UnixFileMode.UserWrite);

        var ex = Assert.Throws<InvalidOperationException>(() => VolumeAccess.EnsureOwned(Data, Key));

        Assert.Contains(archive, ex.Message);
    }

    [Fact]
    public void Beside_the_key_only_the_key_and_its_archives_are_its_concern()
    {
        // The key may share a directory with anything: the tests keep theirs in the system
        // temp directory, where other users' files are unreadable by design.
        Seed();
        if (OperatingSystem.IsWindows() || !Denies(_dir)) return;
        var neighbour = Path.Combine(Keys, "someone-elses.secret");
        File.WriteAllText(neighbour, "not ours");
        File.SetUnixFileMode(neighbour, UnixFileMode.None);

        VolumeAccess.EnsureOwned(Data, Key);

        var archived = Key + ".20261019T120000.bak";
        File.WriteAllText(archived, "the key a rotation replaced");
        File.SetUnixFileMode(archived, UnixFileMode.None);
        var ex = Assert.Throws<InvalidOperationException>(() => VolumeAccess.EnsureOwned(Data, Key));
        Assert.Contains(archived, ex.Message);
        Assert.DoesNotContain(neighbour, ex.Message);
    }

    [Fact]
    public void A_file_another_process_holds_open_is_not_a_refusal()
    {
        // Test hosts in parallel shards share a data directory; a backup being written by
        // one must not stop another's boot. Observable where opens are exclusive (Windows).
        Seed();
        using var held = new FileStream(
            Path.Combine(Data, "backups", "coffer-x.cofferbak"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        VolumeAccess.EnsureOwned(Data, Key);
    }
}
