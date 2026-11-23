import { useState, type ReactNode } from 'react';

import { Bar } from '@/components/chart/Series';
import { AxisLabels, ChartFrame, ValueAxis, VIEWBOX, ZeroRule } from '@/components/chart/ChartFrame';
import { band, extent, ticks, vertical } from '@/components/chart/scale';
import { currencySymbol, formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';
import { NO_RATE_LABEL } from '@/lib/conversion';
import type { RateUnavailable } from '@/lib/types';
import type { ReportChartStack, ReportGroupBy, ReportRest } from '@/types/report';

import { NoRate } from '../home/Money';
import { OTHER_FILL, RANKED_BG, assignHues, hueFill, otherLabel, shadesOf } from './chartSeries';
import { GroupName } from './GroupName';
import { fullGroupName, groupNameParts, type GroupNameParts } from './groupLabel';
import type { ChartRow as PivotRow } from './ReportView';

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
 *
 * COLOUR ONLY OVER TIME. A bucketed chart needs a key, so each series has a hue
 * of its own (chartSeries.ts) and the legend shows it. A ranking names every
 * bar beside it, so its bars share one neutral fill.
 *
 * THE COLOUR COUNT DOES NOT DECIDE HOW MANY SERIES. "All" draws every group:
 * past nine, a series takes a deeper shade of a hue already used, and past
 * those the colours repeat. A repeat is told apart by naming it: hovering a
 * segment (its tooltip) or a legend entry lights that series and dims the rest.
 * Other exists only when the report's own cap folded groups away, with the
 * server's `rest` for its figures. A fold whose figures need a missing rate
 * (ADR-0114 D9) has none, so Other is named, reads "no rate" and is drawn
 * nowhere; the report views withhold the whole chart first in that case.
 */

const { width: VB_W, height: VB_H } = VIEWBOX;

/**
 * Below this a bar is a line, so the series is drawn without being readable.
 * Geometry, not data: it falls out of the viewBox and the slot count, and it
 * would be the same figure on any ledger.
 */
const MIN_BAR_PX = 3;

/** One drawn series: a group, or Other. */
interface Series {
    key: string;
    /**
     * The name as drawn: a subcategory under its whole path. A chart is flat and
     * its parents are never drawn beside it, so nothing else names them.
     */
    parts: GroupNameParts;
    /** The whole name as one string, for a tooltip and the accessible text. */
    label: string;
    byPeriod: ReadonlyMap<string, number>;
    /**
     * Null for an Other with no figures: one of them needs a missing rate
     * (ADR-0114 D9), so it gets no bar and its figure reads "no rate".
     */
    total: number | null;
    /** The missing rates a null `total` needed, named on its hover. */
    rateUnavailable?: readonly RateUnavailable[];
    /** The fill class over time. A ranking draws every bar in one neutral. */
    fill: string;
}

type FiguredSeries = Series & { total: number };

const hasFigure = (s: Series): s is FiguredSeries => s.total !== null;

/** Other's key. Group keys are ids or prefixed names, so none can collide. */
const OTHER_KEY = 'rest:';

/**
 * Axis labels, short. `formatCurrency` is right for a row and wrong for an
 * axis: four gridlines of five-figure amounts with cents are more digits than chart.
 *
 * Abbreviation is chosen from the STEP, not from each value. Rounding every
 * value to the nearest thousand on its own printed two ticks half a thousand
 * apart as the same abbreviated label — an axis with two rules carrying the same number, which is worse than
 * a long label because it reads as correct. The unit may only hide digits the
 * step does not use, so two ticks can never collapse onto one label.
 *
 * The symbol is the ledger's currency's, as the table's figures are.
 */
export function axisMoneyFor(tickValues: readonly number[], currency: string): (v: number) => string {
    const symbol = currencySymbol(currency);
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
        if (v === 0) return `${symbol}0`;
        const scaled = Math.abs(v) / unit;
        const text = decimals === 0
            ? String(Math.round(scaled))
            // Trailing ".0" is noise once the neighbours are distinct anyway.
            : scaled.toFixed(1).replace(/\.0$/, '');
        return `${v < 0 ? '-' : ''}${symbol}${text}${suffix}`;
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

/**
 * The rows drawn, largest first. When the request carried `topN` the server
 * says which. Without that mark, the leaves.
 */
function drawnOf(rows: readonly PivotRow[]): PivotRow[] {
    const marked = rows.some((r) => r.drawn !== undefined);
    const drawn = marked ? rows.filter((r) => r.drawn === true) : leavesOf(rows);
    return drawn.sort(byMagnitude);
}

function seriesOf(
    rows: readonly PivotRow[],
    groupBy: ReportGroupBy,
    rest: ReportRest | null,
    systemIds: ReadonlySet<string>,
): Series[] {
    const drawn = drawnOf(rows);
    const identities = drawn.map((r) => ({
        key: r.key,
        groupId: r.groupId,
        name: r.name,
        isSystemCategory: r.groupId !== null && systemIds.has(r.groupId),
    }));
    const hues = assignHues(identities, groupBy);
    const shades = shadesOf(identities, hues);
    // Labelled as the table names the group; the colour rule above reads the
    // stored name, which is what a chip's name pattern matches.
    const series: Series[] = drawn.map((r) => {
        const parts = groupNameParts(groupBy, r.name, r.path);
        return {
            key: r.key,
            parts,
            label: fullGroupName(parts),
            byPeriod: r.byPeriod,
            total: r.total,
            fill: hueFill(hues.get(r.key)!, shades.get(r.key)),
        };
    });

    // The fold, as the server figured it. Its amounts are never the browser's
    // sum of rows: top-N did not send the rows it cut.
    if (rest && rest.count > 0) {
        const parts = otherLabel(rest, groupBy);
        series.push({
            key: OTHER_KEY,
            parts,
            label: fullGroupName(parts),
            ...restFigures(rest),
            fill: OTHER_FILL,
        });
    }
    return series;
}

/**
 * The rest's figures, or none when one of them needs a missing rate: its bar is
 * then not drawn, as a partial sum is no figure (ADR-0114 D9).
 */
function restFigures(rest: ReportRest): Pick<Series, 'byPeriod' | 'total' | 'rateUnavailable'> {
    const byPeriod = rest.byPeriod ?? [];
    if (rest.total === null || byPeriod.some((p) => p.amount === null)) {
        return {
            byPeriod: new Map(),
            total: null,
            rateUnavailable: [...(rest.rateUnavailable ?? []), ...byPeriod.flatMap((p) => p.rateUnavailable ?? [])],
        };
    }
    return { byPeriod: new Map(byPeriod.map((p) => [p.period, p.amount!])), total: rest.total };
}

const NO_SYSTEM_IDS: ReadonlySet<string> = new Set();

export function ReportChart({
    rows, periods, bucketed, stack = 'stacked', currency, groupBy,
    rest = null, systemCategoryIds = NO_SYSTEM_IDS, fit = false,
}: {
    rows: readonly PivotRow[];
    periods: readonly string[];
    /** True when the spec carries a time bucket. */
    bucketed: boolean;
    stack?: ReportChartStack;
    currency: string;
    /** Category charts colour each category as its chip; the rest by rank. */
    groupBy: ReportGroupBy;
    /** What top-N did not keep. Present only when the request carried `topN`. */
    rest?: ReportRest | null;
    /** The ledger's Uncategorized, whose own colour is the neutral one. */
    systemCategoryIds?: ReadonlySet<string>;
    /**
     * Draw inside a box of fixed height (a home report tile) rather than at the
     * chart's own minimum: the plot takes what the axis and the legend leave, and
     * the legend scrolls past two lines, so chart and legend share the height.
     */
    fit?: boolean;
}) {
    if (rows.length === 0) return null;
    const series = seriesOf(rows, groupBy, rest, systemCategoryIds);
    return bucketed && periods.length > 0
        ? <OverTime series={series} periods={periods} stack={stack} currency={currency} fit={fit} />
        : <Ranked series={series} currency={currency} />;
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
 *
 * ONE FILL. With the name beside each bar a colour carries nothing, and a
 * ranking in nine hues reads as nine kinds of thing. Other is the last row;
 * one with no figures has no bar, and its figure reads "no rate".
 */
function Ranked({ series: ranked, currency }: { series: readonly Series[]; currency: string }) {
    const figured = ranked.filter(hasFigure);
    const widest = Math.max(...figured.map((r) => Math.abs(r.total)), 1);
    const anyNegative = figured.some((r) => r.total < 0);

    // With negatives the track splits at the middle so bars run both ways;
    // without, the whole track is positive and nothing is spent on an empty
    // left half.
    const half = anyNegative ? 50 : 100;
    const widthPct = (v: number) => (Math.abs(v) / widest) * half;

    // The figures are monospace, so their widest is a count of characters: every
    // row's figure column is that wide, aligned, and never cut. At a narrow width
    // the NAME gives way (it truncates, its full text in the title) and the bar
    // keeps a floor, so no bar is squeezed to nothing and no figure is clipped.
    const figureText = (r: Series) => (hasFigure(r) ? formatCurrency(r.total, currency) : NO_RATE_LABEL);
    const figureChars = Math.max(...ranked.map((r) => figureText(r).length), 1);

    return (
        <div
            className="flex h-full min-h-0 flex-col gap-1 overflow-y-auto pr-2"
            role="list"
            aria-label={summarize(figured, currency)}
        >
            {ranked.map((r) => (
                <div key={r.key} role="listitem" className="flex min-w-0 items-center gap-3 text-xs">
                    <span className="flex min-w-0 shrink basis-fixed-160px text-text" data-ranked-name>
                        <GroupName parts={r.parts} />
                    </span>
                    {hasFigure(r) ? (
                        <span className="relative h-control-20px min-w-fixed-48px flex-1 rounded bg-surface-muted" data-ranked-track>
                            {anyNegative ? (
                                <span
                                    aria-hidden
                                    className="absolute inset-y-0 left-1/2 w-px bg-border-strong"
                                />
                            ) : null}
                            <span
                                aria-hidden
                                className={cn('absolute inset-y-0.5 rounded-sm', RANKED_BG)}
                                style={r.total < 0
                                    ? { right: '50%', width: `${widthPct(r.total)}%` }
                                    : { left: anyNegative ? '50%' : '0', width: `${widthPct(r.total)}%` }}
                            />
                        </span>
                    ) : (
                        // No track either: an empty one reads as a bar of zero.
                        <span aria-hidden className="h-control-20px min-w-fixed-48px flex-1" />
                    )}
                    <span
                        className="shrink-0 whitespace-nowrap text-right font-mono tabular-nums text-text-muted"
                        style={{ width: `${figureChars}ch` }}
                        data-ranked-figure
                    >
                        {hasFigure(r)
                            ? formatCurrency(r.total, currency)
                            : <NoRate missing={r.rateUnavailable ?? []} />}
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
 * downward. "One negative value in a stack puts a segment below the segment it
 * is meant to sit on" is true of a single running total and false of two: each
 * column reads as gross one way above the line and gross the other below, which
 * is what a cash-flow column means anyway.
 *
 * Stacked is the default because it is what this shape is usually asked -- what
 * did this period come to, and of what -- and because it carries far more
 * series legibly: twelve columns against a hundred and forty-four hairlines.
 * Grouped answers a different question, how one series moved, and is clearer
 * at two or three, which is why it stays available rather than being decided
 * for the reader.
 */
function OverTime({
    // Largest first, Other last: on top of each stack, and the first a grouped
    // cluster drops when it runs out of width.
    series: ranked, periods, stack, currency, fit,
}: {
    series: readonly Series[];
    periods: readonly string[];
    stack: ReportChartStack;
    currency: string;
    fit: boolean;
}) {
    const valueAt = (r: Series, p: string) => r.byPeriod.get(p) ?? 0;

    // The series under the pointer, from a segment or a legend entry. The rest
    // dim, so a colour another series shares stops being ambiguous.
    const [lit, setLit] = useState<string | null>(null);
    const fillOf = (r: Series) => cn(r.fill, lit !== null && lit !== r.key && 'opacity-25');
    const hoverOf = (r: Series) => (over: boolean) => setLit(over ? r.key : null);
    const titleOf = (r: Series, p: string) => `${r.label} · ${p} · ${formatCurrency(valueAt(r, p), currency)}`;

    // An Other with no figures draws nothing; the legend still names it.
    const figured = ranked.filter(hasFigure);
    const unfigured = ranked.filter((r) => !hasFigure(r));

    // Grouped has to fit every series into one period's slot; stacked needs
    // only one column per period, so the legibility cap applies to grouped
    // alone.
    const perPeriod = VB_W / Math.max(periods.length, 1);
    const visible = stack === 'grouped'
        ? figured.slice(0, Math.max(1, Math.floor(perPeriod / MIN_BAR_PX)))
        : figured;

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
                // In a tile the plot takes what the axis and the legend leave,
                // and never less than a readable floor; the legend gives way
                // below that (Legend).
                heightClass={fit ? 'min-h-fixed-64px flex-1' : 'min-h-fixed-176px flex-1'}
                ariaLabel={summarize(visible, currency)}
                gutter
                // The gridlines beneath the bars, which cover them; zero over
                // them, the line a sign is read against.
                underlay={
                    <ValueAxis
                        tickValues={tickValues}
                        toPercent={(v) => (y(v) / VB_H) * 100}
                        format={axisMoneyFor(tickValues, currency)}
                        zeroRule={false}
                    />
                }
                overlay={<ZeroRule atPercent={(zero / VB_H) * 100} />}
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
                                className={fillOf(r)}
                                title={titleOf(r, p)}
                                onHover={hoverOf(r)}
                            />
                        ));
                    }

                    // Two running totals, so a refund grows the column
                    // DOWNWARD instead of eating into the segment above it.
                    let up = 0;
                    let down = 0;
                    const width = clusters.width * 0.7;
                    const x = clusters.center(pi) - width / 2;
                    return visible.map((r) => {
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
                                className={fillOf(r)}
                                title={titleOf(r, p)}
                                onHover={hoverOf(r)}
                            />
                        );
                    });
                })}
            </ChartFrame>
            <AxisLabels labels={periods} gutter />
            <Legend
                rows={visible}
                unfigured={unfigured}
                dropped={figured.length - visible.length}
                currency={currency}
                fit={fit}
                lit={lit}
                onLit={setLit}
            />
        </figure>
    );
}

