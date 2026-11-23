import { useMemo } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchReportSummary, specToQuery } from '@/lib/api';
import { Checkbox } from '@/components/ui/Checkbox';
import { SegmentedControl, type SegmentedOption } from '@/components/ui/SegmentedControl';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import type {
    ReportChartStack, ReportGroupBy, ReportMeasure, ReportResult, ReportRow, ReportSpec,
    ReportTimeBucket, ReportViewMode,
} from '@/types/report';

import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';
import { LedgerConversionNote } from '@/components/LedgerConversionNote';
import type { RateUnavailable } from '@/lib/types';

import { ReportChart } from './ReportChart';
import { chartTopN, groupKey } from './chartSeries';
import { GROUP_LABELS } from './groupLabel';
import { AGGREGATION_REPORT_NAME } from './catalog';
import { RangePicker, describeRange } from './RangePicker';
import { crossCurrencyChartText, currencyRules } from './reportCurrency';
import { Select } from './ReportSelect';
import { ReportTable } from './ReportTable';
import { ReturnsReportView } from './ReturnsReportView';
import { SaveReportControl } from './SaveReportControl';
import { NO_SYSTEM_IDS, useChartSystemIds } from './useChartSystemIds';

/**
 * One report, as a table (FU-008, ADR-0104 D4).
 *
 * THE SPEC LIVES IN THE URL. Every field is a scalar and the summary endpoint
 * is a GET, so a report someone is looking at is a link they can send — and
 * the Back button moves between questions rather than losing them. `saved`
 * identifies which memorized report Save would update; it does not supply the
 * spec, so there is one source of truth on screen and not two.
 *
 * THE CONTROLS ARE A BAR, NOT A DIALOG. Moneydance puts report settings in a
 * modal; that is the piece of its model worth dropping. A report you are
 * reading is a question you are adjusting, and a dialog hides the answer you
 * are adjusting against on every adjustment.
 */

export interface ReportSearch extends Partial<ReportSpec> {
    saved?: string;
    name?: string;
}

const MEASURES: readonly ReportMeasure[] = ['Spending', 'Income', 'Net'];

/**
 * The UI says EXPENSES; the API enum says Spending.
 *
 * One word per thing, and the rest of the app already chose: category kinds
 * are expense/income on the Categories screen, in the importer and in the
 * database. "Spending" appeared only here, so a reader met two names for one
 * idea on a single screen — the section header said one and the card said the
 * other. The enum is a contract and is left alone; this is the label.
 */
const MEASURE_LABELS: Readonly<Record<ReportMeasure, string>> = {
    Spending: 'Expenses',
    Income: 'Income',
    // "Both", not "Net" and not "All".
    //
    // "All" would overclaim: this report only ever reads CATEGORY postings, so
    // every account-to-account transfer is excluded by construction, and a
    // measure called All promises otherwise. "Net" is true of the TOTAL and
    // false of every row — a Groceries row is not netted against anything, it
    // is groceries shown negative. "Both" says which of the two kinds are
    // included, which is the only thing the measure actually decides, and it
    // reads as the union of the two options above it.
    Net: 'Both',
};

/** Top-N choices. `undefined` means no cap. */
const TOP_N: readonly { value: string; label: string; n?: number }[] = [
    { value: 'all', label: 'All' },
    { value: '10', label: 'Top 10', n: 10 },
    { value: '20', label: 'Top 20', n: 20 },
    { value: '50', label: 'Top 50', n: 50 },
];
const GROUPS: readonly ReportGroupBy[] = ['Category', 'Account', 'Payee', 'Tag', 'CategoryKind'];

const BUCKETS: readonly ReportTimeBucket[] = ['None', 'Month', 'Quarter', 'Year'];

const VIEW_OPTIONS: readonly SegmentedOption<ReportViewMode>[] = [
    { value: 'table', label: 'Table' },
    { value: 'chart', label: 'Chart' },
];
const STACK_OPTIONS: readonly SegmentedOption<ReportChartStack>[] = [
    { value: 'stacked', label: 'Stacked' },
    { value: 'grouped', label: 'Grouped' },
];

