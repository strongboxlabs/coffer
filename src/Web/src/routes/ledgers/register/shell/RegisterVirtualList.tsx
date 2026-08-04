import { useMemo, type ReactNode, type Ref } from 'react';
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso';

import { buildTimelineSentinels } from './registerSentinels';

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
 * whether logical-index threading is in play. `firstItemIndex` is optional:
 * bank threads it (eviction-stable logical indices); investment aggregates
 * multi-posting rows BEFORE the list and feeds plain local indices, so it omits
 * the prop. That is the one genuine mechanical difference the audit found.
 */
export interface RegisterVirtualListProps<Row> {
    virtuosoRef: Ref<VirtuosoHandle>;
    /** customScrollParent from the enclosing RegisterScrollSurface. */
    scrollParent: HTMLElement | null;
    rows: readonly Row[];
    getRowId: (row: Row) => string;
    renderRow: (index: number, row: Row) => ReactNode;
    /** A row's posted date (YYYY-MM-DD…) for viewport-month tracking; return
     *  undefined for rows without a date (the update is skipped). */
    getRowPostedAt: (row: Row) => string | undefined;
    /** Fires with the viewport centre row's YYYY-MM as the user scrolls. */
    onViewportMonthChange: (yearMonth: string) => void;
    onLoadNewer: () => void;
    onLoadOlder: () => void;
    /** Logical index of `rows[0]` (eviction-stable). Provide it for logical-
     *  index threading (bank); OMIT to feed virtuoso plain local indices
     *  (investment, which aggregates before the list). */
    firstItemIndex?: number;
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
    firstItemIndex,
    initialTopMostItemIndex,
    atTimelineHead,
    atTimelineTail,
    oldestLabel,
}: RegisterVirtualListProps<Row>) {
    const components = useMemo(
        () => buildTimelineSentinels<Row>({ atTimelineHead, atTimelineTail, oldestLabel }),
        [atTimelineHead, atTimelineTail, oldestLabel],
    );

    // Logical index of rows[0]; 0 when the page feeds local indices.
    const offset = firstItemIndex ?? 0;

    return (
        <Virtuoso
            ref={virtuosoRef}
            customScrollParent={scrollParent ?? undefined}
            data={rows as Row[]}
            computeItemKey={(_, row) => getRowId(row)}
            initialTopMostItemIndex={initialTopMostItemIndex ?? 0}
            // Only thread firstItemIndex when the page uses logical indices;
            // undefined keeps virtuoso on plain local indices (investment).
            firstItemIndex={firstItemIndex}
            startReached={onLoadNewer}
            endReached={onLoadOlder}
            // Pre-fetch margin so the loading state doesn't pop in at the edge.
            increaseViewportBy={{ top: 400, bottom: 400 }}
            components={components}
            itemContent={(index, row) => renderRow(index, row)}
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
