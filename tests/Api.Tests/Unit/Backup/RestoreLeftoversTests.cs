using Coffer.Api.Backup;
using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// What a restore leaves behind, and how secrets are written (FU-075, FU-079, FU-073):
/// no row data in the kept outcome, no plaintext left in temp after a kill, secret files
/// owner-only and whole, and a stale staged key never mistaken for this restore's.
/// </summary>
public sealed class RestoreLeftoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coffer-leftovers-" + Guid.NewGuid().ToString("N")[..8]);

    public RestoreLeftoversTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // --- FU-075: no row data in the outcome ---------------------------------

    [Theory]
    [InlineData("DETAIL:  Failing row contains (5c1e..., Payee A, 4210.00).")]
    [InlineData("pg_restore: detail: Failing row contains (5c1e..., Payee A, 4210.00).")]
    [InlineData("psql: detail: Failing row contains (5c1e..., Payee A, 4210.00).")]
    public void A_failing_row_never_reaches_the_kept_outcome(string detailLine)
    {
        // The outcome is kept indefinitely and readable by whoever holds the attempt id;
        // DETAIL quotes the row that failed, in either tool's spelling.
        var summary = BackupService.SummarizeForOutcome(
            "pg_restore failed (exit 1): pg_restore: error: could not execute query: ERROR:  "
            + "new row for relation \"txn_headers\" violates check constraint \"ck_amount\"\n"
            + detailLine);

        Assert.Contains("violates check constraint", summary);
        Assert.DoesNotContain("Payee A", summary);
        Assert.DoesNotContain("4210", summary);
        Assert.DoesNotContain("Failing row", summary);
    }

    // --- FU-075: nothing left in temp after a kill --------------------------

    [Fact]
    public void A_boot_removes_the_work_directories_and_upload_buffers_a_killed_run_left()
    {
        var restoreDir = Directory.CreateDirectory(Path.Combine(_dir, "coffer-restore-abc")).FullName;
        File.WriteAllText(Path.Combine(restoreDir, "archive.dump"), "plaintext of every row");
        Directory.CreateDirectory(Path.Combine(_dir, "coffer-verify-def"));
        // The pg tools' password file (FU-083): the database password, in temp.
        var passDir = Directory.CreateDirectory(Path.Combine(_dir, "coffer-pgpass-ghi")).FullName;
        File.WriteAllText(Path.Combine(passDir, "pgpass"), "*:*:*:*:the database password");
        File.WriteAllText(Path.Combine(_dir, "ASPNETCORE_0f1e2d3c.tmp"), "half an upload");
        var unrelatedDir = Directory.CreateDirectory(Path.Combine(_dir, "someone-else")).FullName;
        var unrelatedFile = Path.Combine(_dir, "notes.tmp");
        File.WriteAllText(unrelatedFile, "not ours");

        var removed = BackupService.SweepLeftoversAtBoot(_dir, _dir);

        Assert.Equal(4, removed);
        Assert.False(Directory.Exists(restoreDir));
        Assert.False(Directory.Exists(Path.Combine(_dir, "coffer-verify-def")));
        Assert.False(Directory.Exists(passDir));
        Assert.False(File.Exists(Path.Combine(_dir, "ASPNETCORE_0f1e2d3c.tmp")));
        Assert.True(Directory.Exists(unrelatedDir));   // only what is ours, by name
        Assert.True(File.Exists(unrelatedFile));
    }

    // --- FU-079: secret files ------------------------------------------------

    [Fact]
    public void A_secret_file_is_written_whole_with_no_temporary_file_left()
    {
        var path = Path.Combine(_dir, "passphrase");

        SecretFile.WriteAllText(path, "correct horse battery staple");
        SecretFile.WriteAllText(path, "a second passphrase");   // overwrite

        Assert.Equal("a second passphrase", File.ReadAllText(path));
        Assert.Equal(new[] { "passphrase" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_secret_file_is_readable_by_its_owner_only()
    {
        // The modes do not exist on Windows; the file inherits the directory's ACL there,
        // as master.key does. Asserted where it means something: the Linux the server runs on.
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "source-master.key");

        await SecretFile.WriteAllTextAsync(path, "q83vEjRWeJq8zN7w...");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
