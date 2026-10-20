namespace Coffer.Api.Backup;

/// <summary>
/// Filesystem staging for the bootstrap-UI restore (ADR-0061). The setup
/// endpoint stages an uploaded <c>.cofferbak</c> + its passphrase here with a
/// marker; the next API boot (before serving/migrating) applies it via
/// <c>BackupService.RestoreAsync</c> and then shreds the staging. What that boot
/// did is written to <see cref="RecordOutcome"/>, which outlives the shredding:
/// the browser that asked for the restore reads it back by attempt id, because a
/// server answering again says nothing about whether the restore happened.
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
    /// <summary>
    /// Where staging lives. <c>data/restore-staging/</c> beside the binary in
    /// production; overridable so concurrent test shards do not share it.
    /// </summary>
    /// <remarks>
    /// The override exists because this is a filesystem SINGLETON keyed to the
    /// binary's location, and the test suite runs several shards in parallel
    /// out of one bin directory. Two shards that both touch restore staging —
    /// the admin restore lives in one namespace, the bootstrap restore in
    /// another — then race on the same files: one clears staging while the
    /// other is mid-restore, and the failure surfaces as a 500 or a stray
    /// "nothing was staged" in whichever shard lost. That race was latent for
    /// as long as those two namespaces have been in different shards; it only
    /// started firing when the partition changed their timing.
    /// <para>Unset in production, which is every deployment: the default is
    /// exactly the path it always was.</para>
    /// </remarks>
    private static string Dir =>
        Environment.GetEnvironmentVariable("COFFER_RESTORE_STAGING_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(AppContext.BaseDirectory, "data", "restore-staging");
    private static string MarkerPath => Path.Combine(Dir, "restore.pending");
    private static string PassphrasePath => Path.Combine(Dir, "passphrase");
    private static string AttemptPath => Path.Combine(Dir, "attempt");
    private static string AdoptedKeyArchivePath => Path.Combine(Dir, "adopted-key-archive");
    private static string OutcomePath => Path.Combine(Dir, "outcome.json");

    /// <summary>Where the uploaded artifact is written. Public so the setup
    /// endpoint streams the upload straight to it before verifying.</summary>
    public static string ArchivePath => Path.Combine(Dir, "archive.cofferbak");

    /// <summary>
    /// Where a multi-part upload accumulates before it becomes the archive
    /// (ADR-0101). Separate from <see cref="ArchivePath"/> on purpose: a
    /// half-received upload must never be mistaken for a backup anyone asked to
    /// restore, and only <see cref="PromoteUploadAsync"/> — called after the
    /// caller states how many parts there were and the count checks out — turns
    /// one into the other.
    /// </summary>
    private static string UploadPath => Path.Combine(Dir, "upload.cofferbak");

    /// <summary>
    /// Why writing <paramref name="incomingBytes"/> more into staging would leave the volume
    /// with less than <paramref name="reserveBytes"/> free, or null when it fits (FU-072).
    /// </summary>
    /// <remarks>The staging directory shares a volume with the master key and the stored
    /// backups; filling it is the harm, not the upload's size as such. Measured on the
    /// directory itself, so it is the filesystem that actually holds staging.</remarks>
    public static string? WouldFillTheVolume(long incomingBytes, long reserveBytes)
    {
        EnsureDir();
        long free;
        try
        {
            free = new DriveInfo(Path.GetFullPath(Dir)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A volume whose free space cannot be read is not one to fill on trust.
            return "The free space on the staging volume could not be read, so the upload was not "
                   + $"accepted ({ex.GetType().Name}).";
        }
        if (free - incomingBytes >= reserveBytes) return null;
        return $"Not enough free space for this upload: {incomingBytes / (1024 * 1024)} MB would leave "
               + $"{Math.Max(0, free - incomingBytes) / (1024 * 1024)} MB on the data volume, and at least "
               + $"{reserveBytes / (1024 * 1024)} MB must stay free — it also holds the stored backups. "
               + "Free some space (old backups, Docker images) and try again.";
    }

    /// <summary>Bytes received so far, or null when no upload is in progress.</summary>
    public static long? UploadedBytes() =>
        File.Exists(UploadPath) ? new FileInfo(UploadPath).Length : null;

    /// <summary>
    /// Append one part to the in-progress upload. <paramref name="part"/> is
    /// 1-based and must be the next one expected, which is derived from what has
    /// already landed rather than tracked in memory — so an interrupted upload
    /// resumes after a restart, and two browser tabs cannot interleave into a
    /// corrupt archive that only fails at decrypt.
    /// </summary>
    public static async Task AppendUploadAsync(
        int part, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureDir();
        // Part 1 starts a fresh upload — the only way to abandon a stalled one
        // without an explicit reset call.
        if (part == 1) TryDelete(UploadPath);
        await using var dest = new FileStream(
            UploadPath, FileMode.Append, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(dest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Make the completed upload the archive to restore. Returns false when
    /// nothing was uploaded.
    /// </summary>
    public static bool PromoteUpload()
    {
        if (!File.Exists(UploadPath)) return false;
        TryDelete(ArchivePath);
        File.Move(UploadPath, ArchivePath);
        return true;
    }

    /// <summary>Discard an in-progress upload.</summary>
    public static void ClearUpload() => TryDelete(UploadPath);

    /// <summary>True when a complete restore request is staged (all three files).</summary>
    public static bool IsPending() =>
        File.Exists(MarkerPath) && File.Exists(ArchivePath) && File.Exists(PassphrasePath);

    /// <summary>Ensure the staging dir exists (call before writing the upload).</summary>
    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    /// <summary>Finalize a staged request: record the passphrase and a fresh
    /// attempt id, then drop the marker. Call only after the archive is written
    /// AND its passphrase verified, so a half-written request never trips
    /// <see cref="IsPending"/>. Returns the attempt id, which the caller hands to
    /// the browser to ask for the outcome after the restart.</summary>
    public static async Task<Guid> CommitAsync(string passphrase, CancellationToken ct = default)
    {
        var attemptId = Guid.NewGuid();
        // The passphrase opens every backup: owner-only and written whole (FU-079).
        await Coffer.Api.Crypto.SecretFile.WriteAllTextAsync(PassphrasePath, passphrase, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(AttemptPath, attemptId.ToString("D"), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(MarkerPath, "pending", ct).ConfigureAwait(false);
        return attemptId;
    }

    /// <summary>
    /// Where the key that a source-key adoption replaced was archived. Recorded by
    /// the adoption boot, read by the restore boot: if the restore then fails, the
    /// original key goes back, so "nothing was changed" is true of the key file too.
    /// </summary>
    public static void RecordAdoptedKeyArchive(string archivePath) =>
        File.WriteAllText(AdoptedKeyArchivePath, archivePath);

    /// <summary>The archived pre-adoption key, or null when no key was adopted.</summary>
    public static string? ReadAdoptedKeyArchive() =>
        File.Exists(AdoptedKeyArchivePath) ? File.ReadAllText(AdoptedKeyArchivePath).Trim() : null;

    /// <summary>The staged attempt's id, or null when none was recorded.</summary>
    public static Guid? ReadAttemptId() =>
        File.Exists(AttemptPath) && Guid.TryParse(File.ReadAllText(AttemptPath).Trim(), out var id)
            ? id
            : null;

    /// <summary>What the boot that applied a staged restore did.
    /// <see cref="Outcome"/> is "applied", "failed" (the live database was not
    /// changed), or "unknown" (the swap's result could not be read back).</summary>
    public sealed record RestoreOutcome(
        Guid AttemptId, string Outcome, DateTime FinishedAtUtc, string? PreviousDatabase, string? Error);

    /// <summary>Record the outcome of the last attempt, replacing any earlier
    /// one. Written to a temporary file and moved into place, so a reader never
    /// sees half a record.</summary>
    public static void RecordOutcome(RestoreOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        EnsureDir();
        var temp = OutcomePath + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(outcome));
        File.Move(temp, OutcomePath, overwrite: true);
    }

    /// <summary>The recorded outcome for <paramref name="attemptId"/>, or null
    /// when the last recorded attempt is a different one (or none is).</summary>
    public static RestoreOutcome? ReadOutcome(Guid attemptId)
    {
        if (!File.Exists(OutcomePath)) return null;
        try
        {
            var outcome = System.Text.Json.JsonSerializer.Deserialize<RestoreOutcome>(File.ReadAllText(OutcomePath));
            return outcome?.AttemptId == attemptId ? outcome : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
        // A master key: owner-only and written whole (FU-079), like master.key itself.
        await Coffer.Api.Crypto.SecretFile.WriteAllTextAsync(SourceKeyPath, base64Key.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>True when a source key is staged for adoption.</summary>
    public static bool HasSourceKey() => File.Exists(SourceKeyPath);

    /// <summary>
    /// True when the staged source key was written BEFORE the archive it is staged beside
    /// (FU-073), so it belongs to an earlier attempt rather than this one.
    /// </summary>
    /// <remarks>Both restore endpoints write the archive first and the key after it, in the
    /// same request, and clear any older key when a restore begins. A key older than its
    /// archive can therefore only be a leftover that survived that, and adopting it would
    /// replace the live master key on the strength of a request that never supplied one.</remarks>
    public static bool SourceKeyIsOlderThanArchive() =>
        HasSourceKey() && File.Exists(ArchivePath)
        && File.GetLastWriteTimeUtc(SourceKeyPath) < File.GetLastWriteTimeUtc(ArchivePath);

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
        TryDelete(UploadPath);
        TryDelete(PassphrasePath);
        TryDelete(SourceKeyPath);
        TryDelete(AttemptPath);
        TryDelete(AdoptedKeyArchivePath);
        TryDelete(MarkerPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort shred */ }
        catch (UnauthorizedAccessException) { }
    }
}