/** Fill the gaps so the page always has a complete, runnable spec. */
export function specFromSearch(search: ReportSearch): ReportSpec {
    return {
        measure: search.measure ?? 'Spending',
        groupBy: search.groupBy ?? 'Category',
        bucket: search.bucket ?? 'Month',
        dateMode: search.dateMode ?? 'Sliding',
        windowCount: search.windowCount ?? (search.dateMode === 'Static' ? undefined : 12),
        windowUnit: search.windowUnit ?? 'Month',
        windowAlign: search.windowAlign ?? true,
        from: search.from,
        to: search.to,
        tagId: search.tagId,
        rollup: search.rollup ?? false,
        topN: search.topN,
        // Table by default. The table is what makes the numbers checkable, and
        // a report that opens as a picture asks to be believed before it can
        // be verified (ADR-0104 D4).
        view: search.view ?? 'table',
        // Stacked by default: it is what a time axis is usually asked, and it
        // carries every series where grouped runs out of width.
        chartStack: search.chartStack ?? 'stacked',
    };
}

/**
 * Whether this report has a second view worth offering.
 *
 * ADR-0104 D4: "Reports with only one sensible view do not render the toggle." A chart
 * of nothing is not a view, so an empty result keeps the toggle away rather
 * than offering a blank frame.
 */
export function hasChartView(rows: readonly PivotRow[]): boolean {
    return rows.length > 0;
}

/**
 * The columns, ascending — EVERY period in range, not only the ones with
 * postings.
 *
 * Taking the axis from the rows drops a month with no activity entirely: the
 * gap reads as no column rather than as zero, and the columns either side of
 * it then look adjacent when they are not. A quiet month is a fact about the
 * ledger and belongs on the chart.
 *
 * Falls back to the periods present when the range is open-ended, which is the
 * one case where nothing says how far the axis should run.
 */
export function periodsOf(
    rows: readonly ReportRow[],
    bucket: ReportTimeBucket,
    fromUtc: string | null,
    toUtc: string | null,
): string[] {
    if (bucket === 'None') return [];

    const present = new Set<string>();
    for (const r of rows) if (r.period) present.add(r.period);

    if (fromUtc === null || toUtc === null) return [...present].sort();

    const out: string[] = [];
    const end = new Date(toUtc);
    const cursor = new Date(fromUtc);
    // Guarded: a malformed range must not spin. 3660 is ten years of days,
    // which is past any axis a person reads.
    // The end is exclusive: a window ending at midnight on the 1st has no
    // column for that month.
    for (let i = 0; cursor < end && i < 3660; i++) {
        out.push(labelFor(cursor, bucket));
        advance(cursor, bucket);
    }
    // Anything the server labelled that the walk did not produce still shows,
    // rather than a figure vanishing because a boundary was computed one way
    // here and another way there.
    for (const p of present) if (!out.includes(p)) out.push(p);
    return [...new Set(out)].sort();
}

function labelFor(at: Date, bucket: ReportTimeBucket): string {
    const y = at.getUTCFullYear();
    const m = at.getUTCMonth() + 1;
    if (bucket === 'Year') return String(y);
    if (bucket === 'Quarter') return `${y}-Q${Math.floor((m - 1) / 3) + 1}`;
    if (bucket === 'Day') {
        return `${y}-${String(m).padStart(2, '0')}-${String(at.getUTCDate()).padStart(2, '0')}`;
    }
    return `${y}-${String(m).padStart(2, '0')}`;
}

function advance(at: Date, bucket: ReportTimeBucket): void {
    if (bucket === 'Day') at.setUTCDate(at.getUTCDate() + 1);
    else if (bucket === 'Quarter') at.setUTCMonth(at.getUTCMonth() + 3);
    else if (bucket === 'Year') at.setUTCFullYear(at.getUTCFullYear() + 1);
    else at.setUTCMonth(at.getUTCMonth() + 1);
}

export interface PivotRow {
    key: string;
    name: string;
    /** The server's `groupPath`: the group's full name, top-level category first. */
    path: readonly string[];
    /** The group's id: null for the payee dimension and the untagged bucket. */
    groupId: string | null;
    parentId: string | null;
    /** 0 = top level. Carried so ordering and indentation cannot disagree. */
    depth: number;
    /** The currency the row's figures are in: an account's own, else the reporting one. */
    currencyCode: string;
    /** Null where a posting of the period needs a missing rate (ADR-0114 D9). */
    byPeriod: Map<string, number | null>;
    /** Null when a posting needs a missing rate. */
    total: number | null;
    /** Every missing rate the row's figures needed. */
    rateUnavailable: RateUnavailable[];
    /** Whether a chart draws it; undefined when the server did not say (no `topN`). */
    drawn?: boolean;
}

