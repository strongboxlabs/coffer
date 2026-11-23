import { describe, expect, it } from 'vitest';

import type { StoredDashboardPrefs } from '@/lib/types';

import { CATALOG, REPORT_TILES_MAX, type WidgetKey } from './dashboardCatalog';
import { defaultLayout, hiddenWidgets, resolveLayout, resolveSettings, toDocument, type Layout } from './dashboardLayout';

const v1 = (widgets: { key: string; visible: boolean }[]): StoredDashboardPrefs =>
    ({ widgets, updatedAt: '2026-10-01T00:00:00Z' });

/** Each column's widgets in order, as `key` or `key(hidden)`. */
function shape(layout: Layout) {
    const label = (w: Layout['widgets'][number]) => (w.visible ? w.key : `${w.key}(hidden)`);
    return {
        main: layout.widgets.filter((w) => w.column === 'main').map(label),
        side: layout.widgets.filter((w) => w.column === 'side').map(label),
    };
}

const V1_DEFAULT = ['net-worth', 'accounts', 'investments', 'upcoming', 'activity', 'spending']
    .map((key) => ({ key, visible: true }));

describe('the default layout', () => {
    it('lists the main and side columns in catalog order, the catalog-only widgets hidden', () => {
        expect(shape(defaultLayout())).toEqual({
            main: ['problems', 'bills', 'review', 'budget-pace', 'cash-flow', 'recent-transactions(hidden)'],
            side: ['data-health', 'net-worth', 'investments', 'watchlist(hidden)', 'activity(hidden)', 'accounts(hidden)', 'cash-forecast(hidden)'],
        });
        expect(defaultLayout().columns).toBe('wide-narrow');
        // A report tile is in no layout until a report is chosen for it.
        expect(defaultLayout().widgets.map((w) => w.key)).not.toContain('report');
    });

    it('is what nothing stored, an empty document, and the earlier default all resolve to', () => {
        const expected = JSON.stringify(toDocument(defaultLayout()));
        for (const stored of [undefined, null, { widgets: [], updatedAt: null }, v1([]), v1(V1_DEFAULT)]) {
            expect(JSON.stringify(toDocument(resolveLayout(stored as StoredDashboardPrefs)))).toBe(expected);
        }
    });
});

describe('a layout an earlier release saved', () => {
    it('keeps its order and visibility in the new columns and inserts the new widgets at fixed places', () => {
        const layout = resolveLayout(v1([
            { key: 'spending', visible: true },
            { key: 'upcoming', visible: false },
            { key: 'net-worth', visible: true },
            { key: 'accounts', visible: true },
            { key: 'activity', visible: false },
            { key: 'investments', visible: true },
        ]));

        expect(shape(layout)).toEqual({
            main: ['problems', 'budget-pace', 'cash-flow', 'bills(hidden)', 'review', 'recent-transactions(hidden)'],
            side: ['net-worth', 'accounts(hidden)', 'activity(hidden)', 'investments', 'data-health', 'watchlist(hidden)', 'cash-forecast(hidden)'],
        });
        // Bills keeps the 30 days the earlier screen showed; everything else starts at
        // its defaults.
        expect(layout.widgets.find((w) => w.key === 'bills')?.settings.horizonDays).toBe(30);
        expect(layout.widgets.find((w) => w.key === 'net-worth')?.settings.months).toBe(12);
    });

    it('drops unknown and repeated keys, first occurrence winning, and appends what it omitted as visible', () => {
        const layout = resolveLayout(v1([
            { key: 'nonsense', visible: true },
            { key: 'upcoming', visible: false },
            { key: 'upcoming', visible: true },
            { key: 'investments', visible: false },
        ]));
        expect(shape(layout)).toEqual({
            // Omitted by the stored layout, so shown by the earlier screen: spending.
            main: ['problems', 'bills(hidden)', 'review', 'budget-pace', 'cash-flow', 'recent-transactions(hidden)'],
            side: ['investments(hidden)', 'net-worth', 'accounts(hidden)', 'activity', 'data-health', 'watchlist(hidden)', 'cash-forecast(hidden)'],
        });
    });

    it('migrates every order and hide set the earlier editor could produce', () => {
        const keys = ['net-worth', 'accounts', 'investments', 'upcoming', 'activity', 'spending'];
        const permutations = (xs: string[]): string[][] =>
            xs.length <= 1 ? [xs] : xs.flatMap((x, i) => permutations([...xs.slice(0, i), ...xs.slice(i + 1)]).map((p) => [x, ...p]));
        const hideSets = [new Set<string>(), new Set(['upcoming']), new Set(['net-worth', 'spending', 'activity'])];
        const target: Record<string, WidgetKey> = {
            upcoming: 'bills', spending: 'budget-pace', 'net-worth': 'net-worth',
            investments: 'investments', activity: 'activity', accounts: 'accounts',
        };
        let checked = 0;

        for (const order of permutations(keys)) {
            for (const hidden of hideSets) {
                // The earlier editor never hid accounts.
                const stored = order.map((key) => ({ key, visible: key === 'accounts' || !hidden.has(key) }));
                const layout = resolveLayout(v1(stored));
                const isDefault = order.join() === keys.join() && hidden.size === 0;

                // Every widget this build has that appears once, each once; no report tile.
                expect(layout.widgets.map((w) => w.key).sort())
                    .toEqual(CATALOG.filter((e) => e.key !== 'report').map((e) => e.key).sort());
                expect(layout.widgets.find((w) => w.key === 'accounts')?.visible).toBe(false);
                if (isDefault) continue;

                for (const key of keys.filter((k) => k !== 'accounts')) {
                    expect(layout.widgets.find((w) => w.key === target[key])?.visible).toBe(!hidden.has(key));
                }
                // Saved relative order survives within each column.
                for (const column of ['main', 'side'] as const) {
                    const migrated = layout.widgets.filter((w) => w.column === column).map((w) => w.key);
                    const expected = order.map((k) => target[k]).filter((k) => migrated.includes(k));
                    expect(migrated.filter((k) => expected.includes(k))).toEqual(expected);
                }
                checked++;
            }
        }
        expect(checked).toBe(720 * 3 - 1);
    });
});

