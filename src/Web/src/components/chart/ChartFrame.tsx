import type { ReactNode } from 'react';

import { cn } from '@/lib/cn';

import { hideLabelBelow, hideLastBelow, labelLength, labelSpacings } from './axisLabels';

/**
 * The box a chart draws in, and the rules drawn over it.
 *
 * ONE `viewBox` with `preserveAspectRatio="none"`, filling its container
 * without measuring it. No ResizeObserver, which is not a style choice: the
 * test environment stubs it to a no-op, so a chart that sized itself from its
 * container renders 0×0 while every assertion passes (ADR-0104 D6). Height
 * comes in as a pinned Tailwind class, never a number, so the same chart is a
 * hero and a table row without learning its own size.
 *
 * `role="img"` with an `aria-label` that STATES THE FINDING. A chart's
 * accessible name is the only thing a screen reader gets, so "spending by
 * category" is a wasted sentence — it should say what the picture shows.
 */

/** The coordinate space every mark is positioned in. */
export const VIEWBOX = { width: 600, height: 180 } as const;

export function ChartFrame({
    children, overlay, underlay, heightClass, ariaLabel, gutter = false,
    width = VIEWBOX.width, height = VIEWBOX.height,
}: {
    /** SVG marks, already positioned. */
    children: ReactNode;
    /**
     * HTML drawn BENEATH the marks, positioned in percentages: the gridlines a
     * value is read against, which a bar must cover rather than be struck
     * through by.
     */
    underlay?: ReactNode;
    /**
     * HTML drawn over the chart, positioned in PERCENTAGES.
     *
     * This is where a 1px rule belongs. An SVG stroke is scaled by the
     * viewBox, so the same component's "hairline" is thick in a hero and
     * invisible in a row; an HTML border is 1px at every size and takes the
     * theme tokens directly.
     */
    overlay?: ReactNode;
    /** Pinned height token, e.g. `h-fixed-176px`. Never a numeric sizer. */
    heightClass: string;
    ariaLabel: string;
    /**
     * Reserve a left margin outside the plot for a `ValueAxis` to label into.
     *
     * Without it the labels are drawn at the plot's own left edge, which is
     * where the first bar is — so the earliest period of every chart reads
     * through a value label printed over it. A chart with no value axis has nothing to put there and
     * should keep the width.
     */
    gutter?: boolean;
    width?: number;
    height?: number;
}) {
    return (
        <div className={cn('relative', heightClass)}>
            {/* The PLOT, inset from the gutter. Marks and overlay share this
                box, so a rule at 40% and a bar at 40% land on the same line. */}
            <div className={cn('absolute inset-y-0 right-0', gutter ? 'left-fixed-40px' : 'left-0')}>
                {underlay}
                <svg
                    viewBox={`0 0 ${width} ${height}`}
                    preserveAspectRatio="none"
                    className="absolute inset-0 h-full w-full"
                    role="img"
                    aria-label={ariaLabel}
                >
                    {children}
                </svg>
                {overlay}
            </div>
        </div>
    );
}

/**
 * A horizontal rule across the chart at `atPercent` of its height — a ceiling,
 * a target, the zero line.
 *
 * HTML rather than SVG, for the reason in `overlay` above. Give it a `label`
 * only where the surrounding UI does not already say what it is. The label sits
 * at the start: a cumulative line starts at zero there, and what is drawn after
 * today (a projection, a scheduled bill) is at the end.
 */
export function Rule({
    atPercent, label, tone = 'subtle',
}: {
    atPercent: number;
    label?: string;
    tone?: 'subtle' | 'strong';
}) {
    return (
        <div
            aria-hidden
            className={cn(
                'pointer-events-none absolute inset-x-0 border-t border-dashed',
                tone === 'strong' ? 'border-text-muted' : 'border-text-subtle',
            )}
            style={{ top: `${atPercent}%` }}
        >
            {label !== undefined ? (
                <span className="absolute -top-4 left-0 bg-surface px-1 text-[0.625rem] text-text-muted">
                    {label}
                </span>
            ) : null}
        </div>
    );
}

/**
 * The value axis: a labelled rule at each tick, drawn across the chart.
 *
 * Without one a bar chart is decorative — a reader can compare two bars but
 * cannot say what either is worth, and a legend of TOTALS does not answer it
 * for a chart whose bars are per-period.
 *
 * HTML over the SVG, for the reason the overlay exists: these rules are
 * hairlines at any chart size, and the labels keep the page's type scale
 * instead of being stretched by a non-uniform viewBox.
 *
 * Zero is drawn STRONGER than the rest. It is the line every other value is
 * read against, and on a chart carrying negatives it is the only thing that
 * says which side of nothing a bar is on.
 */
