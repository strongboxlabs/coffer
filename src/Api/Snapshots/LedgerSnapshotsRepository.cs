using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Snapshots;

/// <summary>
/// Repository for <c>ledger_snapshots</c> (ADR-0037, migration 111).
/// Owns the per-ledger snapshot lifecycle:
///
///   * <see cref="CreateAsync"/> — capture the in-scope graph into
///     <c>content_json</c> server-side (mig 179, OOM-proof), apply
///     the 5-cap eviction rule.
///   * <see cref="ListAsync"/> — return up to 5 summary rows ordered
///     newest-first.
///   * <see cref="RestoreAsync"/> — transactional in-place restore;
///     validates schema-version match before invoking the SQL
///     restore function.
///   * <see cref="DeleteAsync"/> — remove one snapshot.
/// </summary>
/// <remarks>
/// The eviction rule (auto-evicts-auto-first, manual-at-cap refuses,
/// all-manual pool causes the auto-snap to be skipped) lives here
/// rather than in a DB constraint — too non-trivial for a CHECK,
/// readable as plain LINQ.
/// </remarks>
public sealed class LedgerSnapshotsRepository
{
    /// <summary>Hard cap of total snapshots per ledger per ADR-0037
    /// §Retention.</summary>
    public const int SnapshotCap = 5;

    /// <summary>Command timeout for the heavy snapshot SQL — the server-side
    /// payload CAPTURE (create) and the delete+reinsert (restore). Both scale with
    /// ledger size and ran well past the 30s Npgsql default, which timed out prod
    /// restore. These are rare, admin-gated operations, so a generous cap is
    /// correct and stays as headroom even though mig 188 cut restore's derived-state
    /// work (realized_gains is now captured rather than re-derived, and balances
    /// rebuild in one set-based pass).</summary>
    private const int SnapshotOpTimeoutSeconds = 600;

    private readonly AppDbContext _db;
    private readonly ILogger<LedgerSnapshotsRepository> _logger;

