// The Upgrade tab's endpoints (ADR-0110), all behind RequireAdmin on the cookie session.

import type { UpgradeSettings, UpgradeStatus } from '../types/upgrade';
import { request } from './_request';
import { stepUpHeaders } from './stepUp';

/** The word typed to confirm an upgrade. The server checks it again, trimmed and case-insensitive. */
export const UPGRADE_CONFIRM_PHRASE = 'upgrade';

/** React Query key for the Upgrade tab's status. */
export const UPGRADE_QUERY_KEY = ['admin-upgrade'] as const;

/** `GET /api/admin/upgrade` — everything the tab shows. Goes to GitHub only when the automatic
 *  check is on and the last result is stale. */
export function fetchUpgradeStatus(): Promise<UpgradeStatus> {
    return request<UpgradeStatus>('/api/admin/upgrade');
}

/** `POST /api/admin/upgrade/check` — check for a release now, whether or not the automatic check is on. */
export function checkForUpgrade(): Promise<UpgradeStatus> {
    return request<UpgradeStatus>('/api/admin/upgrade/check', { method: 'POST' });
}

/**
 * `POST /api/admin/upgrade/preflight` — every refusal the server can give without a passkey or a
 * backup, for the same body start takes. Resolves with nothing when start would not refuse. It reads
 * the release fresh, so a refusal means the server has just looked: ask for the status again. 422
 * codes: `upgrade-phrase-wrong`, `upgrade-unavailable`, `backup-passphrase-not-set`,
 * `upgrade-in-progress`, `backup-in-progress`, `upgrade-nothing-newer`.
 */
export function preflightUpgrade(version: string, phrase: string): Promise<void> {
    return request<void>('/api/admin/upgrade/preflight', { method: 'POST', body: { version, phrase } });
}

/**
 * `POST /api/admin/upgrade` — start an upgrade to `version`. A fresh passkey tap rides with it
 * (ADR-0105 D1), and the server takes the backup after it answers, so this resolves with the status
 * of an attempt that is "backing-up", not one that has finished.
 *
 * The tap is asked for after {@link preflightUpgrade}, so a refusal the server can already see never
 * spends one. Start gives the same refusals again, since time passes between the two.
 */
export async function startUpgrade(version: string, phrase: string): Promise<UpgradeStatus> {
    const headers = await stepUpHeaders('upgrade');
    return request<UpgradeStatus>('/api/admin/upgrade', {
        method: 'POST',
        body: { version, phrase },
        headers,
    });
}

/** `PUT /api/admin/upgrade/settings` — the in-app switch, the automatic check and the private fork. */
export function saveUpgradeSettings(settings: UpgradeSettings): Promise<void> {
    return request<void>('/api/admin/upgrade/settings', { method: 'PUT', body: settings });
}

/** `PUT /api/admin/upgrade/token` — store the private-repository token, sealed. 422 `upgrade-token-invalid`
 *  for anything but one value without spaces. The token is never read back. */
export function saveUpgradeToken(token: string): Promise<void> {
    return request<void>('/api/admin/upgrade/token', { method: 'PUT', body: { token } });
}

/** `DELETE /api/admin/upgrade/token` — remove it. Idempotent. */
export function clearUpgradeToken(): Promise<void> {
    return request<void>('/api/admin/upgrade/token', { method: 'DELETE' });
}