export function ValueAxis({
    tickValues, toPercent, format, zeroRule = true,
}: {
    tickValues: readonly number[];
    /** Value to a percentage of the frame's height. */
    toPercent: (value: number) => number;
    format: (value: number) => string;
    /**
     * Draw zero's rule here. A chart that puts the axis beneath its marks draws
     * zero over them instead (`ZeroRule` in the overlay), so the line a sign is
     * read against is never hidden by a bar that hangs from it.
     */
    zeroRule?: boolean;
}) {
    return (
        <>
            {tickValues.map((t) => (
                <div
                    key={t}
                    aria-hidden
                    className={cn(
                        'pointer-events-none absolute inset-x-0 border-t',
                        t === 0 ? (zeroRule ? 'border-text-subtle' : 'border-transparent') : 'border-border/60 border-dashed',
                    )}
                    style={{ top: `${toPercent(t)}%` }}
                >
                    {/* In the gutter, not over the plot: `right-full` puts the
                        label's right edge on the plot's left edge. Sitting at
                        `left-0` it covered the first bar, and masking it with
                        a background only hid the overlap.

                        Bottom-aligned to its own rule rather than centred on
                        it: the lowest tick is usually zero, which sits on the
                        frame's floor, and a centred label hangs into whatever
                        is drawn below. */}
                    <span className="absolute bottom-0 right-full whitespace-nowrap pr-1 text-[0.5625rem] leading-[1.4] text-text-subtle">
                        {format(t)}
                    </span>
                </div>
            ))}
        </>
    );
}

/** Zero's rule, solid, for an overlay over the marks (see ValueAxis's `zeroRule`). */
export function ZeroRule({ atPercent }: { atPercent: number }) {
    return (
        <div
            aria-hidden
            data-zero-rule
            className="pointer-events-none absolute inset-x-0 border-t border-text-subtle"
            style={{ top: `${atPercent}%` }}
        />
    );
}

/** A vertical rule at `atPercent` of the width — today, a period boundary. */
export function Marker({ atPercent }: { atPercent: number }) {
    return (
        <div
            aria-hidden
            className="pointer-events-none absolute inset-y-0 w-px bg-border-strong"
            style={{ left: `${atPercent}%` }}
        />
    );
}

/**
 * The last label, centred on its column while it fits there, and otherwise moved
 * back by what it is wider than its column, so it ends at the plot's edge: `cqw`
 * is its column's width, a percentage its own.
 */
const LAST_INSIDE = 'translate-x-[min(0px,calc(50cqw_-_50%))]';

/**
 * Tick labels under a chart, one cell per column, each label centred under the
 * column it names. `labels` holds one entry per column, in order.
 *
 * No label overlaps another at any width. Each column is a container, and each
 * label is drawn only while its column is wide enough for it at the widest
 * spacing it belongs to (axisLabels.ts): as the chart narrows the labels thin to
 * every second, fourth and so on, evenly spaced from the first, with the last
 * period's label drawn too while the axis (a container too) has room for it and
 * the first. A label never wraps. The first may run into the
 * gutter, which is empty under the plot; the last sits at its column's end when
 * it is wider than its column, so it never runs past the plot's edge.
 *
 * Deliberately NOT inside the SVG: text in a `preserveAspectRatio="none"`
 * viewBox is stretched with the box, so the same label is condensed in a wide
 * chart and smeared in a narrow one. As HTML it keeps the page's type scale.
 * The columns are the plot's own bands (equal width, as `band` lays them out),
 * so a label can never drift from its column at any width.
 */
export function AxisLabels({
    labels, gutter = false,
}: { labels: readonly string[]; gutter?: boolean }) {
    if (labels.length === 0) return null;
    const spacings = labelSpacings(labels.length);
    const length = labelLength(labels);
    const last = labels.length - 1;
    // `mt-3`, not `mt-1`. The value axis puts its lowest label just inside the
    // frame's bottom edge, and at mt-1 the first period label sat under it —
    // close enough that the two overlapped and "2025-11" rendered as "25-11".
    //
    // `gutter` has to match the frame's. These labels name COLUMNS, so an
    // indent the plot has and the labels do not slides every period label off
    // the thing it names.
    return (
        <div
            aria-hidden
            data-axis-labels
            className={cn(
                '@container/axis mt-3 grid shrink-0 text-[0.625rem] text-text-subtle',
                gutter ? 'ml-fixed-40px' : undefined,
            )}
            style={{ gridTemplateColumns: `repeat(${labels.length}, minmax(0, 1fr))` }}
        >
            {labels.map((l, i) => (
                <span key={i} data-axis-column className="@container/period flex min-w-0 justify-center">
                    <span
                        data-axis-label
                        className={cn(
                            'shrink-0 whitespace-nowrap tabular-nums',
                            hideLabelBelow(length, spacings[i]!),
                            i === last && last > 0 && [hideLastBelow(length, labels.length), LAST_INSIDE],
                        )}
                    >
                        {l}
                    </span>
                </span>
            ))}
        </div>
    );
}
