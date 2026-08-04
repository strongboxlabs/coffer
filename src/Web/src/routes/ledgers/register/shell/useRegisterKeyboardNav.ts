import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Shared register keyboard-navigation controller (review #18).
 * Extracted verbatim from the duplicated focus + keyboard wiring
 * BankRegisterPage and InvestmentRegisterPage each reimplemented:
 *
 *   * `focusedRowId` state + its synchronous ref-mirror (so OS
 *     auto-repeat on a held arrow key, and click-then-immediate-
 *     arrow, read the current value instead of the closure-captured
 *     last-commit value),
 *   * `moveFocus(delta)` — edge-paginate + clamp + scrollIntoView,
 *   * the document-level ArrowUp / ArrowDown / Enter (+ optional `N`)
 *     keydown handler with the typing-target guard.
 *
 * The two pages differ only in the row collection, the per-row id
 * extractor, how the scroll index maps (logical vs local), whether
 * `N` opens a new row, and what Enter does on the focused row — all
 * parameterized below. The control flow is byte-for-byte the same.
 */

export interface UseRegisterKeyboardNavArgs<Row> {
    /** The visible row collection in display order. */
    rows: readonly Row[];
    /** Stable id for a row, or null when the row has no focusable
     *  identity (e.g. a non-txn investment entry). */
    getRowId: (row: Row) => string | null;
    /** Paginate toward newer entries when arrowing up past the
     *  loaded-window's top edge. */
    onLoadNewer: () => void;
    /** Paginate toward older entries when arrowing down past the
     *  loaded-window's bottom edge. */
    onLoadOlder: () => void;
    /** When true (a client-side status filter is active), do NOT paginate at
     *  the window edges — the sparse filtered subset would auto-load itself
     *  into eviction (the flash-then-blank bug, #322). Mirrors the virtuoso
     *  edge-load suppression; both read the single `isClientFiltered` rule so
     *  the policy can't drift between mouse and keyboard, or between pages. */
    suppressEdgeLoad?: boolean;
    /** Scroll a freshly-focused row into view. `localIndex` is the
     *  row's position inside `rows`; callers that track a logical
     *  (eviction-stable) index add their `firstItemIndex` offset. */
    scrollRowIntoView: (localIndex: number) => void;
    /** False while an edit row / new-row form is open — suppresses
     *  the document handler so the editor owns the keyboard. */
    enabled: boolean;
    /** Invoked on Enter when a row is focused. Receives the current
     *  focused row id + the raw event; the page resolves the id to a
     *  row, decides whether to open edit (e.g. only txn rows, never
     *  target-split rows), and calls `e.preventDefault()` itself
     *  exactly when it acts — matching the per-page originals, which
     *  only suppressed the default when they actually opened edit. */
    onEnterRow: (focusedRowId: string, e: globalThis.KeyboardEvent) => void;
    /** Optional `N`-to-create handler. When omitted (investment),
     *  the `N` shortcut is not bound. */
    onCreate?: () => void;
}

export interface UseRegisterKeyboardNavResult {
    focusedRowId: string | null;
    /** Set focus — updates the ref synchronously before queuing the
     *  React state update. ALWAYS use this, never a raw setter. */
    setFocusedRowId: (id: string | null) => void;
    /** Live ref mirror of `focusedRowId` for synchronous reads. */
    focusedRowIdRef: React.MutableRefObject<string | null>;
    moveFocus: (delta: number) => void;
}

export function useRegisterKeyboardNav<Row>({
    rows,
    getRowId,
    onLoadNewer,
    onLoadOlder,
    suppressEdgeLoad = false,
    scrollRowIntoView,
    enabled,
    onEnterRow,
    onCreate,
}: UseRegisterKeyboardNavArgs<Row>): UseRegisterKeyboardNavResult {
    const [focusedRowId, setFocusedRowIdState] = useState<string | null>(null);
    // Mirror of focusedRowId in a ref so rapid-fire keydown handlers
    // (OS auto-repeat from holding Arrow Up/Down) AND click-then-
    // immediate-arrow sequences read the current value synchronously
    // — without this, the keydown handler reads the closure-captured
    // focusedRowId from React's last commit, which lags any setter
    // call that hasn't been committed yet.
    const focusedRowIdRef = useRef<string | null>(null);
    const setFocusedRowId = useCallback((id: string | null) => {
        focusedRowIdRef.current = id;
        setFocusedRowIdState(id);
    }, []);

    const moveFocus = useCallback(
        (delta: number) => {
            if (rows.length === 0) return;
            // Read from the ref so OS auto-repeat (held arrow key)
            // advances correctly instead of all keypresses reading
            // the same closure-captured focusedRowId.
            const currentId = focusedRowIdRef.current;
            const currentIndex = currentId === null
                ? -1
                : rows.findIndex((r) => getRowId(r) === currentId);

            // At the loaded-window edges, trigger a paginate so the
            // user can arrow-key past the boundary. The fetch is
            // async; focus stays on the edge row — the next press
            // lands into the newly-loaded entries.
            if (currentIndex === 0 && delta < 0) {
                if (!suppressEdgeLoad) onLoadNewer();
                return;
            }
            if (currentIndex === rows.length - 1 && delta > 0) {
                if (!suppressEdgeLoad) onLoadOlder();
                return;
            }

            // First arrow-down with no focus lands on row 0; first
            // arrow-up with no focus lands on the last row.
            let nextIndex: number;
            if (currentIndex === -1) {
                nextIndex = delta > 0 ? 0 : rows.length - 1;
            } else {
                nextIndex = Math.max(
                    0,
                    Math.min(rows.length - 1, currentIndex + delta),
                );
            }
            const nextRow = rows[nextIndex];
            if (nextRow === undefined) return;
            const nextId = getRowId(nextRow);
            if (nextId === null) return;
            setFocusedRowId(nextId);
            scrollRowIntoView(nextIndex);
        },
        [rows, getRowId, onLoadNewer, onLoadOlder, suppressEdgeLoad, scrollRowIntoView, setFocusedRowId],
    );

    useEffect(() => {
        function onDocKeyDown(e: globalThis.KeyboardEvent) {
            const target = e.target as HTMLElement | null;
            const tag = target?.tagName;
            // Text-like inputs (text/number/date/search/email) and
            // textareas own their own keyboard semantics — never
            // hijack from them. Checkboxes and buttons don't grab
            // Enter / N for anything useful, so we let our handler
            // fire even when they're focused (this is what makes
            // Enter-after-checkbox-click reliably open edit).
            const isTextLikeInput =
                tag === 'INPUT'
                && (() => {
                    const t = (target as HTMLInputElement).type;
                    return t !== 'checkbox' && t !== 'radio' && t !== 'button' && t !== 'submit';
                })();
            const isTypingTarget =
                isTextLikeInput
                || tag === 'TEXTAREA'
                || tag === 'SELECT'
                || target?.isContentEditable === true;

            if (onCreate && (e.key === 'n' || e.key === 'N')) {
                if (isTypingTarget) return;
                if (!enabled) return;
                if (e.metaKey || e.ctrlKey || e.altKey) return;
                e.preventDefault();
                onCreate();
            } else if (e.key === 'Enter') {
                if (isTypingTarget) return;
                if (!enabled) return;
                const currentId = focusedRowIdRef.current;
                if (currentId === null) return;
                // The page decides whether this row is editable and
                // calls `e.preventDefault()` itself only when it
                // actually opens edit — preserving the per-page
                // originals, which left the default intact on
                // non-editable focused rows (split-parent / target).
                onEnterRow(currentId, e);
            } else if (e.key === 'ArrowDown') {
                if (isTypingTarget) return;
                if (!enabled) return;
                e.preventDefault();
                moveFocus(1);
            } else if (e.key === 'ArrowUp') {
                if (isTypingTarget) return;
                if (!enabled) return;
                e.preventDefault();
                moveFocus(-1);
            }
        }
        document.addEventListener('keydown', onDocKeyDown);
        return () => document.removeEventListener('keydown', onDocKeyDown);
    }, [enabled, onCreate, onEnterRow, moveFocus]);

    return { focusedRowId, setFocusedRowId, focusedRowIdRef, moveFocus };
}