/**
 * The key, and the honest note about anything not drawn. A cap that is not
 * stated reads as "this is everything", which is the one thing a report must
 * never imply. An Other with no figures is listed with no swatch, because no
 * bar carries its colour, and its figure reads "no rate".
 *
 * TWO PARTS. The series, which may scroll; and what is not drawn one by one
 * (Other, a fold with no figures, the series too narrow to draw), on a line of
 * its own that never scrolls out of sight. In a tile (`fit`) the series take
 * their whole lines and the plot gives way, down to its floor; only past that
 * do they scroll, inside their own box.
 */
function Legend({
    rows, unfigured, dropped, currency, fit, lit, onLit,
}: {
    rows: readonly FiguredSeries[];
    unfigured: readonly Series[];
    dropped: number;
    currency: string;
    fit: boolean;
    /** The series lit by the pointer, if any, and the way to light one. */
    lit: string | null;
    onLit: (key: string | null) => void;
}) {
    const entry = (r: FiguredSeries) => (
        <LegendEntry key={r.key} dim={lit !== null && lit !== r.key} onLit={(on) => onLit(on ? r.key : null)}>
            <span className="inline-flex min-w-0 max-w-full items-center gap-1">
                <LegendChip fill={r.fill} />
                <GroupName parts={r.parts} />
            </span>
            <span className="font-mono tabular-nums text-text-subtle">
                {formatCurrency(r.total, currency)}
            </span>
        </LegendEntry>
    );
    const drawn = rows.filter((r) => r.key !== OTHER_KEY);
    const other = rows.filter((r) => r.key === OTHER_KEY);
    const folded = other.length > 0 || unfigured.length > 0 || dropped > 0;
    const series = drawn.length > 0 ? (
        <div
            data-legend-series
            className={cn(
                'flex flex-wrap items-center gap-x-3 gap-y-1 overflow-y-auto',
                fit ? LEGEND_FIT_HEIGHT : LEGEND_CAPPED_HEIGHT,
            )}
        >
            {drawn.map(entry)}
        </div>
    ) : null;
    return (
        <figcaption className={cn(
            'mt-2 flex flex-col gap-y-1 text-[0.6875rem] text-text-muted',
            LEGEND_LINE,
            fit && 'min-h-0',
        )}>
            {/* In a tile the series are laid out in the room the figure leaves them
                (min-h-0), and drawn in the whole lines that room holds. */}
            {fit && series ? <div data-legend-room className="min-h-0">{series}</div> : series}
            {folded ? (
                <div data-legend-fold className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1">
                    {other.map(entry)}
                    {unfigured.map((r) => (
                        <LegendEntry key={r.key}>
                            <span className="inline-flex min-w-0 max-w-full items-center gap-1">
                                <GroupName parts={r.parts} />
                                <span aria-hidden>·</span>
                            </span>
                            <NoRate missing={r.rateUnavailable ?? []} />
                        </LegendEntry>
                    ))}
                    {dropped > 0 ? (
                        <span className="text-text-subtle">
                            +{dropped} more not shown, too narrow to read. Switch to stacked, cap
                            the report with Show, or widen the period.
                        </span>
                    ) : null}
                </div>
            ) : null}
        </figcaption>
    );
}

