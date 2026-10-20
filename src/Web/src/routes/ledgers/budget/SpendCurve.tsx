import { Band, Line, type Point } from '@/components/chart/Series';
import { ChartFrame, Marker, Rule, VIEWBOX } from '@/components/chart/ChartFrame';
import { vertical } from '@/components/chart/scale';

/**
 * Cumulative spend for a month, drawn against a shaded band of what the
 * trailing months did by the same day, stopping dead at today, with a dashed
 * projection continuing at the rate seen so far.
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

export interface SpendCurveProps {
    /** Running total per day; index 0 is day 1. */
    series: readonly number[];
    /** The trailing window's day-of-month profile, accumulated. */
    normal: readonly number[];
    daysInMonth: number;
    elapsedDays: number;
    ceiling: number | null;
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
    heightClass,
    ariaLabel,
    showCeilingLabel = true,
    ceilingLabel = 'ceiling',
}: SpendCurveProps) {
    const spent = elapsedDays > 0 ? (series[Math.min(elapsedDays, series.length) - 1] ?? 0) : 0;
    const rate = elapsedDays > 0 ? spent / elapsedDays : 0;
    const projectedEnd = rate * daysInMonth;

    // Headroom so nothing sits flush against the frame — a line touching the
    // top edge reads as clipped rather than high. Not `extent`, which always
    // includes zero and pads BOTH ends: a cumulative total never goes below
    // zero, and a floor below it would waste a fifth of the chart.
    const top = Math.max(spent, ceiling ?? 0, projectedEnd, normal.at(-1) ?? 0, 1) * 1.18;
    const x = (day: number) => ((day - 1) / Math.max(daysInMonth - 1, 1)) * VB_W;
    const y = vertical([0, top], VB_H);
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

    // Split where the projection passes the ceiling, so the part that is a
    // problem is the part that is red. A segment left with a single point
    // draws nothing — which is right, and is why a projection already over
    // the ceiling at its first step is entirely red.
    const projUnder: Point[] = [];
    const projOver: Point[] = [];
    if (elapsedDays >= 1 && elapsedDays < daysInMonth && spent > 0) {
        for (let d = elapsedDays; d <= daysInMonth; d++) {
            const v = rate * d;
            const pt = at(d, v);
            if (ceiling === null || v <= ceiling) {
                projUnder.push(pt);
            } else {
                if (projOver.length === 0 && projUnder.length > 0) projOver.push(projUnder.at(-1)!);
                projOver.push(pt);
            }
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
                </>
            }
        >
            <Band upper={hi} lower={lo} className="fill-surface-hover/40" />
            <Line points={projUnder} className="stroke-accent/70" dashed />
            <Line points={projOver} className="stroke-state-danger" dashed />
            <Line points={solid} className="stroke-accent" width={2.5} />
        </ChartFrame>
    );
}
