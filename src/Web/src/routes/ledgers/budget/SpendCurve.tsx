import { cn } from '@/lib/cn';

/**
 * Cumulative spend for a month, drawn against a shaded band of what the
 * trailing months did by the same day, stopping dead at today, with a dashed
 * projection continuing at the rate seen so far.
 *
 * Shared by the page hero and by an expanded category row, so the two are the
 * same picture at two sizes — you learn the shape once. Everything is one
 * `<svg>` with a `viewBox` and `preserveAspectRatio="none"`, so it fills its
 * container without measuring: no ResizeObserver, which matters because the
 * test environment stubs that to a no-op and a measuring chart would render
 * 0×0 while every assertion passed.
 */

const VB_W = 600;
const VB_H = 180;

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
    // top edge reads as clipped rather than high.
    const top = Math.max(spent, ceiling ?? 0, projectedEnd, normal.at(-1) ?? 0, 1) * 1.18;
    const x = (day: number) => ((day - 1) / Math.max(daysInMonth - 1, 1)) * VB_W;
    const y = (v: number) => VB_H - (v / top) * VB_H;

    // "About here", not a statistical interval — calling it anything more
    // precise would be a claim the data does not support.
    const hi: string[] = [];
    const lo: string[] = [];
    for (let d = 1; d <= daysInMonth; d++) {
        const v = normal[d - 1] ?? 0;
        hi.push(`${x(d).toFixed(1)},${y(v * 1.14).toFixed(1)}`);
        lo.push(`${x(d).toFixed(1)},${y(v * 0.86).toFixed(1)}`);
    }
    const band = [...hi, ...lo.reverse()].join(' ');

    const solid: string[] = [];
    for (let d = 1; d <= Math.min(elapsedDays, daysInMonth); d++) {
        solid.push(`${x(d).toFixed(1)},${y(series[d - 1] ?? 0).toFixed(1)}`);
    }

    // Split where the projection passes the ceiling, so the part that is a
    // problem is the part that is red.
    const projUnder: string[] = [];
    const projOver: string[] = [];
    if (elapsedDays >= 1 && elapsedDays < daysInMonth && spent > 0) {
        for (let d = elapsedDays; d <= daysInMonth; d++) {
            const v = rate * d;
            const pt = `${x(d).toFixed(1)},${y(v).toFixed(1)}`;
            if (ceiling === null || v <= ceiling) {
                projUnder.push(pt);
            } else {
                if (projOver.length === 0 && projUnder.length > 0) projOver.push(projUnder.at(-1)!);
                projOver.push(pt);
            }
        }
    }

    return (
        <div className={cn('relative', heightClass)}>
            <svg
                viewBox={`0 0 ${VB_W} ${VB_H}`}
                preserveAspectRatio="none"
                className="absolute inset-0 h-full w-full"
                role="img"
                aria-label={ariaLabel}
            >
                <polygon points={band} className="fill-surface-hover/40" />
                {projUnder.length > 1 ? (
                    <polyline
                        points={projUnder.join(' ')}
                        className="fill-none stroke-accent/70"
                        strokeWidth={2}
                        strokeDasharray="4 4"
                    />
                ) : null}
                {projOver.length > 1 ? (
                    <polyline
                        points={projOver.join(' ')}
                        className="fill-none stroke-state-danger"
                        strokeWidth={2}
                        strokeDasharray="4 4"
                    />
                ) : null}
                {solid.length > 1 ? (
                    <polyline
                        points={solid.join(' ')}
                        className="fill-none stroke-accent"
                        strokeWidth={2.5}
                        strokeLinecap="round"
                        strokeLinejoin="round"
                    />
                ) : null}
            </svg>

            {/* The ceiling is HTML, not SVG: a dashed border takes the theme
                tokens directly and stays 1px at any chart size, where an SVG
                stroke is scaled by the viewBox — so the same rule would be
                thick in the hero and hairline in a row. */}
            {ceiling !== null && ceiling > 0 ? (
                <div
                    aria-hidden
                    className="pointer-events-none absolute inset-x-0 border-t border-dashed border-text-subtle"
                    style={{ top: `${(y(ceiling) / VB_H) * 100}%` }}
                >
                    {showCeilingLabel ? (
                        <span className="absolute -top-4 right-0 bg-surface px-1 text-[0.625rem] text-text-muted">
                            {ceilingLabel}
                        </span>
                    ) : null}
                </div>
            ) : null}

            {elapsedDays >= 1 && elapsedDays < daysInMonth ? (
                <div
                    aria-hidden
                    className="pointer-events-none absolute inset-y-0 w-px bg-border-strong"
                    style={{ left: `${(x(Math.min(elapsedDays, daysInMonth)) / VB_W) * 100}%` }}
                />
            ) : null}
        </div>
    );
}
