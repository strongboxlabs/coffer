import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { ApiError, fetchSelectionSummary } from '@/lib/api';
import type { RegisterFilterArgs } from '@/lib/api/register';
import type {
    SelectionRequest,
    SelectionStatusFilter,
    SelectionSummary,
} from '@/lib/types';

// Bulk-selection hook (ADR-0024). Owns the discriminated selection
// state and a debounced server-summary query so the footer's
// "N selected · Σ $X.XX" stays correct across:
//
//   * explicit mode — user clicked specific row checkboxes;
//   * 'all' mode — user clicked the header "select all" checkbox.
//
// 'all' mode captures Gmail's "everything in the current view"
// semantics: a `selectedAt` timestamp anchors the predicate to the
// moment the user clicked, so newly created / newly imported rows
// after that point do NOT silently join the selection. The
// `excludeIds` set tracks rows the user individually unchecks while
// in 'all' mode.
//
// The hook never enumerates ids client-side for 'all' mode — count
// + Σ come from the server via `selectionSummary` endpoint. This
// keeps the SPA correct even when selected rows have been evicted
// from the windowed register (out of view ≠ out of selection).
//
// Filter changes (account, status filter) reset the selection: per
// ADR-0024, switching filters is treated as a fresh intent, and
// trying to "follow" the predicate silently is more confusing than
// just clearing.

export interface UseSelectionArgs {
    ledgerId: string;
    /** Account scope for the register view. Selection resets when
     *  this changes (per ADR-0024). */
    accountId: string;
    /** Status filter for the register view ('all' / 'cleared' /
     *  'uncleared' / 'scheduled'). Resets selection on change. */
    statusFilter: SelectionStatusFilter;
    /** Active structured/search filter (mig 164). Threaded into 'all'-mode
     *  selections so a select-all covers exactly the filtered set (not the whole
     *  account). Resets the selection when it changes (a new filter is a fresh
     *  intent). Non-status dimensions only — status is `statusFilter`. */
    filter?: RegisterFilterArgs;
    /** Debounce window for the server summary call. Default 200ms —
     *  fast enough that the footer feels live, slow enough to coalesce
     *  rapid checkbox interactions into one round-trip. */
    debounceMs?: number;
}

export interface UseSelectionResult {
    /** The active selection in API shape (ready to send). */
    selection: SelectionRequest;
    /** Server-computed summary (count + Σ). Null until the first
     *  query resolves or when the selection is empty. */
    summary: SelectionSummary | null;
    /** True while a summary query is in flight. The footer can use
     *  this to dim the count subtly. */
    isSummarising: boolean;
    /** Server-side error from the most recent summary query, if
     *  any. Currently surfaces as a fallback to the last good
     *  summary; consumers may render a quieter inline message. */
    summaryError: unknown;
    /** True when the user has actively engaged selection (non-empty
     *  explicit set OR 'all' mode active). Drives the bulk-action
     *  footer's visibility. */
    hasSelection: boolean;
    /** True when ALL visible rows are effectively selected (counts
     *  as a 'checked' header checkbox). False otherwise. Each row
     *  carries `headerId + createdAt` so the predicate stays
     *  selectedAt-aware in `'all'` mode. */
    isAllVisibleSelected: (
        visible: readonly { headerId: string; createdAt: string }[],
    ) => boolean;
    /** True when SOME but not all of the visible rows are selected
     *  (indeterminate header checkbox). */
    isSomeVisibleSelected: (
        visible: readonly { headerId: string; createdAt: string }[],
    ) => boolean;
    /** True for individual row checkbox state — honors `selectedAt`
     *  in `'all'` mode. Pass the row's headerId AND its createdAt
     *  so the predicate stays consistent with the server's
     *  `created_at <= selectedAt` guard. Rows created after the
     *  user clicked "select all" render unchecked (ADR-0024 #3). */
    isSelected: (headerId: string, createdAt: string) => boolean;
    /** Toggle a single header's selection. In 'all' mode this
     *  adds/removes from `excludeIds`; in 'explicit' mode this
     *  adds/removes from `headerIds`. */
    toggleId: (headerId: string) => void;
    /** Shift-click range select: select every header between the anchor (the
     *  last plainly-toggled row) and `headerId` within `orderedHeaderIds` (the
     *  displayed order). Falls back to a plain toggle when there's no anchor in
     *  the loaded window. */
    extendSelectionTo: (headerId: string, orderedHeaderIds: readonly string[]) => void;
    /** Header-checkbox click. Transitions:
     *    explicit-empty → all-mode (select all rows in the view).
     *    explicit-with-ids → explicit-empty (clear).
     *    all-mode → explicit-empty (clear).
     *  The header checkbox's three states cycle "off → all → off".
     */
    toggleAll: () => void;
    /** Programmatically clear the selection (e.g. after a bulk
     *  action completes). Always lands in `explicit-empty`. */
    clear: () => void;
    /** Replace the selection with an explicit set of header ids.
     *  Used by the Show-Other-Side arrival path to lock the focused
     *  header in as a single-row selection. Lands in `explicit`. */
    setExplicit: (headerIds: readonly string[]) => void;
}