describe('a version 2 layout', () => {
    it('round-trips', () => {
        const layout = resolveLayout(v1([{ key: 'spending', visible: false }]));
        const stored = { ...toDocument(layout), updatedAt: '2026-10-01T00:00:00Z' };
        expect(resolveLayout(stored)).toEqual(layout);
    });

    it('repairs what it cannot use instead of refusing it', () => {
        const stored: StoredDashboardPrefs = {
            version: 2,
            columns: 'triple',
            widgets: [
                { id: 'gone', key: 'gone', visible: true, column: 'main', settings: {} },
                { id: 'net-worth', key: 'net-worth', visible: true, column: 'nowhere', settings: { months: 7, showSplit: 'yes', extra: 1 } },
                { id: 'net-worth', key: 'net-worth', visible: false, column: 'main', settings: {} },
                { id: 'activity', key: 'activity', visible: true, column: 'main', settings: { families: ['quote', 'nope', 'quote'] } },
            ],
            updatedAt: null,
        };
        const layout = resolveLayout(stored);

        expect(layout.columns).toBe('wide-narrow');
        expect(layout.widgets.filter((w) => w.key === 'net-worth')).toHaveLength(1);
        const netWorth = layout.widgets.find((w) => w.key === 'net-worth')!;
        expect(netWorth.column).toBe('side');
        expect(netWorth.settings).toEqual({ months: 12, showSplit: true });
        expect(layout.widgets.find((w) => w.key === 'activity')?.settings.families).toEqual(['quote']);
        // Missing widgets come back with their catalog visibility: Problems shown,
        // Accounts not.
        expect(layout.widgets.find((w) => w.key === 'problems')?.visible).toBe(true);
        expect(layout.widgets.find((w) => w.key === 'accounts')?.visible).toBe(false);
        expect(layout.widgets.map((w) => w.key)).not.toContain('gone');
    });
});

/** A layout as the first release of version 2 saved it: its ten widgets, To review
 *  with no settings, and none of the widgets added since. */
