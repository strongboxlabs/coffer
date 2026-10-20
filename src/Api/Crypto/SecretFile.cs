using System.Text;

namespace Coffer.Api.Crypto;

/// <summary>
/// The one way to write a file that holds a secret (FU-079): readable by its owner only,
/// from the first byte, and written whole or not at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>master.key</c> was already written this way (<see cref="MasterKeyStore.Write"/>);
/// the setup link in <c>bootstrap.url</c> and the restore staging's passphrase and source
/// key were written 0644 and in place. World-readable is the obvious half: anything else
/// in the container, a host backup, a sidecar sharing the volume can read the passphrase
/// that opens every backup. In place is the other: a crash mid-write leaves a truncated
/// secret, which reads as a wrong passphrase or an invalid key with nothing to say why.
/// </para>
/// <para>
/// The temporary file is created owner-only (on Linux, through the create mode, so there
/// is no instant at which the bytes are world-readable), flushed to the device, then
/// renamed over the target, which is atomic within a volume. On Windows, which has no Unix
/// mode, the file inherits the directory's ACL, as <c>master.key</c> does.
/// </para>
/// </remarks>
public static class SecretFile
{
    /// <summary>Write <paramref name="contents"/> (UTF-8, no BOM) to <paramref name="path"/>.</summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);
        var temp = Prepare(path);
        try
        {
            using (var fs = new FileStream(temp, CreateOptions()))
            {
                fs.Write(Encoding.UTF8.GetBytes(contents));
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <inheritdoc cref="WriteAllText"/>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);
        var temp = Prepare(path);
        try
        {
            await using (var fs = new FileStream(temp, CreateOptions()))
            {
                await fs.WriteAsync(Encoding.UTF8.GetBytes(contents), ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string Prepare(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Unique, so two writers of the same secret cannot interleave in one temp file.
        return $"{path}.{Guid.NewGuid():N}.tmp";
    }

    private static FileStreamOptions CreateOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
