import { describe, expect, it } from 'vitest';

import demoHomeLayout from '@/lib/demoHomeLayout.json';
import type { DashboardPrefsV2 } from '@/lib/types';

import { findCatalogReport } from '../reports/catalog';
import { catalogEntry, parseReportTileId, type SettingSpec } from './dashboardCatalog';
import { resolveLayout, toDocument } from './dashboardLayout';

/**
 * The Demo's Home layout and catalog pins (`src/lib/demoHomeLayout.json`), which the API
 * embeds and writes into every new Demo. Its widget, setting and catalog keys are this
 * SPA's, so this is the drift test that keeps the file in step with both catalogs: every
 * key exists, every setting holds a value its kind allows, and the layout is one the Home
 * screen's own resolver keeps exactly as written, with nothing dropped, repaired or added.
 *
 * The file names the Demo's rows by reference, because they exist only after its import:
 * `{ categoryRef: [segments] }` and `{ accountRef: role }` stand for an id, and
 * `{ savedReportRef: n }` for the tile id of the n-th saved report. The API resolves them;
 * here each becomes a stand-in id of the same shape.
 */

type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

const REFS = ['categoryRef', 'accountRef', 'savedReportRef'] as const;

/** A stand-in id for the n-th reference. */
const standIn = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

function isObject(value: unknown): value is Record<string, Json> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The one reference an object is, or null for an object that is not one. */
function refOf(value: unknown): { name: string; target: Json } | null {
    if (!isObject(value)) return null;
    const entries = Object.entries(value);
    if (entries.length !== 1 || !entries[0]![0].endsWith('Ref')) return null;
    return { name: entries[0]![0], target: entries[0]![1] };
}

/** The file's layout with each reference replaced by a stand-in of the shape the API writes. */
function withStandIns(node: Json, counter = { n: 0 }): Json {
    const ref = refOf(node);
    if (ref) {
        counter.n += 1;
        return ref.name === 'savedReportRef' ? `report:saved:${standIn(1000 + Number(ref.target))}` : standIn(counter.n);
    }
    if (Array.isArray(node)) return node.map((item) => withStandIns(item, counter));
    if (isObject(node)) {
        return Object.fromEntries(Object.entries(node).map(([key, value]) => [key, withStandIns(value, counter)]));
    }
    return node;
}

function settingProblem(widget: string, name: string, spec: SettingSpec, value: Json): string | null {
    const where = `${widget}.${name}`;
    switch (spec.kind) {
        case 'boolean':
            return typeof value === 'boolean' ? null : `${where} is not a boolean`;
        case 'choice':
            return spec.options.some((o) => o.value === value) ? null : `${where} is not one of its options`;
        case 'set': {
            const allowed = new Set(spec.options.map((o) => o.value));
            return Array.isArray(value) && value.length > 0 && value.every((v) => typeof v === 'string' && allowed.has(v))
                ? null
                : `${where} is not a non-empty set of its options`;
        }
        case 'categories':
            if (!Array.isArray(value)) return `${where} is not a list`;
            if (value.length > spec.max) return `${where} lists more than ${spec.max} categories`;
            return value.every((v) => {
                const ref = refOf(v);
                return ref?.name === 'categoryRef' && Array.isArray(ref.target) && ref.target.length > 0
                    && ref.target.every((s) => typeof s === 'string');
            })
                ? null
                : `${where} lists something other than category references`;
        case 'account': {
            const ref = refOf(value);
            return value === null || (ref?.name === 'accountRef' && typeof ref.target === 'string')
                ? null
                : `${where} is neither null nor an account reference`;
        }
    }
}

