import { Bar } from '@/components/chart/Series';
import { AxisLabels, ChartFrame, ValueAxis, VIEWBOX } from '@/components/chart/ChartFrame';
import { band, extent, ticks, vertical } from '@/components/chart/scale';
import { formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';
import type { ReportChartStack } from '@/types/report';

import type { PivotRow } from './ReportView';

/**
 * The report's Chart view (ADR-0104 D4, FU-008 #1).
 *
 * THE SPEC PICKS THE SHAPE. A report with no time bucket is one number per
 * group, which is a ranking; a bucketed one is a group x period matrix, which
 * is a comparison over time. The only genuine choice inside that is stacked vs
 * grouped on the time axis, and that is the only control offered.
 *
 * NEVER A PIE. Every measure here can go negative -- a refund on an expense
 * category, a Net total -- and a share of a whole cannot. A proportional chart
 * over a set containing a negative is not a worse chart, it is a false one.
 * Allocation is the report where a donut IS honest, and it has its own surface.
 */

const { width: VB_W, height: VB_H } = VIEWBOX;

/**
 * Below this a bar is a line, so the series is drawn without being readable.
 * Geometry, not data: it falls out of the viewBox and the slot count, and it
 * would be the same figure on any ledger.
 */
const MIN_BAR_PX = 3;

/** Palette positions, cycled. Classes, so both themes follow the tokens. */
const FILLS = [
    'fill-cat-groc-text', 'fill-cat-din-text', 'fill-cat-house-text',
    'fill-cat-util-text', 'fill-cat-sub-text', 'fill-cat-tran-text',
    'fill-cat-sal-text', 'fill-cat-xfer-text',
] as const;

/** The same palette as backgrounds, for the HTML ranked rows. */
const BGS = [
    'bg-cat-groc-text', 'bg-cat-din-text', 'bg-cat-house-text',
    'bg-cat-util-text', 'bg-cat-sub-text', 'bg-cat-tran-text',
    'bg-cat-sal-text', 'bg-cat-xfer-text',
] as const;

const fillFor = (i: number) => FILLS[i % FILLS.length]!;
const bgFor = (i: number) => BGS[i % BGS.length]!;

/**
 * Axis labels, short. `formatCurrency` is right for a row and wrong for an
 * axis: four gridlines of five-figure amounts with cents are more digits than chart.
 *
 * Abbreviation is chosen from the STEP, not from each value. Rounding every
 * value to the nearest thousand on its own printed two ticks half a thousand
 * apart as the same abbreviated label — an axis with two rules carrying the same number, which is worse than
 * a long label because it reads as correct. The unit may only hide digits the
 * step does not use, so two ticks can never collapse onto one label.
 */
export function axisMoneyFor(tickValues: readonly number[]): (v: number) => string {
    const step = tickValues.length > 1
        ? Math.abs(tickValues[1]! - tickValues[0]!)
        : 1;
    const largest = Math.max(...tickValues.map(Math.abs), 0);

    // ONE unit for the whole axis, picked from its largest value. Choosing per
    // value would put a whole-dollar label next to a k label, which reads as two scales.
    let unit = largest >= 1_000_000 ? 1_000_000 : largest >= 1_000 ? 1_000 : 1;
    // ...then dropped until the step fits in one decimal of it. `ticks` only
    // ever steps by 1, 2 or 5 times a power of ten, so at a tenth of the unit
    // or coarser every tick lands exactly on a tenth — no tick is rounded into
    // its neighbour, and none needs a second decimal to escape one.
    while (unit > 1 && step < unit / 10) unit /= 1_000;

    const suffix = unit === 1_000_000 ? 'M' : unit === 1_000 ? 'k' : '';
    const decimals = step / unit >= 1 ? 0 : 1;

    return (v) => {
        // Zero takes no suffix: a zero followed by k is a unit on a value that has none.
        if (v === 0) return '$0';
        const scaled = Math.abs(v) / unit;
        const text = decimals === 0
            ? String(Math.round(scaled))
            // Trailing ".0" is noise once the neighbours are distinct anyway.
            : scaled.toFixed(1).replace(/\.0$/, '');
        return `${v < 0 ? '-' : ''}$${text}${suffix}`;
    };
}

/**
 * Only the rows a reader would compare. A roll-up parent and its own children
 * are the same money counted twice, and drawing both makes the parent look
 * like a peer at twice its true share.
 */
function leavesOf(rows: readonly PivotRow[]): PivotRow[] {
    const parents = new Set(rows.map((r) => r.parentId).filter((p): p is string => p !== null));
    return rows.filter((r) => !parents.has(r.key));
}

const byMagnitude = (a: PivotRow, b: PivotRow) => Math.abs(b.total) - Math.abs(a.total);

export function ReportChart({
    rows, periods, bucketed, stack = 'stacked', currency = 'USD',
}: {
    rows: readonly PivotRow[];
    periods: readonly string[];
    /** True when the spec carries a time bucket. */
    bucketed: boolean;
    stack?: ReportChartStack;
    currency?: string;
}) {
    if (rows.length === 0) return null;
    return bucketed && periods.length > 0
        ? <OverTime rows={rows} periods={periods} stack={stack} currency={currency} />
        : <Ranked rows={rows} currency={currency} />;
}

/**
 * One row per group, largest first -- HTML, not SVG, and deliberately.
 *
 * A ranked list is a table with a magnitude column; it has no coordinate space
 * worth having. Drawn in the shared viewBox the category NAMES would be
 * stretched by `preserveAspectRatio="none"`, which is exactly why the kit's
 * labels and rules are HTML overlays already. As rows, each name sits beside
 * its own bar and the legend disappears -- and a legend of a dozen colour
 * swatches to match up is the same unreadable forest as a dozen hairline bars,
 * moved below the chart.
 *
 * It also matches the TABLE, which lists groups down the left. The vertical
 * version claimed to do that and had it backwards.
 */
function Ranked({ rows, currency }: { rows: readonly PivotRow[]; currency: string }) {
    const ranked = leavesOf(rows).sort(byMagnitude);
    const widest = Math.max(...ranked.map((r) => Math.abs(r.total)), 1);
    const anyNegative = ranked.some((r) => r.total < 0);

    // With negatives the track splits at the middle so bars run both ways;
    // without, the whole track is positive and nothing is spent on an empty
    // left half.
    const half = anyNegative ? 50 : 100;
    const widthPct = (v: number) => (Math.abs(v) / widest) * half;

    return (
        <div
            className="flex h-full min-h-0 flex-col gap-1 overflow-y-auto pr-2"
            role="list"
            aria-label={summarize(ranked, currency)}
        >
            {ranked.map((r, i) => (
                <div key={r.key} role="listitem" className="flex min-w-0 items-center gap-3 text-xs">
                    <span className="w-fixed-160px shrink-0 truncate text-text" title={r.name}>
                        {r.name}
                    </span>
                    <span className="relative h-control-20px min-w-0 flex-1 rounded bg-surface-muted">
                        {anyNegative ? (
                            <span
                                aria-hidden
                                className="absolute inset-y-0 left-1/2 w-px bg-border-strong"
                            />
                        ) : null}
                        <span
                            aria-hidden
                            className={cn('absolute inset-y-0.5 rounded-sm', bgFor(i))}
                            style={r.total < 0
                                ? { right: '50%', width: `${widthPct(r.total)}%` }
                                : { left: anyNegative ? '50%' : '0', width: `${widthPct(r.total)}%` }}
                        />
                    </span>
                    <span className="w-fixed-96px shrink-0 text-right font-mono tabular-nums text-text-muted">
                        {formatCurrency(r.total, currency)}
                    </span>
                </div>
            ))}
        </div>
    );
}

/**
 * One cluster per period. Stacked by default, grouped on request.
 *
 * STACKING IS SIGN-SEPARATED: positives accumulate upward from zero, negatives
 * downward. An earlier version of this file refused to stack at all, on the
 * grounds that "one negative value in a stack puts a segment below the segment
 * it is meant to sit on". That is true of a single running total and false of
 * two: each column reads as gross one way above the line and gross the other
 * below, which is what a cash-flow column means anyway.
 *
 * Stacked is the default because it is what this shape is usually asked -- what
 * did this period come to, and of what -- and because it carries far more
 * series legibly: twelve columns against a hundred and forty-four hairlines.
 * Grouped answers a different question, how one series moved, and is clearer
 * at two or three, which is why it stays available rather than being decided
 * for the reader.
 */
function OverTime({
    rows, periods, stack, currency,
}: {
    rows: readonly PivotRow[];
    periods: readonly string[];
    stack: ReportChartStack;
    currency: string;
}) {
    const ranked = leavesOf(rows).sort(byMagnitude);
    const valueAt = (r: PivotRow, p: string) => r.byPeriod.get(p) ?? 0;

    // Grouped has to fit every series into one period's slot; stacked needs
    // only one column per period, so the legibility cap applies to grouped
    // alone.
    const perPeriod = VB_W / Math.max(periods.length, 1);
    const visible = stack === 'grouped'
        ? ranked.slice(0, Math.max(1, Math.floor(perPeriod / MIN_BAR_PX)))
        : ranked;

    // The domain differs by mode: grouped needs the largest SINGLE value,
    // stacked the largest COLUMN -- and a column's two halves accumulate
    // separately, so a period with both reaches further each way.
    const domainValues = stack === 'grouped'
        ? visible.flatMap((r) => periods.map((p) => valueAt(r, p)))
        : periods.flatMap((p) => {
            let up = 0;
            let down = 0;
            for (const r of visible) {
                const v = valueAt(r, p);
                if (v >= 0) up += v; else down += v;
            }
            return [up, down];
        });

    const domain = extent(domainValues);
    const y = vertical(domain, VB_H);
    const zero = y(0);

    const clusters = band(periods.length, VB_W);
    const inner = band(Math.max(visible.length, 1), clusters.width * 0.8);

    const tickValues = ticks(domain, 4);

    return (
        <figure className="m-0 flex h-full min-h-0 flex-col">
            <ChartFrame
                heightClass="min-h-fixed-176px flex-1"
                ariaLabel={summarize(visible, currency)}
                gutter
                overlay={
                    <ValueAxis
                        tickValues={tickValues}
                        toPercent={(v) => (y(v) / VB_H) * 100}
                        format={axisMoneyFor(tickValues)}
                    />
                }
            >
                {periods.map((p, pi) => {
                    if (stack === 'grouped') {
                        const left = clusters.center(pi) - clusters.width * 0.4;
                        return visible.map((r, si) => (
                            <Bar
                                key={`${p}_${r.key}`}
                                x={left + inner.center(si) - inner.width * 0.4}
                                width={inner.width * 0.8}
                                baselineY={zero}
                                valueY={y(valueAt(r, p))}
                                className={fillFor(si)}
                            />
                        ));
                    }

                    // Two running totals, so a refund grows the column
                    // DOWNWARD instead of eating into the segment above it.
                    let up = 0;
                    let down = 0;
                    const width = clusters.width * 0.7;
                    const x = clusters.center(pi) - width / 2;
                    return visible.map((r, si) => {
                        const v = valueAt(r, p);
                        if (v === 0) return null;
                        const from = v >= 0 ? up : down;
                        const to = from + v;
                        if (v >= 0) up = to; else down = to;
                        return (
                            <Bar
                                key={`${p}_${r.key}`}
                                x={x}
                                width={width}
                                baselineY={y(from)}
                                valueY={y(to)}
                                className={fillFor(si)}
                            />
                        );
                    });
                })}
            </ChartFrame>
            <AxisLabels labels={axisLabels(periods)} gutter />
            <Legend rows={visible} dropped={ranked.length - visible.length} currency={currency} />
        </figure>
    );
}

/**
 * Period labels, thinned to what fits.
 *
 * Every period still gets a COLUMN -- a quiet month is a fact about the ledger
 * and the chart draws it either way. This thins only the TEXT, which would
 * otherwise overlap into an unreadable band.
 */
export function axisLabels(periods: readonly string[]): string[] {
    const max = 12;
    if (periods.length <= max) return [...periods];
    const step = Math.ceil(periods.length / max);
    return periods.filter((_, i) => i % step === 0);
}

/**
 * The key, and the honest note about anything not drawn. A cap that is not
 * stated reads as "this is everything", which is the one thing a report must
 * never imply.
 */
function Legend({
    rows, dropped, currency,
}: { rows: readonly PivotRow[]; dropped: number; currency: string }) {
    return (
        <figcaption className="mt-2 flex max-h-fixed-96px flex-wrap items-center gap-x-3 gap-y-1 overflow-y-auto text-[0.6875rem] text-text-muted">
            {rows.map((r, i) => (
                <span key={r.key} className="inline-flex items-center gap-1">
                    <svg aria-hidden width="8" height="8" viewBox="0 0 8 8" className="shrink-0">
                        <rect width="8" height="8" rx="1" className={fillFor(i)} />
                    </svg>
                    <span className="truncate">{r.name}</span>
                    <span className="font-mono tabular-nums text-text-subtle">
                        {formatCurrency(r.total, currency)}
                    </span>
                </span>
            ))}
            {dropped > 0 ? (
                <span className="text-text-subtle">
                    +{dropped} more not shown, too narrow to read. Switch to stacked, cap
                    the report with Show, or widen the period.
                </span>
            ) : null}
        </figcaption>
    );
}

/** The accessible name: what the picture SHOWS, not what it is. */
function summarize(rows: readonly PivotRow[], currency: string): string {
    if (rows.length === 0) return 'No data in this range.';
    const top = rows[0]!;
    return `${rows.length} series. Largest: ${top.name} at ${formatCurrency(top.total, currency)}.`;
}
