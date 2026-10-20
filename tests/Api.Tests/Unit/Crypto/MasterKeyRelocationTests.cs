using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// Moving the master key off the volume that holds every backup (FU-077): the key and
/// the keys rotation archived move to the configured location, never through a moment
/// where neither copy is whole, and never onto storage a container discards.
/// </summary>
public sealed class MasterKeyRelocationTests : IDisposable
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\nid=v2\n";
    private const string OtherKey = "HxwdGhscGRgXFhUUExIREA8ODQwLCgkIBwYFBAMCAQA=\nid=v1\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"coffer-relocate-{Guid.NewGuid():N}");
    private string LegacyDir => Path.Combine(_root, "data");
    private string TargetDir => Path.Combine(_root, "keys");
    private string Legacy => Path.Combine(LegacyDir, "master.key");
    private string Target => Path.Combine(TargetDir, "master.key");

    private static readonly Func<string, string, bool> Allowed = (_, _) => true;

    public MasterKeyRelocationTests()
    {
        Directory.CreateDirectory(LegacyDir);
        Directory.CreateDirectory(TargetDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Moves_the_key_and_its_archives_to_the_configured_location()
    {
        File.WriteAllText(Legacy, Key);
        File.WriteAllText(Legacy + ".20260901T000000Z.bak", OtherKey);

        var outcome = MasterKeyRelocation.Run(Target, Legacy, Allowed);

        Assert.True(outcome.Moved);
        Assert.Equal(Key, File.ReadAllText(Target));
        Assert.Equal(OtherKey, File.ReadAllText(Target + ".20260901T000000Z.bak"));
        Assert.False(File.Exists(Legacy));
        Assert.Empty(Directory.EnumerateFiles(LegacyDir));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Target));
    }

    [Fact]
    public void Does_nothing_when_there_is_nothing_at_the_old_location_or_the_two_are_one()
    {
        Assert.False(MasterKeyRelocation.Run(Target, Legacy, Allowed).Moved);

        File.WriteAllText(Legacy, Key);
        Assert.False(MasterKeyRelocation.Run(Legacy, Legacy, Allowed).Moved);
        Assert.Equal(Key, File.ReadAllText(Legacy));
    }

    [Fact]
    public void Finishes_a_move_that_copied_and_stopped_before_the_delete()
    {
        File.WriteAllText(Legacy, Key);
        File.WriteAllText(Target, Key);

        MasterKeyRelocation.Run(Target, Legacy, Allowed);

        Assert.False(File.Exists(Legacy));
        Assert.Equal(Key, File.ReadAllText(Target));
    }

    [Fact]
    public void Refuses_to_choose_between_two_different_keys_and_touches_neither()
    {
        File.WriteAllText(Legacy, Key);
        File.WriteAllText(Target, OtherKey);

        var ex = Assert.Throws<InvalidOperationException>(() => MasterKeyRelocation.Run(Target, Legacy, Allowed));

        Assert.Contains("Two different master keys", ex.Message);
        Assert.Equal(Key, File.ReadAllText(Legacy));
        Assert.Equal(OtherKey, File.ReadAllText(Target));
    }

    [Fact]
    public void Refuses_a_location_that_is_not_a_volume_of_its_own_and_moves_nothing()
    {
        File.WriteAllText(Legacy, Key);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MasterKeyRelocation.Run(Target, Legacy, (_, _) => false));

        Assert.Contains("not on a volume of its own", ex.Message);
        Assert.Equal(Key, File.ReadAllText(Legacy));
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public void An_archive_a_pending_restore_names_is_renamed_in_its_record_too()
    {
        // A restore that adopted a source key recorded where the replaced key went, to
        // put it back if the restore fails. A moved archive must still be that one.
        File.WriteAllText(Legacy, Key);
        var archive = Legacy + ".20261017T120000Z.bak";
        File.WriteAllText(archive, OtherKey);
        var record = archive;

        MasterKeyRelocation.Run(Target, Legacy, Allowed, (() => record, moved => record = moved));

        Assert.Equal(Target + ".20261017T120000Z.bak", record);
        Assert.Equal(OtherKey, File.ReadAllText(record));
    }

    [Fact]
    public void An_archive_with_a_different_file_at_its_new_name_stays_where_it_is()
    {
        File.WriteAllText(Legacy, Key);
        var name = "master.key.20260901T000000Z.bak";
        File.WriteAllText(Path.Combine(LegacyDir, name), OtherKey);
        File.WriteAllText(Path.Combine(TargetDir, name), Key);

        var outcome = MasterKeyRelocation.Run(Target, Legacy, Allowed);

        Assert.Equal(OtherKey, File.ReadAllText(Path.Combine(LegacyDir, name)));
        Assert.Contains(outcome.Notes, n => n.StartsWith("Left ", StringComparison.Ordinal));
    }

    // --- whether a location is a volume of its own -----------------------------------

    [Theory]
    [InlineData("/app/keys/master.key", true)]          // its own volume
    [InlineData("/app/data/keys/master.key", false)]    // inside the data volume
    [InlineData("/app/master.key", false)]              // the container's writable layer
    [InlineData("/run/secrets/coffer_kek", true)]       // a mounted secret
    public void A_key_may_live_only_on_a_mount_that_is_neither_the_root_nor_the_data_volume(string target, bool own)
    {
        var mounts = MasterKeyRelocation.MountPoints(
        [
            "100 1 0:50 / / rw,relatime - overlay overlay rw",
            "101 100 8:1 /var/lib/docker/volumes/coffer_data/_data /app/data rw,relatime - ext4 /dev/sda1 rw",
            "102 100 8:1 /var/lib/docker/volumes/coffer_keys/_data /app/keys rw,relatime - ext4 /dev/sda1 rw",
            "103 100 0:60 / /run/secrets/coffer_kek ro,relatime - tmpfs tmpfs ro",
        ]);

        Assert.Equal(own, MasterKeyRelocation.IsOwnVolume(target, "/app/data/master.key", mounts));
    }

    [Fact]
    public void Mount_points_decode_the_kernels_escapes_and_match_by_whole_path_segments()
    {
        var mounts = MasterKeyRelocation.MountPoints(
            ["1 0 0:1 / / rw - overlay overlay rw", @"2 1 8:1 / /app/my\040keys rw - ext4 /dev/sda1 rw"]);

        Assert.Contains("/app/my keys", mounts);
        Assert.Equal("/app/my keys", MasterKeyRelocation.MountPointOf("/app/my keys", mounts));
        // A sibling that only shares a prefix is not under the mount.
        Assert.Equal("/", MasterKeyRelocation.MountPointOf("/app/my keystore", mounts));
    }

    [Fact]
    public void Outside_a_container_any_location_is_allowed()
    {
        // A host filesystem persists; the guard is for a container's writable layer.
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;
        Assert.True(MasterKeyRelocation.IsOwnVolume(Target, Legacy));
    }
}
