using System.Runtime.InteropServices;

namespace Coffer.Api.Crypto;

/// <summary>
/// Filesystem home for the deployment-level master KEK (ADR-0092 D1). The key
/// is a single base64 line at <c>Api:MasterKey:Path</c>, defaulting to
/// <c>data/master.key</c> beside the binary — the same <c>coffer_data</c> volume
/// that already carries <c>bootstrap.url</c> (ADR-0059) and the restore staging
/// (ADR-0061), and a *different* volume from <c>postgres_data</c>, so one dump
/// never carries both the wrapped material and the key that opens it.
/// </summary>
/// <remarks>
/// <para><b>Why a file rather than an environment variable.</b> ADR-0014
/// §Layer 4 graded the env var "simplest / vulnerable to env dump or
/// <c>/proc</c> read" and framed graduating to a better source as a deployment
/// change. A value in the environment is visible in <c>docker inspect</c>,
/// <c>/proc/&lt;pid&gt;/environ</c>, every child process's environment, and most
/// crash dumps. A file at a *configurable* path is strictly better on all of
/// those AND is the shape every real secret-injection mechanism already
/// speaks — <c>/run/secrets/…</c> (Docker secrets), a projected Kubernetes
/// Secret, a Key Vault CSI mount. One setting replaces the env var and covers
/// the injection stories the env var covered badly.</para>
///
/// <para><b>Why not <c>IOptions</c> for the key itself.</b> Same reason
/// <see cref="MasterKey"/> is a singleton rather than a bound option: the key is
/// secret material, not configuration, and the config tree ends up in log dumps
/// and <c>appsettings.json</c> commits. Only the <i>path</i> is configuration.</para>
/// </remarks>
public sealed class MasterKeyStore
{
    /// <summary>Default location, relative to the binary — on the
    /// <c>coffer_data</c> volume in the Docker image.</summary>
    public const string DefaultRelativePath = "data/master.key";

    /// <summary>Absolute path this store reads and writes.</summary>
    public string Path { get; }

    /// <summary>
    /// Build a store for <paramref name="configuredPath"/>, falling back to
    /// <see cref="DefaultRelativePath"/> beside the binary when it is null or
    /// blank. Mirrors how <c>Api:Backup:Directory</c> resolves, so tests point
    /// this at a temp file the same way.
    /// </summary>
    public MasterKeyStore(string? configuredPath)
        => Path = string.IsNullOrWhiteSpace(configuredPath)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "data", "master.key")
            : configuredPath;

    /// <summary>True when a key file is present (contents unvalidated).</summary>
    public bool Exists() => File.Exists(Path);

    /// <summary>Prefix of the optional id line. See <see cref="Read"/>.</summary>
    private const string IdPrefix = "id=";

    /// <summary>
    /// The raw base64 contents, or null when no file is present. Whitespace is
    /// trimmed — an operator-placed file routinely carries a trailing newline,
    /// and a key that fails only because of one would be a miserable diagnosis.
    /// Validation belongs to <see cref="MasterKeyLoader"/>.
    /// </summary>
    public string? ReadRaw() => Read().Key;

    /// <summary>
    /// The key and, when the file carries one, its id.
    /// </summary>
    /// <remarks>
    /// File format: the first non-empty line is the base64 key; an optional
    /// <c>id=v2</c> line names the KEK id. A bare single-line file — which is what
    /// an operator writes by hand, and what a Docker/Kubernetes secret projects —
    /// stays valid and reports a null id, so the caller falls back to the
    /// configured default.
    ///
    /// The id belongs WITH the key rather than in the environment: rotation mints
    /// a new key and a new id together, and if the id lived elsewhere a rotation
    /// would stamp <c>ledgers.lek_kek_id = v2</c> on every row while the next boot
    /// went on calling itself v1. Lines are order-insensitive so a hand-edited file
    /// isn't fragile.
    /// </remarks>
    public (string? Key, string? Id) Read()
    {
        if (!Exists()) return (null, null);

        string? key = null, id = null;
        foreach (var raw in File.ReadAllLines(Path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[IdPrefix.Length..].Trim();
                if (value.Length > 0) id ??= value;
            }
            else
            {
                key ??= line;
            }
        }
        return (key, id);
    }

    /// <summary>
    /// Persist the key — and optionally its id — atomically, owner-read/write only.
    /// </summary>
    /// <remarks>
    /// Write-temp-then-replace, so a crash mid-write can never leave a truncated
    /// key file — which would read as "no valid key" over live wrapped material
    /// and strand the install (ADR-0092 D3). Permissions are applied to the temp
    /// file <i>before</i> the key lands in it, so the bytes are never briefly
    /// world-readable.
    /// </remarks>
    public void Write(string base64Key, string? kekId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Always newline-terminated — the POSIX convention, and without it `cat` runs
        // the key straight into whatever follows, which is exactly how an operator
        // eyeballs this file.
        var contents = string.IsNullOrWhiteSpace(kekId)
            ? $"{base64Key.Trim()}\n"
            : $"{base64Key.Trim()}\n{IdPrefix}{kekId.Trim()}\n";

        var temp = Path + ".tmp";
        // Create empty + lock down, THEN write the secret into it.
        using (File.Create(temp)) { }
        RestrictToOwner(temp);
        File.WriteAllText(temp, contents);
        // Replace is atomic where the platform supports it; Move with overwrite
        // is the portable equivalent and is atomic within a volume on both
        // Linux and Windows.
        File.Move(temp, Path, overwrite: true);
        RestrictToOwner(Path);
    }

    /// <summary>
    /// Put an archived key file back as the live one — the rollback for a rotation
    /// whose database re-wrap failed after the new key was already written
    /// (ADR-0092 D4).
    /// </summary>
    public void RestoreFromArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        File.Move(archivePath, Path, overwrite: true);
        RestrictToOwner(Path);
    }

    /// <summary>
    /// Move an existing key file aside instead of clobbering it, returning the
    /// archive path (null when there was nothing to archive). Used when a restore
    /// adopts a source install's KEK (ADR-0092 D4) so a mistaken restore stays
    /// reversible.
    /// </summary>
    /// <param name="stamp">Caller-supplied suffix — a timestamp or run id. Taken
    /// as a parameter rather than read from the clock so the caller owns
    /// determinism in tests.</param>
    public string? Archive(string stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        if (!Exists()) return null;

        var archived = $"{Path}.{stamp}.bak";
        File.Move(Path, archived, overwrite: true);
        RestrictToOwner(archived);
        return archived;
    }

    /// <summary>
    /// Owner-only permissions (<c>0600</c>). No-op on Windows, where the Unix
    /// mode APIs throw: NTFS inherits ACLs from the parent directory, and the
    /// Windows story is the installer's ACL on the data directory, not a
    /// per-file mode. Documented rather than silently skipped.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