/** A row a chart can draw: every figure present. */
export type ChartRow = Omit<PivotRow, 'byPeriod' | 'total'> & {
    byPeriod: Map<string, number>;
    total: number;
};

/** The rows, when every figure is present; null when one needs a missing rate. */
export function chartRowsOf(rows: readonly PivotRow[]): ChartRow[] | null {
    const out: ChartRow[] = [];
    for (const r of rows) {
        if (r.total === null || [...r.byPeriod.values()].some((v) => v === null)) return null;
        out.push(r as ChartRow);
    }
    return out;
}

/** Part by part, so a longer path sorts after its own prefix and the separator never competes with letters. */
function byPath(a: PivotRow, b: PivotRow): number {
    const n = Math.min(a.path.length, b.path.length);
    for (let i = 0; i < n; i++) {
        const c = a.path[i]!.localeCompare(b.path[i]!);
        if (c !== 0) return c;
    }
    return a.path.length - b.path.length || a.name.localeCompare(b.name);
}

/** A sum that is null when either side is: a missing rate is never added as zero. */
function plus(a: number | null, b: number | null): number | null {
    return a === null || b === null ? null : a + b;
}

/**
 * Collapse cells into one row per group, ordered as a tree.
 *
 * ARBITRARY DEPTH. This walked exactly two levels — roots, then their direct
 * children — and everything deeper fell through to a catch-all that appended
 * it at the END, styled as a child. That put every deeper category under
 * whichever parent happened to be last, which is
 * what it looked like: one category with a run of categories from entirely
 * different branches beneath it. The server rolls up the whole ancestor chain,
 * so the client has no business assuming two levels.
 *
 * DEPTH IS COMPUTED HERE AND ONLY HERE. Two definitions of "top level" — this
 * function treating a row whose parent is absent as a root, a renderer checking
 * `parentId === null` — would order a category whose parent has no postings at
 * the top and draw it as a child. One number on the row makes that impossible.
 */
export function pivot(rows: readonly ReportRow[]): PivotRow[] {
    const byKey = new Map<string, PivotRow>();
    for (const r of rows) {
        const key = groupKey(r.groupId, r.groupName);
        let row = byKey.get(key);
        if (!row) {
            row = {
                key, name: r.groupName, path: r.groupPath, groupId: r.groupId, parentId: r.parentId,
                depth: 0, currencyCode: r.currencyCode, byPeriod: new Map(), total: 0, rateUnavailable: [],
            };
            byKey.set(key, row);
        }
        if (typeof r.drawn === 'boolean') row.drawn = (row.drawn ?? false) || r.drawn;
        if (r.period) row.byPeriod.set(r.period, plus(row.byPeriod.has(r.period) ? row.byPeriod.get(r.period)! : 0, r.amount));
        row.total = plus(row.total, r.amount);
        if (r.rateUnavailable) row.rateUnavailable.push(...r.rateUnavailable);
    }

    // A parent outside the result set is not a parent HERE: its child is the
    // shallowest thing shown, so it is drawn at the top level.
    const children = new Map<string | null, PivotRow[]>();
    for (const row of byKey.values()) {
        const parent = row.parentId !== null && byKey.has(row.parentId) ? row.parentId : null;
        const bucket = children.get(parent);
        if (bucket) bucket.push(row);
        else children.set(parent, [row]);
    }
    // By the whole path, which is what a top-level subcategory shows: by its own
    // name it would sort among the roots by a word the reader does not see first.
    // Siblings under one parent share every part but the last, so for them this is
    // their own names' order.
    for (const bucket of children.values()) bucket.sort(byPath);

    const ordered: PivotRow[] = [];
    const seen = new Set<string>();
    const walk = (parentKey: string | null, depth: number) => {
        for (const row of children.get(parentKey) ?? []) {
            // A cycle in the category graph would otherwise recurse forever.
            // The server guards its own walk at 100; this guards the render.
            if (seen.has(row.key)) continue;
            seen.add(row.key);
            row.depth = depth;
            ordered.push(row);
            walk(row.key, depth + 1);
        }
    };
    walk(null, 0);

    // Anything a cycle kept out still gets shown, at the top level, rather
    // than vanishing from a report whose total already counts it.
    for (const row of byKey.values()) {
        if (!seen.has(row.key)) {
            row.depth = 0;
            ordered.push(row);
        }
    }
    return ordered;
}

