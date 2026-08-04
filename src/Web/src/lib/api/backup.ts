// Admin whole-DB backup endpoints (ADR-0060), all behind RequireAdmin.
// Authenticated-admin restore (ADR-0071 D3) is here too; the bootstrap (pre-auth)
// restore + `coffer-api restore` remain for their cases.

import type { BackupKekCheck, BackupRetention, BackupSchedule, BackupSummary } from '../types/backup';
import { request, requestBlob, requestMultipart } from './_request';

/** The exact phrase an admin must type to confirm a destructive restore. */
export const RESTORE_CONFIRM_PHRASE = 'yes i agree';

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

/** `GET /api/admin/backups/{id}` — the encrypted .cofferbak bytes. */
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
 * `POST /api/admin/backups/restore` (ADR-0071 D3) — restore the whole database
 * from an uploaded `.cofferbak`. Destructive: on success (202) the server
 * restarts and everyone is signed out. `confirm` must equal
 * {@link RESTORE_CONFIRM_PHRASE}; set `acknowledgeKekMismatch` to proceed past a
 * cross-install KEK warning (422 `backup-kek-mismatch`).
 */
export function restoreBackup(
    file: File,
    passphrase: string,
    confirm: string,
    acknowledgeKekMismatch = false,
): Promise<void> {
    const form = new FormData();
    form.append('archive', file, file.name || 'backup.cofferbak');
    form.append('passphrase', passphrase);
    form.append('confirm', confirm);
    if (acknowledgeKekMismatch) form.append('acknowledgeKekMismatch', 'true');
    return requestMultipart<void>('/api/admin/backups/restore', form);
}

/** `POST /api/admin/backups/restore/validate` — pre-flight KEK check (ADR-0074).
 *  Uploads only the backup's leading bytes (the header carries the fingerprint,
 *  unencrypted), so it learns whether the file matches this install's Master KEK
 *  before committing to a destructive restore — no whole-file upload. */
export function validateRestoreKek(file: File): Promise<BackupKekCheck> {
    const form = new FormData();
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
