import { useCallback, useEffect, useRef, useState } from 'react';

import { fetchRegister } from '@/lib/api';
import type { RegisterFilterArgs } from '@/lib/api/register';
import type { RegisterEntry } from '@/lib/types';

// Sliding-window register hook. Maintains a flat array of entries
// in time-DESC plus per-page cursor metadata so we can cap memory
// without losing the ability to reload evicted pages.
//
// Design notes:
//
//   * Soft cap on entries in memory (MAX_ENTRIES). Every load past
//     the cap evicts whole pages from the FAR edge — opposite to
//     the scroll direction that triggered the load — so the user
//     never sees the rows they're about to look at vanish. If they
//     scroll back across the eviction boundary the hook re-fetches
//     transparently via the saved per-page cursor.
//
//   * Per-page metadata (`pages: PageMeta[]`) carries both the top
//     and bottom boundary cursors of each loaded page. The server
//     hands these to us on every response (`cursorForNewer` =
//     boundary just-newer-than the page's first entry;
//     `cursorForOlder` = boundary just-older-than the page's last
//     entry). When we drop a page from one end, the next page's
//     boundary cursor becomes the new outer cursor — no need to
//     synthesize cursors client-side.
//
//   * `firstItemIndex` is the LOGICAL index of `entries[0]` — i.e.
//     the index the rest of the world (virtuoso, the consumer's
//     focus state) should use. When we evict N entries from the
//     front, `firstItemIndex` rises by N and virtuoso preserves
//     scroll position relative to the still-present items. The
//     consumer wires this to virtuoso's `firstItemIndex` prop and
//     converts its own focus/select state to logical indices when
//     it asks virtuoso to scroll.
//
//   * State is held as one combined `WindowState` rather than five
//     separate `useState` slots. Eviction needs `entries`, `pages`,
//     and `firstItemIndex` to update atomically (counts must stay
//     consistent with cursor metadata, and the index shift must
//     match the entries slice). Separate `useState` updaters defer
//     their work until React's batched flush; trying to coordinate
//     them via a shared `let` in the calling scope is fragile and
//     was the root cause of the visible "count drops to 930" jolt
//     during eviction at the timeline tail.
//
//   * Three load shapes:
//       (a) Initial load: most-recent K entries, no anchor.
//       (b) Initial load with focus: K entries anchored at the
//           focused header (entry[0] is the focused row).
//       (c) Edge load: cursor + direction, plus eviction trim at
//           the opposite edge if over cap.
//     (a)/(b) run in a useEffect keyed on the identity-defining
//     args. (c) runs from `loadOlder` / `loadNewer` callbacks the
//     caller wires into virtuoso's `startReached` / `endReached`.

/** Soft cap on entries kept in memory at any one time. ~10× the
 *  default page size — eviction kicks in well past the typical
 *  session's working set while keeping heap predictable on
 *  phones. ~500 bytes/entry × 1000 = ~500 KB worst case.
 *
 *  Eviction is page-granular (whole pages, so cursor boundaries
 *  remain server-anchored), and it doesn't fire until the window
 *  exceeds MAX_ENTRIES + EVICTION_HYSTERESIS. Without the
 *  hysteresis a partial last page (e.g. 30-row remainder at the
 *  timeline tail) would push us over MAX, trigger eviction of a
 *  full 100-row page, and drop the visible total below 1000 —
 *  observable as a "count jolt" at the edges. With the hysteresis
 *  the count sits in the [MAX, MAX + hysteresis] band normally
 *  and only crosses below MAX when actual data is missing. */
const MAX_ENTRIES = 1000;
const EVICTION_HYSTERESIS = 100;

/** Per-page boundary cursors tracked alongside `entries`. Each
 *  page's `length` is its slot in the `entries` array; `top` and
 *  `bottom` are the cursors the server returned, ready to feed
 *  back to it for re-fetch or further pagination. */
interface PageMeta {
    length: number;
    /** Cursor for `direction='after'` — walks toward newer entries
     *  starting just-above this page. `null` only when this page
     *  is at the timeline head. */
    top: string | null;
    /** Cursor for `direction='before'` — walks toward older entries
     *  starting just-below this page. `null` only when this page
     *  is at the timeline tail. */
    bottom: string | null;
}

