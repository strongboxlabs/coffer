// Installation-wide metadata endpoints (ADR-0044).

import type { VersionResponse } from '../types/meta';
import { request } from './_request';

/**
 * GET /api/meta/version — the API + DB version axes for the About
 * panel. Authenticated; the SPA supplies its own (UI) axis from the
 * build-time constants in vite-env.d.ts.
 */
export function fetchVersion(): Promise<VersionResponse> {
    return request<VersionResponse>('/api/meta/version');
}
