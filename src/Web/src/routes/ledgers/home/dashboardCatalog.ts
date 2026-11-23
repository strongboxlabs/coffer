// The home screen's widget catalog: the one place that knows which widgets exist,
// what they are called, where they go by default, and what each setting may hold.
//
// The API stores the layout and checks only its structure (ADR-0057, the dashboard
// exception): it knows no key, column or setting. Everything a layout means is
// resolved here, so a new widget or setting needs no API change, and a stored value
// this catalog does not recognise falls back to its default rather than failing.

export type Column = 'main' | 'side';

export type ColumnsMode = 'wide-narrow' | 'equal' | 'single';

export const COLUMNS_MODES: readonly ColumnsMode[] = ['wide-narrow', 'equal', 'single'];

export const DEFAULT_COLUMNS: ColumnsMode = 'wide-narrow';

/**
 * A setting the inline panel edits: an on/off, a pick from fixed values, a set of
 * fixed values, the ledger's own categories, chosen one at a time up to `max`, or
 * one of the accounts the widget shows, or none. A `set` always keeps one option
 * (showing nothing would be a widget with no visible effect); a `categories` list may
 * be empty, which the widget says; an `account` of null is the widget's own choice of
 * `noneLabel`, and its options are the widget's data (WidgetPanel's `choices`).
 */
export type SettingSpec =
    | { kind: 'boolean'; label: string; default: boolean }
    | { kind: 'choice'; label: string; default: number | string; options: readonly ChoiceOption[] }
    | { kind: 'set'; label: string; default: readonly string[]; options: readonly ChoiceOption[] }
    | { kind: 'categories'; label: string; default: readonly string[]; max: number }
    | { kind: 'account'; label: string; default: null; noneLabel: string };

export interface ChoiceOption {
    value: number | string;
    label: string;
}

export interface CatalogEntry {
    key: WidgetKey;
    title: string;
    /** One line for the hidden-widgets tray. */
    description: string;
    column: Column;
    defaultVisible: boolean;
    settings: Readonly<Record<string, SettingSpec>>;
    /**
     * The widget shows nothing until a subject is chosen (Watchlist's categories),
     * so the tray's Add opens its settings first, and cancelling them adds nothing.
     */
    subjectFirst?: boolean;
    /**
     * The widget appears once per subject rather than once (a report tile, one per
     * report), at most this many shown at a time. Its id names the subject, and it
     * is in no layout until one is chosen.
     */
    instances?: number;
}

export const WIDGET_KEYS = [
    'problems',
    'bills',
    'review',
    'budget-pace',
    'cash-flow',
    'recent-transactions',
    'data-health',
    'net-worth',
    'investments',
    'watchlist',
    'activity',
    'accounts',
    'report',
    'cash-forecast',
] as const;

export type WidgetKey = (typeof WIDGET_KEYS)[number];

const n = (value: number, label = String(value)): ChoiceOption => ({ value, label });

/** The most report tiles the home screen shows at a time (ADR-0104 D2, as amended). */
export const REPORT_TILES_MAX = 6;

