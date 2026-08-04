// DTOs for per-ledger snapshots (ADR-0037 / migration 111).
// Mirrors `src/Api/Contracts/SnapshotDtos.cs` — keep in sync.

/**
 * One row in the snapshots-list response. Does NOT include the
 * compressed content blob (too big for list payloads); the SPA never
 * needs it directly — content goes server-side to the restore
 * endpoint.
 */
export interface SnapshotSummary {
    id: string;
    createdAt: string;
    createdByUserId: string;
    /** "auto" (weekly system-fired) or "manual" (user-fired). */
    kind: 'auto' | 'manual';
    /** Optional free-form note attached to manual snaps. Always null
     *  on auto-snaps. */
    description: string | null;
    /** DB schema version at snapshot time (e.g. "111_ledger_snapshots.sql").
     *  Restore refuses on mismatch (Phase 1). */
    schemaVersion: string;
    /** Uncompressed JSON size in bytes. Rendered as "47 MB before
     *  compression" on the snapshots panel. */
    contentSizeUncompressed: number;
}

export interface CreateSnapshotRequest {
    /** Optional free-form note. Trimmed; empty → null. Max 200 chars
     *  enforced server-side. */
    description: string | null;
}

export interface CreateSnapshotResponse {
    snapshot: SnapshotSummary;
}
