// The layout resolver: whatever the API returns (a version-2 layout, a layout an
// earlier release wrote, or nothing) becomes a complete layout this build can draw.
// It repairs rather than rejects: an unknown key is dropped, a missing one inserted,
// an out-of-range setting replaced by its default. A stale document can never brick
// the home screen.

import type { DashboardPrefsV2, DashboardSettingValue, StoredDashboardPrefs } from '@/lib/types';

import {
    CATALOG,
    catalogEntry,
    COLUMNS_MODES,
    DEFAULT_COLUMNS,
    parseReportTileId,
    reportTileId,
    type CatalogEntry,
    type Column,
    type ColumnsMode,
    type SettingSpec,
    type WidgetKey,
} from './dashboardCatalog';

export type WidgetSettings = Record<string, DashboardSettingValue>;

export interface LayoutWidget {
    /** Equals `key` for a widget that appears once. A widget that appears once per
     *  subject (a report tile) is identified by its subject: `report:saved:<uuid>`
     *  or `report:catalog:<key>`. */
    id: string;
    key: WidgetKey;
    visible: boolean;
    column: Column;
    settings: WidgetSettings;
}

export interface Layout {
    columns: ColumnsMode;
    /** Order within a column is order in this list. */
    widgets: LayoutWidget[];
}

/** The catalog default for every setting a widget declares. */
export function defaultSettings(key: WidgetKey): WidgetSettings {
    const entry = catalogEntry(key);
    const out: WidgetSettings = {};
    for (const [name, spec] of Object.entries(entry?.settings ?? {})) out[name] = defaultOf(spec);
    return out;
}

function defaultOf(spec: SettingSpec): DashboardSettingValue {
    return spec.kind === 'set' || spec.kind === 'categories' ? [...spec.default] : spec.default;
}

/**
 * A widget's settings with every value checked against the catalog: a value the
 * catalog does not allow, or a setting it does not declare, is replaced or dropped.
 */
export function resolveSettings(key: WidgetKey, stored: unknown): WidgetSettings {
    const entry = catalogEntry(key);
    const raw = stored && typeof stored === 'object' && !Array.isArray(stored)
        ? (stored as Record<string, unknown>)
        : {};
    const out: WidgetSettings = {};
    for (const [name, spec] of Object.entries(entry?.settings ?? {})) {
        out[name] = repair(spec, raw[name]);
    }
    return out;
}

function repair(spec: SettingSpec, value: unknown): DashboardSettingValue {
    switch (spec.kind) {
        case 'boolean':
            return typeof value === 'boolean' ? value : spec.default;
        case 'choice':
            return spec.options.some((o) => o.value === value) ? (value as number | string) : spec.default;
        case 'set': {
            if (!Array.isArray(value)) return [...spec.default];
            const allowed = new Set(spec.options.map((o) => o.value));
            const kept = [...new Set(value.filter((v): v is string => typeof v === 'string' && allowed.has(v)))];
            // A set showing nothing would be a widget with no visible effect.
            return kept.length > 0 ? kept : [...spec.default];
        }
        case 'categories': {
            // Ids only, each once, in the order chosen, and no more than the widget
            // shows. Whether an id still names a category is the widget's question:
            // this sees no accounts, so an unknown id is kept and ignored where it
            // is drawn. An empty list is a choice still to make, and stays empty.
            if (!Array.isArray(value)) return [...spec.default];
            const kept = [...new Set(value.filter((v): v is string => typeof v === 'string'))];
            return kept.slice(0, spec.max);
        }
        case 'account':
            // An id, or none. Whether it still names an account is the widget's
            // question, for the reason a category list's is.
            return typeof value === 'string' && value !== '' ? value : spec.default;
    }
}

function widgetOf(entry: CatalogEntry, overrides: Partial<LayoutWidget> = {}): LayoutWidget {
    return {
        id: entry.key,
        key: entry.key,
        visible: entry.defaultVisible,
        column: entry.column,
        settings: defaultSettings(entry.key),
        ...overrides,
    };
}

/** The widgets that appear once, in catalog order. A widget that appears once per
 *  subject is in no layout until a subject is chosen. */
const SINGLETONS: readonly CatalogEntry[] = CATALOG.filter((e) => e.instances === undefined);

/** The default layout: every widget that appears once, in catalog order, with its
 *  default column and visibility. */