/** Combined window state. Held as a single React state slot so
 *  eviction can update all three fields atomically. */
interface WindowState {
    entries: RegisterEntry[];
    pages: PageMeta[];
    firstItemIndex: number;
}

const EMPTY_STATE: WindowState = {
    entries: [],
    pages: [],
    firstItemIndex: 0,
};

export interface UseWindowedRegisterArgs {
    ledgerId: string;
    accountId: string;
    /** Optional focus seed — when present, the initial load
     *  anchors the page on this header (matching counterparty leg
     *  for the Show-Other-Side arrival pattern). Changing this
     *  value mid-mount resets the window and re-anchors. */
    focusHeaderId?: string;
    /** Per-request page size. The server's effective maximum is
     *  500; default is 100 — large enough that scrolling doesn't
     *  feel network-bound, small enough that each load is cheap. */
    pageSize?: number;
    /** When true, the window walks the soft-hidden rows instead of
     *  the visible register (ADR-0072 D1 — the Hidden view). Changing
     *  it resets and re-seeds the window. Defaults to false. */
    hidden?: boolean;
    /** Server-side filter (mig 164). Passed to every page fetch so the
     *  keyset cursor walks only matching entries. Changing it resets +
     *  re-seeds the window (same as `hidden`). MUST be a stable reference
     *  across renders (the caller memoizes it) — it's a direct effect dep. */
    filter?: RegisterFilterArgs;
    /** Column sort (mig 166). Passed to every page fetch. Changing it resets +
     *  re-seeds the window (a new order needs a fresh keyset walk) — same as
     *  `filter`. Stable reference expected (the caller memoizes it). */
    sort?: { column: string; dir: 'asc' | 'desc' };
}

export interface UseWindowedRegisterResult {
    /** Window of entries, time-DESC. `entries[0]` is the newest
     *  currently loaded. */
    entries: RegisterEntry[];
    /** Logical index of `entries[0]`. Bumps up on eviction from
     *  the front; bumps down on prepend. Pass to virtuoso's
     *  `firstItemIndex` so scroll position survives evictions. */
    firstItemIndex: number;
    /** True once the first fetch (initial / focus seed) returned;
     *  consumers gate the empty-state UI on this. */
    initialLoaded: boolean;
    /** Error from the initial fetch, if it threw. Null on success
     *  or while in-flight. */
    initialError: unknown;
    /** True when a `loadOlder` is in-flight; the caller can use
     *  this to render a skeleton at the bottom edge. */
    loadingOlder: boolean;
    /** True when a `loadNewer` is in-flight; render skeleton at
     *  the top edge. */
    loadingNewer: boolean;
    /** True when the window's newer edge is the absolute timeline
     *  head — no more rows exist past `entries[0]`. Drives the
     *  "newest transaction" sentinel above the list. */
    atTimelineHead: boolean;
    /** True when the window's older edge is the absolute timeline
     *  tail — no more rows exist past the last entry. Drives the
     *  "oldest transaction" sentinel below the list. */
    atTimelineTail: boolean;
    /** Discard the current window and re-fetch. Used after a
     *  mutation that may have changed the window's contents —
     *  e.g. a PATCH that turned a single-row into a multi-split
     *  (ADR-0025). Without this, the SPA can keep showing
     *  pre-save entries until the user scrolls past an eviction
     *  boundary.
     *
     *  <para>Optional <c>anchorHeaderId</c> overrides the URL's
     *  <c>focusHeaderId</c> for this one fetch: the server
     *  centres the new window on that row (start-at semantics),
     *  guaranteeing it lands at <c>focusIndex = 0</c>. Used by
     *  the post-PATCH path so the just-saved row is visible +
     *  focusable even when the user is editing a row deep in
     *  history. Without an anchor, refresh re-fetches the
     *  timeline-top page and a deep-history edit silently
     *  scrolls out of view.</para> */
    refresh: (anchorHeaderId?: string) => void;
    /** Trigger a load at the older edge. No-op when no older
     *  history is available (the timeline tail) or when an older
     *  load is already in flight. */
    loadOlder: () => void;
    /** Trigger a load at the newer edge. No-op at the timeline
     *  head or when a newer load is already in flight. */
    loadNewer: () => void;
    /** LOGICAL index of the focused row in the timeline, when the
     *  hook was seeded with `focusHeaderId` and the focused entry
     *  is still in the window. -1 otherwise. Consumers pass this
     *  to virtuoso's `initialTopMostItemIndex` to land on the
     *  row. */
    focusIndex: number;
    /** In-place mutation hook for optimistic updates. The mapper
     *  runs once per entry; return the same entry to leave it
     *  alone, return a patched copy to apply changes. */
    mutateEntries: (
        mapper: (entry: RegisterEntry) => RegisterEntry,
    ) => void;
    /** Drop an entry by predicate. Used by the delete flow. */
    removeEntries: (
        predicate: (entry: RegisterEntry) => boolean,
    ) => void;
}

