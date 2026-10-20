import { describe, expect, it } from 'vitest';

import { ensureRowFullyVisible } from './ensureRowFullyVisible';

// jsdom has no layout, so the rects are stubbed. That is not a weakness here:
// this function's whole job is to turn two rectangles and a scrollTop into a
// new scrollTop, and the arithmetic is the thing under test. Where the
// rectangles come from is virtuoso's problem, and the point of correcting from
// the DOM is that it does not matter.

function scrollerWith(
    { viewTop = 100, viewBottom = 500, rowTop, rowBottom, scrollTop = 0 }:
    { viewTop?: number; viewBottom?: number; rowTop: number; rowBottom: number; scrollTop?: number },
) {
    const row = document.createElement('div');
    row.setAttribute('data-headerid', 'h-1');
    row.getBoundingClientRect = () => ({
        top: rowTop, bottom: rowBottom, height: rowBottom - rowTop,
        left: 0, right: 0, width: 0, x: 0, y: rowTop, toJSON: () => ({}),
    });

    const scroller = document.createElement('div');
    scroller.append(row);
    scroller.getBoundingClientRect = () => ({
        top: viewTop, bottom: viewBottom, height: viewBottom - viewTop,
        left: 0, right: 0, width: 0, x: 0, y: viewTop, toJSON: () => ({}),
    });
    scroller.scrollTop = scrollTop;
    return scroller;
}

describe('ensureRowFullyVisible', () => {
    it('pulls down a row clipped at the top', () => {
        // THE CASE THIS EXISTS FOR. Virtuoso's smart scroll is a no-op when the
        // row is "in the viewport", and a row cut off by a few pixels at the
        // top passes that test — so it stayed half under the header band.
        const scroller = scrollerWith({ rowTop: 92, rowBottom: 128, scrollTop: 400 });

        ensureRowFullyVisible(scroller, 'h-1');

        // 8px above the viewport top, plus the 6px gap.
        expect(scroller.scrollTop).toBe(400 - 14);
    });

    it('pushes up a row clipped at the bottom', () => {
        const scroller = scrollerWith({ rowTop: 480, rowBottom: 516, scrollTop: 400 });

        ensureRowFullyVisible(scroller, 'h-1');

        expect(scroller.scrollTop).toBe(400 + 22);
    });

    it('LEAVES a comfortably visible row exactly where it is', () => {
        // The reason this corrects afterwards instead of using align:'start'.
        // That fixed the clipping and yanked the row to the top of the list
        // after every save, which is disruptive mid-list.
        const scroller = scrollerWith({ rowTop: 250, rowBottom: 286, scrollTop: 400 });

        ensureRowFullyVisible(scroller, 'h-1');

        expect(scroller.scrollTop).toBe(400);
    });

    it('aligns the top of a row taller than the viewport', () => {
        // It cannot be fully visible, so the only readable choice is to show
        // its start and let the rest run off the bottom.
        const scroller = scrollerWith({
            viewTop: 100, viewBottom: 200, rowTop: 150, rowBottom: 600, scrollTop: 400,
        });

        ensureRowFullyVisible(scroller, 'h-1');

        expect(scroller.scrollTop).toBe(400 + 50 - 6);
    });

    it('does nothing when the row is not in the DOM', () => {
        // A split LEG carries no `data-headerid` — a leg is not the header's
        // row — and an anchor can point at a row the window has since evicted.
        const scroller = scrollerWith({ rowTop: 92, rowBottom: 128, scrollTop: 400 });

        ensureRowFullyVisible(scroller, 'h-missing');

        expect(scroller.scrollTop).toBe(400);
    });

    it('does nothing without a scroller or an anchor', () => {
        const scroller = scrollerWith({ rowTop: 92, rowBottom: 128, scrollTop: 400 });

        ensureRowFullyVisible(null, 'h-1');
        ensureRowFullyVisible(scroller, null);

        expect(scroller.scrollTop).toBe(400);
    });

    it('survives an id that is not a valid CSS selector', () => {
        // Header ids are GUIDs today, but a selector built by string
        // concatenation throws on anything exotic and would take the whole
        // focus arrival down with it.
        const scroller = scrollerWith({ rowTop: 92, rowBottom: 128, scrollTop: 400 });

        expect(() => ensureRowFullyVisible(scroller, 'a"]:not(b)')).not.toThrow();
    });
});
