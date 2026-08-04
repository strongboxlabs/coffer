// Admin user-management endpoints (ADR-0083). RequireAdmin server-side.

import type { AdminUser } from '../types/adminUser';
import { request } from './_request';

/** GET /api/admin/users — every user on the deployment. */
export function fetchAdminUsers(): Promise<AdminUser[]> {
    return request<AdminUser[]>('/api/admin/users');
}

/** PUT /api/admin/users/{userId}/disabled — soft-disable / re-enable a user. */
export function setUserDisabled(userId: string, disabled: boolean): Promise<void> {
    return request<void>(`/api/admin/users/${encodeURIComponent(userId)}/disabled`, {
        method: 'PUT',
        body: { disabled },
    });
}

/** PUT /api/admin/users/{userId}/admin — grant / revoke the instance admin flag. */
export function setUserAdmin(userId: string, isAdmin: boolean): Promise<void> {
    return request<void>(`/api/admin/users/${encodeURIComponent(userId)}/admin`, {
        method: 'PUT',
        body: { isAdmin },
    });
}
