import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchReportSummary, specToQuery } from '@/lib/api';
import { Checkbox } from '@/components/ui/Checkbox';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { cn } from '@/lib/cn';
import { formatCurrency } from '@/lib/money';
import type {
    ReportGroupBy, ReportMeasure, ReportResult, ReportRow, ReportSpec, ReportTimeBucket,
} from '@/types/report';

import { ReportChart } from './ReportChart';
import { AGGREGATION_REPORT_NAME } from './catalog';
import { RangePicker, describeRange } from './RangePicker';
import { SaveReportControl } from './SaveReportControl';

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

/**
 * The group-by names a reader would use. Only CategoryKind needs one: the enum
 * value is a type name and "Income vs expense" is the question people actually
 * ask of it — which is the whole of what a cash-flow report is.
 */
const GROUP_LABELS: Readonly<Partial<Record<ReportGroupBy, string>>> = {
    CategoryKind: 'Income vs expense',
};
const BUCKETS: readonly ReportTimeBucket[] = ['None', 'Month', 'Quarter', 'Year'];

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
 * D4: "Reports with only one sensible view do not render the toggle." A chart
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
    for (let i = 0; cursor <= end && i < 3660; i++) {
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
    parentId: string | null;
    /** 0 = top level. Carried so ordering and indentation cannot disagree. */
    depth: number;
    byPeriod: Map<string, number>;
    total: number;
}

/** Indent per depth. A lookup, because Tailwind cannot see a computed class. */
const DEPTH_PAD = ['', 'pl-3', 'pl-6', 'pl-8', 'pl-10'] as const;

function padFor(depth: number): string {
    return DEPTH_PAD[Math.min(depth, DEPTH_PAD.length - 1)];
}

/**
 * Collapse cells into one row per group, ordered as a tree.
 *
 * ARBITRARY DEPTH. This walked exactly two levels — roots, then their direct
 * children — and everything deeper fell through to a catch-all that appended
 * it at the END, styled as a child. On a real ledger that put many
 * unrelated categories under whichever parent happened to be last, which is
 * what it looked like: one category with a run of categories from entirely
 * different branches beneath it. The server rolls up the whole ancestor chain,
 * so the client has no business assuming two levels.
 *
 * DEPTH IS COMPUTED HERE AND ONLY HERE. The old code had two definitions of
 * "top level" — this function treated a row whose parent is absent as a root,
 * while the renderer checked `parentId === null` — so a category whose parent
 * had no postings was ordered at the top and drawn as a child. One number on
 * the row makes that impossible.
 */
