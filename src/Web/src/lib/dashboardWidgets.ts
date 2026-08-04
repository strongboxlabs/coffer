// The Overview widget catalog + layout resolver (ADR-0056 slice 3). The catalog
// is the source of truth for which widgets exist and their labels; the stored
// `dashboard` preference only carries order + visibility. Keeping the catalog
// here (not in the API) means adding a widget needs no backend change.

import type { DashboardPrefs } from './types';

export type DashboardWidgetKey =
    | 'net-worth'
    | 'accounts'
    | 'investments'
    | 'upcoming'
    | 'activity';

export interface DashboardWidgetMeta {
    key: DashboardWidgetKey;
    label: string;
    /** Always shown + never hideable — the navigation backbone. */
    alwaysVisible: boolean;
}

/** Canonical widgets in their default order. */
export const DASHBOARD_WIDGETS: readonly DashboardWidgetMeta[] = [
    { key: 'net-worth', label: 'Net worth', alwaysVisible: false },
    { key: 'accounts', label: 'Accounts', alwaysVisible: true },
    { key: 'investments', label: 'Investments', alwaysVisible: false },
    { key: 'upcoming', label: 'Upcoming', alwaysVisible: false },
    { key: 'activity', label: 'Recent activity', alwaysVisible: false },
] as const;

export interface ResolvedWidget extends DashboardWidgetMeta {
    visible: boolean;
}

/**
 * Merge a stored layout with the canonical catalog into the effective ordered
 * widget list:
 *   - stored entries first, in their saved order (unknown keys ignored),
 *   - then any canonical widgets the stored layout omits, in catalog order,
 *     defaulted visible (so widgets we add later appear for existing users),
 *   - `alwaysVisible` widgets (accounts) are forced visible regardless.
 * An empty/absent pref yields the canonical default (all visible, catalog order).
 */
export function resolveDashboardLayout(prefs: DashboardPrefs | undefined): ResolvedWidget[] {
    const byKey = new Map(DASHBOARD_WIDGETS.map((w) => [w.key, w]));
    const resolved: ResolvedWidget[] = [];
    const placed = new Set<DashboardWidgetKey>();

    for (const stored of prefs?.widgets ?? []) {
        const meta = byKey.get(stored.key as DashboardWidgetKey);
        if (!meta || placed.has(meta.key)) continue;
        resolved.push({ ...meta, visible: meta.alwaysVisible || stored.visible });
        placed.add(meta.key);
    }
    for (const meta of DASHBOARD_WIDGETS) {
        if (placed.has(meta.key)) continue;
        resolved.push({ ...meta, visible: true });
    }
    return resolved;
}
