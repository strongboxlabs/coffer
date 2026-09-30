// Admin whole-DB backup endpoints (ADR-0060), all behind RequireAdmin.
// Authenticated-admin restore (ADR-0071 D3) is here too; the bootstrap (pre-auth)
// restore covers a fresh install. ADR-0094 removed the `coffer-api restore` CLI, so
// those two are the whole story.

import { startAuthentication } from '@simplewebauthn/browser';

import type { BackupKekCheck, BackupRetention, BackupSchedule, BackupSummary } from '../types/backup';
import { request, requestBlob, requestMultipart } from './_request';

/** The exact phrase an admin must type to confirm a destructive restore. */
export const RESTORE_CONFIRM_PHRASE = 'yes i agree';

/**
 * React Query key for the stored-backup list.
 *
 * Exported so every screen that shows the list, and every mutation that
 * changes it, name the SAME cache entry. They did not: the backups panel
 * invalidated `admin-backups` while the restore card read `backups`, so
 * creating or deleting a backup left the restore picker showing a list that
 * was no longer true — offering an artifact that had just been deleted, or
 * hiding the picker entirely after the first backup was made.
 */
export const BACKUPS_QUERY_KEY = ['admin-backups'] as const;

/** `GET /api/admin/backups` — stored artifacts, newest first. */
export function fetchBackups(): Promise<BackupSummary[]> {
    return request<BackupSummary[]>('/api/admin/backups');
}

/** `POST /api/admin/backups` — create one now using the stored passphrase.
 *  422 `backup-passphrase-not-set` until a passphrase is configured. */
export function createBackup(): Promise<BackupSummary> {
    return request<BackupSummary>('/api/admin/backups', { method: 'POST' });
}

