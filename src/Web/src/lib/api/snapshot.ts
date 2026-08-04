// Per-ledger snapshot endpoints (ADR-0037 slice 2). The backend
// (slice 1) ships the create / list / restore / delete surface;
// these are the typed SPA helpers that drive the Settings →
// Snapshots panel.

import type {
    CreateSnapshotRequest,
    CreateSnapshotResponse,
    SnapshotSummary,
} from '../types/snapshot';
import { request } from './_request';

/** `GET /api/ledgers/{id}/snapshots`. Returns up to 5 snapshots,
 *  newest first. No content blob. */
export function fetchSnapshots(ledgerId: string): Promise<SnapshotSummary[]> {
    return request<SnapshotSummary[]>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/snapshots`);
}

/** `POST /api/ledgers/{id}/snapshots`. Create a manual snapshot.
 *  422 `snapshot-manual-at-cap` when the ledger already has 5
 *  snapshots. */
export function createSnapshot(
    ledgerId: string,
    body: CreateSnapshotRequest,
): Promise<CreateSnapshotResponse> {
    return request<CreateSnapshotResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/snapshots`,
        { method: 'POST', body });
}

/** `POST /api/ledgers/{id}/snapshots/{sid}/restore`. In-place
 *  destructive restore. 422 codes the caller may surface:
 *  `snapshot-not-found`, `snapshot-schema-version-mismatch`,
 *  `snapshot-payload-corrupt`. */
export function restoreSnapshot(
    ledgerId: string,
    snapshotId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/snapshots/${encodeURIComponent(snapshotId)}/restore`,
        { method: 'POST' });
}

/** `DELETE /api/ledgers/{id}/snapshots/{sid}`. Idempotent — second
 *  delete on a now-missing id still returns 204. */
export function deleteSnapshot(
    ledgerId: string,
    snapshotId: string,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/snapshots/${encodeURIComponent(snapshotId)}`,
        { method: 'DELETE' });
}