export function useWindowedRegister(
    args: UseWindowedRegisterArgs,
): UseWindowedRegisterResult {
    const { ledgerId, accountId, focusHeaderId, pageSize = 100, hidden = false, filter, sort } = args;

    const [windowState, setWindowState] = useState<WindowState>(EMPTY_STATE);
    const { entries, pages, firstItemIndex } = windowState;
    const [initialLoaded, setInitialLoaded] = useState(false);
    const [initialError, setInitialError] = useState<unknown>(null);
    const [loadingOlder, setLoadingOlder] = useState(false);
    const [loadingNewer, setLoadingNewer] = useState(false);

    // Latched on first load. LOGICAL index — comparable with
    // virtuoso's `initialTopMostItemIndex` even after eviction.
    const [focusIndex, setFocusIndex] = useState(-1);

    // Refresh nonce — bumping it triggers the initial-load
    // useEffect via dep change, even when the identity args
    // (ledger/account/focus/pageSize) are unchanged. Drives the
    // `refresh()` method used by the mutation callbacks after a
    // PATCH or DELETE so the windowed register doesn't keep
    // showing pre-save entries (ADR-0025 follow-up — relying on
    // a `?focus=` URL change was fragile because the URL value
    // could match the prior search param and React would batch
    // away the dep change).
    const [refreshNonce, setRefreshNonce] = useState(0);

    // Anchor override for the *next* refresh. When set, the
    // initial-load effect uses this id as `startingAtHeaderId`
    // instead of the URL's `focusHeaderId`. Cleared back to
    // undefined on a no-arg refresh so subsequent refreshes
    // honour the URL again. Drives the post-PATCH "land on the
    // saved row" path — see `refresh(anchorHeaderId)` doc above.
    const [anchorOverride, setAnchorOverride] = useState<string | undefined>(undefined);

    // Generation counter — increments on every initial-load
    // invocation. Mid-flight effects compare against the latest
    // generation and bail if stale.
    const generationRef = useRef(0);

    useEffect(() => {
        const generation = ++generationRef.current;
        setWindowState(EMPTY_STATE);
        setInitialLoaded(false);
        setInitialError(null);
        setFocusIndex(-1);
        let cancelled = false;
        // Anchor priority: the one-shot override (set by
        // `refresh(anchorHeaderId)`) wins; otherwise fall back to
        // the URL-driven `focusHeaderId`. Either way, when an
        // anchor is in play we land focusIndex on the row.
        const anchor = anchorOverride ?? focusHeaderId;
        (async () => {
            try {
                const page = await fetchRegister({
                    ledgerId,
                    accountId,
                    limit: pageSize,
                    startingAtHeaderId: anchor,
                    hidden,
                    filter,
                    sort,
                });
                if (cancelled || generation !== generationRef.current) return;
                setWindowState({
                    entries: page.entries,
                    pages: [
                        {
                            length: page.entries.length,
                            top: page.cursorForNewer,
                            bottom: page.cursorForOlder,
                        },
                    ],
                    firstItemIndex: 0,
                });
                if (anchor !== undefined && page.entries.length > 0) {
                    // Server places the anchored entry at index 0
                    // (logical index 0 too, since firstItemIndex
                    // starts at 0).
                    setFocusIndex(0);
                }
                setInitialLoaded(true);
            } catch (err) {
                if (!cancelled && generation === generationRef.current) {
                    setInitialError(err);
                    setInitialLoaded(true);
                }
            }
        })();
        return () => {
            cancelled = true;
        };
    }, [ledgerId, accountId, focusHeaderId, pageSize, hidden, filter, sort, refreshNonce, anchorOverride]);

    const refresh = useCallback((anchorHeaderId?: string) => {
        // Set the one-shot anchor (or clear it back to undefined
        // when called without an arg) and bump the nonce. Both
        // setters batch into one re-render; the effect deps see
        // both new values together and re-run exactly once.
        setAnchorOverride(anchorHeaderId);
        setRefreshNonce((n) => n + 1);
    }, []);

    const loadOlder = useCallback(() => {
        const cursorForOlder = pages.at(-1)?.bottom ?? null;
        if (cursorForOlder === null || loadingOlder) return;
        const generation = generationRef.current;
        setLoadingOlder(true);
        (async () => {
            try {
                const page = await fetchRegister({
                    ledgerId,
                    accountId,
                    limit: pageSize,
                    cursor: cursorForOlder,
                    direction: 'before',
                    hidden,
                    filter,
                    sort,
                });
                if (generation !== generationRef.current) return;
                if (page.entries.length === 0) {
                    // Server says no more older — record by clearing
                    // the bottom cursor on the last page.
                    setWindowState((prev) => {
                        if (prev.pages.length === 0) return prev;
                        const last = prev.pages[prev.pages.length - 1];
                        if (last === undefined || last.bottom === null) return prev;
                        return {
                            ...prev,
                            pages: [
                                ...prev.pages.slice(0, -1),
                                { ...last, bottom: null },
                            ],
                        };
                    });
                    return;
                }
                const newPage: PageMeta = {
                    length: page.entries.length,
                    top: page.cursorForNewer,
                    bottom: page.cursorForOlder,
                };
                setWindowState((prev) => applyOlderLoad(prev, page.entries, newPage));
            } finally {
                if (generation === generationRef.current) setLoadingOlder(false);
            }
        })();
    }, [ledgerId, accountId, pages, loadingOlder, pageSize, hidden, filter, sort]);

    const loadNewer = useCallback(() => {
        const cursorForNewer = pages[0]?.top ?? null;
        if (cursorForNewer === null || loadingNewer) return;
        const generation = generationRef.current;
        setLoadingNewer(true);
        (async () => {
            try {
                const page = await fetchRegister({
                    ledgerId,
                    accountId,
                    limit: pageSize,
                    cursor: cursorForNewer,
                    direction: 'after',
                    hidden,
                    filter,
                    sort,
                });
                if (generation !== generationRef.current) return;
                if (page.entries.length === 0) {
                    setWindowState((prev) => {
                        if (prev.pages.length === 0) return prev;
                        const first = prev.pages[0];
                        if (first === undefined || first.top === null) return prev;
                        return {
                            ...prev,
                            pages: [
                                { ...first, top: null },
                                ...prev.pages.slice(1),
                            ],
                        };
                    });
                    return;
                }
                const newPage: PageMeta = {
                    length: page.entries.length,
                    top: page.cursorForNewer,
                    bottom: page.cursorForOlder,
                };
                setWindowState((prev) => applyNewerLoad(prev, page.entries, newPage));
            } finally {
                if (generation === generationRef.current) setLoadingNewer(false);
            }
        })();
    }, [ledgerId, accountId, pages, loadingNewer, pageSize, hidden, filter, sort]);

    const mutateEntries = useCallback(
        (mapper: (entry: RegisterEntry) => RegisterEntry) => {
            setWindowState((prev) => ({
                ...prev,
                entries: prev.entries.map(mapper),
            }));
        },
        [],
    );

    const removeEntries = useCallback(
        (predicate: (entry: RegisterEntry) => boolean) => {
            // We don't try to keep `pages` page-length metadata
            // perfectly aligned with single-entry deletes — that
            // would require knowing which page contained the
            // deleted entry. Instead, we shrink the last page by
            // the delete count. Cursor at its bottom edge stays
            // valid because deleting an entry doesn't move the
            // server-side cursor boundary; at worst we under-count
            // by one and trigger eviction slightly later.
            setWindowState((prev) => {
                const nextEntries = prev.entries.filter((e) => !predicate(e));
                const removed = prev.entries.length - nextEntries.length;
                if (removed === 0) return prev;
                if (prev.pages.length === 0) {
                    return { ...prev, entries: nextEntries };
                }
                const last = prev.pages[prev.pages.length - 1];
                if (last === undefined) {
                    return { ...prev, entries: nextEntries };
                }
                return {
                    ...prev,
                    entries: nextEntries,
                    pages: [
                        ...prev.pages.slice(0, -1),
                        { ...last, length: Math.max(0, last.length - removed) },
                    ],
                };
            });
        },
        [],
    );

    // Edge predicates — derived from the page metadata. `initialLoaded`
    // gates them so an empty pre-fetch window doesn't claim "at head
    // and tail" before the first response.
    const atTimelineHead = initialLoaded && (pages[0]?.top ?? null) === null;
    const atTimelineTail =
        initialLoaded && (pages.at(-1)?.bottom ?? null) === null;

    return {
        entries,
        firstItemIndex,
        initialLoaded,
        initialError,
        loadingOlder,
        loadingNewer,
        refresh,
        loadOlder,
        loadNewer,
        atTimelineHead,
        atTimelineTail,
        focusIndex,
        mutateEntries,
        removeEntries,
    };
}

