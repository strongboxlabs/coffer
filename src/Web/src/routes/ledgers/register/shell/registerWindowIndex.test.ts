import { describe, it, expect } from 'vitest';

import { toWindowIndex } from './registerWindowIndex';

// `aria-rowindex` is fed from whatever `itemContent` receives, and virtuoso
// emits an index shifted by `firstItemIndex` — seeded to 1,000,000 so rows can
// be prepended without going negative. Unmapped, the register announced its
// first row as "row 1000001".
//
// This is a pure-function test on purpose. Under jsdom virtuoso emits LOCAL
// indices (firstItemIndex needs layout to take effect), so a rendered-DOM
// assertion reads 1 and 2 with or without the mapping and cannot tell the two
// apart — it was green against the bug.
describe('toWindowIndex', () => {
    const OFFSET_BASE = 1_000_000;

    it('strips the front-shift offset', () => {
        expect(toWindowIndex(OFFSET_BASE, OFFSET_BASE, 50)).toBe(0);
        expect(toWindowIndex(OFFSET_BASE + 7, OFFSET_BASE, 50)).toBe(7);
    });

    it('tracks the offset down as rows are prepended', () => {
        // A page of 30 newer rows arrived: the offset drops by 30 and the row
        // that was first is now 30th.
        const offset = OFFSET_BASE - 30;
        expect(toWindowIndex(OFFSET_BASE, offset, 80)).toBe(30);
    });

    it('falls back to the emitted index when the subtraction is out of range', () => {
        // The re-mount race: the offset has not resettled, so virtuoso is
        // already emitting a local index and subtracting would go negative.
        expect(toWindowIndex(0, OFFSET_BASE, 50)).toBe(0);
        expect(toWindowIndex(3, OFFSET_BASE, 50)).toBe(3);
        // ...and the other direction, past the end of the loaded window.
        expect(toWindowIndex(OFFSET_BASE + 999, OFFSET_BASE, 50)).toBe(OFFSET_BASE + 999);
    });

    it('is the identity when nothing is shifted', () => {
        expect(toWindowIndex(0, 0, 10)).toBe(0);
        expect(toWindowIndex(9, 0, 10)).toBe(9);
    });
});
