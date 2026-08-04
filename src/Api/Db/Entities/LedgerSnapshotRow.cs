namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_snapshots</c> (ADR-0037, migration 111).
/// Capped per-ledger snapshots of the user-curated graph; auto-snaps
/// fire weekly via <c>SnapshotScheduler</c>, manual snaps come from
/// the API. Restored in place via <c>LedgerSnapshotRestorer</c>.
/// </summary>
public sealed class LedgerSnapshotRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>The user who triggered this snapshot. Auto-snaps use
    /// <see cref="UserRow.SystemUserId"/>; manual snaps the acting user.</summary>
    public Guid CreatedByUserId { get; init; }

    /// <summary>"auto" (weekly system-fired) or "manual" (user-fired).
    /// Drives the eviction rule per ADR-0037 §Retention: auto-evicts-
    /// auto-first, manuals survive until explicit delete.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Optional free-form note attached to manual snaps
    /// ("before MD-import"). Always null for auto-snaps.</summary>
    public string? Description { get; init; }

    /// <summary>DB schema version (from __schema_migrations) at the
    /// moment of snapshot. Restore refuses on mismatch with the
    /// live DB — forward-migration of older payloads is deferred
    /// (per ADR-0037 §"Schema-version compatibility, Phase 1").</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>Legacy (v1) gzip-compressed JSON of the in-scope ledger graph
    /// (per ADR-0037 §Scope). Populated only for pre-mig-179 snapshots; empty for
    /// v2 (server-side) snapshots, which carry the graph in <see cref="ContentJson"/>.</summary>
    public byte[] Content { get; init; } = Array.Empty<byte>();

    /// <summary>v2 (mig 179): the in-scope ledger graph captured + stored entirely
    /// server-side as jsonb (Postgres TOAST-compresses it). NON-NULL marks a v2
    /// snapshot (created + restored without materialising the payload in the API —
    /// OOM-proof). NULL for legacy v1 snapshots (see <see cref="Content"/>). The
    /// full value is never SELECTed into the app; callers project
    /// <c>ContentJson != null</c> only, and restore reads it server-side.</summary>
    public string? ContentJson { get; init; }

    /// <summary>Uncompressed payload size in bytes. Persisted at
    /// create time so the SPA's snapshots panel can display "47 MB
    /// before compression" without decompressing every row.</summary>
    public int ContentSizeUncompressed { get; init; }
}
