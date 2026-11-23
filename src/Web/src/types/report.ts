// Reports (FU-008, ADR-0104). Mirrors the API's ReportSpec / ReportResult and
// the saved_reports row.
//
// Every summary figure that combines accounts is in the ledger's reporting currency
// (the result's `currencyCode`), each posting converted at its own date's rate
// (ADR-0114 D7): the total, the period totals, the rest, and every row and group total
// but an account's. A row or group total grouped by account is that account's own
// figure, in its own currency, unconverted; every row and group total names its
// currency in its own `currencyCode`. A converted figure that needs a missing rate is
// null, and the row or total that carries it names each missing pair and day in
// `rateUnavailable`, which is absent when nothing is missing (ADR-0114 D9).

import type { RateUnavailable } from '@/lib/types/conversion';

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
    /**
     * Which report this spec belongs to. ABSENT means the transaction
     * aggregation, so every row saved before a second report existed keeps
     * reading as it was written; only a different report carries a marker.
     * On a `returns` spec `measure` and `groupBy` are inert placeholders (the
     * type needs them), and `bucket` is the interval: Month, Quarter or Year.
     */
    kind?: ReportKind;
    /** Returns only: the chart's measure, the index or the gain. Client-only, like {@link view}. */
    returnsShow?: ReturnsShow;
    /** Returns only: one account's own series. Omitted, the ledger (and each account, see below). */
    accountId?: string;
    /** Returns only: also draw one line per account. Omitted means yes. */
    includeAccounts?: boolean;
    /**
     * Returns only: the benchmark symbol to draw beside the series (ADR-0114 D1), one
     * of the ledger's benchmarks when picked. Absent or null draws none. Sent as the
     * `benchmark` query parameter and stored with a save; the server checks only its
     * shape (a symbol such as VT), so a saved spec keeps a symbol later removed from
     * the ledger, and the result then says `benchmark_not_in_ledger`. Kept while the
     * Gain view hides the control.
     */
    benchmark?: string | null;
}

/** The report a spec describes. Only `returns` is marked; the aggregation is the absence. */
export type ReportKind = 'returns';

/** The Returns chart's measure: time-weighted index on base 100, or cumulative gain in money. */
export type ReturnsShow = 'index' | 'gain';

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
    /** The group's own name: for a category, only the last of `groupPath`. */
    groupName: string;
    /**
     * The group's full name: a category's names from its top-level category down
     * to its own (`['Salary', 'Base']`), the one name for any other dimension. The
     * view decides how much of it to show (groupNameParts).
     */
    groupPath: string[];
    parentId: string | null;
    /** In `currencyCode`. Null when a posting needs a missing rate; never for an account's row. */
    amount: number | null;
    /** The account's own currency for a row grouped by account; else the reporting currency. */
    currencyCode: string;
    /**
     * Whether a chart draws this row as a series. Set only when the request
     * carried `topN`: the leaves of the kept rows, and each direct row below.
     * A rolled-up kept parent is not drawn, because its children and its
     * direct row already are.
     */
    drawn?: boolean | null;
    /**
     * A rolled-up kept parent's own postings, apart from its children: one
     * extra row per period, carrying the parent's group and only its direct
     * amount, so the browser never subtracts children from a parent. Drawn as
     * a series of its own.
     */
    isDirect?: boolean | null;
    rateUnavailable?: RateUnavailable[];
}

/**
 * What top-N did not keep, as one figure: the groups past the cut, counted,
 * and their amounts per period. Per period, the drawn rows plus `byPeriod`
 * equal `periodTotals`, so a chart's Other segment is the server's figure and
 * never a sum the browser made.
 *
 * Its figures are converted as every row is; `total` is null when it needs a
 * missing rate, named in `rateUnavailable`.
 */
export interface ReportRest {
    count: number;
    total: number | null;
    byPeriod: ReportPeriodTotal[] | null;
    /**
     * The one group folded, when `count` is 1, so the fold can be named rather
     * than counted. A rolled-up parent's own postings fold as `isDirect`.
     */
    only?: ReportRestGroup | null;
    rateUnavailable?: RateUnavailable[];
}

export interface ReportRestGroup {
    groupId: string;
    groupName: string;
    /** As `ReportRow.groupPath`. */
    groupPath: string[];
    isDirect: boolean;
}

/**
 * `total` is the server's total, NOT the sum of `rows`. Under rollup a parent
 * row already contains its children, and under top-N rows were cut, so adding
 * the rows up double-counts or falls short. For every dimension but Tag it is
 * the sum of the underlying postings; a Tag result counts a posting once per tag
 * it carries, so its total is the sum of the tag rows.
 */
export interface ReportResult {
    rows: ReportRow[];
    /** Null when a posting needs a missing rate; `rateUnavailable` names every one. */
    total: number | null;
    /**
     * The RESOLVED window the server reported over — a sliding range worked
     * out against the user's today. Null when open-ended. The table draws
     * its columns from this, not from the rows: a period with no postings
     * produces no row, so taking the axis from the rows dropped empty months
     * silently and made adjacent columns look like adjacent months.
     */
    fromUtc: string | null;
    toUtc: string | null;
    /**
     * One total per period, on the same footing as `total`: every period of the
     * window, zero where nothing posted, when the window has both bounds. Empty
     * with no time bucket. A column's height comes from here, never from rows.
     */
    periodTotals: ReportPeriodTotal[];
    /** One total per group across the window, whether or not top-N kept its rows. */
    groupTotals: ReportGroupTotal[];
    /** The ledger's reporting currency: the total, the period totals and the rest are in
     *  it, and so is every row but an account's, which names its own. */
    currencyCode: string;
    /** Present only when the request carried `topN`. */
    rest?: ReportRest | null;
    rateUnavailable?: RateUnavailable[];
}