/** `DELETE /api/admin/backups/{id}` — idempotent. */
export function deleteBackup(id: string): Promise<void> {
    return request<void>(`/api/admin/backups/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

/**
 * Fallback part size (49 MB) for the moment before the server's configured
 * value has loaded, and if that read fails.
 *
 * It is a FALLBACK, not the value: `Api:Backup:PartSizeMb` is the authority
 * (ADR-0101) and this must never quietly win, or an operator who lowered the
 * setting for a stricter proxy would watch the browser keep sending pieces too
 * big for it. Same number as the server's default, so the two agree until the
 * real one arrives.
 */
export const BACKUP_PART_SIZE_FALLBACK = 49 * 1024 * 1024;

/** `GET /api/admin/backups/restore/limits` — the configured part size. */
export function fetchRestoreLimits(): Promise<{ partSizeBytes: number }> {
    return request<{ partSizeBytes: number }>('/api/admin/backups/restore/limits');
}

/** How many parts an artifact of this size is sent in, at a given part size. */
export function backupPartCount(sizeBytes: number, partSizeBytes: number): number {
    return sizeBytes <= partSizeBytes ? 1 : Math.ceil(sizeBytes / partSizeBytes);
}

/** `GET /api/admin/backups/{id}` — the encrypted .cofferbak bytes. Whole: a
 *  download is a RESPONSE, and no proxy caps those the way it caps a request
 *  body, so there is nothing here to work around. */
export function downloadBackup(id: string): Promise<Blob> {
    return requestBlob(`/api/admin/backups/${encodeURIComponent(id)}`);
}

/** `POST /api/admin/backups/{id}/pin` — "never delete" pin (excluded from local
 *  + Drive retention). 404 for an unknown id. */
export function pinBackup(id: string): Promise<void> {
    return request<void>(`/api/admin/backups/${encodeURIComponent(id)}/pin`, { method: 'POST' });
}

/** `DELETE /api/admin/backups/{id}/pin` — remove the pin. Idempotent. */
export function unpinBackup(id: string): Promise<void> {
    return request<void>(`/api/admin/backups/${encodeURIComponent(id)}/pin`, { method: 'DELETE' });
}

/** `PUT /api/admin/backups/passphrase` — set / rotate the backup passphrase
 *  (sealed under the master KEK server-side). 422 `backup-passphrase-invalid`
 *  when shorter than the server minimum. */
export function setBackupPassphrase(passphrase: string): Promise<void> {
    return request<void>('/api/admin/backups/passphrase', {
        method: 'PUT',
        body: { passphrase },
    });
}

/**
 * Reveal the stored backup passphrase (ADR-0092 D7), behind a fresh passkey
 * assertion — the same step-up the master-KEK reveal uses.
 *
 * This exists because the passphrase was always recoverable in principle (the
 * server unseals it on every scheduled backup) while the product offered no way,
 * so an operator who forgot it accumulated backups that all still succeeded and
 * were all unrestorable.
 *
 * Throws an `ApiError` (422 when no passphrase is set or its ciphertext won't open
 * under the current KEK; 401 on a failed ceremony) or a DOMException from the
 * browser prompt. Never cache the result.
 */
export async function revealBackupPassphrase(): Promise<string> {
    const begin = await request<{
        challengeId: string;
        assertionOptions: Parameters<typeof startAuthentication>[0]['optionsJSON'];
    }>('/api/admin/backups/passphrase/reveal/begin', { method: 'POST' });

    const assertionResponse = await startAuthentication({
        optionsJSON: begin.assertionOptions,
    });

    const result = await request<{ passphrase: string }>(
        '/api/admin/backups/passphrase/reveal',
        { method: 'POST', body: { challengeId: begin.challengeId, assertionResponse } },
    );
    return result.passphrase;
}

/**
 * `POST /api/admin/backups/restore` (ADR-0071 D3) — restore the whole database
 * from an uploaded `.cofferbak`. Destructive: on success (202) the server
 * restarts and everyone is signed out. `confirm` must equal
 * {@link RESTORE_CONFIRM_PHRASE}; set `acknowledgeKekMismatch` to proceed past a
 * cross-install KEK warning (422 `backup-kek-mismatch`).
 */
export function restoreBackup(
    /** The artifact to restore: an uploaded file, the id of one this install
     *  already holds, or `{ uploadedParts: true }` to use the parts already sent
     *  via {@link uploadRestorePart}. A stored backup is named rather than sent —
     *  it is already on the server's disk, and pushing 141 MB back through a CDN
     *  to reach it is both pointless and, past Cloudflare's 100 MB body cap,
     *  impossible. */
    file: File | { backupId: string } | { uploadedParts: true },
    passphrase: string,
    confirm: string,
    acknowledgeKekMismatch = false,
    sourceMasterKeyBase64?: string,
): Promise<void> {
    const form = new FormData();
    if (file instanceof File) form.append('archive', file, file.name || 'backup.cofferbak');
    else if ('backupId' in file) form.append('backupId', file.backupId);
    else form.append('useUploadedParts', 'true');
    form.append('passphrase', passphrase);
    form.append('confirm', confirm);
    if (acknowledgeKekMismatch) form.append('acknowledgeKekMismatch', 'true');
    // Adopt path (ADR-0092 D4): the source install's master key, so its sealed
    // secrets carry over instead of being cleared. The server validates it against
    // the archive's fingerprint before anything destructive happens, so a wrong
    // paste is refused rather than discovered afterwards.
    const sourceKey = sourceMasterKeyBase64?.trim();
    if (sourceKey) form.append('sourceMasterKeyBase64', sourceKey);
    return requestMultipart<void>('/api/admin/backups/restore', form);
}

/**
 * `POST /api/admin/backups/restore/parts` — send one piece of a restore
 * artifact too large for a single request (ADR-0101).
 *
 * Parts must arrive in order; the server derives which one it expects from what
 * has landed, so it refuses a gap rather than assembling a corrupt archive that
 * would surface as an indistinguishable "wrong passphrase" at decrypt time.
 * Nothing here is destructive — the restore is the separate, confirmed call.
 */
export function uploadRestorePart(
    part: number,
    partCount: number,
    partSizeBytes: number,
    chunk: Blob,
    /** The pre-auth bootstrap path posts the same parts to its own route. */
    url = '/api/admin/backups/restore/parts',
): Promise<{ part: number; partCount: number; receivedBytes: number; complete: boolean }> {
    const form = new FormData();
    form.append('archive', chunk, 'part.cofferbak');
    form.append('part', String(part));
    form.append('partCount', String(partCount));
    // Declared, not assumed: the server checks each part begins at
    // (part - 1) * partSizeBytes, which makes a wrong value here fail on the
    // very next part instead of assembling a corrupt archive.
    form.append('partSizeBytes', String(partSizeBytes));
    return requestMultipart(url, form);
}

/**
 * Send a whole file as parts, reporting progress. Returns once every part has
 * been acknowledged; the caller then restores with `{ uploadedParts: true }`.
 */
export async function uploadRestoreInParts(
    file: File,
    partSizeBytes: number,
    onProgress?: (sentBytes: number, totalBytes: number) => void,
    url?: string,
): Promise<void> {
    const partCount = backupPartCount(file.size, partSizeBytes);
    for (let part = 1; part <= partCount; part++) {
        const start = (part - 1) * partSizeBytes;
        const end = Math.min(start + partSizeBytes, file.size);
        await uploadRestorePart(part, partCount, partSizeBytes, file.slice(start, end), url);
        onProgress?.(end, file.size);
    }
}

/** The `.002-of-003` tail a part file downloaded from off-host storage carries. */
const PART_SUFFIX = /\.cofferbak\.(\d{3})-of-(\d{3})$/;

/**
 * Read the part number and expected count out of a filename, or null when the
 * name is not a part — the test for "did the operator pick a set of parts or a
 * whole backup?".
 */
export function parseBackupPartName(name: string): { part: number; partCount: number } | null {
    const m = PART_SUFFIX.exec(name);
    return m ? { part: Number(m[1]), partCount: Number(m[2]) } : null;
}

/**
 * Order a set of picked part files, or explain why they are not a set.
 *
 * A file picker hands files over in whatever order it likes, and the operator
 * may have grabbed the wrong number of them off a Drive folder. Both are caught
 * here, by name, before a byte is uploaded — the alternative is discovering it
 * as a decrypt failure, which reads as "wrong passphrase" and sends them looking
 * in the wrong place entirely.
 */
export function orderBackupParts(files: File[]): { parts: File[] } | { error: string } {
    const parsed = files.map((f) => ({ file: f, meta: parseBackupPartName(f.name) }));
    const unnamed = parsed.find((p) => p.meta === null);
    if (unnamed) return { error: `${unnamed.file.name} is not a backup part.` };

    const counts = new Set(parsed.map((p) => p.meta!.partCount));
    if (counts.size > 1)
        return { error: 'These parts are from different backups — pick one backup’s parts.' };

    const partCount = parsed[0].meta!.partCount;
    if (parsed.length !== partCount)
        return {
            error: `This backup has ${partCount} parts, but ${parsed.length} ${
                parsed.length === 1 ? 'was' : 'were'
            } selected. Restoring needs all of them.`,
        };

    const ordered = [...parsed].sort((a, b) => a.meta!.part - b.meta!.part);
    const missing = ordered.find((p, i) => p.meta!.part !== i + 1);
    if (missing) return { error: `Part ${ordered.findIndex((p) => p === missing) + 1} is missing.` };

    return { parts: ordered.map((p) => p.file) };
}

/**
 * Upload an already-split set of parts exactly as they were downloaded —
 * the shape a backup sits in on Google Drive once it is over the part size
 * (ADR-0101). Reassembling them locally first would ask the operator to
 * concatenate files by hand on the worst day they have had.
 */
export async function uploadRestoreFromParts(
    parts: File[],
    onProgress?: (sentBytes: number, totalBytes: number) => void,
    url?: string,
): Promise<void> {
    const total = parts.reduce((sum, f) => sum + f.size, 0);
    // The size these parts were ACTUALLY cut at — the first part's length, not
    // whatever this install is configured for now. A set downloaded months ago
    // was cut under whatever the setting was then, and must still go back up.
    const partSizeBytes = parts[0].size;
    let sent = 0;
    for (let i = 0; i < parts.length; i++) {
        await uploadRestorePart(i + 1, parts.length, partSizeBytes, parts[i], url);
        sent += parts[i].size;
        onProgress?.(sent, total);
    }
}

/** `POST /api/admin/backups/restore/validate` — pre-flight KEK check (ADR-0074).
 *  Uploads only the backup's leading bytes (the header carries the fingerprint,
 *  unencrypted), so it learns whether the file matches this install's Master KEK
 *  before committing to a destructive restore — no whole-file upload. */
export function validateRestoreKek(file: File | { backupId: string }): Promise<BackupKekCheck> {
    const form = new FormData();
    // A stored backup is checked in place: the server reads its own header, so
    // even the 8 KB slice is unnecessary.
    if (!(file instanceof File)) {
        const form2 = new FormData();
        form2.append('backupId', file.backupId);
        return requestMultipart<BackupKekCheck>('/api/admin/backups/restore/validate', form2);
    }
    form.append('archive', file.slice(0, 8192), file.name || 'backup.cofferbak');
    return requestMultipart<BackupKekCheck>('/api/admin/backups/restore/validate', form);
}

/** `GET /api/admin/backups/schedule` — the daily schedule + passphrase flag. */
export function fetchBackupSchedule(): Promise<BackupSchedule> {
    return request<BackupSchedule>('/api/admin/backups/schedule');
}

/** `GET /api/admin/backups/retention` — the GFS retention policy (ADR-0074). */
export function fetchBackupRetention(): Promise<BackupRetention> {
    return request<BackupRetention>('/api/admin/backups/retention');
}

/** `PUT /api/admin/backups/retention` — set the GFS retention policy. Governs
 *  local backups AND the Google Drive mirror. 422 `backup-retention-invalid`
 *  when a tier is out of range. */
export function setBackupRetention(
    body: { retentionDaily: number; retentionWeekly: number; retentionMonthly: number },
): Promise<BackupRetention> {
    return request<BackupRetention>('/api/admin/backups/retention', { method: 'PUT', body });
}

/** `PUT /api/admin/backups/schedule` — set the daily schedule. 422
 *  `backup-passphrase-not-set` when enabling without a passphrase. */
export function saveBackupSchedule(
    body: { enabled: boolean; hourLocal: number; minuteLocal: number; timezone: string },
): Promise<BackupSchedule> {
    return request<BackupSchedule>('/api/admin/backups/schedule', { method: 'PUT', body });
}
