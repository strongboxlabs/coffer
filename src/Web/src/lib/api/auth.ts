// Auth endpoints.

import type { CurrentUser } from '../types/auth';
import { request } from './_request';

/**
 * GET /api/auth/me — the protected-route auth check. Returns the
 * authenticated user's identity; throws an ApiError with status 401
 * if the session is missing or stale (the loader catches that and
 * redirects to /login).
 */
export function fetchCurrentUser(): Promise<CurrentUser> {
    return request<CurrentUser>('/api/auth/me');
}
