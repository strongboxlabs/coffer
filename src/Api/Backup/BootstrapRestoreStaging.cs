namespace Coffer.Api.Backup;

/// <summary>
/// Filesystem staging for the bootstrap-UI restore (ADR-0061). The setup
/// endpoint stages an uploaded <c>.cofferbak</c> + its passphrase here with a
/// marker; the next API boot (before serving/migrating) applies it via
/// <c>BackupService.RestoreAsync(clean: true)</c> and then shreds the staging.
///
/// Lives under <c>data/restore-staging/</c> beside the binary — the same volume
/// as <c>bootstrap.url</c> (ADR-0059), so it survives the deliberate restart
/// between "operator clicks Restore" and the boot that applies it. The passphrase
/// sits here briefly (between upload and that boot) and is deleted right after;
/// the volume is on the Layer-1-encrypted host disk (operations.md), which is the
/// trust boundary the encrypted artifact already relies on.
/// </summary>
public static class BootstrapRestoreStaging
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "data", "restore-staging");
    private static string MarkerPath => Path.Combine(Dir, "restore.pending");
    private static string PassphrasePath => Path.Combine(Dir, "passphrase");

    /// <summary>Where the uploaded artifact is written. Public so the setup
    /// endpoint streams the upload straight to it before verifying.</summary>
    public static string ArchivePath => Path.Combine(Dir, "archive.cofferbak");

    /// <summary>True when a complete restore request is staged (all three files).</summary>
    public static bool IsPending() =>
        File.Exists(MarkerPath) && File.Exists(ArchivePath) && File.Exists(PassphrasePath);

    /// <summary>Ensure the staging dir exists (call before writing the upload).</summary>
    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    /// <summary>Finalize a staged request: record the passphrase + drop the
    /// marker. Call only after the archive is written AND its passphrase
    /// verified, so a half-written request never trips <see cref="IsPending"/>.</summary>
    public static async Task CommitAsync(string passphrase, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(PassphrasePath, passphrase, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(MarkerPath, "pending", ct).ConfigureAwait(false);
    }

    /// <summary>The staged passphrase (read at boot to apply the restore).</summary>
    public static string ReadPassphrase() => File.ReadAllText(PassphrasePath);

    /// <summary>Delete the staged artifact, passphrase, and marker.</summary>
    public static void Clear()
    {
        TryDelete(ArchivePath);
        TryDelete(PassphrasePath);
        TryDelete(MarkerPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort shred */ }
        catch (UnauthorizedAccessException) { }
    }
}
