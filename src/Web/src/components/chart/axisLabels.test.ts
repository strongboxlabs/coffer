import { describe, expect, it } from 'vitest';

import { LABEL_SPACINGS, hideLabelBelow, hideLastBelow, labelLength, labelSpacings } from './axisLabels';

/** The labels drawn when the columns call for spacing `k`. */
const drawnAt = (count: number, k: number) =>
    labelSpacings(count).flatMap((widest, i) => (widest >= k ? [i] : []));

describe('labelSpacings', () => {
    it('draws every label where they fit', () => {
        for (const count of [1, 2, 6, 13, 30]) {
            expect(drawnAt(count, 1)).toEqual(Array.from({ length: count }, (_, i) => i));
        }
    });

    it('thins to every second, fourth and so on, evenly spaced from the first, with the last drawn', () => {
        expect(drawnAt(13, 2)).toEqual([0, 2, 4, 6, 8, 12]);
        expect(drawnAt(13, 4)).toEqual([0, 4, 12]);
        expect(drawnAt(25, 4)).toEqual([0, 4, 8, 12, 16, 24]);
        expect(drawnAt(30, 4)).toEqual([0, 4, 8, 12, 16, 20, 29]);
    });

    it('keeps one and a half spacings clear before the last, which may sit at its column\'s end', () => {
        // Six months two apart: the fifth would sit beside the last, so it gives way.
        expect(drawnAt(6, 2)).toEqual([0, 2, 5]);
        for (const count of [2, 3, 5, 6, 7, 12, 13, 24, 30, 90, 365]) {
            for (const k of LABEL_SPACINGS) {
                const drawn = drawnAt(count, k);
                const last = count - 1;
                for (let j = 1; j < drawn.length; j++) {
                    const gap = drawn[j]! - drawn[j - 1]!;
                    // The first and the last alone are kept apart by the axis's width (hideLastBelow).
                    if (drawn[j] === last && drawn[j - 1] === 0) continue;
                    if (drawn[j] === last) expect(gap + 0.5, `${count} at ${k}`).toBeGreaterThanOrEqual(1.5 * k);
                    else expect(gap, `${count} at ${k}`).toBe(k);
                }
            }
        }
    });

    it('draws the first and the last at every spacing (the last while the axis has room, below)', () => {
        for (const count of [2, 6, 13, 365]) {
            for (const k of LABEL_SPACINGS) {
                expect(drawnAt(count, k)[0]).toBe(0);
                expect(drawnAt(count, k).at(-1)).toBe(count - 1);
            }
        }
        expect(drawnAt(6, 4)).toEqual([0, 5]);
        expect(labelSpacings(1)).toEqual([Infinity]);
    });
});

describe('hideLabelBelow', () => {
    it('hides a label below its text and a gap, over the widest spacing it is drawn at', () => {
        expect(hideLabelBelow(7, 1)).toBe('@max-[8ch]/period:hidden');
        expect(hideLabelBelow(7, 4)).toBe('@max-[2ch]/period:hidden');
        expect(hideLabelBelow(10, 2)).toBe('@max-[5.5ch]/period:hidden');
        expect(hideLabelBelow(4, 8)).toBe('@max-[0.625ch]/period:hidden');
        expect(hideLabelBelow(7, Infinity)).toBe('');
    });

    it('has a threshold for every spacing, each the length over the spacing', () => {
        for (const length of [4, 7, 10] as const) {
            for (const k of LABEL_SPACINGS) {
                const value = Number(/@max-\[([\d.]+)ch\]/.exec(hideLabelBelow(length, k))![1]);
                expect(value, `${length} at ${k}`).toBeCloseTo((length + 1) / k, 10);
            }
        }
    });
});

describe('hideLastBelow', () => {
    const value = (cls: string) => Number(/@max-\[([\d.]+)ch\]\/axis:hidden/.exec(cls)![1]);

    it('hides the last while the axis cannot hold it and the first apart', () => {
        expect(hideLastBelow(7, 2)).toBe('@max-[15.5ch]/axis:hidden');
        expect(hideLastBelow(7, 6)).toBe('@max-[13ch]/axis:hidden');
        expect(hideLastBelow(10, 30)).toBe('@max-[17ch]/axis:hidden');
        expect(hideLastBelow(4, 3)).toBe('@max-[8.5ch]/axis:hidden');
    });

    it('never lets the two meet: each threshold covers every count it is used for', () => {
        // With n columns the first and last clear each other once the axis is
        // n / (n - 0.5) times one and a half labels and a gap.
        for (const length of [4, 7, 10] as const) {
            for (let n = 2; n <= 400; n++) {
                const needed = ((1.5 * length + 1) * n) / (n - 0.5);
                expect(value(hideLastBelow(length, n)), `${length} over ${n}`).toBeGreaterThanOrEqual(needed - 1e-9);
            }
        }
    });
});

describe('labelLength', () => {
    it('rounds the longest label up to a year, a month or quarter, or a day', () => {
        expect(labelLength(['2025', '2026'])).toBe(4);
        expect(labelLength(['2026-01', '2026-02'])).toBe(7);
        expect(labelLength(['2026-Q1', '2026-Q2'])).toBe(7);
        expect(labelLength(['2026-01-31', '2026-02-01'])).toBe(10);
    });
});