interface SelectionState {
    kind: 'explicit' | 'all';
    /** Explicit-mode: ids the user picked. 'all'-mode: ids the user
     *  individually unchecked. */
    ids: ReadonlySet<string>;
    /** 'all'-mode only: the moment the user clicked select-all.
     *  ISO-8601 UTC string. Empty in explicit mode. */
    selectedAt: string;
}

const EMPTY_STATE: SelectionState = {
    kind: 'explicit',
    ids: new Set(),
    selectedAt: '',
};

export function useSelection(args: UseSelectionArgs): UseSelectionResult {
    const { ledgerId, accountId, statusFilter, filter, debounceMs = 200 } = args;

    const [state, setState] = useState<SelectionState>(EMPTY_STATE);
    // Anchor for shift-click range selection: the last row toggled with a plain
    // (non-shift) click. A shift-click then selects the range between it and the
    // clicked row in the displayed order. Reset alongside the selection.
    const anchorRef = useRef<string | null>(null);
    const [summary, setSummary] = useState<SelectionSummary | null>(null);
    const [isSummarising, setIsSummarising] = useState(false);
    const [summaryError, setSummaryError] = useState<unknown>(null);

    // Reset on filter change (per ADR-0024).
    useEffect(() => {
        setState(EMPTY_STATE);
        anchorRef.current = null;
        setSummary(null);
        setSummaryError(null);
    }, [ledgerId, accountId, statusFilter, filter]);

    // Esc clears the selection — the keyboard companion to the footer's "✕".
    // Ignored while focus is in a field (search / inline editor / category
    // picker), where Esc means "close / cancel" and must not also nuke the
    // selection. Only acts when something is selected, so it doesn't swallow
    // Esc from anything else on the page.
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => {
            if (e.key !== 'Escape') return;
            const el = e.target as HTMLElement | null;
            const tag = el?.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
                || el?.isContentEditable) {
                return;
            }
            let cleared = false;
            setState((prev) => {
                if (prev.kind === 'explicit' && prev.ids.size === 0) return prev;
                cleared = true;
                return EMPTY_STATE;
            });
            if (cleared) anchorRef.current = null;
        };
        document.addEventListener('keydown', onKey);
        return () => document.removeEventListener('keydown', onKey);
    }, []);

    // Build the API-shape selection from local state.
    const selection: SelectionRequest = useMemo(() => {
        if (state.kind === 'explicit') {
            // Carry accountId so the server computes the Σ on this account
            // (GetSelectionSummaryAsync only sums when AccountId is set). The
            // bulk-apply query ignores it for explicit selections.
            return { kind: 'explicit', accountId, headerIds: Array.from(state.ids) };
        }
        return {
            kind: 'all',
            accountId,
            statusFilter,
            selectedAt: state.selectedAt,
            excludeIds: Array.from(state.ids),
            search: filter?.search,
            dateFrom: filter?.dateFrom,
            dateTo: filter?.dateTo,
            amountMin: filter?.amountMin,
            amountMax: filter?.amountMax,
            securityId: filter?.securityId,
            tag: filter?.tag,
            categoryId: filter?.categoryId,
        };
    }, [state, accountId, statusFilter, filter]);

    const hasSelection =
        state.kind === 'all' || state.ids.size > 0;

    // Debounced summary query. AbortController cancels the in-flight
    // request when state changes faster than the debounce window.
    const generationRef = useRef(0);
    useEffect(() => {
        if (!hasSelection) {
            setSummary(null);
            setSummaryError(null);
            return;
        }
        const generation = ++generationRef.current;
        const controller = new AbortController();
        const handle = window.setTimeout(() => {
            setIsSummarising(true);
            fetchSelectionSummary(ledgerId, selection, controller.signal)
                .then((s) => {
                    if (generation !== generationRef.current) return;
                    setSummary(s);
                    setSummaryError(null);
                })
                .catch((err: unknown) => {
                    if (generation !== generationRef.current) return;
                    // AbortError is normal during rapid interactions —
                    // ignore.
                    if (err instanceof DOMException && err.name === 'AbortError') {
                        return;
                    }
                    setSummaryError(err);
                })
                .finally(() => {
                    if (generation === generationRef.current) {
                        setIsSummarising(false);
                    }
                });
        }, debounceMs);
        return () => {
            window.clearTimeout(handle);
            controller.abort();
        };
    }, [ledgerId, selection, hasSelection, debounceMs]);

    const isSelected = useCallback(
        (headerId: string, createdAt: string) => {
            if (state.kind === 'explicit') return state.ids.has(headerId);
            // 'all'-mode: ids is the EXCLUDE set, AND the row must
            // have existed at selectedAt time. String comparison on
            // ISO-8601 UTC is lexicographic-equivalent to time
            // comparison (Postgres serializes them in the same
            // sortable shape).
            if (createdAt > state.selectedAt) return false;
            return !state.ids.has(headerId);
        },
        [state],
    );

    const isAllVisibleSelected = useCallback(
        (visible: readonly { headerId: string; createdAt: string }[]) => {
            if (visible.length === 0) return false;
            for (const row of visible) {
                if (!isSelected(row.headerId, row.createdAt)) return false;
            }
            return true;
        },
        [isSelected],
    );

    const isSomeVisibleSelected = useCallback(
        (visible: readonly { headerId: string; createdAt: string }[]) => {
            let any = false;
            let all = true;
            for (const row of visible) {
                if (isSelected(row.headerId, row.createdAt)) any = true;
                else all = false;
            }
            return any && !all;
        },
        [isSelected],
    );

    const toggleId = useCallback((headerId: string) => {
        // A plain toggle (re)sets the range anchor so a following shift-click
        // extends from here.
        anchorRef.current = headerId;
        setState((prev) => {
            const ids = new Set(prev.ids);
            if (ids.has(headerId)) ids.delete(headerId);
            else ids.add(headerId);
            return { ...prev, ids };
        });
    }, []);

    const extendSelectionTo = useCallback(
        (headerId: string, orderedHeaderIds: readonly string[]) => {
            const anchor = anchorRef.current;
            const ai = anchor === null ? -1 : orderedHeaderIds.indexOf(anchor);
            const bi = orderedHeaderIds.indexOf(headerId);
            // No usable anchor (first click, or the anchor scrolled out of the
            // loaded window) → behave like a plain toggle and set the anchor.
            if (ai === -1 || bi === -1) {
                anchorRef.current = headerId;
                setState((prev) => {
                    const ids = new Set(prev.ids);
                    if (ids.has(headerId)) ids.delete(headerId);
                    else ids.add(headerId);
                    return { ...prev, ids };
                });
                return;
            }
            const [lo, hi] = ai <= bi ? [ai, bi] : [bi, ai];
            const range = orderedHeaderIds.slice(lo, hi + 1);
            // Shift-click builds an EXPLICIT range from the anchor. If we were in
            // 'all' mode, collapse to a fresh explicit range. The anchor stays
            // put so a further shift-click re-extends from the same origin.
            setState((prev) => {
                const ids = prev.kind === 'explicit' ? new Set(prev.ids) : new Set<string>();
                for (const id of range) ids.add(id);
                return { kind: 'explicit', ids, selectedAt: '' };
            });
        },
        [],
    );

    const toggleAll = useCallback(() => {
        setState((prev) => {
            // Off (explicit-empty) → on ('all' mode anchored at now).
            if (prev.kind === 'explicit' && prev.ids.size === 0) {
                return {
                    kind: 'all',
                    ids: new Set(),
                    selectedAt: new Date().toISOString(),
                };
            }
            // Anything else → off.
            return EMPTY_STATE;
        });
    }, []);

    const clear = useCallback(() => {
        setState(EMPTY_STATE);
    }, []);

    const setExplicit = useCallback((headerIds: readonly string[]) => {
        setState({
            kind: 'explicit',
            ids: new Set(headerIds),
            selectedAt: '',
        });
    }, []);

    return {
        selection,
        summary,
        isSummarising,
        summaryError,
        hasSelection,
        isAllVisibleSelected,
        isSomeVisibleSelected,
        isSelected,
        toggleId,
        extendSelectionTo,
        toggleAll,
        clear,
        setExplicit,
    };
}

/** Treat an ApiError or any thrown value as a human-readable error
 *  message. Mirrors the helpers in RegisterPage; lifted here so
 *  consumers of the hook don't have to redefine. */
export function selectionErrorMessage(error: unknown): string {
    if (error instanceof ApiError) return error.detail;
    if (error instanceof Error && error.message.length > 0) return error.message;
    return 'Could not compute the selection summary.';
}