// --------------------------------------------------------------------
// Load + eviction helpers (pure, work over WindowState snapshots)
// --------------------------------------------------------------------

/** Append a page to the older edge; evict whole pages from the
 *  newer edge if the total now exceeds `MAX_ENTRIES + EVICTION_HYSTERESIS`.
 *  The user is scrolling down (toward older entries) — evicting
 *  from the top is invisible to them. */
function applyOlderLoad(
    prev: WindowState,
    newEntries: readonly RegisterEntry[],
    newPage: PageMeta,
): WindowState {
    const appendedPages = [...prev.pages, newPage];
    const appendedEntries = [...prev.entries, ...newEntries];
    let total = appendedPages.reduce((s, p) => s + p.length, 0);
    let evictedFromFront = 0;
    let keepFromPage = 0;
    while (
        total > MAX_ENTRIES + EVICTION_HYSTERESIS
        && keepFromPage < appendedPages.length - 1
    ) {
        const drop = appendedPages[keepFromPage];
        if (drop === undefined) break;
        evictedFromFront += drop.length;
        total -= drop.length;
        keepFromPage++;
    }
    return {
        entries: evictedFromFront > 0
            ? appendedEntries.slice(evictedFromFront)
            : appendedEntries,
        pages: keepFromPage > 0
            ? appendedPages.slice(keepFromPage)
            : appendedPages,
        firstItemIndex: prev.firstItemIndex + evictedFromFront,
    };
}

