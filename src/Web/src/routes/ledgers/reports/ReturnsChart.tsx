import { useState, type MouseEvent } from 'react';

import { AxisLabels, ChartFrame, ValueAxis, VIEWBOX, ZeroRule } from '@/components/chart/ChartFrame';
import { Line, pointsAttr, type Point } from '@/components/chart/Series';
import { band, extent, ticks, vertical } from '@/components/chart/scale';
import { cn } from '@/lib/cn';
import { formatCurrency } from '@/lib/money';
import type { ReturnsBenchmark, ReturnsSeries, ReturnsShow } from '@/types/report';

import { CHART_HUES, type ChartHue } from './chartSeries';
import { axisMoneyFor, LegendChip, LegendEntry } from './ReportChart';
import {
    axisLabelsFor, benchmarkAnchor, benchmarkLabel, drawableBenchmark, formatIndex, seriesName, valueOf,
} from './returnsModel';

/**
 * The Returns report's Chart view: the ledger's line, thick, and one thin line
 * per account (ADR-0104 D4, D6), on the chart kit.
 *
 * A NULL POINT IS A BREAK. A line stops at the last valued point and starts
 * again at the next one; it is never drawn through zero and never carried flat
 * across the stretch. Both ends of an interior gap get an open dot, so a reader
 * can see the series stopped and where it came back. The end of the line gets a
 * filled dot.
 *
 * ONE VIEWBOX, NO MEASURING. Lines are SVG in the kit's viewBox; dots, the
 * tooltip and every rule are HTML over it in percentages. A circle in a
 * `preserveAspectRatio="none"` box is an ellipse, and text in one is smeared.
 *
 * ONE KEY, AS EVERY REPORT KEYS ITS SERIES (amended 2026-11-21). The legend gives
 * each line the square swatch the aggregation chart's key gives a series, and its
 * latest value beside its name, as that key gives each series its total. A column
 * of coloured values at the plot's right edge read as an axis, so there is none.
 * Hovering a legend entry or a line lights that line and dims the rest, as the
 * aggregation chart lights a series; over a line, a tooltip names it, the period
 * end nearest the pointer and its value there.
 *
 * COLOUR. The ledger is the accent. Accounts take the categorical chart hues in
 * the order the server lists them, as the aggregation chart does by rank, and the
 * hues repeat past nine, told apart by name in the legend and the tooltip, and by
 * lighting one.
 *
 * GAIN IS IN EACH LINE'S OWN CURRENCY. The ledger's line arrives converted into
 * the reporting currency (ADR-0114 D7); each account's stays in its own. Nothing
 * converts or sums here: lines of different currencies share a plot and are not
 * added, and each value in the legend and the tooltip carries its own currency.
 * The axis names a currency only when every drawn line has the same one.
 *
 * THE BENCHMARK (ADR-0114 D3) is one dashed slate line on the Index view, drawn
 * against the ledger's line, or the one account's when the report names one. Its
 * points arrive rebased to that series' 100 and paused with it, so a null is a
 * break like any other. It is drawn with a non-scaling stroke, so the dash reads
 * the same at every width.
 */

const { width: VB_W, height: VB_H } = VIEWBOX;

/**
 * Written out, because Tailwind reads class names off the source text and
 * could not see one assembled from a hue at run time.
 */
const STROKE: Readonly<Record<ChartHue, string>> = {
    groc: 'stroke-cat-groc-chart', din: 'stroke-cat-din-chart', house: 'stroke-cat-house-chart',
    util: 'stroke-cat-util-chart', sub: 'stroke-cat-sub-chart', tran: 'stroke-cat-tran-chart',
    sal: 'stroke-cat-sal-chart', phone: 'stroke-cat-phone-chart', rec: 'stroke-cat-rec-chart',
};
const FILL: Readonly<Record<ChartHue, string>> = {
    groc: 'fill-cat-groc-chart', din: 'fill-cat-din-chart', house: 'fill-cat-house-chart',
    util: 'fill-cat-util-chart', sub: 'fill-cat-sub-chart', tran: 'fill-cat-tran-chart',
    sal: 'fill-cat-sal-chart', phone: 'fill-cat-phone-chart', rec: 'fill-cat-rec-chart',
};
const BG: Readonly<Record<ChartHue, string>> = {
    groc: 'bg-cat-groc-chart', din: 'bg-cat-din-chart', house: 'bg-cat-house-chart',
    util: 'bg-cat-util-chart', sub: 'bg-cat-sub-chart', tran: 'bg-cat-tran-chart',
    sal: 'bg-cat-sal-chart', phone: 'bg-cat-phone-chart', rec: 'bg-cat-rec-chart',
};
const BORDER: Readonly<Record<ChartHue, string>> = {
    groc: 'border-cat-groc-chart', din: 'border-cat-din-chart', house: 'border-cat-house-chart',
    util: 'border-cat-util-chart', sub: 'border-cat-sub-chart', tran: 'border-cat-tran-chart',
    sal: 'border-cat-sal-chart', phone: 'border-cat-phone-chart', rec: 'border-cat-rec-chart',
};

