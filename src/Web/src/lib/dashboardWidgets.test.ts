import { describe, expect, it } from 'vitest';

import {
    DASHBOARD_WIDGETS,
    resolveDashboardLayout,
    type DashboardWidgetKey,
} from './dashboardWidgets';

/**
 * The layout resolver had no test at all, and it carries the one property every
 * future widget depends on: a widget added to the catalog must appear for people
 * who already have a stored layout.
 *
 * That story fails SILENTLY if it breaks. The build stays green, the widget
 * renders perfectly in isolation, and it simply never reaches anyone whose
 * `dashboard` preference predates it — which is everyone with an existing
 * install, i.e. the only people who matter. The Spending tile is the first
 * widget added since the resolver was written, so it is the first time the
 * property has actually been exercised.
 */

const keys = (ws: { key: DashboardWidgetKey }[]) => ws.map((w) => w.key);

describe('resolveDashboardLayout', () => {
    it('shows a NEW catalog widget to someone whose stored layout predates it', () => {
        // The property the whole upgrade story rests on. A layout saved before
        // Spending existed lists the five older widgets; Spending must still
        // arrive, and arrive visible.
        const stored = {
            widgets: [
                { key: 'net-worth', visible: true },
                { key: 'accounts', visible: true },
                { key: 'investments', visible: false },
                { key: 'upcoming', visible: true },
                { key: 'activity', visible: true },
            ],
        };

        const resolved = resolveDashboardLayout(stored);
        const spending = resolved.find((w) => w.key === 'spending');

        expect(spending).toBeDefined();
        expect(spending?.visible).toBe(true);
    });

    it('appends unknown-to-the-store widgets AFTER the ones the user ordered', () => {
        // A new widget must not barge into the middle of a layout somebody
        // arranged deliberately.
        const stored = {
            widgets: [
                { key: 'activity', visible: true },
                { key: 'accounts', visible: true },
            ],
        };

        expect(keys(resolveDashboardLayout(stored)).slice(0, 2)).toEqual(['activity', 'accounts']);
    });

    it('honours a hidden widget rather than resurrecting it', () => {
        const stored = { widgets: [{ key: 'net-worth', visible: false }] };
        expect(resolveDashboardLayout(stored).find((w) => w.key === 'net-worth')?.visible).toBe(false);
    });

    it('forces alwaysVisible widgets visible even when the store says otherwise', () => {
        // Accounts is the navigation backbone; a stale or hand-edited preference
        // must not be able to strand someone without it.
        const stored = { widgets: [{ key: 'accounts', visible: false }] };
        expect(resolveDashboardLayout(stored).find((w) => w.key === 'accounts')?.visible).toBe(true);
    });

    it('ignores a stored key the catalog no longer has', () => {
        const stored = { widgets: [{ key: 'retired-widget', visible: true }] };
        expect(keys(resolveDashboardLayout(stored))).toEqual(keys([...DASHBOARD_WIDGETS]));
    });

    it('does not duplicate a widget the store lists twice', () => {
        const stored = {
            widgets: [
                { key: 'accounts', visible: true },
                { key: 'accounts', visible: false },
            ],
        };
        expect(keys(resolveDashboardLayout(stored)).filter((k) => k === 'accounts')).toHaveLength(1);
    });

    it('gives a brand-new install the whole catalog, visible, in catalog order', () => {
        expect(keys(resolveDashboardLayout(undefined))).toEqual(keys([...DASHBOARD_WIDGETS]));
        expect(resolveDashboardLayout(undefined).every((w) => w.visible)).toBe(true);
    });
});
