using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Coffer.Api.Backup;

/// <summary>
/// Server-side persistence for encrypted backup artifacts (ADR-0060): writes,
/// lists, streams, deletes, and retention-caps the <c>.cofferbak</c> files under
/// the backups directory. Pure filesystem — no DB, no crypto (the encrypted
/// bytes arrive via a writer delegate from <see cref="BackupManager"/>).
/// Retained like snapshots: newest <c>KeepCount</c> kept, older pruned.
/// </summary>
public sealed partial class BackupStore
{
    private const string Extension = ".cofferbak";
    // Filename: coffer-{UTC yyyyMMddTHHmmssfffZ}-{8 hex}. Millisecond resolution
    // so the just-created artifact sorts unambiguously newest (a second-grained
    // stamp let rapid creates tie, and the random suffix is not chronological);
    // the random suffix only guards the rare same-millisecond filename clash.
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly string _directory;
    private readonly ILogger<BackupStore> _logger;

    public BackupStore(string directory, ILogger<BackupStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Create a new artifact: open a fresh file and let <paramref name="writeEncrypted"/>
    /// stream the encrypted bytes into it, then apply retention. A failed write
    /// removes the partial file so a half-written artifact never lists as
    /// restorable.
    /// </summary>
    public async Task<BackupFileInfo> CreateAsync(
        Func<Stream, CancellationToken, Task> writeEncrypted,
        RetentionPolicy retention,
        IReadOnlySet<string>? pinnedIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writeEncrypted);
        ArgumentNullException.ThrowIfNull(retention);
        Directory.CreateDirectory(_directory);

        var name = $"coffer-{DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{RandSuffix()}";
        var path = Path.Combine(_directory, name + Extension);
        try
        {
            await using var fs = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await writeEncrypted(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        // Describe BEFORE retention: the new artifact is the newest (ms stamp),
        // so retention never prunes it — but read its metadata first so even a
        // pathological same-ms tie can't leave us describing a deleted path.
        var info = Describe(path);
        ApplyRetention(retention, pinnedIds);
        _logger.LogInformation("Stored backup {Id} ({Size} bytes).", info.Id, info.SizeBytes);
        return info;
    }

    /// <summary>All stored backups, newest first.</summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*" + Extension)
            .Select(Describe)
            // Total, deterministic order: timestamp is only second-resolution,
            // so the id (fixed-width timestamp + random suffix) breaks ties so
            // retention prunes the same artifact every run.
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Open a stored artifact for download, or null when the id is
    /// unknown / malformed. The caller owns disposing the stream.</summary>
    public Stream? OpenRead(string id)
    {
        var path = ResolvePath(id);
        return path is null ? null : new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>Delete one artifact. Idempotent — an unknown id returns false.</summary>
    public bool Delete(string id)
    {
        var path = ResolvePath(id);
        if (path is null) return false;
        TryDelete(path);
        _logger.LogInformation("Deleted backup {Id}.", id);
        return true;
    }

    /// <summary>True when the id names a real stored artifact.</summary>
    public bool Exists(string id) => ResolvePath(id) is not null;

    // -----------------------------------------------------------------

    private void ApplyRetention(RetentionPolicy retention, IReadOnlySet<string>? pinnedIds)
    {
        var all = List();
        foreach (var id in SelectForDeletion(all, DateTime.UtcNow, retention, pinnedIds))
        {
            TryDelete(Path.Combine(_directory, id + Extension));
            _logger.LogInformation("Pruned backup {Id} (tiered retention).", id);
        }
    }

    /// <summary>
    /// Tiered grandfather-father-son selection (ADR-0060): return the ids to
    /// delete. A backup is KEPT if it falls in any tier —
    /// <list type="bullet">
    ///   <item>within the last <c>DailyDays</c> days (keep all), or</item>
    ///   <item>the newest backup of its ISO week, for weeks in the last
    ///   <c>WeeklyWeeks</c> weeks, or</item>
    ///   <item>the newest backup of its calendar month, for months in the last
    ///   <c>MonthlyMonths</c> months.</item>
    /// </list>
    /// Everything else (older than the monthly window, or a non-representative
    /// duplicate within a week/month past the daily window) is deleted, EXCEPT
    /// <paramref name="pinnedIds"/> — "never delete" pins (ADR-0062) are always
    /// kept. Pure + clock-injected so the bucketing is unit-tested without disk.
    /// </summary>
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<BackupFileInfo> backups, DateTime nowUtc, RetentionPolicy policy,
        IReadOnlySet<string>? pinnedIds = null)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(policy);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (pinnedIds is { Count: > 0 }) keep.UnionWith(pinnedIds);

        // Daily tier: keep everything within the window.
        var dailyCutoff = nowUtc.AddDays(-policy.DailyDays);
        foreach (var b in backups.Where(b => b.CreatedAtUtc >= dailyCutoff))
            keep.Add(b.Id);

        // Weekly tier: newest per ISO (year, week), for weeks in the window.
        KeepNewestPerBucket(
            backups, nowUtc.AddDays(-7 * policy.WeeklyWeeks),
            b => (System.Globalization.ISOWeek.GetYear(b.CreatedAtUtc),
                  System.Globalization.ISOWeek.GetWeekOfYear(b.CreatedAtUtc)),
            keep);

        // Monthly tier: newest per (year, month), for months in the window.
        KeepNewestPerBucket(
            backups, nowUtc.AddMonths(-policy.MonthlyMonths),
            b => (b.CreatedAtUtc.Year, b.CreatedAtUtc.Month),
            keep);

        return backups.Where(b => !keep.Contains(b.Id)).Select(b => b.Id).ToList();
    }

    private static void KeepNewestPerBucket<TKey>(
        IReadOnlyList<BackupFileInfo> backups,
        DateTime cutoff,
        Func<BackupFileInfo, TKey> bucket,
        HashSet<string> keep) where TKey : notnull
    {
        var representatives = backups
            .Where(b => b.CreatedAtUtc >= cutoff)
            .GroupBy(bucket)
            // Newest in the bucket; id breaks the tie deterministically (the
            // CreatedAtUtc is only millisecond-resolution).
            .Select(g => g
                .OrderByDescending(b => b.CreatedAtUtc)
                .ThenByDescending(b => b.Id, StringComparer.Ordinal)
                .First());
        foreach (var b in representatives)
            keep.Add(b.Id);
    }

    /// <summary>
    /// Resolve an id to its on-disk path, or null when the id is malformed or
    /// the file is absent. The id is validated against the strict filename
    /// pattern AND the resolved path is confirmed inside the backups directory,
    /// so a crafted id can't traverse out (defense in depth).
    /// </summary>
    private string? ResolvePath(string id)
    {
        if (string.IsNullOrEmpty(id) || !IdPattern().IsMatch(id)) return null;
        var path = Path.Combine(_directory, id + Extension);
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    private static BackupFileInfo Describe(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var size = new FileInfo(path).Length;
        var createdAt = ParseTimestamp(id) ?? File.GetLastWriteTimeUtc(path);
        return new BackupFileInfo(id, size, createdAt);
    }

    private static DateTime? ParseTimestamp(string id)
    {
        // coffer-{timestamp}-{rand}
        var parts = id.Split('-');
        if (parts.Length != 3) return null;
        return DateTime.TryParseExact(
            parts[1], TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : null;
    }

    private static string RandSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete {Path}.", path); }
        catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Could not delete {Path}.", path); }
    }

    [GeneratedRegex(@"^coffer-\d{8}T\d{9}Z-[0-9a-f]{8}$")]
    private static partial Regex IdPattern();
}

/// <summary>Metadata for a stored backup artifact (the wire-facing shape lives
/// in BackupDtos as BackupSummary).</summary>
public sealed record BackupFileInfo(string Id, long SizeBytes, DateTime CreatedAtUtc);

/// <summary>
/// Tiered (grandfather-father-son) backup retention windows (ADR-0060):
/// keep all for <see cref="DailyDays"/> days, then the newest per ISO week for
/// <see cref="WeeklyWeeks"/> weeks, then the newest per month for
/// <see cref="MonthlyMonths"/> months.
/// </summary>
public sealed record RetentionPolicy(int DailyDays, int WeeklyWeeks, int MonthlyMonths);