const SLICE_ONE: StoredDashboardPrefs = {
    version: 2,
    columns: 'wide-narrow',
    widgets: [
        { id: 'problems', key: 'problems', visible: true, column: 'main', settings: { collapseWhenClear: true } },
        { id: 'bills', key: 'bills', visible: true, column: 'main', settings: {} },
        { id: 'review', key: 'review', visible: true, column: 'main', settings: {} },
        { id: 'budget-pace', key: 'budget-pace', visible: true, column: 'main', settings: {} },
        { id: 'cash-flow', key: 'cash-flow', visible: true, column: 'main', settings: {} },
        { id: 'data-health', key: 'data-health', visible: true, column: 'side', settings: {} },
        { id: 'net-worth', key: 'net-worth', visible: true, column: 'side', settings: {} },
        { id: 'investments', key: 'investments', visible: true, column: 'side', settings: {} },
        { id: 'activity', key: 'activity', visible: false, column: 'side', settings: {} },
        { id: 'accounts', key: 'accounts', visible: false, column: 'side', settings: {} },
    ],
    updatedAt: '2026-10-31T00:00:00Z',
};

describe('a layout saved before the newer widgets existed', () => {
    it('gains them hidden, each in its catalog column, and fills To review\'s settings', () => {
        const layout = resolveLayout(SLICE_ONE);
        expect(shape(layout)).toEqual({
            main: ['problems', 'bills', 'review', 'budget-pace', 'cash-flow', 'recent-transactions(hidden)'],
            side: ['data-health', 'net-worth', 'investments', 'activity(hidden)', 'accounts(hidden)', 'watchlist(hidden)', 'cash-forecast(hidden)'],
        });
        expect(layout.widgets.find((w) => w.key === 'review')?.settings).toEqual({ rows: 5, groupByAccount: false });
        expect(layout.widgets.find((w) => w.key === 'cash-forecast')?.settings).toEqual({ days: 30, focusAccountId: null });
        expect(layout.widgets.find((w) => w.key === 'recent-transactions')?.settings).toEqual({ rows: 10, direction: 'all' });
        expect(layout.widgets.find((w) => w.key === 'watchlist')?.settings).toEqual({ categoryIds: [], sort: 'chosen' });
    });

    it('lists the hidden tray in catalog order, the same for it as for a fresh layout', () => {
        const fresh = hiddenWidgets(defaultLayout()).map((w) => w.key);
        expect(fresh).toEqual(['recent-transactions', 'watchlist', 'activity', 'accounts', 'cash-forecast']);
        // The upgraded layout holds the newer widgets at the END of its list; the
        // tray does not follow the list.
        expect(hiddenWidgets(resolveLayout(SLICE_ONE)).map((w) => w.key)).toEqual(fresh);
    });
});

describe('a category list setting', () => {
    const ids = (stored: unknown) => resolveSettings('watchlist', { categoryIds: stored }).categoryIds;

    it('keeps ids only, each once, in the order chosen', () => {
        expect(ids(['cat-b', 7, 'cat-a', null, 'cat-b', { id: 'x' }])).toEqual(['cat-b', 'cat-a']);
    });

    it('keeps at most eight, the first eight', () => {
        const ten = Array.from({ length: 10 }, (_, i) => `cat-${i}`);
        expect(ids(ten)).toEqual(ten.slice(0, 8));
    });

    it('lets an empty list stay empty, and reads anything else as empty', () => {
        // Unlike a set, which falls back to its default rather than show nothing:
        // an empty watchlist says so and asks for a choice.
        expect(ids([])).toEqual([]);
        expect(ids('cat-a')).toEqual([]);
        expect(ids(undefined)).toEqual([]);
    });
});

/** A stored version 2 layout: the default one, then these widgets after it. */
const withTiles = (tiles: { id: string; key?: string; visible?: boolean; column?: string; settings?: Record<string, unknown> }[]): StoredDashboardPrefs => {
    const doc = toDocument(defaultLayout());
    return {
        ...doc,
        widgets: [
            ...doc.widgets,
            ...tiles.map((t) => ({
                id: t.id, key: t.key ?? 'report', visible: t.visible ?? true, column: t.column ?? 'main',
                settings: (t.settings ?? {}) as Record<string, never>,
            })),
        ],
        updatedAt: '2026-10-31T00:00:00Z',
    };
};

const SAVED_A = '11111111-1111-4111-8111-111111111111';
const SAVED_B = '22222222-2222-4222-8222-222222222222';
const tiles = (layout: Layout) => layout.widgets.filter((w) => w.key === 'report');

