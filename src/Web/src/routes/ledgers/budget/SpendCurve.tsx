import { Band, Line, type Point } from '@/components/chart/Series';
import { ChartFrame, Marker, Rule, VIEWBOX } from '@/components/chart/ChartFrame';
import { vertical } from '@/components/chart/scale';

import { pastBy, projectedDays, type ScheduledDays } from './cumulative';

/**
 * Cumulative spend for a month, drawn against a shaded band of what the
 * trailing months did by the same day, stopping dead at today, with a dashed
 * projection from there: what is spent so far plus the larger of the spend still
 * scheduled and what the band's baseline usually adds over the rest of the
 * month, plus every credit still scheduled (`projectedDays`, the same rule the
 * figures beside the chart read).
 *
 * Built on the shared chart kit (ADR-0104 D6), which this component is the
 * ancestor of: the kit generalises what was proved here, and this moved onto
 * it rather than staying beside it. Everything the original did deliberately
 * still holds, now in one place instead of this file — one viewBox with
 * `preserveAspectRatio="none"` so it never measures its container, theme
 * classes so both themes follow the tokens, and the ceiling as HTML so a 1px
 * rule stays 1px whether this is a hero or a table row.
 */

const { width: VB_W, height: VB_H } = VIEWBOX;

const NOTHING_SCHEDULED: ScheduledDays = { debits: [], credits: [] };

export interface SpendCurveProps {
    /** Running total per day; index 0 is day 1. */
    series: readonly number[];
    /** The trailing window's mean day-of-month profile, accumulated: the band is
     *  drawn around it, and the projection adds what it usually adds after today. */
    normal: readonly number[];
    daysInMonth: number;
    elapsedDays: number;
    ceiling: number | null;
    /** What the figures are in: the projection turns red only past the ceiling as
     *  the two print, as the words beside the chart judge it. */
    currency: string;
    /**
     * Rows dated after today, per day and split by sign (index 0 is day 1): each
     * day with one is marked with a circle on its date, where the projection steps
     * by as much as its rows move it (ADR-0109 D10).
     */
    scheduled?: ScheduledDays;
    /** Tailwind height utility — a pinned token, never a numeric sizer. */
    heightClass: string;
    ariaLabel: string;
    /** Hide the ceiling's own caption when the surrounding UI already says it. */
    showCeilingLabel?: boolean;
    ceilingLabel?: string;
}