export function defaultLayout(): Layout {
    return { columns: DEFAULT_COLUMNS, widgets: SINGLETONS.map((e) => widgetOf(e)) };
}

/** A new report tile, shown, in the report entry's column with its default settings. */
export function reportTile(id: string): LayoutWidget {
    return widgetOf(catalogEntry('report')!, { id, visible: true });
}

// ---- version 1 --------------------------------------------------------------------

/** The keys an earlier release knew, in its catalog order. */
const V1_KEYS = ['net-worth', 'accounts', 'investments', 'upcoming', 'activity', 'spending'] as const;

type V1Key = (typeof V1_KEYS)[number];

/** Where each v1 key lands. `accounts` lands hidden: v1 forced it visible, so nobody
 *  chose it (ADR-0109 D7). */
const V1_MAP: Record<V1Key, { key: WidgetKey; column: Column; hidden?: boolean }> = {
    upcoming: { key: 'bills', column: 'main' },
    spending: { key: 'budget-pace', column: 'main' },
    'net-worth': { key: 'net-worth', column: 'side' },
    investments: { key: 'investments', column: 'side' },
    activity: { key: 'activity', column: 'side' },
    accounts: { key: 'accounts', column: 'side', hidden: true },
};

/**
 * The layout an earlier release actually drew from what it stored: its saved order
 * with unknown and repeated keys dropped, then any key it omitted appended visible,
 * and `accounts` always visible. The same rules its resolver applied, so the
 * migration starts from what the user saw.
 */
function effectiveV1(widgets: readonly unknown[]): { key: V1Key; visible: boolean }[] {
    const out: { key: V1Key; visible: boolean }[] = [];
    const placed = new Set<V1Key>();
    for (const w of widgets) {
        if (!w || typeof w !== 'object') continue;
        const { key, visible } = w as { key?: unknown; visible?: unknown };
        if (typeof key !== 'string' || !(V1_KEYS as readonly string[]).includes(key)) continue;
        const k = key as V1Key;
        if (placed.has(k)) continue;
        placed.add(k);
        out.push({ key: k, visible: k === 'accounts' || visible === true });
    }
    for (const k of V1_KEYS) if (!placed.has(k)) out.push({ key: k, visible: true });
    return out;
}

function isV1Default(effective: readonly { key: V1Key; visible: boolean }[]): boolean {
    return effective.length === V1_KEYS.length
        && effective.every((w, i) => w.key === V1_KEYS[i] && w.visible);
}

/**
 * A customised v1 layout in v2: saved relative order and visibility kept, each key
 * in its column, then the widgets v1 never had inserted at fixed places with their
 * catalog visibility. Settings start at their defaults, except Bills' horizon,
 * which matches the 30 days v1 showed.
 */
function migrateV1(effective: readonly { key: V1Key; visible: boolean }[]): Layout {
    const main: LayoutWidget[] = [];
    const side: LayoutWidget[] = [];
    for (const w of effective) {
        const target = V1_MAP[w.key];
        const entry = catalogEntry(target.key)!;
        const widget = widgetOf(entry, { visible: target.hidden ? false : w.visible, column: target.column });
        if (target.key === 'bills') widget.settings = { ...widget.settings, horizonDays: 30 };
        (target.column === 'main' ? main : side).push(widget);
    }

    const insertAfter = (list: LayoutWidget[], after: WidgetKey, key: WidgetKey) => {
        const at = list.findIndex((w) => w.key === after);
        list.splice(at < 0 ? list.length : at + 1, 0, widgetOf(catalogEntry(key)!));
    };
    main.unshift(widgetOf(catalogEntry('problems')!));
    insertAfter(main, 'bills', 'review');
    insertAfter(main, 'budget-pace', 'cash-flow');
    side.push(widgetOf(catalogEntry('data-health')!));

    // Anything else this build has and v1 did not: appended to its column, hidden.
    const placed = new Set([...main, ...side].map((w) => w.key));
    for (const entry of SINGLETONS) {
        if (placed.has(entry.key)) continue;
        (entry.column === 'main' ? main : side).push(widgetOf(entry, { visible: false }));
    }
    return { columns: DEFAULT_COLUMNS, widgets: [...main, ...side] };
}

// ---- version 2 --------------------------------------------------------------------

