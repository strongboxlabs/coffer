/**
 * Scales: the only arithmetic in the kit, kept apart from anything that draws.
 *
 * A scale maps a domain value to a position inside a fixed `viewBox`. It never
 * measures an element, which is the property the whole kit exists to keep —
 * the test environment stubs ResizeObserver to a no-op, so a chart that sized
 * itself from its container would render 0×0 while every assertion passed
 * (ADR-0104 D6).
 *
 * Pure functions over plain numbers, so a chart's geometry can be asserted
 * without rendering anything.
 */

/** The drawing area a scale maps into. Chosen by the chart, not measured. */
export interface Viewport {
    width: number;
    height: number;
}

export interface LinearScale {
    /** Domain value to pixel position. */
    (value: number): number;
    /** The domain this was built over, after any padding. */
    readonly domain: readonly [number, number];
}

/**
 * Map `[min, max]` onto `[0, length]`.
 *
 * A zero-width domain is widened rather than divided by: a series where every
 * value is identical is a real series, and the honest picture is a flat line
 * through the middle rather than a division by zero or a line pinned to an
 * edge.
 */
export function linear(
    domain: readonly [number, number],
    range: readonly [number, number],
): LinearScale {
    let [d0, d1] = domain;
    if (d0 === d1) {
        // Widen symmetrically so the single value lands mid-range.
        const pad = Math.abs(d0) || 1;
        d0 -= pad;
        d1 += pad;
    }
    const [r0, r1] = range;
    const span = d1 - d0;
    const fn = ((value: number) => r0 + ((value - d0) / span) * (r1 - r0)) as {
        (value: number): number;
        domain: readonly [number, number];
    };
    fn.domain = [d0, d1] as const;
    return fn;
}

/**
 * A vertical scale: domain to pixels with the origin at the BOTTOM, which is
 * how a reader expects a chart to work and the opposite of how SVG numbers its
 * y axis. Built as its own function rather than left to each caller to invert,
 * because an inverted chart is the kind of bug that looks deliberate.
 */
export function vertical(
    domain: readonly [number, number],
    height: number,
): LinearScale {
    return linear(domain, [height, 0]);
}

/**
 * Evenly spaced positions for N categorical items — months on an axis, bars in
 * a group. Returns the CENTRE of each band, because that is what both a bar
 * and a tick label want.
 */
export function band(count: number, width: number): {
    center: (index: number) => number;
    width: number;
} {
    const step = count > 0 ? width / count : width;
    return {
        center: (index: number) => step * index + step / 2,
        width: step,
    };
}

/**
 * The domain to draw a series over, padded so nothing sits flush against the
 * frame — a line touching the top edge reads as clipped rather than as high.
 *
 * ZERO IS ALWAYS INCLUDED. Every measure in this app can go negative (a refund
 * on an expense category, a Net total, an underwater net worth), and a chart
 * whose baseline is the lowest value drawn makes a column of all-negative
 * numbers look like growth. The zero line has to be visible for the sign to be.
 */
export function extent(
    values: readonly number[],
    { pad = 0.18 }: { pad?: number } = {},
): readonly [number, number] {
    let min = 0;
    let max = 0;
    for (const v of values) {
        if (Number.isFinite(v)) {
            if (v < min) min = v;
            if (v > max) max = v;
        }
    }
    // An all-zero series still needs a domain with width.
    if (min === 0 && max === 0) return [0, 1];
    return [min === 0 ? 0 : min * (1 + pad), max === 0 ? 0 : max * (1 + pad)];
}

/**
 * Roughly `count` round numbers spanning a domain, for axis ticks.
 *
 * "Roughly" is deliberate: a tick step is rounded to 1, 2, 5 or 10 × a power of
 * ten, so the labels read as money rather than as whatever the domain divided
 * by five happens to be. The count that comes back may differ from the one
 * asked for, which is the trade.
 */
export function ticks(
    domain: readonly [number, number],
    count = 5,
): readonly number[] {
    const [d0, d1] = domain;
    const span = d1 - d0;
    if (span === 0 || !Number.isFinite(span) || count < 1) return [d0];

    const rough = span / count;
    const magnitude = 10 ** Math.floor(Math.log10(Math.abs(rough)));
    const normalized = rough / magnitude;
    const step = (normalized >= 7.5 ? 10 : normalized >= 3 ? 5 : normalized >= 1.5 ? 2 : 1) * magnitude;

    // Round to the step's own precision, NOT to a multiple of the step. A tick
    // is a label, and `Math.round(t / step) * step` cannot clean 0.6 when the
    // step is 0.2, because 3 * 0.2 itself lands a hair above 0.6 — the dust is
    // in the step, so multiplying by it puts the dust back.
    const decimals = Math.max(0, -Math.floor(Math.log10(step)));
    const snap = (t: number) => Number(t.toFixed(Math.min(decimals, 12)));

    const out: number[] = [];
    // Start at the first step at or above the domain floor.
    for (let t = Math.ceil(d0 / step) * step; t <= d1 + step * 1e-9; t += step) {
        out.push(snap(t));
    }
    return out;
}