interface Colours { stroke: string; fill: string; bg: string; border: string }

const LEDGER_COLOURS: Colours = {
    stroke: 'stroke-accent', fill: 'fill-accent', bg: 'bg-accent', border: 'border-accent',
};

/**
 * The benchmark's slate: the one neutral no series takes (chartSeries.ts keeps it
 * out of the nine hues), in the -text shade, which holds its contrast as a line.
 */
const BENCHMARK_COLOURS: Colours = {
    stroke: 'stroke-cat-xfer-text', fill: 'fill-cat-xfer-text', bg: 'bg-cat-xfer-text', border: 'border-cat-xfer-text',
};

function coloursFor(s: ReturnsSeries, accountRank: number): Colours {
    if (s.scope === 'ledger') return LEDGER_COLOURS;
    const hue = CHART_HUES[accountRank % CHART_HUES.length]!;
    return { stroke: STROKE[hue], fill: FILL[hue], bg: BG[hue], border: BORDER[hue] };
}

interface Drawn {
    key: string;
    /** The series the line is, or for the benchmark, the series it is drawn against. */
    series: ReturnsSeries;
    /** What the legend calls it. */
    name: string;
    kind: 'ledger' | 'account' | 'benchmark';
    colours: Colours;
    /** Per period; null breaks the line. */
    values: (number | null)[];
}

/** The unbroken runs of a series, as index ranges, and the gaps between them. */
function runsOf(values: readonly (number | null)[]): { start: number; end: number }[] {
    const runs: { start: number; end: number }[] = [];
    let start = -1;
    values.forEach((v, i) => {
        if (v === null) {
            if (start >= 0) runs.push({ start, end: i - 1 });
            start = -1;
        } else if (start < 0) {
            start = i;
        }
    });
    if (start >= 0) runs.push({ start, end: values.length - 1 });
    return runs;
}

/** The accessible name: what the picture shows, not what it is. */
function finding(drawn: readonly Drawn[], show: ReturnsShow, periods: number, interval: string): string {
    const ledger = drawn.find((d) => d.kind === 'ledger');
    const bench = drawn.find((d) => d.kind === 'benchmark');
    const accounts = drawn.filter((d) => d.kind === 'account').length;
    const span = `over ${periods} ${interval}${periods === 1 ? '' : 's'}`;
    const ends = (d: Drawn) => {
        const vals = d.values.filter((v): v is number => v !== null);
        return { first: vals[0]!, last: vals[vals.length - 1]! };
    };
    const benchText = bench ? `the benchmark, ${bench.name}, at ${ends(bench).last.toFixed(0)}` : '';
    if (ledger) {
        const withText = accounts === 0
            ? (bench ? `, with ${benchText}` : '')
            : `, with ${accounts} account line${accounts === 1 ? '' : 's'}${bench ? ` and ${benchText}` : ''}`;
        const { first, last } = ends(ledger);
        return show === 'index'
            ? `Ledger time-weighted return index went from ${first.toFixed(0)} to ${last.toFixed(0)} ${span}${withText}.`
            : `Ledger cumulative gain ended at ${formatCurrency(last, ledger.series.currencyCode)} ${span}${withText}.`;
    }
    return `${accounts} account ${show === 'index' ? 'index' : 'gain'} line${accounts === 1 ? '' : 's'} ${span}`
        + `${bench ? `, with ${benchText}` : ''}.`;
}