function resolveV2(doc: Partial<DashboardPrefsV2>): Layout {
    const columns = (COLUMNS_MODES as readonly string[]).includes(doc.columns as string)
        ? (doc.columns as ColumnsMode)
        : DEFAULT_COLUMNS;

    const widgets: LayoutWidget[] = [];
    const placed = new Set<string>();
    const shown = new Map<WidgetKey, number>();
    for (const w of Array.isArray(doc.widgets) ? doc.widgets : []) {
        const entry = w && typeof w === 'object' ? catalogEntry(String(w.key)) : undefined;
        // Unknown keys are dropped.
        if (!entry) continue;
        // A widget that appears once is known by its key. A report tile is known by
        // the report its id names: an id naming none is dropped, and each report is
        // in the layout at most once. A report that no longer exists keeps its slot;
        // the tile says so.
        const ref = entry.key === 'report' ? parseReportTileId(String(w.id)) : null;
        if (entry.key === 'report' && !ref) continue;
        const id = ref ? reportTileId(ref) : entry.key;
        if (placed.has(id)) continue;
        placed.add(id);
        let visible = typeof w.visible === 'boolean' ? w.visible : entry.defaultVisible;
        // Past the most a widget may show at once, the rest are kept, hidden.
        if (visible && entry.instances !== undefined) {
            const count = shown.get(entry.key) ?? 0;
            if (count >= entry.instances) visible = false;
            else shown.set(entry.key, count + 1);
        }
        widgets.push({
            id,
            key: entry.key,
            visible,
            column: w.column === 'main' || w.column === 'side' ? w.column : entry.column,
            settings: resolveSettings(entry.key, w.settings),
        });
    }
    // A widget added to the catalog since the layout was saved: appended to its
    // column with its catalog visibility.
    for (const entry of SINGLETONS) {
        if (!placed.has(entry.key)) widgets.push(widgetOf(entry));
    }
    return { columns, widgets };
}

/**
 * Whatever GET returned, as a complete layout. A document with no `version` is the
 * layout an earlier release wrote: empty or that release's default becomes this
 * build's default, anything else is migrated. Nothing is written here; the next save
 * writes version 2.
 */
export function resolveLayout(stored: StoredDashboardPrefs | undefined | null): Layout {
    if (!stored) return defaultLayout();
    if ((stored as Partial<DashboardPrefsV2>).version === 2) return resolveV2(stored as Partial<DashboardPrefsV2>);

    const widgets = Array.isArray(stored.widgets) ? (stored.widgets as readonly unknown[]) : [];
    if (widgets.length === 0) return defaultLayout();
    const effective = effectiveV1(widgets);
    return isV1Default(effective) ? defaultLayout() : migrateV1(effective);
}

/** The document a save writes. */
export function toDocument(layout: Layout): DashboardPrefsV2 {
    return {
        version: 2,
        columns: layout.columns,
        widgets: layout.widgets.map((w) => ({
            id: w.id,
            key: w.key,
            visible: w.visible,
            column: w.column,
            settings: w.settings,
        })),
    };
}

/** Whether two layouts would save the same document. */
export function sameLayout(a: Layout, b: Layout): boolean {
    return JSON.stringify(toDocument(a)) === JSON.stringify(toDocument(b));
}

/** The visible widgets of one column, in order. */
export function columnWidgets(layout: Layout, column: Column): LayoutWidget[] {
    return layout.widgets.filter((w) => w.column === column && w.visible);
}

/**
 * The hidden widgets, in catalog order. Not in the layout's own order: a widget
 * added to the catalog since a layout was saved is appended to its end, so the
 * layout's order would list it after older ones, and the same tray would read
 * differently for a fresh layout and an upgraded one.
 */
export function hiddenWidgets(layout: Layout): LayoutWidget[] {
    // Hidden report tiles sit at the report entry's place, in the layout's order.
    return CATALOG.flatMap((e) => layout.widgets.filter((w) => w.key === e.key && !w.visible));
}

/** How many of a widget that appears once per subject are on the screen. */
export function shownCount(layout: Layout, key: WidgetKey): number {
    return layout.widgets.filter((w) => w.key === key && w.visible).length;
}

/** The layout without one widget: a report tile taken off for good. */
export function withoutWidget(layout: Layout, id: string): Layout {
    return { ...layout, widgets: layout.widgets.filter((w) => w.id !== id) };
}
