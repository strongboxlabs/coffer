import { cn } from '@/lib/cn';

/**
 * The marks a chart draws, as components over pre-scaled points.
 *
 * Every one takes POSITIONS, not data. Scaling happens in `scale.ts` and
 * nothing here knows what a value means — which keeps the geometry assertable
 * on its own and stops a series quietly re-deriving a domain the axis already
 * chose.
 *
 * Colour arrives as a class, never as a prop with a hex in it, so both themes
 * follow the tokens and the contrast audit can see every mark (ADR-0104 D6).
 */

export interface Point {
    x: number;
    y: number;
}

/** One decimal is plenty at these sizes, and it keeps the DOM readable. */
const fmt = (n: number) => n.toFixed(1);

export const pointsAttr = (points: readonly Point[]): string =>
    points.map((p) => `${fmt(p.x)},${fmt(p.y)}`).join(' ');

/**
 * A line through the points. Nothing is drawn for fewer than two, because a
 * one-point polyline is invisible anyway and rendering it would leave an empty
 * element for a test to find and believe in.
 */
export function Line({
    points, className, dashed = false, width = 2,
}: {
    points: readonly Point[];
    /** Stroke colour as a theme class, e.g. `stroke-accent`. */
    className: string;
    dashed?: boolean;
    width?: number;
}) {
    if (points.length < 2) return null;
    return (
        <polyline
            points={pointsAttr(points)}
            className={cn('fill-none', className)}
            strokeWidth={width}
            // Round caps on a SOLID line soften its ends and its corners.
            // On a dashed one they would round every dash, lengthening each
            // and closing the gaps — a different texture, not a nicety. The
            // dash is carrying a meaning here ("projected, not measured"), so
            // it keeps the butt cap it has always had.
            strokeLinecap={dashed ? 'butt' : 'round'}
            strokeLinejoin="round"
            {...(dashed ? { strokeDasharray: '4 4' } : {})}
        />
    );
}

/**
 * The region between two edges — a band of expected values, or a filled area
 * between a series and its baseline.
 *
 * One closed polygon, out along `upper` and back along `lower`, which is what
 * makes it a single fill rather than two strokes with a gap between them.
 */
export function Band({
    upper, lower, className,
}: {
    upper: readonly Point[];
    lower: readonly Point[];
    /** Fill colour as a theme class, e.g. `fill-surface-hover/40`. */
    className: string;
}) {
    if (upper.length === 0 || lower.length === 0) return null;
    return (
        <polygon
            points={pointsAttr([...upper, ...[...lower].reverse()])}
            className={className}
        />
    );
}

/**
 * A filled area between a series and a baseline — `Band` with the flat edge
 * supplied for you, which is the net-worth and debt-paydown shape.
 */
export function Area({
    points, baselineY, className,
}: {
    points: readonly Point[];
    /** Pixel y of the baseline, normally the zero line rather than the floor. */
    baselineY: number;
    className: string;
}) {
    if (points.length < 2) return null;
    const flat = points.map((p) => ({ x: p.x, y: baselineY }));
    return <Band upper={points} lower={flat} className={className} />;
}

/**
 * One rectangle, given its baseline and its value position.
 *
 * Takes both rather than a height, so a NEGATIVE bar is drawn by the same code
 * as a positive one — it hangs below the baseline instead of being a rectangle
 * with a negative height, which renders as nothing at all and is the classic
 * way a refund disappears from a chart.
 */
export function Bar({
    x, width, baselineY, valueY, className,
}: {
    x: number;
    width: number;
    baselineY: number;
    valueY: number;
    /** Fill colour as a theme class, e.g. `fill-accent`. */
    className: string;
}) {
    const top = Math.min(baselineY, valueY);
    const height = Math.abs(valueY - baselineY);
    return (
        <rect
            x={fmt(x)}
            y={fmt(top)}
            width={fmt(Math.max(width, 0))}
            // A zero-value bar still gets a hairline, so a category that
            // netted to nothing is visibly present rather than absent.
            height={fmt(Math.max(height, 1))}
            className={className}
        />
    );
}

/**
 * An annular segment — the arc both a donut slice and a gauge are made of.
 *
 * Angles are in turns clockwise from twelve o'clock (0 = top, a quarter = right),
 * because every caller here thinks in fractions of a whole and nobody has to
 * remember which way SVG's radians run.
 */
export function Arc({
    cx, cy, outerRadius, innerRadius, startTurn, endTurn, className,
}: {
    cx: number;
    cy: number;
    outerRadius: number;
    innerRadius: number;
    startTurn: number;
    endTurn: number;
    /** Fill colour as a theme class. */
    className: string;
}) {
    const sweep = endTurn - startTurn;
    if (sweep <= 0) return null;

    // A full turn cannot be expressed as one arc: start and end land on the
    // same point and the renderer draws nothing. Two halves, as a ring.
    if (sweep >= 1) {
        return (
            <>
                <Arc {...{ cx, cy, outerRadius, innerRadius, className }}
                    startTurn={0} endTurn={0.5} />
                <Arc {...{ cx, cy, outerRadius, innerRadius, className }}
                    startTurn={0.5} endTurn={1} />
            </>
        );
    }

    const at = (turn: number, r: number) => {
        const a = (turn - 0.25) * 2 * Math.PI;   // 0 turns = twelve o'clock
        return { x: cx + r * Math.cos(a), y: cy + r * Math.sin(a) };
    };
    const large = sweep > 0.5 ? 1 : 0;
    const o0 = at(startTurn, outerRadius);
    const o1 = at(endTurn, outerRadius);
    const i1 = at(endTurn, innerRadius);
    const i0 = at(startTurn, innerRadius);

    const d = [
        `M ${fmt(o0.x)} ${fmt(o0.y)}`,
        `A ${fmt(outerRadius)} ${fmt(outerRadius)} 0 ${large} 1 ${fmt(o1.x)} ${fmt(o1.y)}`,
        `L ${fmt(i1.x)} ${fmt(i1.y)}`,
        `A ${fmt(innerRadius)} ${fmt(innerRadius)} 0 ${large} 0 ${fmt(i0.x)} ${fmt(i0.y)}`,
        'Z',
    ].join(' ');

    return <path d={d} className={className} />;
}
