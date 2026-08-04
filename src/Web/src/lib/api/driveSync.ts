// Admin Google Drive backup-sync endpoints (ADR-0062 §④a), all behind
// RequireAdmin. The client secret is sent only on connect/start and never
// returned; status carries no token, just connection metadata.

import type { DriveConnectStart, DriveSyncStatus } from '../types/driveSync';
import { request } from './_request';

/** `GET /api/admin/drive-sync` — current status (never the token). */
export function fetchDriveSyncStatus(): Promise<DriveSyncStatus> {
    return request<DriveSyncStatus>('/api/admin/drive-sync');
}

/** `POST /api/admin/drive-sync/connect/start` — begin the auth-code flow with
 *  the admin's own Google Cloud Web OAuth client; returns the Google consent URL
 *  to redirect to. 422 `drive-client-required` when id/secret are blank. */
export function startDriveConnect(clientId: string, clientSecret: string): Promise<DriveConnectStart> {
    return request<DriveConnectStart>('/api/admin/drive-sync/connect/start', {
        method: 'POST',
        body: { clientId, clientSecret },
    });
}

/** `POST /api/admin/drive-sync/disconnect` — forget the token + folder. Idempotent. */
export function disconnectDrive(): Promise<void> {
    return request<void>('/api/admin/drive-sync/disconnect', { method: 'POST' });
}

/** `PUT /api/admin/drive-sync/enabled` — toggle auto-push-with-each-backup.
 *  422 `drive-not-connected` when enabling without a connected account. */
export function setDriveEnabled(enabled: boolean): Promise<DriveSyncStatus> {
    return request<DriveSyncStatus>('/api/admin/drive-sync/enabled', {
        method: 'PUT',
        body: { enabled },
    });
}

/** `POST /api/admin/drive-sync/upload-all` — reconcile the Drive folder to the
 *  local backup set (upload missing, remove extras). 422 `drive-not-connected`
 *  when not connected. */
export function uploadAllToDrive(): Promise<DriveSyncStatus> {
    return request<DriveSyncStatus>('/api/admin/drive-sync/upload-all', { method: 'POST' });
}
