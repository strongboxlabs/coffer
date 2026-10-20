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
    // A write in progress lives under this suffix and is renamed away only once it
    // is whole and verified. It never matches List()'s "*.cofferbak", so an
    // unfinished artifact cannot be listed, aged, mirrored or retained.
    private const string PartialSuffix = ".partial";
    // Filename: coffer-{UTC yyyyMMddTHHmmssfffZ}-{8 hex}. Millisecond resolution
    // so the just-created artifact sorts unambiguously newest (a second-grained
    // stamp let rapid creates tie, and the random suffix is not chronological);
    // the random suffix only guards the rare same-millisecond filename clash.
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>
    /// The size each off-host part is cut to, from
    /// <c>Api:Backup:PartSizeMb</c> (ADR-0101). Default 49 MB.
    /// </summary>
    public long PartSizeBytes { get; }

    private readonly string _directory;
    private readonly ILogger<BackupStore> _logger;
    private readonly TimeProvider _clock;

    public BackupStore(string directory, ILogger<BackupStore> logger, long partSizeBytes, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(partSizeBytes, 1);
        _directory = directory;
        _logger = logger;
        PartSizeBytes = partSizeBytes;
        _clock = clock;
    }

    /// <summary>
    /// Create a new artifact: let <paramref name="writeEncrypted"/> stream the
    /// encrypted bytes into <c>{name}.cofferbak.partial</c>, flush it to disk, run
    /// <paramref name="verify"/> over what was written, and only then rename it to
    /// its listed name; then apply retention. Nothing is listed, counted fresh,
    /// mirrored or retained until it has been written whole and verified, and a
    /// failure at any step removes the partial file.
    /// </summary>
    /// <param name="verify">Reads the written artifact back and throws if it could
    /// not be restored. Null skips verification (tests that stand in a fake writer).</param>
    public async Task<BackupFileInfo> CreateAsync(
        Func<Stream, CancellationToken, Task> writeEncrypted,
        RetentionPolicy retention,
        IReadOnlySet<string>? pinnedIds = null,
        CancellationToken ct = default,
        Func<Stream, CancellationToken, Task>? verify = null)
    {
        ArgumentNullException.ThrowIfNull(writeEncrypted);
        ArgumentNullException.ThrowIfNull(retention);
        Directory.CreateDirectory(_directory);

        var name = $"coffer-{_clock.GetUtcNow().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{RandSuffix()}";
        var path = Path.Combine(_directory, name + Extension);
        var partial = path + PartialSuffix;
        try
        {
            await using (var fs = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await writeEncrypted(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                // Flush(true) asks the OS to put the bytes on the device, so a power
                // loss after the rename cannot leave a listed name over a torn file.
                fs.Flush(flushToDisk: true);
            }

            if (verify is not null)
            {
                await using var back = new FileStream(
                    partial, FileMode.Open, FileAccess.Read, FileShare.Read);
                await verify(back, ct).ConfigureAwait(false);
            }

            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        // Describe BEFORE retention: the new artifact is the newest (ms stamp),
        // so retention never prunes it — but read its metadata first so even a
        // pathological same-ms tie can't leave us describing a deleted path.
        var info = Describe(path);
        // The backup is written, verified and listed: nothing past this point may make
        // the create report failure. A retention pass that throws (a file vanishing
        // under it, a permissions error on an old artifact) would announce a good
        // backup as failed and count toward the scheduler's auto-disable (FU-067); the
        // next create prunes again.
        try
        {
            ApplyRetention(retention, pinnedIds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Backup {Id} was stored, but pruning older backups failed; the next backup tries again.", info.Id);
        }
        _logger.LogInformation("Stored backup {Id} ({Size} bytes).", info.Id, info.SizeBytes);
        return info;
    }

    /// <summary>
    /// Remove every <c>.partial</c> file: a write that was killed mid-way (OOM, power
    /// loss, container stop). Called once at startup, before anything can write, so
    /// it can never delete the file a live create is still filling.
    /// </summary>
    public void DeletePartials()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension + PartialSuffix))
        {
            TryDelete(file);
            _logger.LogWarning("Removed an unfinished backup write {File}.", Path.GetFileName(file));
        }
    }

    /// <summary>All stored backups, newest first.</summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*" + Extension)
            // A file can vanish between being listed and being read (a delete, or a
            // retention pass in another request); that is not an error in the listing.
            .Select(TryDescribe)
            .OfType<BackupFileInfo>()
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

    /// <summary>
    /// How many files this artifact is pushed off-host as. One for anything at
    /// or under <see cref="PartSizeBytes"/>.
    /// </summary>
    /// <remarks>
    /// The artifact on disk is always ONE file — splitting happens only at the
    /// boundary that needs it. Locally there is no size limit to clear, and a
    /// single file is what an operator copying it out by hand wants; a download
    /// is a RESPONSE, which no proxy caps the way it caps a request body.
    /// </remarks>
    public int PartCountFor(long sizeBytes) =>
        sizeBytes <= PartSizeBytes
            ? 1
            : (int)((sizeBytes + PartSizeBytes - 1) / PartSizeBytes);

    /// <summary>The filename a given part is stored under off-host.</summary>
    /// <remarks>
    /// <c>{id}.cofferbak.002-of-003</c>. The count is IN the name so a folder of
    /// parts is self-describing: whoever is looking at the Drive folder — or has
    /// just downloaded the set onto a laptop on the day the install is gone —
    /// can see both the order and whether the set is complete, with no manifest
    /// file to lose. It is also what lets the restore form accept the parts as
    /// they were downloaded, in any order the file picker hands them over. A
    /// single-part artifact keeps the plain <c>{id}.cofferbak</c> name, so
    /// nothing about existing backups changes.
    /// </remarks>
    public static string PartName(string id, int part, int partCount) =>   // name only — no size involved
        partCount <= 1
            ? id + Extension
            : string.Create(CultureInfo.InvariantCulture,
                $"{id}{Extension}.{part:000}-of-{partCount:000}");

    /// <summary>
    /// Open one part of a stored artifact — a bounded window onto the single
    /// file — or null when the id is unknown or the part is out of range.
    /// Parts are 1-based; every part but the last is exactly
    /// <see cref="PartSizeBytes"/> long, which is what lets a receiver check a
    /// set is whole from the byte count alone.
    /// </summary>
    public Stream? OpenReadPart(string id, int part)
    {
        var path = ResolvePath(id);
        if (path is null || part < 1) return null;

        var size = new FileInfo(path).Length;
        var count = PartCountFor(size);
        if (part > count) return null;

        var offset = (long)(part - 1) * PartSizeBytes;
        var length = Math.Min(PartSizeBytes, size - offset);
        return new PartStream(path, offset, length);
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
        foreach (var id in SelectForDeletion(all, _clock.GetUtcNow().UtcDateTime, retention, pinnedIds))
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
    /// kept — and the newest backup, which is kept whatever the tiers say: a policy
    /// that selects nothing (0/0/0, or a clock behind the artifact) must not delete
    /// the backup that was just written. Pure + clock-injected so the bucketing is
    /// unit-tested without disk.
    /// </summary>
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<BackupFileInfo> backups, DateTime nowUtc, RetentionPolicy policy,
        IReadOnlySet<string>? pinnedIds = null)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(policy);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (pinnedIds is { Count: > 0 }) keep.UnionWith(pinnedIds);
        if (backups.Count > 0)
        {
            keep.Add(backups
                .OrderByDescending(b => b.CreatedAtUtc)
                .ThenByDescending(b => b.Id, StringComparer.Ordinal)
                .First().Id);
        }

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

    internal static BackupFileInfo? TryDescribe(string path)
    {
        try { return Describe(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>The creation time an artifact id encodes, or null for an id that is not
    /// one of ours. Lets the Drive mirror date a remote backup it can no longer see locally.</summary>
    public static DateTime? TryParseCreatedAt(string id) => ParseTimestamp(id);

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

    /// <summary>
    /// A read-only window onto a byte range of a file. Seeded at
    /// <c>offset</c> and length-capped, so a caller that streams it to a
    /// response or an upload sees exactly one part and cannot read past it.
    /// </summary>
    private sealed class PartStream : Stream
    {
        private readonly FileStream _file;
        private readonly long _length;
        private long _position;

        public PartStream(string path, long offset, long length)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _file.Seek(offset, SeekOrigin.Begin);
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // Known up front, so the response carries a Content-Length and the
        // browser can show real progress rather than an indeterminate spinner.
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var room = (int)Math.Min(buffer.Length, _length - _position);
            if (room <= 0) return 0;
            var read = _file.Read(buffer[..room]);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var room = (int)Math.Min(buffer.Length, _length - _position);
            if (room <= 0) return 0;
            var read = await _file.ReadAsync(buffer[..room], cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
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
