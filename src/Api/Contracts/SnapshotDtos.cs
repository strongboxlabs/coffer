namespace Coffer.Api.Contracts;

/// <summary>Body for <c>POST /api/ledgers/{lid}/snapshots</c>.</summary>
public sealed record CreateSnapshotRequest(
    /// <summary>Optional free-form note attached to a manual snapshot
    /// ("before MD-import"). Max ~200 chars; trimmed; empty → null.</summary>
    string? Description);

/// <summary>One row in the snapshots-list response. Does NOT include
/// the compressed content blob — too large for list payloads.</summary>
public sealed record SnapshotSummaryDto(
    Guid Id,
    DateTime CreatedAt,
    Guid CreatedByUserId,
    /// <summary>"auto" or "manual" per ADR-0037 §Retention.</summary>
    string Kind,
    string? Description,
    string SchemaVersion,
    /// <summary>Uncompressed JSON size in bytes. The SPA renders this
    /// as "47 MB before compression" on the snapshots panel.</summary>
    int ContentSizeUncompressed,
    /// <summary>
    /// Whether a restore of this snapshot could succeed right now — that is,
    /// whether <see cref="SchemaVersion"/> still matches the live schema
    /// (ADR-0037 Phase 1 refuses a cross-version restore).
    ///
    /// <para>Computed SERVER-side on purpose. The refusal rule lives in
    /// LedgerSnapshotsRepository, and a SPA that re-derived it by comparing
    /// against /api/meta/version would be a second implementation of the same
    /// rule, free to drift from the one that actually decides. The panel used
    /// to offer Restore on every row and let the server refuse after the
    /// confirm — the user learned their pre-upgrade snapshot was worthless at
    /// the moment they needed it.</para>
    /// </summary>
    bool Restorable);

/// <summary>Response shape for the create endpoint. Mirrors the
/// summary DTO plus a sentinel for the SkippedDueToFullPool outcome
/// so the SPA can show "auto-snap skipped" affordances.</summary>
public sealed record CreateSnapshotResponse(
    /// <summary>Null when the create was skipped (manual at cap is a
    /// 422 not a 200, so this only fires for the all-manual-pool
    /// auto-snap-skip path — which doesn't reach this endpoint
    /// directly; reserved for the scheduler logging surface).</summary>
    SnapshotSummaryDto? Snapshot);
