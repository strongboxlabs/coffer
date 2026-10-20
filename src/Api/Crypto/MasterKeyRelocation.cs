namespace Coffer.Api.Crypto;

/// <summary>
/// Moves the master key, and the keys rotation archived beside it, from the old default
/// location to the configured one (FU-077).
/// </summary>
/// <remarks>
/// <para>The key lived at <c>data/master.key</c> on <c>coffer_data</c>, the volume that also
/// holds every <c>.cofferbak</c>: read access to that one volume (a host backup of it,
/// <c>docker cp</c>, an unencrypted disk) yielded the key and every backup, with only the
/// backup passphrase in the way. Rotation archived old keys beside it, so a rotation did not
/// contain an exposure either. The compose file now mounts a volume of its own,
/// <c>coffer_keys</c>, and points <c>Api:MasterKey:Path</c> at it; this moves what an
/// existing install has across on its first boot there.</para>
/// <para>Copy, verify, then delete: the key is the one file whose loss costs every sealed
/// secret, so it is never in a state where neither copy is whole. The same for each
/// archived key. Run before the key is resolved, so the boot then reads it from where it
/// now is.</para>
/// <para>Fails closed. Inside a container it moves the key only onto a volume of its own —
/// not the container's writable layer, which the next <c>docker compose up</c> discards,
/// and not back onto the data volume — and it refuses to choose between two different
/// keys. Either refusal stops the boot with what to do, rather than serving under a key
/// that might be the wrong one or minting a new one over sealed data.</para>
/// </remarks>
public static class MasterKeyRelocation
{
    /// <summary>What a run did, one line each, for the boot log.</summary>
    public sealed record Outcome(IReadOnlyList<string> Notes)
    {
        public bool Moved => Notes.Count > 0;
    }

    /// <summary>
    /// Move the key at <paramref name="legacyPath"/>, and the archives beside it, to
    /// <paramref name="configuredPath"/>'s directory.
    /// </summary>
    /// <param name="isOwnVolume">Whether the configured path is on storage that persists and
    /// is not the legacy one: <see cref="IsOwnVolume"/> outside tests.</param>
    /// <param name="restoreStagingRecord">Reads and rewrites the path a pending restore
    /// recorded for the key its adoption archived, so a moved archive stays the one the
    /// restore puts back if it fails. Null where there is no staging.</param>
    /// <exception cref="InvalidOperationException">The move would be unsafe; the message says
    /// what to do.</exception>
    public static Outcome Run(
        string configuredPath, string legacyPath, Func<string, string, bool> isOwnVolume,
        (Func<string?> Read, Action<string> Write)? restoreStagingRecord = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        ArgumentNullException.ThrowIfNull(isOwnVolume);

        var target = Path.GetFullPath(configuredPath);
        var legacy = Path.GetFullPath(legacyPath);
        if (string.Equals(target, legacy, PathComparison)) return new Outcome([]);

        var legacyDir = Path.GetDirectoryName(legacy)!;
        var targetDir = Path.GetDirectoryName(target)!;
        var legacyName = Path.GetFileName(legacy);
        var archives = Directory.Exists(legacyDir)
            ? Directory.EnumerateFiles(legacyDir, legacyName + ".*.bak").Order(StringComparer.Ordinal).ToList()
            : [];
        if (!File.Exists(legacy) && archives.Count == 0) return new Outcome([]);

        if (!isOwnVolume(target, legacy))
            throw new InvalidOperationException(
                $"The master key is configured at '{target}', but that is not on a volume of its own, "
                + $"so the key at '{legacy}' was not moved there: in a container the next "
                + "`docker compose up` would discard it. Mount a volume at "
                + $"'{targetDir}' (the coffer_keys volume in the current docker-compose.yml), or set "
                + $"COFFER_MASTER_KEY_PATH={legacy} to keep the key where it is.");

        var notes = new List<string>();
        if (File.Exists(legacy))
        {
            if (File.Exists(target))
            {
                if (!SameContents(legacy, target))
                    throw new InvalidOperationException(
                        $"Two different master keys: '{target}' (configured, fingerprint "
                        + $"{Fingerprint(target)}) and '{legacy}' (the old default location, fingerprint "
                        + $"{Fingerprint(legacy)}). Neither was touched. Keep the one this install's data "
                        + "was sealed with — the fingerprint System → Encryption last showed — and "
                        + "move the other away, then start again.");
                // A move that copied and then stopped before the delete.
                notes.Add($"Removed '{legacy}': the same key is already at '{target}'.");
            }
            else
            {
                CopyWhole(legacy, target);
                notes.Add($"Moved the master key from '{legacy}' to '{target}'.");
            }
            File.Delete(legacy);
        }

        foreach (var archive in archives)
        {
            var moved = Path.Combine(targetDir, Path.GetFileName(archive));
            if (File.Exists(moved))
            {
                if (!SameContents(archive, moved))
                {
                    notes.Add($"Left '{archive}' in place: a different file is already at '{moved}'.");
                    continue;
                }
            }
            else
            {
                CopyWhole(archive, moved);
            }
            File.Delete(archive);
            notes.Add($"Moved the archived key '{archive}' to '{moved}'.");
            if (restoreStagingRecord is { } record
                && string.Equals(record.Read()?.Trim(), archive, PathComparison))
                record.Write(moved);
        }
        return new Outcome(notes);
    }