export function ReturnsChart({
    series, show, interval, benchmark = null, fit = false,
}: {
    series: readonly ReturnsSeries[];
    show: ReturnsShow;
    interval: 'month' | 'quarter' | 'year';
    /** The result's benchmark. Drawn on Index only, and only while it is the ledger's. */
    benchmark?: ReturnsBenchmark | null;
    /** Fill a box of fixed height (a home tile) rather than the chart's own height. */
    fit?: boolean;
}) {
    // The line under the pointer, from the plot or the legend; the rest dim, so a
    // colour another line shares stops being ambiguous. Over the plot, also the
    // period end nearest the pointer, for the tooltip.
    const [lit, setLit] = useState<string | null>(null);
    const [probe, setProbe] = useState<{ key: string; i: number } | null>(null);

    // The ledger first in the list, accounts after, the benchmark last, so the legend
    // reads in that order; the ledger is drawn last, on top.
    const accounts = series.filter((s) => s.scope === 'account');
    const ledger = series.find((s) => s.scope === 'ledger');
    const ordered = [...(ledger ? [ledger] : []), ...accounts];

    const lines: Drawn[] = ordered.flatMap((s) => {
        const values = s.points.map((p) => valueOf(p, show));
        if (values.every((v) => v === null)) return [];
        const rank = accounts.indexOf(s);
        return [{
            key: s.scope === 'ledger' ? 'ledger' : `acct:${s.accountId}`,
            series: s,
            name: seriesName(s),
            kind: s.scope,
            colours: coloursFor(s, rank),
            values,
        }];
    });
    if (lines.length === 0) return null;

    const anchor = benchmarkAnchor(series);
    const benchValues = show === 'index' && drawableBenchmark(benchmark) && anchor
        ? anchor.points.map((p) => p.benchmark)
        : [];
    const drawn: Drawn[] = benchValues.some((v) => v !== null)
        ? [...lines, {
            key: 'benchmark',
            series: anchor!,
            name: benchmarkLabel(benchmark!.name, benchmark!.symbol),
            kind: 'benchmark',
            colours: BENCHMARK_COLOURS,
            values: benchValues,
        }]
        : lines;

    const periodEnds = (ledger ?? accounts[0]!).points.map((p) => p.periodEnd);
    const labels = axisLabelsFor(periodEnds, interval);
    const n = periodEnds.length;
    const columns = band(n, VB_W);

    const all = drawn.flatMap((d) => d.values).filter((v): v is number => v !== null);
    // An index reads against its base of 100, so the base is always in view; a
    // gain reads against zero, which `extent` always includes.
    const domain: readonly [number, number] = show === 'index'
        ? (() => {
            const lo = Math.min(100, ...all);
            const hi = Math.max(100, ...all);
            const pad = (hi - lo || 1) * 0.12;
            return [lo - pad, hi + pad] as const;
        })()
        : extent(all);
    const y = vertical(domain, VB_H);
    const tickValues = ticks(domain, 4);

    // This check decides one thing: whether the Gain axis names a currency. Only the
    // ledger's line is converted (into the reporting currency); each account's line
    // stays in its own (ADR-0114 D7), so one plot can hold lines in several currencies.
    // Then the axis names none, and each value in the legend and the tooltip carries
    // its line's own. It reads `lines`, not `drawn`: the benchmark is never drawn on Gain.
    const currencies = new Set(lines.map((d) => d.series.currencyCode));
    const axisFormat = show === 'index'
        ? (v: number) => v.toFixed(0)
        : currencies.size === 1
            ? axisMoneyFor(tickValues, [...currencies][0]!)
            : (v: number) => (v === 0 ? '0' : String(v));
    // A value as the legend and the tooltip print it: an index as the table does, a
    // gain in its line's own currency, as the aggregation key prints a total.
    const valueText = (d: Drawn, v: number) => (show === 'index' ? formatIndex(v) : formatCurrency(v, d.series.currencyCode));

    const xPct = (i: number) => (columns.center(i) / VB_W) * 100;
    const yPct = (v: number) => (y(v) / VB_H) * 100;

    const ends = drawn.map((d) => {
        const last = d.values.length - 1 - [...d.values].reverse().findIndex((v) => v !== null);
        return { d, i: last, v: d.values[last]! };
    });
    // The first period any line has a value; a line that starts later says so.
    const firstAny = Math.min(...drawn.map((d) => d.values.findIndex((v) => v !== null)));

    // The benchmark first, under everything; accounts next; the ledger last so its
    // thick line is on top. The order never changes with the pointer: re-sorting would
    // remount the band under it, and the band would never hear the pointer leave. A lit
    // line is drawn once more over all of them instead, as a copy the pointer passes through.
    const LAYER = { benchmark: 0, account: 1, ledger: 2 } as const;
    const paintOrder = [...drawn].sort((a, b) => LAYER[a.kind] - LAYER[b.kind]);
    // A lit key no longer drawn (the benchmark, after a switch to Gain) lights nothing.
    const litLine = drawn.find((d) => d.key === lit) ?? null;
    const dim = (d: Drawn) => litLine !== null && litLine.key !== d.key;
    const pointsOf = (d: Drawn, r: { start: number; end: number }): Point[] => {
        const points: Point[] = [];
        for (let i = r.start; i <= r.end; i++) points.push({ x: columns.center(i), y: y(d.values[i]!) });
        return points;
    };
    const strokeOf = (d: Drawn, points: Point[], key: string) => (
        <Line
            key={key}
            points={points}
            className={d.colours.stroke}
            // The benchmark's width is in screen pixels (non-scaling), the others' in
            // viewBox units.
            width={d.kind === 'ledger' ? 2.6 : d.kind === 'benchmark' ? 1.5 : 1.4}
            dashed={d.kind === 'benchmark'}
            nonScaling={d.kind === 'benchmark'}
        />
    );

    // The valued period end nearest the pointer, along the line it is over.
    const nearest = (d: Drawn, e: MouseEvent<SVGElement>): number | null => {
        const box = e.currentTarget.ownerSVGElement?.getBoundingClientRect();
        if (!box || box.width === 0) return null;
        const at = ((e.clientX - box.left) / box.width) * VB_W;
        let best: number | null = null;
        d.values.forEach((v, i) => {
            if (v === null) return;
            if (best === null || Math.abs(columns.center(i) - at) < Math.abs(columns.center(best) - at)) best = i;
        });
        return best;
    };
    const leave = () => {
        setLit(null);
        setProbe(null);
    };

    const dot = (key: string, i: number, v: number, d: Drawn, kind: 'gap-edge' | 'late-start' | 'end' | 'probe') => (
        <span
            key={key}
            aria-hidden
            data-point={kind}
            data-series={d.key}
            className={cn(
                'pointer-events-none absolute -translate-x-1/2 -translate-y-1/2 rounded-full',
                d.colours.border,
                // An open marker: an 8px dot with a 2px ring in the line's colour on the surface.
                kind === 'end' ? cn('size-fixed-6px border', d.colours.bg)
                    : kind === 'probe' ? cn('size-icon-2xs border-2', d.colours.bg)
                        : 'size-icon-2xs border-2 bg-surface',
                dim(d) && 'opacity-25',
            )}
            style={{ left: `${xPct(i)}%`, top: `${yPct(v)}%` }}
        />
    );

    const probed = probe ? drawn.find((d) => d.key === probe.key) : undefined;
    const probedValue = probed && probe ? probed.values[probe.i] : null;

    return (
        <figure className={cn('m-0 flex min-h-0 flex-col', fit && 'h-full')}>
            {/* Leaving the plot lets go of any line, whatever a band heard. */}
            <div className={cn('relative', fit ? 'min-h-fixed-64px flex-1' : 'h-fixed-224px')} onMouseLeave={leave}>
                <ChartFrame
                    heightClass="h-full"
                    ariaLabel={finding(drawn, show, n, interval)}
                    gutter
                    underlay={
                        <ValueAxis
                            tickValues={tickValues}
                            toPercent={yPct}
                            format={axisFormat}
                            zeroRule={false}
                        />
                    }
                    overlay={
                        <>
                            {show === 'gain' ? <ZeroRule atPercent={yPct(0)} /> : null}
                            {drawn.flatMap((d) => {
                                const runs = runsOf(d.values);
                                // Interior gaps only: the last point of a run that is followed
                                // by another, and the first point of a run that follows one.
                                return runs.flatMap((r, k) => [
                                    ...(k > 0 ? [dot(`${d.key}:s${k}`, r.start, d.values[r.start]!, d, 'gap-edge')] : []),
                                    ...(k === 0 && r.start > firstAny ? [dot(`${d.key}:late`, r.start, d.values[r.start]!, d, 'late-start')] : []),
                                    ...(k < runs.length - 1 ? [dot(`${d.key}:e${k}`, r.end, d.values[r.end]!, d, 'gap-edge')] : []),
                                ]);
                            })}
                            {ends.map((e) => dot(`${e.d.key}:end`, e.i, e.v, e.d, 'end'))}
                            {probed && probe && probedValue != null ? (
                                <>
                                    {dot(`${probed.key}:probe`, probe.i, probedValue, probed, 'probe')}
                                    <span
                                        role="tooltip"
                                        data-returns-tooltip
                                        className={cn(
                                            'pointer-events-none absolute z-10 -mt-2 -translate-y-full whitespace-nowrap rounded border border-border bg-surface px-1.5 py-0.5 text-[0.6875rem] leading-[1rem] text-text shadow-sm',
                                            // Kept inside the plot: right of the point near the left edge,
                                            // left of it near the right edge, centred between.
                                            xPct(probe.i) < 25 ? '' : xPct(probe.i) > 75 ? '-translate-x-full' : '-translate-x-1/2',
                                        )}
                                        style={{ left: `${xPct(probe.i)}%`, top: `${yPct(probedValue)}%` }}
                                    >
                                        {probed.kind === 'benchmark' ? `Benchmark: ${probed.name}` : probed.name}
                                        {' · '}
                                        {periodEnds[probe.i]!.slice(0, 10)}
                                        {' · '}
                                        <span className="font-mono tabular-nums">{valueText(probed, probedValue)}</span>
                                    </span>
                                </>
                            ) : null}
                        </>
                    }
                >
                    {paintOrder.flatMap((d) => runsOf(d.values).map((r) => {
                        const points = pointsOf(d, r);
                        return (
                            <g key={`${d.key}:${r.start}`} data-line={d.key} className={cn(dim(d) && 'opacity-25')}>
                                {strokeOf(d, points, 'stroke')}
                                {/* What the pointer finds: a band a few pixels either side
                                    of the line, at every width, drawn but not seen. */}
                                <polyline
                                    data-hit={d.key}
                                    points={pointsAttr(points)}
                                    className="fill-none stroke-transparent"
                                    strokeWidth={10}
                                    vectorEffect="non-scaling-stroke"
                                    pointerEvents="stroke"
                                    onMouseEnter={() => setLit(d.key)}
                                    onMouseMove={(e) => {
                                        const i = nearest(d, e);
                                        setProbe(i === null ? null : { key: d.key, i });
                                    }}
                                    onMouseLeave={leave}
                                />
                            </g>
                        );
                    }))}
                    {litLine ? (
                        <g data-lit-copy={litLine.key} pointerEvents="none">
                            {runsOf(litLine.values).map((r) => strokeOf(litLine, pointsOf(litLine, r), `lit:${r.start}`))}
                        </g>
                    ) : null}
                </ChartFrame>
            </div>
            <AxisLabels labels={labels} gutter />
            <figcaption
                data-returns-legend
                className={cn(
                    'mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[0.6875rem] leading-[1rem] text-text-muted',
                    // In a tile the legend gives way to the plot's floor and scrolls, rather
                    // than running out of the tile's fixed height.
                    fit && 'min-h-0 shrink overflow-y-auto',
                )}
            >
                {drawn.map((d) => {
                    const name = d.kind === 'benchmark' ? `Benchmark: ${d.name}` : d.name;
                    const end = ends.find((e) => e.d.key === d.key)!;
                    return (
                        <LegendEntry
                            key={d.key}
                            dim={dim(d)}
                            onLit={(on) => setLit(on ? d.key : null)}
                        >
                            <span className="inline-flex min-w-0 max-w-full items-center gap-1" data-swatch={d.kind}>
                                <LegendChip fill={d.colours.fill} />
                                <span className="min-w-0 truncate" title={name}>{name}</span>
                            </span>
                            <span className="font-mono tabular-nums text-text-subtle" data-legend-value>
                                {valueText(d, end.v)}
                            </span>
                        </LegendEntry>
                    );
                })}
            </figcaption>
        </figure>
    );
}