describe('report tiles in a stored layout', () => {
    it('keeps a tile for a saved report and one for a catalog report, each by its id', () => {
        const layout = resolveLayout(withTiles([
            { id: `report:saved:${SAVED_A}`, settings: { view: 'table', height: 'tall' } },
            { id: 'report:catalog:income-and-expenses', column: 'side' },
        ]));
        expect(tiles(layout)).toEqual([
            { id: `report:saved:${SAVED_A}`, key: 'report', visible: true, column: 'main', settings: { view: 'table', height: 'tall' } },
            { id: 'report:catalog:income-and-expenses', key: 'report', visible: true, column: 'side', settings: { view: 'chart', height: 'compact' } },
        ]);
    });

    it('drops an id that names no report, and a report already in the layout', () => {
        const layout = resolveLayout(withTiles([
            { id: 'report' },
            { id: 'report:saved:not-a-uuid' },
            { id: 'report:elsewhere:x' },
            { id: `report:saved:${SAVED_A}`, settings: { height: 'tall' } },
            { id: `report:saved:${SAVED_A.toUpperCase()}`, settings: { height: 'compact' } },
        ]));
        // The first occurrence wins; the id is kept in one spelling.
        expect(tiles(layout).map((w) => [w.id, w.settings.height])).toEqual([[`report:saved:${SAVED_A}`, 'tall']]);
    });

    it('keeps a tile for a report that may no longer exist: only the saved reports can say', () => {
        const layout = resolveLayout(withTiles([{ id: 'report:catalog:gone-from-the-catalog' }]));
        expect(tiles(layout).map((w) => w.id)).toEqual(['report:catalog:gone-from-the-catalog']);
    });

    it('repairs a tile\'s settings it cannot use', () => {
        const layout = resolveLayout(withTiles([{ id: `report:saved:${SAVED_A}`, settings: { view: 'pie', height: 9, extra: true } }]));
        expect(tiles(layout)[0]!.settings).toEqual({ view: 'chart', height: 'compact' });
    });

    it(`shows at most ${REPORT_TILES_MAX} at once, and keeps the rest hidden`, () => {
        const many = Array.from({ length: REPORT_TILES_MAX + 2 }, (_, i) =>
            ({ id: `report:saved:${String(i).padStart(8, '0')}-0000-4000-8000-000000000000` }));
        const layout = resolveLayout(withTiles([{ id: `report:saved:${SAVED_B}`, visible: false }, ...many]));
        expect(tiles(layout)).toHaveLength(REPORT_TILES_MAX + 3);
        expect(tiles(layout).filter((w) => w.visible)).toHaveLength(REPORT_TILES_MAX);
        expect(tiles(layout).slice(-2).map((w) => w.visible)).toEqual([false, false]);
    });

    it('round-trips', () => {
        const layout = resolveLayout(withTiles([
            { id: `report:saved:${SAVED_A}`, visible: false },
            { id: 'report:catalog:income-and-expenses', settings: { view: 'table' } },
        ]));
        expect(resolveLayout({ ...toDocument(layout), updatedAt: '2026-11-01T00:00:00Z' })).toEqual(layout);
    });

    it('lists a hidden tile in the tray at the report entry\'s place, between Accounts and Cash ahead', () => {
        const layout = resolveLayout(withTiles([{ id: `report:saved:${SAVED_A}`, visible: false }]));
        expect(hiddenWidgets(layout).map((w) => w.id))
            .toEqual(['recent-transactions', 'watchlist', 'activity', 'accounts', `report:saved:${SAVED_A}`, 'cash-forecast']);
    });
});

describe('an account setting', () => {
    const focus = (stored: unknown) => resolveSettings('cash-forecast', { focusAccountId: stored }).focusAccountId;

    it('keeps an id, and reads anything else as none', () => {
        expect(focus('acct-a')).toBe('acct-a');
        for (const other of [null, undefined, '', 7, ['acct-a'], { id: 'acct-a' }]) expect(focus(other)).toBeNull();
    });

    it('keeps the look-ahead to the days offered', () => {
        expect(resolveSettings('cash-forecast', { days: 60 }).days).toBe(60);
        expect(resolveSettings('cash-forecast', { days: 45 }).days).toBe(30);
    });
});