    /// <summary>
    /// True when <paramref name="target"/> may hold the key: always outside a container,
    /// where the filesystem persists; inside one, only on a mount of its own that is neither
    /// the container's root nor the one <paramref name="legacy"/> is on. Fails closed when
    /// the mount table cannot be read.
    /// </summary>
    public static bool IsOwnVolume(string target, string legacy)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
                StringComparison.OrdinalIgnoreCase))
            return true;
        string[] mountInfo;
        try
        {
            mountInfo = File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return IsOwnVolume(target, legacy, MountPoints(mountInfo));
    }

    /// <summary>The decision of <see cref="IsOwnVolume(string, string)"/>, over a given
    /// set of mount points.</summary>
    internal static bool IsOwnVolume(string target, string legacy, IReadOnlyCollection<string> mountPoints)
    {
        // By the file's own path: a key mounted as a single file (a Docker secret) is a
        // mount point itself, while its directory is not.
        var targetMount = MountPointOf(target, mountPoints);
        var legacyMount = MountPointOf(legacy, mountPoints);
        return targetMount != "/" && targetMount != legacyMount;
    }

    /// <summary>The mount points in <c>/proc/self/mountinfo</c> (the fifth field, with the
    /// kernel's octal escapes for spaces and the like decoded).</summary>
    internal static IReadOnlyCollection<string> MountPoints(IEnumerable<string> mountInfoLines) =>
        mountInfoLines
            .Select(l => l.Split(' '))
            .Where(f => f.Length > 4)
            .Select(f => System.Text.RegularExpressions.Regex.Replace(
                f[4], @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The longest mount point <paramref name="path"/> is at or under.</summary>
    internal static string MountPointOf(string path, IReadOnlyCollection<string> mountPoints)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        if (p.Length == 0) p = "/";
        return mountPoints
            .Where(m => m == "/" || p == m || p.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault() ?? "/";
    }

    private static void CopyWhole(string from, string to)
    {
        // Owner-only from the first byte, flushed, and renamed into place (FU-079).
        SecretFile.WriteAllText(to, File.ReadAllText(from));
        if (!SameContents(from, to))
            throw new InvalidOperationException(
                $"Copying '{from}' to '{to}' did not produce the same file; '{from}' was left in place.");
    }

    private static bool SameContents(string a, string b) =>
        File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static string Fingerprint(string keyFile)
    {
        var key = new MasterKeyStore(keyFile).ReadRaw();
        try
        {
            // Formatted as System → Encryption shows it, so the two can be compared by eye.
            return key is null
                ? "(empty)"
                : Convert.ToHexString(Coffer.Api.Backup.KekFingerprint.Compute(Convert.FromBase64String(key)));
        }
        catch (FormatException)
        {
            return "(not a key)";
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
