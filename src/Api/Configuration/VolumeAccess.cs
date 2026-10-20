namespace Coffer.Api.Configuration;

/// <summary>
/// At boot, before the master key is read: what the API keeps on its volumes is its own
/// (FU-084).
/// </summary>
/// <remarks>
/// From 0.122.0 the image runs as an unprivileged user. A volume an earlier, root-run version
/// wrote is root's until it is handed over, which install.sh does on upgrade. A host upgraded
/// some other way, such as a new image tag in .env under the old compose file, would
/// otherwise stop on "Access to the path '/app/keys/master.key' is denied", which names the
/// symptom and not the fix.
///
/// The refusal carries the hand-over itself, as one command for the host. It names no
/// script: <c>scripts/own-api-volumes.sh</c> reaches a host only with a 0.122.0 or later
/// install.sh, so the hosts that see this refusal are exactly the ones without it. It aims
/// at this container by the hostname Docker gives it, the container's short id.
///
/// Only a refusal counts. A file another process holds open, or a directory removed
/// while it is walked, says nothing about ownership, and test hosts share a data
/// directory across processes.
/// </remarks>
public static class VolumeAccess
{
    /// <summary>
    /// Throw, naming the fix, if a directory under <paramref name="dataRoot"/> refuses a new
    /// file or a file in it refuses to be read, or if the master key's directory refuses a
    /// new file (rotation writes beside the key) or the key or one of its archives
    /// (<c>&lt;key&gt;.&lt;stamp&gt;.bak</c>) refuses to be read. Only the key's own files
    /// are looked at there: it may share a directory with anything, the system temp
    /// directory included. What does not exist yet is skipped; the API creates it.
    /// </summary>
    public static void EnsureOwned(string dataRoot, string masterKeyPath) =>
        EnsureOwned(dataRoot, masterKeyPath, Environment.MachineName);

    internal static void EnsureOwned(string dataRoot, string masterKeyPath, string hostName)
    {
        var refused = new List<string>();
        if (Directory.Exists(dataRoot)) refused.AddRange(UnderTree(dataRoot));

        var keyPath = Path.GetFullPath(masterKeyPath);
        var keyDirectory = Path.GetDirectoryName(keyPath)!;
        if (Directory.Exists(keyDirectory))
        {
            if (RefusesNewFile(keyDirectory)) refused.Add(keyDirectory);
            refused.AddRange(Listed(() => Directory.EnumerateFiles(keyDirectory, Path.GetFileName(keyPath) + "*"))
                .Where(RefusesRead));
        }

        refused = refused.Distinct(StringComparer.Ordinal).ToList();
        if (refused.Count == 0) return;

        var container = ContainerOf(hostName);
        string[] directories = keyDirectory.StartsWith(
                Path.TrimEndingDirectorySeparator(dataRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? [dataRoot]
            : [dataRoot, keyDirectory];
        throw new InvalidOperationException(
            $"The API is refused access to {string.Join(", ", refused.Take(3))}"
            + (refused.Count > 3 ? $" and {refused.Count - 3} more" : "")
            + $". From 0.122 it runs as an unprivileged user (uid {AppUid}), and an earlier version "
            + "wrote its volumes as root. Hand them over once by running this on the host:"
            + Environment.NewLine + Environment.NewLine + "    "
            + HandOverCommand(container.Name, directories)
            + Environment.NewLine + Environment.NewLine
            + (container.Exact ? "" : "Replace coffer-api with the API container's name if COFFER_CONTAINER_PREFIX is set. ")
            + "Upgrading with a freshly fetched install.sh does the same.");
    }

    /// <summary>The user the image's USER line names.</summary>
    internal const int AppUid = 1654;

    /// <summary>
    /// The hand-over as one command for the host. A one-off container takes
    /// <paramref name="container"/>'s volumes and its image, and gives
    /// <paramref name="directories"/> to <see cref="AppUid"/>, as root with only the
    /// capabilities chown needs and no network. Then the API restarts. upgrading.md prints
    /// it for the default container name; <c>VolumeAccessTests</c> holds the two equal.
    /// </summary>
    internal static string HandOverCommand(string container, IEnumerable<string> directories) =>
        "sudo docker run --rm --user 0 --cap-drop ALL --cap-add CHOWN --cap-add DAC_READ_SEARCH --network none "
        + $"--volumes-from {container} --entrypoint chown "
        + "\"$(sudo docker inspect " + container + " --format '{{.Image}}')\" "
        + $"-R {AppUid}:{AppUid} {string.Join(' ', directories)} && sudo docker restart {container}";

    /// <summary>
    /// This container, by the hostname Docker gave it (its id's first twelve hex digits),
    /// or the compose file's default name when the hostname is not one.
    /// </summary>
    internal static (string Name, bool Exact) ContainerOf(string hostName) =>
        hostName.Length == 12 && hostName.All(char.IsAsciiHexDigitLower)
            ? (hostName, true)
            : ("coffer-api", false);

    private static IEnumerable<string> UnderTree(string root)
    {
        var directories = new Stack<string>([root]);
        while (directories.TryPop(out var directory))
        {
            if (RefusesNewFile(directory))
            {
                yield return directory;
                continue;   // its contents are the same story
            }
            foreach (var file in Listed(() => Directory.EnumerateFiles(directory)))
                if (RefusesRead(file)) yield return file;
            foreach (var child in Listed(() => Directory.EnumerateDirectories(directory)))
                directories.Push(child);
        }
    }

    private static bool RefusesNewFile(string directory) => Refuses(() =>
    {
        var probe = Path.Combine(directory, $".coffer-write-probe-{Guid.NewGuid():N}");
        using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
    });

    private static bool RefusesRead(string file) => Refuses(() =>
    {
        using (File.OpenRead(file)) { }
    });

    private static bool Refuses(Action attempt)
    {
        try
        {
            attempt();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<string> Listed(Func<IEnumerable<string>> list)
    {
        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed while it was walked, or not listable: the directory's own probe
            // is what decides whether the API can use it.
            return [];
        }
    }
}