/** Everything wrong with a layout file of this shape; empty when it is in step. */
function problemsOf(file: Json): string[] {
    const problems: string[] = [];
    if (!isObject(file) || !isObject(file.layout) || !Array.isArray(file.catalogPins)) return ['the file has no layout or pins'];
    const layout = file.layout;

    for (const key of file.catalogPins) {
        const tile = typeof key === 'string' ? parseReportTileId(`report:catalog:${key}`) : null;
        if (tile?.kind !== 'catalog' || !findCatalogReport(tile.key)) problems.push(`pin ${String(key)} is no catalog report`);
    }

    const savedPlaces: number[] = [];
    for (const widget of Array.isArray(layout.widgets) ? layout.widgets : []) {
        if (!isObject(widget)) {
            problems.push('a widget is not an object');
            continue;
        }
        const entry = catalogEntry(String(widget.key));
        if (!entry) {
            problems.push(`widget ${String(widget.key)} is not in the catalog`);
            continue;
        }
        const ref = refOf(widget.id);
        if (entry.key === 'report') {
            if (ref?.name === 'savedReportRef' && Number.isInteger(ref.target) && Number(ref.target) >= 1) {
                savedPlaces.push(Number(ref.target));
            } else {
                const tile = typeof widget.id === 'string' ? parseReportTileId(widget.id) : null;
                if (tile?.kind !== 'catalog' || !findCatalogReport(tile.key))
                    problems.push(`report tile ${JSON.stringify(widget.id)} names no report`);
            }
        } else if (widget.id !== entry.key) {
            problems.push(`widget ${entry.key} has the id ${JSON.stringify(widget.id)}`);
        }

        const settings = isObject(widget.settings) ? widget.settings : {};
        for (const name of Object.keys(settings)) {
            if (!(name in entry.settings)) problems.push(`${entry.key}.${name} is not one of its settings`);
        }
        for (const [name, spec] of Object.entries(entry.settings)) {
            if (!(name in settings)) problems.push(`${entry.key}.${name} is not set`);
            else {
                const problem = settingProblem(entry.key, name, spec, settings[name]!);
                if (problem) problems.push(problem);
            }
        }
    }
    if (new Set(savedPlaces).size !== savedPlaces.length) problems.push('a saved report has two tiles');

    const unknownRef = (node: Json): void => {
        const ref = refOf(node);
        if (ref && !(REFS as readonly string[]).includes(ref.name)) problems.push(`${ref.name} is not a reference the API resolves`);
        if (Array.isArray(node)) node.forEach(unknownRef);
        else if (isObject(node)) Object.values(node).forEach(unknownRef);
    };
    unknownRef(layout);

    // The Home screen's own resolver keeps the layout exactly as written.
    const document = withStandIns(layout) as unknown as DashboardPrefsV2;
    const kept = toDocument(resolveLayout({ ...document, updatedAt: null }));
    if (JSON.stringify(kept) !== JSON.stringify(document)) problems.push('the Home resolver changes the layout');
    return problems;
}

const FILE = demoHomeLayout as unknown as Json;

/** The file with one change, for the mutations below. */
function mutated(change: (file: { catalogPins: Json[]; layout: { widgets: Record<string, Json>[] } }) => void): Json {
    const copy = structuredClone(FILE) as unknown as { catalogPins: Json[]; layout: { widgets: Record<string, Json>[] } };
    change(copy);
    return copy as unknown as Json;
}

const widgetOf = (file: { layout: { widgets: Record<string, Json>[] } }, key: string) =>
    file.layout.widgets.find((w) => w.key === key)!;

describe('the Demo Home layout file', () => {
    it('names only catalog widgets, settings and reports, and the Home resolver keeps it as written', () => {
        expect(problemsOf(FILE)).toEqual([]);
    });

    it('shows a category watchlist, the cash forecast on one account, and a tile for each saved report', () => {
        const file = mutated(() => {}) as unknown as { layout: { widgets: Record<string, Json>[] } };
        const watchlist = widgetOf(file, 'watchlist');
        expect(watchlist.visible).toBe(true);
        expect((watchlist.settings as Record<string, Json[]>).categoryIds!.length).toBeGreaterThan(0);
        const forecast = widgetOf(file, 'cash-forecast');
        expect(forecast.visible).toBe(true);
        expect(refOf((forecast.settings as Record<string, Json>).focusAccountId)?.name).toBe('accountRef');
        const tiles = file.layout.widgets.filter((w) => w.key === 'report');
        expect(tiles.map((w) => refOf(w.id)?.target)).toEqual([1, 2, 3]);
        expect(tiles.every((w) => w.visible === true)).toBe(true);
    });

    it.each([
        ['an unknown widget key', mutated((f) => { widgetOf(f, 'bills').key = 'no-such-widget'; })],
        ['an unknown catalog pin', mutated((f) => { f.catalogPins.push('no-such-report'); })],
        ['a catalog pin that is not a key', mutated((f) => { f.catalogPins.push('Not A Key'); })],
        ['an unknown setting', mutated((f) => { (widgetOf(f, 'bills').settings as Record<string, Json>).colour = 'red'; })],
        ['a setting left out', mutated((f) => { delete (widgetOf(f, 'bills').settings as Record<string, Json>).rows; })],
        ['a choice outside its options', mutated((f) => { (widgetOf(f, 'bills').settings as Record<string, Json>).rows = 9; })],
        ['more watchlist categories than it shows', mutated((f) => {
            const settings = widgetOf(f, 'watchlist').settings as Record<string, Json[]>;
            settings.categoryIds = Array.from({ length: 9 }, (_, i) => ({ categoryRef: ['Personal', `Leaf ${i}`] }));
        })],
        ['a reference the API does not resolve', mutated((f) => {
            (widgetOf(f, 'cash-forecast').settings as Record<string, Json>).focusAccountId = { securityRef: 'fund' };
        })],
        ['a report tile naming no report', mutated((f) => { f.layout.widgets.find((w) => w.key === 'report')!.id = 'report:somewhere'; })],
        ['a widget the resolver would add', mutated((f) => { f.layout.widgets = f.layout.widgets.filter((w) => w.key !== 'accounts'); })],
        ['a widget repeated', mutated((f) => { f.layout.widgets.push(structuredClone(widgetOf(f, 'review'))); })],
    ])('finds %s', (_, file) => {
        expect(problemsOf(file)).not.toEqual([]);
    });
});
