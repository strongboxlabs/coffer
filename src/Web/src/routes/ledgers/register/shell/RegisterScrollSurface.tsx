import type { ReactNode } from 'react';

// Shared register scroll surface (ADR-0030 reuse).
//
// The scrollable list region both registers render into. The `relative`
// wrapper anchors the absolutely-positioned RegisterScrollTrack on the
// right; the scroll container absolute-fills it so virtuoso has a stable
// surface to measure against. When the track is mounted the native scrollbar
// is hidden (Google Photos pattern) so the custom track is the only affordance;
// when it isn't (a sort the date-rail can't represent — non-date, or date
// ascending), the native scrollbar is restored so the list still scrolls.
// Either way `pr-12` reserves the gutter so the data columns keep aligning with
// the header band's `pr-12` group.

export interface RegisterScrollSurfaceProps {
    /** Ref callback for the scroll surface (virtuoso's scroll parent). */
    scrollRef: (el: HTMLDivElement | null) => void;
    /** Id for the scroll region (focus / scroll-into-view targeting). */
    scrollRegionId?: string;
    /** The list itself (Virtuoso / empty state). */
    children: ReactNode;
    /** Scroll-track overlay (RegisterScrollTrack), sibling of the list. */
    scrollTrack?: ReactNode;
}

export function RegisterScrollSurface({
    scrollRef,
    scrollRegionId,
    children,
    scrollTrack,
}: RegisterScrollSurfaceProps) {
    // Hide the native scrollbar ONLY when the custom date-rail is mounted (it's
    // then the scroll affordance). When the rail is absent — a sort it can't
    // represent — restore the native scrollbar, or the list has no way to scroll
    // at all. pr-12 stays either way so columns keep aligning with the header.
    const hasTrack = scrollTrack != null;
    return (
        <div className="relative min-h-0 flex-1">
            <div
                ref={scrollRef}
                role="grid"
                // -1 is ARIA's "the total is not known", and it is the honest
                // answer for a sliding window: the register holds at most ~1100
                // entries and both edges page, so the rows in the DOM are a
                // slice whose offset into the account is not derivable here.
                //
                // Bank used to pass the LOADED count, which announced "of 87"
                // on an account with 43,000 entries — a number that also
                // changed as the user scrolled. A real total is obtainable (the
                // scroll-rail buckets sum to it), but pairing it with a
                // window-local aria-rowindex would announce "row 5 of 43082"
                // for a row that is the 12,000th, which is a worse lie than no
                // total. Fixed here rather than per-page so the two registers
                // cannot drift.
                aria-rowcount={-1}
                id={scrollRegionId}
                className={
                    'absolute inset-0 overflow-y-auto pr-12'
                    + (hasTrack ? ' [scrollbar-width:none] [&::-webkit-scrollbar]:hidden' : '')
                }
            >
                {children}
            </div>
            {scrollTrack}
        </div>
    );
}
