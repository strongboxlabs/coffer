// Reports (FU-008, ADR-0104). Mirrors the API's ReportSpec / ReportResult and
// the saved_reports row.

/** Spending, Income, or Net (income − spending). */
export type ReportMeasure = 'Spending' | 'Income' | 'Net';

/** `None` collapses the time axis to a single total per group. */
export type ReportTimeBucket = 'None' | 'Day' | 'Month' | 'Quarter' | 'Year';

export type ReportGroupBy = 'Category' | 'Account' | 'Payee' | 'Tag' | 'CategoryKind';

/**
 * `Sliding` resolves against the moment the report RUNS, server-side.
 * "Last 12 months" means the last twelve from today; a fixed range that was
 * once correct returns a progressively staler answer while looking like it
 * worked (ADR-0104 D5).
 */
export type ReportDateMode = 'Static' | 'Sliding';

export type ReportWindowUnit = 'Day' | 'Month' | 'Quarter' | 'Year';

/**
 * Everything that defines a report. Sent to the API as a query string and
 * stored verbatim on a saved report, so the two can never disagree about what
 * a report is.
 */
export interface ReportSpec {
    measure: ReportMeasure;
    groupBy: ReportGroupBy;
    bucket: ReportTimeBucket;
    dateMode: ReportDateMode;
    /** Required when `dateMode` is `Sliding`. */
    windowCount?: number;
    windowUnit?: ReportWindowUnit;
    /**
     * Snap a sliding window's start to a period boundary. On, "12 months" is
     * twelve WHOLE months; off, it is exactly twelve months back from this
     * instant, which draws thirteen columns with two partial ones.
     */
    windowAlign?: boolean;
    /** Only when `dateMode` is `Static`. ISO-8601. */
    from?: string;
    to?: string;
    tagId?: string;
    rollup?: boolean;
    topN?: number;
    /**
     * Table or Chart (ADR-0104 D4): "the toggle state is part of what a save
     * records", so it lives on the SPEC and not in component state.
     *
     * CLIENT-ONLY. `specToQuery` does not send it, because the server computes
     * the same numbers either way and a view preference is not a query
     * parameter. It survives a save because a saved report stores the spec as
     * jsonb, so no migration and no server change — an older saved row simply
     * has no `view` and reads as the table it was written as.
     */
    view?: ReportViewMode;
    /**
     * Stacked or grouped, on the time-series shape only (ADR-0104 D4/D6).
     *
     * The one place a real choice exists, so the one place with a control.
     * Stacked answers "what did this period cost, and of what"; grouped
     * answers "how did this series move across periods". Saved with the spec
     * like {@link view}, and client-only for the same reason — the server
     * computes the same numbers either way.
     */
    chartStack?: ReportChartStack;
}

/** How a report is drawn. The default is the table, which is checkable. */
export type ReportViewMode = 'table' | 'chart';

/** Bars side by side, or accumulated. Only meaningful over a time axis. */
export type ReportChartStack = 'stacked' | 'grouped';

/**
 * One cell. `period` is null when the spec asked for no time axis; otherwise
 * `2026-05`, `2026-Q2` or `2026`. `groupId` is null for the payee dimension and
 * for the untagged bucket.
 */
export interface ReportRow {
    period: string | null;
    groupId: string | null;
    groupName: string;
    parentId: string | null;
    amount: number;
}

/**
 * `total` is the true sum of the underlying postings, NOT the sum of `rows`.
 * Under rollup a parent row already contains its children, and under the tag
 * dimension a posting can appear beneath more than one tag — in both cases
 * adding the rows up double-counts.
 */
export interface ReportResult {
    rows: ReportRow[];
    total: number;
    /**
     * The RESOLVED window the server reported over — a sliding range worked
     * out against the instant it ran. Null when open-ended. The table draws
     * its columns from this, not from the rows: a period with no postings
     * produces no row, so taking the axis from the rows dropped empty months
     * silently and made adjacent columns look like adjacent months.
     */
    fromUtc: string | null;
    toUtc: string | null;
}

export type SavedReportVisibility = 'private' | 'ledger';

export interface SavedReport {
    id: string;
    name: string;
    spec: ReportSpec;
    visibility: SavedReportVisibility;
    ownerUserId: string;
    createdAt: string;
    updatedAt: string;
}

/**
 * One entry in the sidebar's pinned list. Carries the name because a pin row
 * has none of its own — the rail renders labels.
 */
export interface PinnedReport {
    /** Set for a memorized report. Exactly one of this and `catalogKey`. */
    savedReportId: string | null;
    /** Set for a canned report; the client resolves name and spec from CATALOG. */
    catalogKey: string | null;
    /** Null for a catalog pin. */
    name: string | null;
    /**
     * A saved report's spec, carried so a rail link can expand the whole
     * question into the URL. With only an id the view would open its DEFAULT
     * spec under a pinned name, which is the same lie as a stale title. Null
     * for a catalog pin, whose spec comes from CATALOG.
     */
    spec: ReportSpec | null;
    sortOrder: number;
}
