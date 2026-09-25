import { useMemo, useState, type ReactNode, type Ref } from 'react';
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso';

import { buildTimelineSentinels } from './registerSentinels';
import { toWindowIndex } from './registerWindowIndex';

/**
 * The shared register list — the ONE `<Virtuoso>` both the bank and investment
 * registers render. It owns every list-behavior knob so they can't drift
 * between the two pages (feedback: registers unified by default):
 *
 *   * item keying,
 *   * the EDGE AUTO-LOAD POLICY — start/endReached page the window in both
 *     directions. Filtering is server-side (mig 164), so the payload IS the
 *     filtered set: edge-load walks only matching entries and is never
 *     suppressed (the old client-filter suppression for #322 is obsolete),
 *   * the pre-fetch viewport margin,
 *   * the timeline sentinels (newest / oldest edge markers),
 *   * the viewport-month tracking that drives the scroll-track "you are here".
 *
 * The pages supply ONLY what genuinely differs: the row collection, how to
 * render a row, how to read a row's posted date (the row types differ), and
 * and how to read a row's posted date (the row types differ). Scroll-position
 * compensation is NOT among them: this component derives its own
 * `firstItemIndex` from `rows`, so neither page can get the index space wrong.
 */
/**
 * Seed for the front-shift offset.
 *
 * virtuoso requires a POSITIVE `firstItemIndex` and logs an error below zero,
 * so the baseline has to leave room for rows to be prepended. A window holds at
 * most ~1100 entries and prepends a page at a time, so a million is headroom
 * nothing will spend.
 */
const OFFSET_BASE = 1_000_000;

/**
 * The next offset, given the previous rows and the new ones.
 *
 * Front-anchored: find the row that USED to be first inside the new array. If
 * it moved down by k, k rows were prepended and the offset decreases by k. If
 * it is gone, find the new first row in the OLD array; if it sat at j, j rows
 * were dropped from the front and the offset increases by j. If neither is
 * found the list was replaced wholesale (a refresh or a re-seed), and the
 * baseline resets — there is no continuity to preserve.
 */
function nextShift<Row>(
    prev: { rows: readonly Row[]; ids: string[]; offset: number },
    rows: readonly Row[],
    getRowId: (row: Row) => string,
): { rows: readonly Row[]; ids: string[]; offset: number } {
    const ids = rows.map(getRowId);
    const base = { rows, ids };

    if (prev.ids.length === 0 || ids.length === 0) {
        return { ...base, offset: OFFSET_BASE };
    }

    const prepended = ids.indexOf(prev.ids[0]!);
    if (prepended >= 0) {
        return { ...base, offset: Math.max(0, prev.offset - prepended) };
    }

    const removed = prev.ids.indexOf(ids[0]!);
    if (removed > 0) {
        return { ...base, offset: prev.offset + removed };
    }

    return { ...base, offset: OFFSET_BASE };
}

export interface RegisterVirtualListProps<Row> {
    virtuosoRef: Ref<VirtuosoHandle>;
    /** customScrollParent from the enclosing RegisterScrollSurface. */
    scrollParent: HTMLElement | null;
    rows: readonly Row[];
    getRowId: (row: Row) => string;
    /** Called per rendered row. `index` is the row's position within the
     *  LOADED WINDOW (0-based) — not virtuoso's logical index, which carries
     *  the front-shift offset below and would number the first row 1,000,000.
     *  It feeds `aria-rowindex`, so the announced position has to be one a
     *  listener can act on. */
    renderRow: (index: number, row: Row) => ReactNode;
    /** A row's posted date (YYYY-MM-DD…) for viewport-month tracking; return
     *  undefined for rows without a date (the update is skipped). */
    getRowPostedAt: (row: Row) => string | undefined;
    /** Fires with the viewport centre row's YYYY-MM as the user scrolls. */
    onViewportMonthChange: (yearMonth: string) => void;
    onLoadNewer: () => void;
    onLoadOlder: () => void;
    initialTopMostItemIndex?: number;
    /** Timeline edge flags from useRegisterController → sentinels. */
    atTimelineHead: boolean;
    atTimelineTail: boolean;
    oldestLabel: string | null;
}

export function RegisterVirtualList<Row>({
    virtuosoRef,
    scrollParent,
    rows,
    getRowId,
    renderRow,
    getRowPostedAt,
    onViewportMonthChange,
    onLoadNewer,
    onLoadOlder,
    initialTopMostItemIndex,
    atTimelineHead,
    atTimelineTail,
    oldestLabel,
}: RegisterVirtualListProps<Row>) {
    const components = useMemo(
        () => buildTimelineSentinels<Row>({ atTimelineHead, atTimelineTail, oldestLabel }),
        [atTimelineHead, atTimelineTail, oldestLabel],
    );

    // ----------------------------------------------------------------
    // Front-shift offset, owned HERE rather than taken from a page.
    //
    // virtuoso reads the DELTA of `firstItemIndex` as "this many rows entered
    // or left the FRONT", and compensates scrollTop so the viewport does not
    // jump. It is a property of the RENDERED array.
    //
    // It used to be a prop. Bank passed `register.firstItemIndex`, which the
    // windowing hook computes in ENTRY space — but every page renders ROWS,
    // after regrouping target splits, filtering by status and expanding groups,
    // so the two counts differ. Bank therefore fed a delta that was wrong
    // whenever any of those changed the count, and negative on the first
    // prepend, which virtuoso rejects. Investment omitted the prop and got no
    // compensation at all, so its viewport jumped on both edges. One problem,
    // two wrong answers. Deriving it from `rows` makes the entry-vs-row mistake
    // unconstructible instead of documented.
    //
    // Derived from the previous props during render, holding the previous value
    // in STATE — React's documented pattern for this. A ref written during
    // render would double-apply the delta under StrictMode's double-invoke, and
    // an effect would land the compensation a commit late, which is precisely
    // the frame that jumps.
    const [shift, setShift] = useState(() => ({
        rows: rows as readonly Row[],
        ids: rows.map(getRowId),
        offset: OFFSET_BASE,
    }));
    if (shift.rows !== rows) {
        setShift(nextShift(shift, rows, getRowId));
    }
    const offset = shift.offset;

    return (
        <Virtuoso
            ref={virtuosoRef}
            customScrollParent={scrollParent ?? undefined}
            data={rows as Row[]}
            computeItemKey={(_, row) => getRowId(row)}
            initialTopMostItemIndex={initialTopMostItemIndex ?? 0}
            firstItemIndex={offset}
            startReached={onLoadNewer}
            endReached={onLoadOlder}
            // Pre-fetch margin so the loading state doesn't pop in at the edge.
            increaseViewportBy={{ top: 400, bottom: 400 }}
            components={components}
            itemContent={(index, row) =>
                renderRow(toWindowIndex(index, offset, rows.length), row)
            }
            // Drive the scroll-track marker from the actual visible range.
            // virtuoso emits logical indices (offset by firstItemIndex) when it
            // is set; fall back to a local-index reading if the subtraction
            // lands out of bounds (re-mount races where firstItemIndex hasn't
            // resettled) — matches the per-page originals.
            rangeChanged={(range) => {
                const mid = Math.floor((range.startIndex + range.endIndex) / 2);
                let local = mid - offset;
                if (local < 0 || local >= rows.length) local = mid;
                if (local < 0 || local >= rows.length) return;
                const row = rows[local];
                if (row === undefined) return;
                const postedAt = getRowPostedAt(row);
                if (postedAt) onViewportMonthChange(postedAt.slice(0, 7));
            }}
        />
    );
}