    public LedgerSnapshotsRepository(
        AppDbContext db,
        ILogger<LedgerSnapshotsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Discriminator for an attempted snapshot creation.</summary>
    public enum CreateOutcome
    {
        Created,
        /// <summary>Auto-snap couldn't fit because all 5 slots hold
        /// manual snaps; logged + surfaced in the UI so the user can
        /// see why coverage has paused.</summary>
        SkippedDueToFullPool,
        /// <summary>Manual snap rejected because the ledger already
        /// has 5 snapshots. Caller maps this to 422
        /// <c>manual-snapshot-at-cap</c>.</summary>
        AtCap,
    }

    public readonly record struct CreateResult(
        CreateOutcome Outcome,
        LedgerSnapshotRow? Row);

    /// <summary>
    /// Create a snapshot of <paramref name="ledgerId"/>. Inserts the
    /// metadata row, then captures the in-scope graph into
    /// <c>content_json</c> entirely server-side (mig 179 — the payload
    /// never enters managed memory). Applies the eviction rule per
    /// ADR-0037 §Retention.
    /// </summary>
    public async Task<CreateResult> CreateAsync(
        Guid ledgerId,
        string kind,
        Guid createdByUserId,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("auto" or "manual"))
            throw new ArgumentException("kind must be 'auto' or 'manual'", nameof(kind));

        // Server-side payload capture can run tens of seconds on a large ledger —
        // lift the command timeout off the 30s default (see SnapshotOpTimeoutSeconds).
        _db.Database.SetCommandTimeout(SnapshotOpTimeoutSeconds);

        // Count existing + (if needed) find the oldest auto-snap so
        // the eviction decision is one round-trip even at the cap.
        var existing = await _db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Kind, s.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid? evictId = null;
        if (existing.Count >= SnapshotCap)
        {
            if (kind == "manual")
            {
                // Manual at cap: explicit user intent, no silent eviction.
                return new CreateResult(CreateOutcome.AtCap, null);
            }
            // Auto at cap: evict the oldest auto-snap if one exists.
            var oldestAuto = existing.FirstOrDefault(s => s.Kind == "auto");
            if (oldestAuto is null)
            {
                // Pool full of manual snaps. Log + skip; the SPA shows
                // a banner ("Auto-snap skipped: 5 manual snapshots in
                // pool") so the user can see coverage has paused.
                _logger.LogInformation(
                    "Auto-snap skipped for ledger {LedgerId}: pool full of {ManualCount} manual snapshots.",
                    ledgerId, existing.Count);
                return new CreateResult(CreateOutcome.SkippedDueToFullPool, null);
            }
            evictId = oldestAuto.Id;
        }

        // Read the current DB schema version. DbUp records each
        // applied script in __schema_migrations(scriptname); we use
        // the highest (by schemaversionsid) as the version stamp.
        var schemaVersion = await _db.SchemaMigrations.AsNoTracking()
            .OrderByDescending(m => m.SchemaVersionsId)
            .Select(m => m.ScriptName)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshotId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (evictId is { } eid)
        {
            // Remove the doomed auto-snap before inserting so the
            // unique-by-(ledger,created_at) doesn't bite (it doesn't
            // exist; ordering safety regardless).
            await _db.LedgerSnapshots
                .Where(s => s.Id == eid)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // Insert the metadata row first (empty content; content_json filled
        // server-side next). The in-scope graph — which can be hundreds of MB — is
        // captured directly into content_json by ledger_snapshot_write (mig 179),
        // so it NEVER enters managed memory. This is the fix for the OOM that
        // silently failed nightly auto-snapshots on large ledgers: the old path
        // read the payload, JsonSerializer-deserialised it into a multi-GB object
        // graph, re-serialised, and gzipped — all under the container mem_limit.
        var row = new LedgerSnapshotRow
        {
            Id = snapshotId,
            LedgerId = ledgerId,
            CreatedAt = createdAt,
            CreatedByUserId = createdByUserId,
            Kind = kind,
            Description = description,
            SchemaVersion = schemaVersion,
            Content = Array.Empty<byte>(),
            ContentSizeUncompressed = 0,
        };
        _db.LedgerSnapshots.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Server-side capture into content_json; returns the uncompressed byte size.
        var uncompressedSize = await _db.LedgerSnapshotWrite(snapshotId, ledgerId)
            .Select(r => r.ContentSizeUncompressed)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The tracked row's size is stale (server-side UPDATE); return a copy with
        // the captured size for the SPA's snapshots panel.
        return new CreateResult(CreateOutcome.Created, new LedgerSnapshotRow
        {
            Id = snapshotId,
            LedgerId = ledgerId,
            CreatedAt = createdAt,
            CreatedByUserId = createdByUserId,
            Kind = kind,
            Description = description,
            SchemaVersion = schemaVersion,
            Content = Array.Empty<byte>(),
            ContentSizeUncompressed = uncompressedSize,
        });
    }

    /// <summary>
    /// Returns up to <see cref="SnapshotCap"/> snapshots for the
    /// ledger, newest first. Does NOT include <c>Content</c> (too
    /// big for list responses); <see cref="RestoreAsync"/> reads the
    /// payload server-side and never returns it.
    /// </summary>
    public async Task<IReadOnlyList<LedgerSnapshotRow>> ListAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        return await _db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new LedgerSnapshotRow
            {
                Id = s.Id,
                LedgerId = s.LedgerId,
                CreatedAt = s.CreatedAt,
                CreatedByUserId = s.CreatedByUserId,
                Kind = s.Kind,
                Description = s.Description,
                SchemaVersion = s.SchemaVersion,
                Content = Array.Empty<byte>(),
                ContentSizeUncompressed = s.ContentSizeUncompressed,
            })
            .Take(SnapshotCap)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Discriminator for restore outcomes.</summary>
    public enum RestoreOutcome
    {
        Restored,
        NotFound,
        WrongLedger,
        SchemaVersionMismatch,
        PayloadCorrupt,
    }

