import { afterEach, describe, expect, it, vi } from 'vitest';

import { fitTextareaHeight, refitOnWidthChange } from './fitTextareaHeight';

// jsdom lays nothing out, so each field stubs the two numbers the browser would
// give it. `scrollHeight` is the content and the padding, without the border, as
// a browser reports it; `couldScroll` is what it reports while the field can show
// a scrollbar, whose width wraps the text onto more lines.
function field({ cannotScroll, couldScroll = cannotScroll, border = '1px' }: {
    cannotScroll: number;
    couldScroll?: number;
    border?: string;
}): HTMLTextAreaElement {
    const el = document.createElement('textarea');
    el.style.border = `${border} solid`;
    Object.defineProperty(el, 'scrollHeight', {
        get: () => (el.style.overflowY === 'hidden' ? cannotScroll : couldScroll),
    });
    document.body.appendChild(el);
    return el;
}

afterEach(() => {
    document.body.innerHTML = '';
    vi.unstubAllGlobals();
});

describe('fitTextareaHeight', () => {
    // The field is border-box, so a height of scrollHeight alone leaves its
    // content taller than its box by the border, and it scrolls by that much.
    it('adds the top and bottom borders to the measured height', () => {
        const el = field({ cannotScroll: 40 });
        fitTextareaHeight(el, 96);
        expect(el.style.height).toBe('42px');

        const thick = field({ cannotScroll: 40, border: '2px' });
        fitTextareaHeight(thick, 96);
        expect(thick.style.height).toBe('44px');
    });

    it('measures while the field cannot scroll, and leaves its overflow to its classes', () => {
        // Measured beside a scrollbar, the same text reads a line taller.
        const el = field({ cannotScroll: 40, couldScroll: 56 });
        fitTextareaHeight(el, 96);
        expect(el.style.height).toBe('42px');
        expect(el.style.overflowY).toBe('');
    });

    it('stops at the cap, past which the field scrolls', () => {
        const el = field({ cannotScroll: 200 });
        fitTextareaHeight(el, 96);
        expect(el.style.height).toBe('96px');
    });
});

describe('refitOnWidthChange', () => {
    it('fits the field again in the next frame when its width changes, and only then', () => {
        let notify = (): void => {};
        const disconnect = vi.fn();
        vi.stubGlobal('ResizeObserver', class {
            constructor(callback: () => void) { notify = callback; }
            observe() {}
            unobserve() {}
            disconnect() { disconnect(); }
        });
        const frames: FrameRequestCallback[] = [];
        vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb));
        vi.stubGlobal('cancelAnimationFrame', () => {});

        const el = field({ cannotScroll: 40 });
        let width = 300;
        Object.defineProperty(el, 'offsetWidth', { get: () => width });
        const stop = refitOnWidthChange(el, 96);
        el.style.height = '10px';

        // The fit's own height change, or a scrollbar appearing, keeps the width.
        notify();
        expect(frames).toHaveLength(0);
        expect(el.style.height).toBe('10px');

        width = 200;
        notify();
        expect(el.style.height).toBe('10px');
        expect(frames).toHaveLength(1);
        frames[0]!(0);
        expect(el.style.height).toBe('42px');

        stop();
        expect(disconnect).toHaveBeenCalledTimes(1);
    });
});