export interface ReportPeriodTotal {
    period: string;
    /** Null when a posting in the period needs a missing rate. */
    amount: number | null;
    rateUnavailable?: RateUnavailable[];
}

export interface ReportGroupTotal {
    groupId: string | null;
    groupName: string;
    /** As `ReportRow.groupPath`. */
    groupPath: string[];
    /** In `currencyCode`. Null when a posting of the group needs a missing rate. */
    amount: number | null;
    /** As the group's rows: the account's own for a group by account, else the reporting currency. */
    currencyCode: string;
    rateUnavailable?: RateUnavailable[];
}

/** Why a returns figure is null. The strings the API sends. */
export type ReturnsReason =
    | 'before_first_funding'
    | 'no_invested_base'
    | 'negative_cumulative_growth'
    /** A figure needs an exchange rate the ledger does not hold (ADR-0114 D9). */
    | 'rate_unavailable'
    /** The benchmark asked for is not one of the ledger's benchmarks (ADR-0114 D1). */
    | 'benchmark_not_in_ledger'
    /** The benchmark has no stored growth for a day the point needs (ADR-0114 D3). */
    | 'benchmark_unavailable'
    | 'prior_index_zero'
    | 'under_a_year_covered'
    | 'too_few_flows'
    | 'single_signed_flows'
    | 'zero_length_window'
    | 'indeterminate'
    | 'no_root_in_range';

/**
 * One period end. `index` is on a base of 100 (112.4 is +12.4%, not annualised);
 * `periodReturn` and `mwrTrailingYear` are decimal fractions (one hundredth is 1%). A null
 * figure carries its reason, and is never a zero nor the last value carried on.
 *
 * `mwrTrailingYear` is the money-weighted return of the 12 months ending at the point,
 * read from history before the range where the year reaches back past it.
 *
 * A point that needs a missing exchange rate is null in every figure, each reason
 * `rate_unavailable`, and `rateUnavailable` names each pair and day. A rate missing only
 * for the year before the range leaves `mwrTrailingYear` alone null that way, and
 * `rateUnavailable` names it. It is empty on every other point.
 *
 * `benchmark` is the requested benchmark's total return on the same base as
 * `index`: 100 at the series' first invested point, paused where the series has no
 * invested base, converted into the series' currency (ADR-0114 D3). It arrives
 * chained and rebased; no client chains or rebases it. With no benchmark asked for,
 * it and its reason are null. A null benchmark otherwise carries
 * `benchmarkUnavailableReason`: the index's own (`before_first_funding`,
 * `no_invested_base`, `rate_unavailable`), `benchmark_not_in_ledger`,
 * `benchmark_unavailable` with `benchmarkUnavailableDate`, or `rate_unavailable`
 * for the benchmark's own conversion with `benchmarkRateUnavailable`.
 */
export interface ReturnsSeriesPoint {
    periodEnd: string;
    index: number | null;
    indexUnavailableReason: ReturnsReason | null;
    periodReturn: number | null;
    periodReturnUnavailableReason: ReturnsReason | null;
    coveredDays: number;
    gain: number | null;
    gainUnavailableReason: ReturnsReason | null;
    mwrTrailingYear: number | null;
    mwrUnavailableReason: ReturnsReason | null;
    rateUnavailable: RateUnavailable[];
    benchmark: number | null;
    benchmarkUnavailableReason: ReturnsReason | null;
    /** The first day, `yyyy-MM-dd`, with no stored growth; set only with
     *  `benchmark_unavailable`. */
    benchmarkUnavailableDate: string | null;
    benchmarkRateUnavailable: RateUnavailable[];
}

/**
 * One line. The ledger line is in the ledger's reporting currency, converted and
 * unhedged (ADR-0114 D7); each account's own line stays in its own currency.
 */
export interface ReturnsSeries {
    scope: 'ledger' | 'account';
    accountId: string | null;
    accountName: string | null;
    currencyCode: string;
    points: ReturnsSeriesPoint[];
}

/**
 * The benchmark a series was asked to compare with: the symbol as asked, and, when
 * it is one of the ledger's benchmarks, the fund's name and the currency its total
 * return is quoted in. When it is not, `name` and `currencyCode` are null and
 * `unavailableReason` is `benchmark_not_in_ledger`.
 */
export interface ReturnsBenchmark {
    symbol: string;
    name: string | null;
    currencyCode: string | null;
    unavailableReason: ReturnsReason | null;
}

export interface ReturnsSeriesResult {
    from: string;
    to: string;
    interval: 'month' | 'quarter' | 'year';
    series: ReturnsSeries[];
    /** Null when the request named no benchmark. */
    benchmark: ReturnsBenchmark | null;
    computedAt: string;
    engineVersion: string;
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
