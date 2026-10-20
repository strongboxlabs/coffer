import { describe, expect, it } from 'vitest';

import { band, extent, linear, ticks, vertical } from './scale';

describe('linear', () => {
    it('maps the domain ends onto the range ends', () => {
        const s = linear([0, 100], [0, 600]);
        expect(s(0)).toBe(0);
        expect(s(100)).toBe(600);
        expect(s(50)).toBe(300);
    });

    it('extrapolates outside the domain rather than clamping', () => {
        // A projection can run past the data it was derived from, and a scale
        // that clamped would draw it flat along the top instead of off the
        // chart, which reads as a plateau that never happened.
        const s = linear([0, 100], [0, 600]);
        expect(s(150)).toBe(900);
        expect(s(-50)).toBe(-300);
    });

    it('widens a zero-width domain instead of dividing by zero', () => {
        // Every value identical is a real series — three months at exactly the
        // same spend. The honest picture is a flat line through the middle.
        const s = linear([50, 50], [0, 600]);
        expect(Number.isFinite(s(50))).toBe(true);
        expect(s(50)).toBe(300);
    });

    it('handles an all-zero domain, where the usual padding has nothing to scale', () => {
        const s = linear([0, 0], [0, 600]);
        expect(s(0)).toBe(300);
        expect(Number.isNaN(s(0))).toBe(false);
    });
});

describe('vertical', () => {
    it('puts the origin at the bottom, where a reader expects it', () => {
        // SVG numbers y downward; a chart that forgot to invert is the kind of
        // bug that looks deliberate.
        const y = vertical([0, 100], 180);
        expect(y(0)).toBe(180);
        expect(y(100)).toBe(0);
        expect(y(50)).toBe(90);
    });
});

describe('band', () => {
    it('centres each item in its own slice', () => {
        const b = band(4, 600);
        expect(b.width).toBe(150);
        expect(b.center(0)).toBe(75);
        expect(b.center(3)).toBe(525);
    });

    it('survives an empty series without dividing by zero', () => {
        const b = band(0, 600);
        expect(Number.isFinite(b.width)).toBe(true);
    });
});

describe('extent', () => {
    it('always includes zero, so the sign of a series is visible', () => {
        // THE ONE THAT MATTERS. Every measure here can go negative — a refund
        // on an expense category, a Net total, an underwater net worth. A
        // domain that started at the lowest value drawn would make a column of
        // all-negative numbers look like growth.
        const [lo, hi] = extent([-80, -40, -10]);
        expect(lo).toBeLessThan(0);
        expect(hi).toBe(0);

        const [lo2, hi2] = extent([10, 40, 80]);
        expect(lo2).toBe(0);
        expect(hi2).toBeGreaterThan(80);
    });

    it('spans both signs when the series crosses zero', () => {
        const [lo, hi] = extent([-30, 60]);
        expect(lo).toBeLessThan(-30);
        expect(hi).toBeGreaterThan(60);
    });

    it('pads so nothing sits flush against the frame', () => {
        // A line touching the top edge reads as clipped rather than as high.
        const [, hi] = extent([100]);
        expect(hi).toBeGreaterThan(100);
    });

    it('gives an all-zero series a domain with width', () => {
        expect(extent([0, 0, 0])).toEqual([0, 1]);
        expect(extent([])).toEqual([0, 1]);
    });

    it('ignores non-finite values rather than poisoning the domain', () => {
        const [lo, hi] = extent([10, Number.NaN, 50, Number.POSITIVE_INFINITY]);
        expect(Number.isFinite(lo)).toBe(true);
        expect(Number.isFinite(hi)).toBe(true);
        expect(hi).toBeGreaterThan(50);
    });
});

describe('ticks', () => {
    it('lands on round numbers rather than on the domain divided by five', () => {
        // Labels have to read as money. 0 · 20 · 40 …, not 19.999999999999996.
        const t = ticks([0, 100], 5);
        expect(t).toEqual([0, 20, 40, 60, 80, 100]);
    });

    it('chooses a 1 / 2 / 5 / 10 step at any magnitude', () => {
        expect(ticks([0, 10_000], 5)).toEqual([0, 2000, 4000, 6000, 8000, 10_000]);
        expect(ticks([0, 3], 3)).toEqual([0, 1, 2, 3]);
    });

    it('returns clean numbers below 1, where the step itself carries float dust', () => {
        // A tick is a LABEL. Snapping to a multiple of the step cannot clean
        // this, because 3 * 0.2 lands a hair above 0.6 in binary floating
        // point — the dust is in the
        // step, so multiplying by it puts the dust back. Asserted as exact
        // equality on purpose: toBeCloseTo would pass on the dusty value.
        expect(ticks([0, 1], 5)).toEqual([0, 0.2, 0.4, 0.6, 0.8, 1]);
    });

    it('spans a domain that crosses zero', () => {
        const t = ticks([-40, 80], 4);
        expect(t).toContain(0);
        expect(Math.min(...t)).toBeGreaterThanOrEqual(-40);
        expect(Math.max(...t)).toBeLessThanOrEqual(80);
    });

    it('does not spin on a zero-width domain', () => {
        expect(ticks([5, 5], 5)).toEqual([5]);
    });
});