/**
 * Every legend line is one rem high and the lines are `gap-y-1` (one `--spacing`)
 * apart, so a box of whole lines is a whole number of (1rem + gap) less one gap.
 * The line height is the text's own, which density does not move; the gap is
 * space, which it does.
 */
const LEGEND_LINE = 'leading-[1rem]';
/*
 * A box of whole lines: the tallest whole number of lines a space holds, as
 * `round(down, space + gap, line + gap) - gap`. The half pixel keeps a space that
 * is exactly whole lines from rounding down a line on a sum that lands a hair under
 * its exact value. Written out in full, twice, because Tailwind reads class names
 * off the source and would never see one assembled at run time.
 */
/**
 * In a tile the series take their whole lines and the plot gives way, down to its
 * floor; past that their box is the whole lines its room holds, so a list that fits
 * never scrolls and one that does not shows no line cut through. Room the box
 * cannot fill with a whole line is left under it, less than a line.
 */
const LEGEND_FIT_HEIGHT = 'max-h-[calc(round(down,100%_+_var(--spacing)_+_0.5px,1rem_+_var(--spacing))_-_var(--spacing))]';
/** On Reports' own page the series box is capped, at the whole lines 96px holds. */
const LEGEND_CAPPED_HEIGHT = 'max-h-[calc(round(down,96px_+_var(--spacing)_+_0.5px,1rem_+_var(--spacing))_-_var(--spacing))]';