    /// <summary>
    /// Restore <paramref name="snapshotId"/> in place on the ledger
    /// it belongs to. Schema-version mismatch refuses (Phase 1, per
    /// ADR-0037). The whole operation runs in one transaction.
    /// </summary>
    public async Task<RestoreOutcome> RestoreAsync(
        Guid ledgerId,
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        // A full-ledger restore (delete + reinsert + a set-based balance rebuild)
        // scales with ledger size and ran well past the 30s default that timed out
        // prod restore. Lift it (see SnapshotOpTimeoutSeconds) before any restore
        // work begins. Since mig 188 there is no FIFO recompute here — holdings,
        // lots AND realized_gains all come from the payload.
        _db.Database.SetCommandTimeout(SnapshotOpTimeoutSeconds);

        // Probe metadata only — never SELECT the (potentially hundreds-of-MB)
        // payload here.
        //
        // The discriminator is v1-vs-server-side, not v2-vs-v1. Since mig 193
        // there are two server-side formats — v2 (content_json) and v3 (rows in
        // ledger_snapshot_parts) — and v3 leaves content_json NULL, so the old
        // `ContentJson != null` probe would route every new snapshot into the v1
        // gzip branch and gunzip an empty byte array. Only v1 carries bytes in
        // `content` (v2 and v3 both write Array.Empty<byte>()), so testing that
        // is exact, translates to a cheap length() with no payload transfer, and
        // fails safe: a partially written v3 goes server-side and raises a clear
        // "neither v3 parts nor v2 content_json" rather than silently mis-reading
        // an empty gzip.
        var snap = await _db.LedgerSnapshots.AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => new
            {
                s.LedgerId,
                s.SchemaVersion,
                IsLegacyV1 = s.Content.Length > 0,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snap is null) return RestoreOutcome.NotFound;
        if (snap.LedgerId != ledgerId) return RestoreOutcome.WrongLedger;

        // Refuse cross-schema-version restore (Phase 1 per ADR-0037).
        var liveSchemaVersion = await _db.SchemaMigrations.AsNoTracking()
            .OrderByDescending(m => m.SchemaVersionsId)
            .Select(m => m.ScriptName)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(snap.SchemaVersion, liveSchemaVersion, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Snapshot {SnapshotId} restore refused: snapshot schema {SnapSchema} != live schema {LiveSchema}.",
                snapshotId, snap.SchemaVersion, liveSchemaVersion);
            return RestoreOutcome.SchemaVersionMismatch;
        }

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!snap.IsLegacyV1)
            {
                // Server-side restore (mig 179 v2 / mig 193 v3). The stored
                // procedure picks the format itself: v3 replays chunks one at a
                // time, v2 reads content_json. Either way nothing is materialised
                // in the API — OOM-proof at any size.
                await _db.LedgerSnapshotRestoreStored(snapshotId, ledgerId)
                    .Select(r => r.LedgerId)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // v1 legacy: gzip payload in `content`, round-tripped through
                // managed memory. Kept for the few pre-mig-179 snapshots; large
                // legacy payloads may OOM here (superseded by v2 — whole-DB backups
                // are the DR path for those). Fetch `content` only on this branch.
                var content = await _db.LedgerSnapshots.AsNoTracking()
                    .Where(s => s.Id == snapshotId)
                    .Select(s => s.Content)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                var envelopeJson = GzipDecompress(content);
                LedgerSnapshotPayload? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<LedgerSnapshotPayload>(envelopeJson);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Snapshot {SnapshotId} payload failed to deserialize.", snapshotId);
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return RestoreOutcome.PayloadCorrupt;
                }
                if (envelope is null || envelope.SnapshotFormat != LedgerSnapshotPayload.CurrentFormat)
                {
                    _logger.LogError(
                        "Snapshot {SnapshotId} envelope has unexpected format {Format}.",
                        snapshotId, envelope?.SnapshotFormat ?? "(null)");
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return RestoreOutcome.PayloadCorrupt;
                }

                // The SQL restore takes the table-name→rows dict, not the envelope.
                var tablesJson = JsonSerializer.Serialize(envelope.Tables);
                await _db.LedgerSnapshotRestore(ledgerId, tablesJson)
                    .Select(r => r.LedgerId)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RestoreOutcome.Restored;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Delete one snapshot. Idempotent — returns false when the
    /// snapshot isn't found (already deleted, never existed).
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid ledgerId,
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _db.LedgerSnapshots
            .Where(s => s.Id == snapshotId && s.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    // GzipDecompress is retained for the v1 legacy restore path (pre-mig-179
    // snapshots stored gzip in `content`). v2 snapshots capture + restore
    // server-side (content_json), so no compression happens in the API.
    private static string GzipDecompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
