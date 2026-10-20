using System.Runtime.InteropServices;

using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// The on-disk contract for the master KEK (ADR-0092 D1). These assertions exist
/// because the failure they guard against is expensive: a truncated or
/// world-readable key file over live wrapped material.
/// </summary>
public sealed class MasterKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("coffer-kek-store").FullName;

    private MasterKeyStore NewStore(string name = "master.key")
        => new(Path.Combine(_dir, name));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_to_the_data_directory_beside_the_binary()
    {
        // The coffer_data volume in the Docker image — same place bootstrap.url
        // and the restore staging live, and NOT the postgres_data volume.
        var store = new MasterKeyStore(null);

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "data", "master.key"),
            store.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_configured_path_falls_back_to_the_default(string configured)
        => Assert.Equal(new MasterKeyStore(null).Path, new MasterKeyStore(configured).Path);

    [Fact]
    public void Honours_an_explicitly_configured_path()
    {
        // The injection story: /run/secrets/…, a projected k8s Secret, a Key Vault
        // CSI mount. All of them are "a file at a path we don't choose".
        var store = NewStore("injected.key");
        Assert.Equal(Path.Combine(_dir, "injected.key"), store.Path);
    }

    [Fact]
    public void Round_trips_a_key()
    {
        var store = NewStore();
        Assert.False(store.Exists());
        Assert.Null(store.ReadRaw());

        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        Assert.True(store.Exists());
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", store.ReadRaw());
    }

    [Fact]
    public void Creates_missing_parent_directories()
    {
        // First boot on a fresh volume: data/ may not exist yet.
        var store = new MasterKeyStore(Path.Combine(_dir, "nested", "deeper", "master.key"));
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Assert.True(store.Exists());
    }

    [Fact]
    public void Always_writes_a_trailing_newline()
    {
        // POSIX convention, and it's how an operator eyeballs the file: without it
        // `cat master.key` runs the key straight into the next line of output.
        var withId = NewStore("with-id.key");
        withId.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "v3");
        Assert.EndsWith("\n", File.ReadAllText(withId.Path));

        var bare = NewStore("bare.key");
        bare.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Assert.EndsWith("\n", File.ReadAllText(bare.Path));
        // Still parses back cleanly — the newline is cosmetic, not part of the key.
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", bare.ReadRaw());
    }

    [Fact]
    public void Round_trips_a_key_with_its_id()
    {
        // The id travels WITH the key: rotation mints both together, and if the id
        // lived in the environment a rotation would stamp lek_kek_id=v2 on every
        // row while the next boot kept calling itself v1.
        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "v2");

        var (key, id) = store.Read();

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", key);
        Assert.Equal("v2", id);
    }

    [Fact]
    public void A_bare_single_line_file_still_reads_with_no_id()
    {
        // What an operator writes by hand, and what a Docker/k8s secret projects.
        // Must stay valid — the id then falls back to the configured default.
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n");

        var (key, id) = store.Read();

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", key);
        Assert.Null(id);
    }

    [Fact]
    public void Reads_the_id_line_before_the_key_line()
    {
        // Order-insensitive so a hand-edited file isn't fragile.
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path,
            "# coffer master key\nid=2026-08\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n");

        var (key, id) = store.Read();

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", key);
        Assert.Equal("2026-08", id);
    }

    [Fact]
    public void RestoreFromArchive_puts_the_previous_key_back()
    {
        // The rollback for a rotation whose database re-wrap failed after the new
        // key was already written.
        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "v1");
        var archived = store.Archive("stamp")!;
        store.Write("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=", "v2");

        store.RestoreFromArchive(archived);

        var (key, id) = store.Read();
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", key);
        Assert.Equal("v1", id);
        Assert.False(File.Exists(archived));   // moved back, not copied
    }

    [Fact]
    public void Trims_surrounding_whitespace_on_write_and_read()
    {
        var store = NewStore();
        store.Write("  AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n");
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", store.ReadRaw());
    }

    [Fact]
    public void Overwrites_in_place_without_leaving_a_temp_file()
    {
        // A stray .tmp beside the key would be a second copy of the secret.
        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        store.Write("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=");

        Assert.Equal("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=", store.ReadRaw());
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Rejects_a_blank_key()
        => Assert.Throws<ArgumentException>(() => NewStore().Write("   "));

    [Fact]
    public void Archive_moves_the_existing_key_aside_and_returns_its_path()
    {
        // Adopting a source install's KEK on restore (D4) must stay reversible.
        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        var archived = store.Archive("20260806T120000Z");

        Assert.NotNull(archived);
        Assert.False(store.Exists());
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            File.ReadAllText(archived!).Trim());
    }

    [Fact]
    public void Archive_is_a_no_op_when_there_is_no_key_yet()
        => Assert.Null(NewStore().Archive("20260806T120000Z"));

    // The documented read-only injection case: the key arrives as an injected
    // secret (/run/secrets/…, a projected Kubernetes Secret) that the app cannot
    // write back to. Both the rotation coordinator and the bootstrap adopt path
    // guard for it by catching IOException or UnauthorizedAccessException
    // SPECIFICALLY — adoption especially, because an unhandled throw there happens
    // before the staged source key is cleared, so the next boot retries forever
    // and the install crash-loops into unavailability instead of refusing one
    // operation.
    //
    // Those guards are only as good as the exception type they catch, and nothing
    // pinned it. If Write ever surfaced something outside those two, the catch
    // clauses would stop matching and the careful recovery would silently become
    // the crash loop it was written to prevent.

    [Fact]
    public void Write_to_an_unusable_location_throws_a_catchable_IO_failure()
    {
        // Unwritable via a path that cannot be a directory, because a FILE already
        // occupies it. Deliberately not done with permission bits: this has to
        // hold for root too (CI images routinely run as root, and root ignores the
        // mode), and it has to hold on Windows. Directory.CreateDirectory is the
        // first thing Write does, so this exercises the real entry point.
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "i am a file");
        var store = new MasterKeyStore(Path.Combine(blocker, "master.key"));

        var ex = Record.Exception(() => store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));

        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"Write threw {ex.GetType().Name}, which the rotation and adopt guards do NOT catch. "
            + "Either widen those catch clauses or keep this contract.");
    }

    [Fact]
    public void Write_to_a_permission_denied_directory_throws_a_catchable_IO_failure()
    {
        // The permission-bit arm, which yields UnauthorizedAccessException rather
        // than IOException. It is unobservable on Windows (no Unix mode APIs) and
        // as root (root bypasses the mode, so the write SUCCEEDS and the assertion
        // fails on a correct implementation — found exactly that way, in a root
        // SDK container).
        //
        // Rather than enumerate the reasons, PROBE: chmod the directory, then try
        // to write into it directly. If that works, this environment cannot
        // observe a permission denial and there is nothing here to assert.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var lockedDir = Path.Combine(_dir, "readonly");
        Directory.CreateDirectory(lockedDir);
        var store = new MasterKeyStore(Path.Combine(lockedDir, "master.key"));
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            try
            {
                File.WriteAllText(Path.Combine(lockedDir, ".probe"), "x");
                return;   // privileged (or the mode didn't bite) — not observable here
            }
            catch (Exception probe) when (probe is IOException or UnauthorizedAccessException)
            {
                // Good: the denial is real, so the assertion below means something.
            }

            var ex = Record.Exception(() => store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));

            Assert.NotNull(ex);
            Assert.True(ex is IOException or UnauthorizedAccessException,
                $"Write threw {ex.GetType().Name}, which the guards do NOT catch.");
        }
        finally
        {
            // Restore write permission or the fixture can't clean itself up.
            File.SetUnixFileMode(lockedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Written_key_is_owner_read_write_only()
    {
        // Skipped on Windows, where the Unix mode APIs don't apply — the ACL on
        // the data directory is the installer's job there.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.Path));
    }

    [Fact]
    public void Archived_key_is_owner_read_write_only()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var store = NewStore();
        store.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        var archived = store.Archive("stamp");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(archived!));
    }
}