/** Prepend a page to the newer edge; evict whole pages from the
 *  older edge if over cap. The user is scrolling up (toward
 *  newer / re-loading evicted territory) — evicting from the
 *  bottom is invisible to them. */
function applyNewerLoad(
    prev: WindowState,
    newEntries: readonly RegisterEntry[],
    newPage: PageMeta,
): WindowState {
    const prependedPages = [newPage, ...prev.pages];
    const prependedEntries = [...newEntries, ...prev.entries];
    let total = prependedPages.reduce((s, p) => s + p.length, 0);
    let evictedFromBack = 0;
    let keepUpToPage = prependedPages.length;
    while (
        total > MAX_ENTRIES + EVICTION_HYSTERESIS
        && keepUpToPage > 1
    ) {
        const drop = prependedPages[keepUpToPage - 1];
        if (drop === undefined) break;
        evictedFromBack += drop.length;
        total -= drop.length;
        keepUpToPage--;
    }
    // Prepending N entries drops `firstItemIndex` by N (data[0] is
    // now N items earlier in logical-index space). Evicting from
    // the back doesn't affect firstItemIndex.
    return {
        entries: evictedFromBack > 0
            ? prependedEntries.slice(
                  0,
                  prependedEntries.length - evictedFromBack,
              )
            : prependedEntries,
        pages: prependedPages.slice(0, keepUpToPage),
        firstItemIndex: prev.firstItemIndex - newEntries.length,
    };
}