/** In catalog order: the default document lists each column in this order. */
export const CATALOG: readonly CatalogEntry[] = [
    {
        key: 'problems',
        title: 'Problems',
        description: 'Feeds, checks and jobs that need fixing.',
        column: 'main',
        defaultVisible: true,
        settings: {
            collapseWhenClear: { kind: 'boolean', label: 'Collapse to one line when clear', default: true },
        },
    },
    {
        key: 'bills',
        title: 'Bills & income',
        description: 'Reminders due, overdue and coming up, to post or skip.',
        column: 'main',
        defaultVisible: true,
        settings: {
            horizonDays: {
                kind: 'choice', label: 'Look ahead', default: 14,
                options: [n(7, '7 days'), n(14, '14 days'), n(30, '30 days')],
            },
            rows: { kind: 'choice', label: 'Rows', default: 8, options: [n(5), n(8), n(12)] },
            includeIncome: { kind: 'boolean', label: 'Include income', default: true },
            showPosted: { kind: 'boolean', label: 'Keep posted and skipped rows', default: false },
        },
    },
    {
        key: 'review',
        title: 'To review',
        description: 'Imported rows waiting for review, newest first.',
        column: 'main',
        defaultVisible: true,
        settings: {
            rows: { kind: 'choice', label: 'Rows', default: 5, options: [n(5), n(8), n(12), n(20)] },
            groupByAccount: { kind: 'boolean', label: 'Group by account', default: false },
        },
    },
    {
        key: 'budget-pace',
        title: 'This month',
        description: "This month's spending against the mark.",
        column: 'main',
        defaultVisible: true,
        settings: {
            windowMonths: {
                kind: 'choice', label: 'Typical over', default: 3,
                options: [n(3, '3 months'), n(6, '6 months'), n(12, '12 months')],
            },
            overRows: {
                kind: 'choice', label: 'Over-the-mark rows', default: 3,
                options: [n(0, 'None'), n(3), n(5)],
            },
        },
    },
    {
        key: 'cash-flow',
        title: 'Cash flow',
        description: 'Income against expenses, month by month.',
        column: 'main',
        defaultVisible: true,
        settings: {
            months: {
                kind: 'choice', label: 'Months', default: 12,
                options: [n(6), n(12), n(24)],
            },
            showTable: { kind: 'boolean', label: 'Show the table', default: true },
        },
    },
    {
        key: 'recent-transactions',
        title: 'Recent transactions',
        description: 'The latest transactions across every account.',
        column: 'main',
        defaultVisible: false,
        settings: {
            rows: { kind: 'choice', label: 'Rows', default: 10, options: [n(5), n(10), n(20)] },
            direction: {
                kind: 'choice', label: 'Show', default: 'all',
                options: [
                    { value: 'all', label: 'All' },
                    { value: 'outflow', label: 'Outflows' },
                    { value: 'inflow', label: 'Inflows' },
                ],
            },
        },
    },
    {
        key: 'data-health',
        title: 'Feeds & checks',
        description: 'Bank feeds, prices, auto-post, backups and checks, with when each last ran.',
        column: 'side',
        defaultVisible: true,
        settings: {},
    },
    {
        key: 'net-worth',
        title: 'Net worth',
        description: 'Net worth today, its change, and its month-ends.',
        column: 'side',
        defaultVisible: true,
        settings: {
            months: { kind: 'choice', label: 'Months', default: 12, options: [n(6), n(12), n(24)] },
            showSplit: { kind: 'boolean', label: 'Show assets and liabilities', default: true },
        },
    },
    {
        key: 'investments',
        title: 'Investments',
        description: 'Holdings, cash, return and allocation.',
        column: 'side',
        defaultVisible: true,
        settings: {
            allocation: {
                kind: 'choice', label: 'Allocation', default: 'asset_class',
                options: [
                    { value: 'asset_class', label: 'Asset class' },
                    { value: 'region', label: 'Region' },
                    { value: 'vehicle', label: 'Vehicle' },
                    { value: 'account', label: 'Account' },
                    { value: 'off', label: 'Off' },
                ],
            },
            returnWindow: {
                kind: 'choice', label: 'Return over', default: 'ytd',
                options: [
                    { value: 'ytd', label: 'Year to date' },
                    { value: '1y', label: '1 year' },
                    { value: 'inception', label: 'Since inception' },
                ],
            },
        },
    },
    {
        key: 'watchlist',
        title: 'Watchlist',
        description: 'Chosen categories against their marks this month.',
        column: 'side',
        defaultVisible: false,
        subjectFirst: true,
        settings: {
            categoryIds: { kind: 'categories', label: 'Categories', default: [], max: 8 },
            sort: {
                kind: 'choice', label: 'Sort', default: 'chosen',
                options: [
                    { value: 'chosen', label: 'Chosen order' },
                    { value: 'most-over', label: 'Most over' },
                ],
            },
        },
    },
    {
        key: 'activity',
        title: 'Imports & refreshes',
        description: 'The latest imports, syncs and price refreshes.',
        column: 'side',
        defaultVisible: false,
        settings: {
            rows: { kind: 'choice', label: 'Rows', default: 5, options: [n(5), n(10)] },
            families: {
                kind: 'set', label: 'Show', default: ['ingest', 'quote'],
                options: [
                    { value: 'ingest', label: 'Imports and syncs' },
                    { value: 'quote', label: 'Price refreshes' },
                ],
            },
        },
    },
    {
        key: 'accounts',
        title: 'Accounts',
        description: 'Every account by type, with balances.',
        column: 'side',
        defaultVisible: false,
        settings: {
            includeInactive: { kind: 'boolean', label: 'Include closed accounts', default: false },
        },
    },
    {
        key: 'report',
        title: 'Report',
        description: 'Any saved or catalog report, at a fixed height.',
        column: 'main',
        defaultVisible: false,
        instances: REPORT_TILES_MAX,
        settings: {
            // In the order Reports offers its own View.
            view: {
                kind: 'choice', label: 'View', default: 'chart',
                options: [{ value: 'table', label: 'Table' }, { value: 'chart', label: 'Chart' }],
            },
            height: {
                kind: 'choice', label: 'Height', default: 'compact',
                options: [{ value: 'compact', label: 'Compact' }, { value: 'tall', label: 'Tall' }],
            },
        },
    },
    {
        key: 'cash-forecast',
        title: 'Cash ahead',
        description: 'Lowest projected balance per account.',
        column: 'side',
        defaultVisible: false,
        settings: {
            days: {
                kind: 'choice', label: 'Look ahead', default: 30,
                options: [n(14, '14 days'), n(30, '30 days'), n(60, '60 days')],
            },
            focusAccountId: { kind: 'account', label: 'Account', default: null, noneLabel: 'Combined' },
        },
    },
];

const byKey = new Map<string, CatalogEntry>(CATALOG.map((e) => [e.key, e]));

/** The catalog entry for a stored key, or undefined for a key this build does not know. */
export function catalogEntry(key: string): CatalogEntry | undefined {
    return byKey.get(key);
}

/**
 * The report a tile shows, as its widget id names it: `report:saved:<uuid>` for a
 * saved report, `report:catalog:<key>` for one of the catalog's. The id is the
 * reference; nothing else in the layout says which report a tile is.
 */
export type ReportTileRef = { kind: 'saved'; id: string } | { kind: 'catalog'; key: string };

const SAVED_TILE = /^report:saved:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const CATALOG_TILE = /^report:catalog:([a-z0-9][a-z0-9-]*)$/;

/** The report a tile's id names, or null for an id that names none. */
export function parseReportTileId(id: string): ReportTileRef | null {
    const saved = SAVED_TILE.exec(id);
    if (saved) return { kind: 'saved', id: saved[1]!.toLowerCase() };
    const catalog = CATALOG_TILE.exec(id);
    return catalog ? { kind: 'catalog', key: catalog[1]! } : null;
}

/** A tile's widget id for a report. */
export function reportTileId(ref: ReportTileRef): string {
    return ref.kind === 'saved' ? `report:saved:${ref.id.toLowerCase()}` : `report:catalog:${ref.key}`;
}

/** A widget has settings only when its catalog entry declares some. */
export function hasSettings(key: WidgetKey): boolean {
    return Object.keys(byKey.get(key)?.settings ?? {}).length > 0;
}
