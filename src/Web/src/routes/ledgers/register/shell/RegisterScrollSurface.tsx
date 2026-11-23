import { useRef, type ReactNode } from 'react';

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
//
// The list is the register's focus home (ADR-0023 §L, useRegisterFocusHome):
// focusable but no Tab stop (tabIndex -1), named, and ringed only for keyboard
// focus, as the app's other focusable containers are. It is not a typing
// target, so the register's keys work with focus on it.

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
    // A press on a row focuses the list (rows do not take focus, and nothing
    // between a row and the list does), and a browser rings focus a pointer
    // gave as soon as the next key is pressed. The list is ringed for keyboard
    // focus only, so focus a press gave it is marked (data-pressed) and shows
    // no ring until the list loses it. Focus the register hands the list after
    // a key (Escape in an editor, Clear selection from the keyboard) is not
    // marked, and is ringed. The press is followed until the pointer goes up
    // anywhere, so a press that gave focus to a control in a row instead marks
    // nothing.
    const pressing = useRef(false);
    const onPointerDown = () => {
        pressing.current = true;
        const release = () => {
            pressing.current = false;
            window.removeEventListener('pointerup', release, true);
            window.removeEventListener('pointercancel', release, true);
        };
        window.addEventListener('pointerup', release, true);
        window.addEventListener('pointercancel', release, true);
    };
    return (
        <div className="relative min-h-0 flex-1">
            <div
                ref={scrollRef}
                onPointerDown={onPointerDown}
                onFocus={(e) => {
                    if (e.target === e.currentTarget && pressing.current) e.currentTarget.setAttribute('data-pressed', '');
                }}
                onBlur={(e) => {
                    if (e.target === e.currentTarget) e.currentTarget.removeAttribute('data-pressed');
                }}
                role="grid"
                // -1 is ARIA's "the total is not known", and it is the honest
                // answer for a sliding window: the register holds at most ~1100
                // entries and both edges page, so the rows in the DOM are a
                // slice whose offset into the account is not derivable here.
                //
                // The LOADED count would announce "of" a few dozen on an
                // account far longer than the loaded window — a number that
                // also changes as the user scrolls. A real total is
                // obtainable (the scroll-rail buckets sum to it), but pairing it
                // with a window-local aria-rowindex would announce a single-digit
                // row "of" the true total for a row thousands deep, which is a
                // worse lie than no total. Set here rather than per-page so the two registers
                // cannot drift.
                aria-rowcount={-1}
                aria-label="Transactions"
                tabIndex={-1}
                // The register's focus home, for what looks it up on the page
                // (registerFocusHome).
                data-register-home="list"
                id={scrollRegionId}
                className={
                    'absolute inset-0 overflow-y-auto pr-12'
                    + ' focus-visible:outline-none not-data-[pressed]:focus-visible:ring-2 not-data-[pressed]:focus-visible:ring-inset not-data-[pressed]:focus-visible:ring-accent'
                    + (hasTrack ? ' [scrollbar-width:none] [&::-webkit-scrollbar]:hidden' : '')
                }
            >
                {children}
            </div>
            {scrollTrack}
        </div>
    );
}