/** A series' swatch in a legend: the one shape every report's key uses. */
export function LegendChip({ fill }: { fill: string }) {
    return (
        <svg aria-hidden data-legend-chip width="8" height="8" viewBox="0 0 8 8" className="shrink-0">
            <rect width="8" height="8" rx="1" className={fill} />
        </svg>
    );
}

/**
 * One key entry: its name (with its swatch), and its figure. The two share a line
 * while they fit, and the figure goes to a line of its own when they do not, so an
 * entry never runs past its box: the name truncates only when it does not fit a
 * line by itself. The lines are the legend's, a gap apart. Every report's chart keys
 * its series with it, and lights one by hovering it.
 */
export function LegendEntry({
    children, dim = false, onLit,
}: {
    children: ReactNode;
    /** Another series is lit, so this one steps back. */
    dim?: boolean;
    onLit?: (on: boolean) => void;
}) {
    return (
        <span
            data-legend-entry
            className={cn('inline-flex min-w-0 max-w-full flex-wrap items-center gap-x-1 gap-y-1', dim && 'opacity-40')}
            {...(onLit ? { onMouseEnter: () => onLit(true), onMouseLeave: () => onLit(false) } : {})}
        >
            {children}
        </span>
    );
}

/** The accessible name: what the picture SHOWS, not what it is. */
function summarize(rows: readonly FiguredSeries[], currency: string): string {
    if (rows.length === 0) return 'No data in this range.';
    const top = rows[0]!;
    return `${rows.length} series. Largest: ${top.label} at ${formatCurrency(top.total, currency)}.`;
}
