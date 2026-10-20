// Admin user-management endpoints (ADR-0083). RequireAdmin server-side.

import type { AdminUser } from '../types/adminUser';
import { request } from './_request';
import { withStepUpIfRequired } from './stepUp';

/** GET /api/admin/users — every user on the deployment. */
export function fetchAdminUsers(): Promise<AdminUser[]> {
    return request<AdminUser[]>('/api/admin/users');
}

/**
 * PUT /api/admin/users/{userId}/disabled — disable / re-enable a user. Disabling
 * also signs them out everywhere and revokes their app connections; re-enabling
 * restores none of that (security audit, H2).
 */
export function setUserDisabled(userId: string, disabled: boolean): Promise<void> {
    return request<void>(`/api/admin/users/${encodeURIComponent(userId)}/disabled`, {
        method: 'PUT',
        body: { disabled },
    });
}

/**
 * PUT /api/admin/users/{userId}/admin — grant / revoke the instance admin flag.
 * Granting needs a passkey tap (security audit, M2); revoking doesn't.
 */
export function setUserAdmin(userId: string, isAdmin: boolean): Promise<void> {
    return withStepUpIfRequired('grant-admin', (headers) =>
        request<void>(`/api/admin/users/${encodeURIComponent(userId)}/admin`, {
            method: 'PUT',
            body: { isAdmin },
            headers,
        }),
    );
}

/** What "sign out everywhere" ended. */
export interface SignOutEverywhereResult {
    sessionsEnded: number;
    mcpTokensRevoked: number;
    appConnectionsRevoked: number;
}

/**
 * POST /api/admin/users/{userId}/sign-out — end every session, MCP token and app
 * connection the user holds. They stay enabled and can sign in again.
 */
export function signUserOutEverywhere(userId: string): Promise<SignOutEverywhereResult> {
    return request<SignOutEverywhereResult>(
        `/api/admin/users/${encodeURIComponent(userId)}/sign-out`,
        { method: 'POST' },
    );
}
