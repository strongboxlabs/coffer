import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';

import {
    applyWidth,
    clampWidth,
    DEFAULT_WIDTH,
    KEY_STEP,
    maxWidthFor,
    MIN_WIDTH,
    readStoredWidth,
    saveWidth,
    viewportWidth,
} from '@/lib/sidebarRail';

/**
 * The drag handle on the sidebar's right edge (FU-051).
 *
 * A `role="separator"` with a value is the ARIA pattern for a splitter the
 * keyboard can move. The width lives in a CSS variable on the document
 * element (lib/sidebarRail.ts), so a drag repaints the rail without
 * re-rendering anything in it. Drag resizes live but saves only on pointer-up:
 * a save per pointermove would be a localStorage write per frame. Arrow keys
 * move it one KEY_STEP, Enter collapses the rail, double-click goes back to
 * the default width. Pointer capture and `touch-action: none` are how the
 * register's scroll track drags (RegisterScrollTrack).
 *
 * The key handler stops every key it is given except Tab and Ctrl+\. The
 * register's document-level handler takes the arrows (moving the focused row)
 * and a bare `n` (a new transaction), and skips only buttons and typing
 * targets, which this div is neither. React delegates at the root, so stopping
 * here keeps the event from ever reaching `document`. Ctrl+\ is let through
 * because the rail's own toggle listens there.
 */
export function SidebarResizeHandle({ onToggleCollapse }: { onToggleCollapse: () => void }) {
    const [width, setWidth] = useState<number>(() => readStoredWidth() ?? DEFAULT_WIDTH);
    const dragRef = useRef<{ startX: number; startWidth: number; last: number } | null>(null);

    // theme-init.js clamps the stored width to the window once, at page load.
    // The window can narrow after that, so on every mount (each expand after a
    // collapse) and on every resize the variable is clamped again to the window
    // as it is now, and the value announced and the width drawn stay one
    // number. Only what is shown changes: the stored width is the reader's
    // choice, and a wider window gets it back.
    const [max, setMax] = useState<number>(() => maxWidthFor(viewportWidth()));
    useEffect(() => {
        function reclamp() {
            setMax(maxWidthFor(viewportWidth()));
            if (dragRef.current !== null) return;
            const stored = readStoredWidth();
            if (stored === null) return;
            applyWidth(stored);
            setWidth(stored);
        }
        reclamp();
        window.addEventListener('resize', reclamp);
        return () => window.removeEventListener('resize', reclamp);
    }, []);

    function show(px: number) {
        applyWidth(px);
        setWidth(px);
    }

    function keep(px: number) {
        show(px);
        saveWidth(px);
    }

    function handlePointerDown(e: PointerEvent<HTMLDivElement>) {
        if (e.button !== 0) return;
        e.preventDefault();
        e.currentTarget.setPointerCapture?.(e.pointerId);
        dragRef.current = { startX: e.clientX, startWidth: width, last: width };
    }

    function handlePointerMove(e: PointerEvent<HTMLDivElement>) {
        const drag = dragRef.current;
        if (drag === null) return;
        drag.last = clampWidth(drag.startWidth + (e.clientX - drag.startX), viewportWidth());
        show(drag.last);
    }

    function handlePointerUp(e: PointerEvent<HTMLDivElement>) {
        const drag = dragRef.current;
        if (drag === null) return;
        dragRef.current = null;
        e.currentTarget.releasePointerCapture?.(e.pointerId);
        if (drag.last !== drag.startWidth) saveWidth(drag.last);
    }

    function handleKeyDown(e: KeyboardEvent<HTMLDivElement>) {
        if (e.key === 'Tab') return;
        if (e.key === '\\' && (e.ctrlKey || e.metaKey)) return;
        e.stopPropagation();
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
            e.preventDefault();
            keep(clampWidth(width + (e.key === 'ArrowRight' ? KEY_STEP : -KEY_STEP), viewportWidth()));
        } else if (e.key === 'Home') {
            e.preventDefault();
            keep(MIN_WIDTH);
        } else if (e.key === 'End') {
            e.preventDefault();
            keep(maxWidthFor(viewportWidth()));
        } else if (e.key === 'Enter') {
            e.preventDefault();
            onToggleCollapse();
        }
    }

    function reset() {
        // Back to the token, not to a stored copy of its value.
        applyWidth(null);
        setWidth(DEFAULT_WIDTH);
        saveWidth(DEFAULT_WIDTH);
    }

    return (
        <div
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize sidebar"
            aria-valuenow={width}
            aria-valuemin={MIN_WIDTH}
            aria-valuemax={max}
            tabIndex={0}
            title="Drag to resize. Arrow keys resize, Enter collapses, double-click resets."
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
            onPointerCancel={handlePointerUp}
            onKeyDown={handleKeyDown}
            onDoubleClick={reset}
            style={{ touchAction: 'none' }}
            // Straddles the border, so the grab area is wider than the line it
            // draws; the line is the accent the focus ring uses.
            className="group/resize absolute inset-y-0 -right-1 z-10 flex w-fixed-8px cursor-col-resize justify-center focus-visible:outline-none"
        >
            <span
                aria-hidden
                className="h-full w-fixed-2px bg-accent opacity-0 transition-opacity group-hover/resize:opacity-60 group-focus-visible/resize:opacity-100"
            />
        </div>
    );
}
