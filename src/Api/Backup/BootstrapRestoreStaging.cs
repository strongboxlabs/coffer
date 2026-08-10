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

    private static string SourceKeyPath => Path.Combine(Dir, "source-master.key");

    /// <summary>
    /// Stage the SOURCE install's master KEK for adoption (ADR-0092 D4) — the
    /// clean-migration path, where the operator has the key the backup's secrets
    /// were sealed under and wants to carry them over rather than re-establish them.
    /// </summary>
    /// <remarks>
    /// Kept beside the archive and the passphrase, on the same volume and under the
    /// same trust boundary (the class remarks above), and shredded by
    /// <see cref="Clear"/> along with everything else. Written separately from the
    /// pending marker so a staged key never on its own makes a restore look ready.
    /// </remarks>
    public static async Task StageSourceKeyAsync(string base64Key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        await File.WriteAllTextAsync(SourceKeyPath, base64Key.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>True when a source key is staged for adoption.</summary>
    public static bool HasSourceKey() => File.Exists(SourceKeyPath);

    /// <summary>The staged source key, or null when none was supplied.</summary>
    public static string? ReadSourceKey() =>
        HasSourceKey() ? File.ReadAllText(SourceKeyPath).Trim() : null;

    /// <summary>
    /// Shred just the staged source key, leaving the pending restore intact. Called
    /// the moment the key has been adopted into the live key file — the restore
    /// itself is applied on the NEXT boot, under the adopted key.
    /// </summary>
    public static void ClearSourceKey() => TryDelete(SourceKeyPath);

    /// <summary>Delete the staged artifact, passphrase, source key, and marker.</summary>
    public static void Clear()
    {
        TryDelete(ArchivePath);
        TryDelete(PassphrasePath);
        TryDelete(SourceKeyPath);
        TryDelete(MarkerPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort shred */ }
        catch (UnauthorizedAccessException) { }
    }
}