export function pivot(rows: readonly ReportRow[]): PivotRow[] {
    const byKey = new Map<string, PivotRow>();
    for (const r of rows) {
        const key = r.groupId ?? `name:${r.groupName}`;
        let row = byKey.get(key);
        if (!row) {
            row = {
                key, name: r.groupName, parentId: r.parentId,
                depth: 0, byPeriod: new Map(), total: 0,
            };
            byKey.set(key, row);
        }
        if (r.period) row.byPeriod.set(r.period, (row.byPeriod.get(r.period) ?? 0) + r.amount);
        row.total += r.amount;
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
    for (const bucket of children.values()) bucket.sort((a, b) => a.name.localeCompare(b.name));

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

export function ReportView() {
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

    const query = useQuery({
        queryKey: ['report-summary', ledgerId, specToQuery(spec)],
        queryFn: () => fetchReportSummary(ledgerId, spec),
    });

    const result: ReportResult | undefined = query.data;
    const periods = result
        ? periodsOf(result.rows, spec.bucket, result.fromUtc, result.toUtc)
        : [];
    const rows = result ? pivot(result.rows) : [];

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
                <p className="mb-3 text-sm text-text-muted">
                    {MEASURE_LABELS[spec.measure]} · by{' '}
                    {(GROUP_LABELS[spec.groupBy] ?? spec.groupBy).toLowerCase()}
                    {spec.bucket === 'None' ? '' : ` · ${spec.bucket.toLowerCase()}ly`}
                    {' · '}{describeRange(spec).toLowerCase()}
                </p>

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
                        <div
                            role="group"
                            aria-label="View"
                            className="flex items-center gap-0.5 rounded border border-border p-0.5 pb-0.5"
                        >
                            {(['table', 'chart'] as const).map((v) => (
                                <button
                                    key={v}
                                    type="button"
                                    aria-pressed={spec.view === v}
                                    onClick={() => patch({ view: v })}
                                    className={cn(
                                        'rounded px-2 py-0.5 text-xs capitalize',
                                        spec.view === v
                                            ? 'bg-surface-muted font-medium text-text'
                                            : 'text-text-muted hover:text-text',
                                    )}
                                >
                                    {v}
                                </button>
                            ))}
                        </div>
                    ) : null}
                    {/* Only where a real choice exists: a ranking has no
                        second shape to offer, and a table has no bars. */}
                    {spec.view === 'chart' && spec.bucket !== 'None' && hasChartView(rows) ? (
                        <div
                            role="group"
                            aria-label="Bars"
                            className="flex items-center gap-0.5 rounded border border-border p-0.5 pb-0.5"
                        >
                            {(['stacked', 'grouped'] as const).map((v) => (
                                <button
                                    key={v}
                                    type="button"
                                    aria-pressed={(spec.chartStack ?? 'stacked') === v}
                                    onClick={() => patch({ chartStack: v })}
                                    className={cn(
                                        'rounded px-2 py-0.5 text-xs capitalize',
                                        (spec.chartStack ?? 'stacked') === v
                                            ? 'bg-surface-muted font-medium text-text'
                                            : 'text-text-muted hover:text-text',
                                    )}
                                >
                                    {v}
                                </button>
                            ))}
                        </div>
                    ) : null}
                    {spec.groupBy === 'Category' ? (
                        <Checkbox
                            label="Roll up"
                            checked={spec.rollup ?? false}
                            onChange={(e) => patch({ rollup: e.target.checked })}
                            wrapperClassName="pb-1.5 text-xs text-text"
                        />
                    ) : null}
                </div>

                {query.isPending ? (
                    <p className="text-xs text-text-muted">Running…</p>
                ) : query.isError ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {(query.error as Error).message}
                    </p>
                ) : rows.length === 0 ? (
                    <p className="text-xs text-text-muted">
                        Nothing in this range. Widen the range, or change the measure.
                    </p>
                ) : spec.view === 'chart' ? (
                    <div className="flex min-h-0 flex-1 flex-col pb-2">
                        <ReportChart
                            rows={rows}
                            periods={periods}
                            bucketed={spec.bucket !== 'None'}
                            stack={spec.chartStack ?? 'stacked'}
                        />
                    </div>
                ) : (
                    <>
                        <div className="min-h-0 flex-1 overflow-auto">
                            <table className="w-full border-collapse text-xs">
                                <thead>
                                    <tr>
                                        {/* The corner: sticky both ways, so it
                                            never slides under either neighbour. */}
                                        <th className="sticky top-0 left-0 z-20 border-b border-border bg-surface-muted py-1 pr-2 text-left text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                            {spec.groupBy}
                                        </th>
                                        {periods.map((p) => (
                                            <th key={p} className="sticky top-0 z-10 border-b border-border bg-surface-muted px-2 py-1 text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                                {p}
                                            </th>
                                        ))}
                                        <th className="sticky top-0 z-10 border-b border-border bg-surface-muted px-2 py-1 text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                                            Total
                                        </th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {rows.map((row) => (
                                        <tr key={row.key}>
                                            <td className={cn(
                                                'sticky left-0 z-10 border-b border-border bg-surface-muted py-1 pr-2',
                                                padFor(row.depth),
                                                row.depth === 0 ? 'font-medium text-text' : 'text-text-muted',
                                            )}>
                                                {row.name}
                                            </td>
                                            {periods.map((p) => (
                                                <td key={p} className="border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text">
                                                    {row.byPeriod.has(p) ? formatCurrency(row.byPeriod.get(p)!, 'USD') : '—'}
                                                </td>
                                            ))}
                                            <td className="border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text">
                                                {formatCurrency(row.total, 'USD')}
                                            </td>
                                        </tr>
                                    ))}
                                    <tr>
                                        <td className="sticky left-0 z-10 border-t border-border-strong bg-surface-muted py-1 pr-2 font-semibold text-text">Total</td>
                                        {periods.map((p) => (
                                            <td key={p} className="border-t border-border-strong px-2 py-1 text-right font-mono tabular-nums text-text" />
                                        ))}
                                        <td className="border-t border-border-strong px-2 py-1 text-right font-mono font-semibold tabular-nums text-text">
                                            {formatCurrency(result!.total, 'USD')}
                                        </td>
                                    </tr>
                                </tbody>
                            </table>
                        </div>

                        <div className="shrink-0">
                        {spec.topN !== undefined ? (
                            <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                                Showing the top {spec.topN} by size. The total is every row in
                                range, including the ones not listed — so these rows add up to
                                less than it.
                            </p>
                        ) : null}
                        {spec.rollup && spec.groupBy === 'Category' ? (
                            <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                                Roll-up is on: a parent row already includes its children, so the rows
                                are subtotals. The total is the true sum of postings, not the sum of
                                the rows above it.
                            </p>
                        ) : null}
                        {spec.groupBy === 'Tag' ? (
                            <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                                A transaction tagged more than once appears under each of its tags, so
                                these rows can add up to more than the total.
                            </p>
                        ) : null}
                        </div>
                    </>
                )}
                </div>
            </MainPane>
        </MainArea>
    );
}

function Select({
    label, value, options, onChange, labels,
}: {
    label: string;
    value: string;
    options: readonly string[];
    onChange: (value: string) => void;
    /** Display text per value, where the UI's word differs from the API's. */
    labels?: Readonly<Record<string, string>>;
}) {
    const id = `report-${label.replace(/\s+/g, '-').toLowerCase()}`;
    return (
        <div className="flex min-w-0 flex-col gap-1">
            <FieldLabel htmlFor={id}>{label}</FieldLabel>
            <select
                id={id}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                className="h-control-28px rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
                {options.map((o) => <option key={o} value={o}>{labels?.[o] ?? o}</option>)}
            </select>
        </div>
    );
}