export function SpendCurve({
    series,
    normal,
    daysInMonth,
    elapsedDays,
    ceiling,
    currency,
    scheduled = NOTHING_SCHEDULED,
    heightClass,
    ariaLabel,
    showCeilingLabel = true,
    ceilingLabel = 'ceiling',
}: SpendCurveProps) {
    const projection = projectedDays(series, elapsedDays, daysInMonth, scheduled, normal) ?? [];
    // The projection draws both ends of each scheduled row's step, so both count:
    // a credit's step starts above where the path lands.
    const projected = projection.flatMap((p) => [p.value, p.beforeBill]);
    const projectedHigh = Math.max(0, ...projected);

    // Headroom so nothing sits flush against the frame — a line touching the
    // top edge reads as clipped rather than high. Not `extent`, which always
    // includes zero and pads BOTH ends: the floor is zero unless something drawn
    // sits below it, so a month that stays above zero spends no height on a floor
    // it never reaches. A cumulative total can go below zero all the same (a
    // refund that leaves a day net negative, or a credit scheduled that is larger
    // than what is spent plus the usual rest of the month), and then the floor is
    // the lowest point drawn, padded as the top is, so the line and its circle stay
    // inside the frame. The top reads every point drawn too, not only today's total
    // and the month's end: a running total can peak before today and fall with a
    // refund, and a baseline can peak before the month ends.
    const drawnDays = series.slice(0, Math.min(elapsedDays, daysInMonth));
    // The band's outer edge, which is its upper edge for a baseline above zero and
    // its lower edge for one below.
    const bandOuter = normal.map((v) => v * 1.14);
    const top = Math.max(...drawnDays, ceiling ?? 0, projectedHigh, ...bandOuter, 1) * 1.18;
    const lowest = Math.min(0, ...drawnDays, ...projected, ...bandOuter);
    const bottom = lowest < 0 ? lowest * 1.18 : 0;
    const x = (day: number) => ((day - 1) / Math.max(daysInMonth - 1, 1)) * VB_W;
    const y = vertical([bottom, top], VB_H);
    const at = (day: number, value: number): Point => ({ x: x(day), y: y(value) });

    // "About here", not a statistical interval — calling it anything more
    // precise would be a claim the data does not support.
    const hi: Point[] = [];
    const lo: Point[] = [];
    for (let d = 1; d <= daysInMonth; d++) {
        const v = normal[d - 1] ?? 0;
        hi.push(at(d, v * 1.14));
        lo.push(at(d, v * 0.86));
    }

    const solid: Point[] = [];
    for (let d = 1; d <= Math.min(elapsedDays, daysInMonth); d++) {
        solid.push(at(d, series[d - 1] ?? 0));
    }

    // The projection as a path of values, day by day, and on each day with a row
    // scheduled a vertical step (two points on one day) by as much as that day's rows
    // move it: a bill by the part of it that lifts the spend scheduled through that
    // day above what the baseline adds from tomorrow through that day, which can be
    // all of it, part of it or none, and a credit down by all of it. The circle marks
    // the date either way.
    // Split into runs wherever it changes sides of the ceiling, so the part that is
    // a problem is the part that is red. The path can come back under (a usual day
    // that is net a refund, or a scheduled credit), so there can be more than two runs,
    // in order. Each run starts at the point the one before ended on, so the line is
    // continuous, and a segment takes the colour of the point it leads to. A run
    // left with a single point draws nothing, which is right, and is why a
    // projection already over the ceiling at its first step is entirely red. Past is
    // pastBy's, as the date and the figure beside the chart read it: a total the two
    // figures print as equal is at the ceiling, not past.
    // Nothing is drawn only for a path flat at zero with no row scheduled: a month
    // with only a refund scheduled still draws its step and circle, which is the mark
    // the legend describes.
    const runs: { over: boolean; points: Point[] }[] = [];
    const steps: { day: number; value: number }[] = [];
    if (elapsedDays >= 1 && elapsedDays < daysInMonth
        && projection.some((p) => p.value !== 0 || p.bill > 0 || p.credit < 0)) {
        const values: { day: number; value: number }[] = [];
        for (const p of projection) {
            if (p.bill > 0 || p.credit < 0) {
                values.push({ day: p.day, value: p.beforeBill });
                steps.push({ day: p.day, value: p.value });
            }
            values.push({ day: p.day, value: p.value });
        }
        for (const { day, value } of values) {
            const pt = at(day, value);
            const over = ceiling !== null && pastBy(value, ceiling, currency) > 0;
            const run = runs.at(-1);
            if (run === undefined) runs.push({ over, points: [pt] });
            else if (run.over === over) run.points.push(pt);
            else runs.push({ over, points: [run.points.at(-1)!, pt] });
        }
    }

    const pct = (v: number) => (y(v) / VB_H) * 100;

    return (
        <ChartFrame
            heightClass={heightClass}
            ariaLabel={ariaLabel}
            overlay={
                <>
                    {ceiling !== null && ceiling > 0 ? (
                        <Rule
                            atPercent={pct(ceiling)}
                            label={showCeilingLabel ? ceilingLabel : undefined}
                        />
                    ) : null}
                    {elapsedDays >= 1 && elapsedDays < daysInMonth ? (
                        <Marker
                            atPercent={(x(Math.min(elapsedDays, daysInMonth)) / VB_W) * 100}
                        />
                    ) : null}
                    {/* A circle where a scheduled bill or refund lands. HTML, not SVG: the
                        frame stretches its viewBox unevenly, which would draw an
                        SVG circle as an ellipse of a different shape at every width. */}
                    {steps.map((s) => (
                        <span
                            key={s.day}
                            aria-hidden
                            data-scheduled-step={s.day}
                            className="pointer-events-none absolute size-fixed-6px -translate-x-1/2 -translate-y-1/2 rounded-full border-[1.5px] border-text-subtle bg-surface"
                            style={{ left: `${(x(s.day) / VB_W) * 100}%`, top: `${pct(s.value)}%` }}
                        />
                    ))}
                </>
            }
        >
            <Band upper={hi} lower={lo} className="fill-surface-hover/40" />
            {runs.map((run, i) => (
                <Line key={i} points={run.points} className={run.over ? 'stroke-mark-danger' : 'stroke-accent/70'} dashed />
            ))}
            <Line points={solid} className="stroke-accent" width={2.5} />
        </ChartFrame>
    );
}