/**
 * The one `/reports/view` route, sending a spec to its report's view.
 *
 * A spec with no `kind` is the aggregation, so a saved row or a link written
 * before a second report existed opens exactly as it always did; only the
 * marker routes elsewhere.
 */
export function ReportView() {
    const search = useSearch({ strict: false }) as ReportSearch;
    return search.kind === 'returns' ? <ReturnsReportView /> : <AggregationReportView />;
}

function AggregationReportView() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const search = useSearch({ strict: false }) as ReportSearch;
    const navigate = useNavigate();

    const spec = useMemo(() => specFromSearch(search), [search]);
    // The report's own name, or the saved report's. NOT a preset name carried
    // in the URL: that stayed put while the controls moved, so the page went
    // on calling itself "Expenses by payee" while showing Both, by category.
    const title = search.name ?? AGGREGATION_REPORT_NAME;

    const patch = (next: Partial<ReportSpec>) => {
        void navigate({
            to: '/ledgers/$ledgerId/reports/view',
            params: { ledgerId },
            search: { ...search, ...next },
            replace: true,
        });
    };

    // A chart always sends a cut (every group over All) so the server marks the
    // rows it draws; the table sends the spec's own cap, none over All. The
    // colour count never lowers it. Two requests for two views, never one
    // result trimmed in the browser.
    const chart = spec.view === 'chart';
    const request = useMemo(
        // Roll-up is a table's subtotals; a chart draws groups side by side and
        // would count a parent twice, so it is always flat there.
        () => (chart ? { ...spec, topN: chartTopN(spec), rollup: false } : spec),
        [chart, spec],
    );
    const query = useQuery({
        queryKey: ['report-summary', ledgerId, specToQuery(request)],
        queryFn: () => fetchReportSummary(ledgerId, request),
        // Switching view can change the request. The previous result keeps the
        // controls in place, the View toggle the reader just pressed among them,
        // while the body says Running.
        placeholderData: keepPreviousData,
    });
    // A category chart waits for the ledger's Uncategorized, and never recolours a
    // series once drawn (useChartSystemIds).
    const categoryChart = chart && spec.groupBy === 'Category';
    const systemIds = useChartSystemIds(ledgerId, categoryChart);

    const result: ReportResult | undefined = query.data;
    const running = query.isPending || query.isPlaceholderData
        || (categoryChart && systemIds === null && !query.isError);
    // The result's own currency. Nothing is drawn before the result arrives, so no
    // figure is ever printed in a currency the report did not name.
    const currency = result?.currencyCode ?? null;
    const periods = result
        ? periodsOf(result.rows, spec.bucket, result.fromUtc, result.toUtc)
        : [];
    // A rolled-up parent's own postings come as a row of their own under a cap;
    // the table lists groups, so it leaves them out.
    const rows = result ? pivot(result.rows.filter((r) => r.isDirect !== true)) : [];
    // Rows in several currencies, or a figure with a missing rate (ADR-0114 D7, D9):
    // currencyRules.
    const { missing, crossCurrency, chartWithheld } = currencyRules(result, spec);
    const chartRows = (result && chart && !chartWithheld ? chartRowsOf(pivot(result.rows)) : null) ?? [];

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: 'Reports',
                            node: (
                                <Link to="/ledgers/$ledgerId/reports" params={{ ledgerId }} className="hover:underline">
                                    Reports
                                </Link>
                            ),
                        },
                        { label: title },
                    ]}
                />
            </TopBar>
            <MainPane>
                {/* Fills the pane and makes the TABLE the scroller, rather
                    than the page. A pivot is wide and long by nature: with the
                    page scrolling, the horizontal bar sat at the bottom of the
                    table, so reaching it meant scrolling past every row first.
                    No max-width either — capping a 12-column pivot at prose
                    width wastes the screen it needs most. */}
                <div className="flex h-full min-w-0 flex-col p-5">
                <div className="flex flex-wrap items-start justify-between gap-2">
                    <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
                    <div className="flex items-start gap-1.5">
                    <SaveReportControl
                        ledgerId={ledgerId}
                        spec={spec}
                        savedId={search.saved}
                        savedName={search.name}
                        onSaved={(report) => {
                            // Land on the saved report, so a second Save
                            // updates it rather than creating another copy.
                            void navigate({
                                to: '/ledgers/$ledgerId/reports/view',
                                params: { ledgerId },
                                search: { ...search, saved: report.id, name: report.name },
                                replace: true,
                            });
                        }}
                    />
                    </div>
                </div>
                <div className="mb-3 space-y-1">
                    <p className="text-sm text-text-muted">
                        {MEASURE_LABELS[spec.measure]} · by{' '}
                        {(GROUP_LABELS[spec.groupBy] ?? spec.groupBy).toLowerCase()}
                        {spec.bucket === 'None' ? '' : ` · ${spec.bucket.toLowerCase()}ly`}
                        {' · '}{describeRange(spec).toLowerCase()}
                    </p>
                    {/* The one ledger-level note on converted totals (ADR-0114 D7). */}
                    <LedgerConversionNote ledgerId={ledgerId} />
                </div>

                <div className="mb-3 flex flex-wrap items-end gap-1.5">
                    <Select label="Measure" value={spec.measure} options={MEASURES}
                        labels={MEASURE_LABELS}
                        onChange={(v) => patch({ measure: v as ReportMeasure })} />
                    <Select label="Group by" value={spec.groupBy} options={GROUPS}
                        labels={GROUP_LABELS}
                        onChange={(v) => patch({ groupBy: v as ReportGroupBy })} />
                    <Select label="Period" value={spec.bucket} options={BUCKETS}
                        onChange={(v) => patch({ bucket: v as ReportTimeBucket })} />
                    <RangePicker spec={spec} onChange={patch} />
                    <Select
                        label="Show"
                        value={spec.topN === undefined ? 'all' : String(spec.topN)}
                        options={TOP_N.map((t) => t.value)}
                        labels={Object.fromEntries(TOP_N.map((t) => [t.value, t.label]))}
                        onChange={(v) => patch({ topN: TOP_N.find((t) => t.value === v)?.n })}
                    />
                    {hasChartView(rows) ? (
                        <SegmentedControl
                            label="View"
                            options={VIEW_OPTIONS}
                            value={spec.view ?? 'table'}
                            onChange={(v) => patch({ view: v })}
                        />
                    ) : null}
                    {/* Only where a real choice exists: a ranking has no
                        second shape to offer, and a table has no bars. */}
                    {spec.view === 'chart' && spec.bucket !== 'None' && hasChartView(rows) && !chartWithheld ? (
                        <SegmentedControl
                            label="Bars"
                            options={STACK_OPTIONS}
                            value={spec.chartStack ?? 'stacked'}
                            onChange={(v) => patch({ chartStack: v })}
                        />
                    ) : null}
                    {spec.groupBy === 'Category' && !chart ? (
                        <Checkbox
                            label="Roll up"
                            checked={spec.rollup ?? false}
                            onChange={(e) => patch({ rollup: e.target.checked })}
                            wrapperClassName="pb-1.5 text-xs text-text"
                        />
                    ) : null}
                </div>

                {running ? (
                    <p className="text-xs text-text-muted">Running…</p>
                ) : query.isError ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {(query.error as Error).message}
                    </p>
                ) : rows.length === 0 ? (
                    <p className="text-xs text-text-muted">
                        Nothing in this range. Widen the range, or change the measure.
                    </p>
                ) : spec.view === 'chart' && crossCurrency !== null && currency !== null ? (
                    <p className="text-xs text-text-muted">{crossCurrencyChartText(crossCurrency, currency)}</p>
                ) : spec.view === 'chart' && chartWithheld ? (
                    <MissingRateNote missing={missing} ratesLink={<ExchangeRatesLink ledgerId={ledgerId} />} />
                ) : spec.view === 'chart' && currency !== null ? (
                    <div className="flex min-h-0 flex-1 flex-col pb-2">
                        <ReportChart
                            rows={chartRows}
                            periods={periods}
                            bucketed={spec.bucket !== 'None'}
                            stack={spec.chartStack ?? 'stacked'}
                            currency={currency}
                            groupBy={spec.groupBy}
                            rest={result!.rest ?? null}
                            systemCategoryIds={systemIds ?? NO_SYSTEM_IDS}
                        />
                    </div>
                ) : (
                    <ReportTable
                        spec={spec}
                        result={result!}
                        rows={rows}
                        periods={periods}
                        scrollClassName="min-h-0 flex-1 overflow-auto"
                    />
                )}
                </div>
            </MainPane>
        </MainArea>
    );
}

