/**
 * The bank editor's draft state, in one place.
 *
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition — it was
 * eight `useState` calls and five mutator closures inlined in the shell, which
 * meant the ONLY way to exercise a reorder or an add was to render the whole
 * editor and drive it through the DOM.
 *
 * THREE PROPERTIES THAT ARE LOAD-BEARING, not incidental:
 *
 *   1. `initial` is captured with `useState`, never `useMemo`. Seeding mints a
 *      fresh React `key` per posting (see ../postingDraft.ts), so it is NOT
 *      idempotent: a recomputed initial would carry different keys from the
 *      live draft, remounting every leg row and destroying focus and any
 *      in-flight edit. `useState(() => …)` runs the initialiser exactly once.
 *
 *   2. `addPosting` RETURNS the new key and sets the focus marker itself, in
 *      the same synchronous statement. Both setState calls therefore land in
 *      one React batch. Any deferral — a useEffect, a callback, a promise —
 *      renders the new row once with `autoFocusAmount=false`, and the row's
 *      focus effect never fires, so clicking "Add split" leaves the caret
 *      wherever it was.
 *
 *   3. The mutators are `useCallback` with EMPTY dep arrays, reachable only
 *      through the functional `setPostings` form. They never close over
 *      `postings`, so their identity is stable for the life of the editor —
 *      which matters because they are passed to memoised row components and to
 *      the picker's eligibility chain (see ../fields/PostingRowEditor.tsx).
 */

import { useCallback, useState } from 'react';

import { emptyDraft, nextKey, type PostingDraft } from '../postingDraft';
import {
    seedCheckNumber,
    seedMemo,
    seedPayee,
    seedPostedAt,
    seedPostings,
    seedTags,
    seedTransactedAt,
} from '../draft';
import type { TxnRowMode } from '../../TxnRowEdit';

/** The seven editable fields, as one value. */
export interface TxnRowDraft {
    payee: string;
    headerMemo: string;
    checkNumber: string;
    /** `yyyy-mm-dd`, the date input's own format — not an ISO stamp. */
    postedAt: string;
    /** `yyyy-mm-dd`, or '' meaning "no distinct tax date". */
    transactedAt: string;
    tags: readonly string[];
    postings: readonly PostingDraft[];
}

export interface TxnRowDraftApi {
    draft: TxnRowDraft;

    setPayee: (v: string) => void;
    setHeaderMemo: (v: string) => void;
    setCheckNumber: (v: string) => void;
    setPostedAt: (v: string) => void;
    setTransactedAt: (v: string) => void;
    setTags: (v: readonly string[]) => void;

    /** Merge `fields` into the posting with this key. */
    patchPosting: (key: string, fields: Partial<PostingDraft>) => void;
    /** Append a posting, mark it for auto-focus, and return its key. */
    addPosting: (prefill?: Partial<PostingDraft>) => string;
    /** Drop a posting — a no-op on the last one; a transaction needs one. */
    removePosting: (key: string) => void;
    /** Drag-and-drop: lift `fromKey` out and re-insert it at `toKey`'s index. */
    reorderPostings: (fromKey: string, toKey: string) => void;
    /** Keyboard / menu reorder: nudge one posting by one slot. */
    movePosting: (key: string, delta: -1 | 1) => void;

    /** Key of the posting whose amount input should claim focus on mount. */
    focusKey: string | null;
    setFocusKey: (key: string | null) => void;
}

export function useTxnRowDraft(mode: TxnRowMode): TxnRowDraftApi {
    // See note 1 — every one of these initialisers runs exactly once.
    const [payee, setPayee] = useState(() => seedPayee(mode));
    const [headerMemo, setHeaderMemo] = useState(() => seedMemo(mode));
    const [checkNumber, setCheckNumber] = useState(() => seedCheckNumber(mode));
    const [postedAt, setPostedAt] = useState(() => seedPostedAt(mode));
    const [transactedAt, setTransactedAt] = useState(() => seedTransactedAt(mode));
    const [tags, setTags] = useState<readonly string[]>(() => seedTags(mode));
    const [postings, setPostings] = useState<readonly PostingDraft[]>(
        () => seedPostings(mode),
    );

    const [focusKey, setFocusKey] = useState<string | null>(null);

    const patchPosting = useCallback((key: string, fields: Partial<PostingDraft>) => {
        setPostings((prev) => prev.map((p) => (p.key === key ? { ...p, ...fields } : p)));
    }, []);

    // See note 2 — the key is minted HERE, outside the updater, so the caller
    // gets it back synchronously and both setState calls batch together.
    const addPosting = useCallback((prefill?: Partial<PostingDraft>) => {
        const key = nextKey();
        setPostings((prev) => [...prev, { ...emptyDraft(), ...prefill, key, legId: null }]);
        setFocusKey(key);
        return key;
    }, []);

    const removePosting = useCallback((key: string) => {
        setPostings((prev) => (prev.length === 1 ? prev : prev.filter((p) => p.key !== key)));
    }, []);

    // Asymmetric by design, and NOT an off-by-one: the moved row is spliced out
    // first, so dropping onto a row BELOW lands after it and dropping onto one
    // ABOVE lands before it. That is what a drag reads as — the pointer is over
    // the row you want to end up next to, on the side you came from.
    const reorderPostings = useCallback((fromKey: string, toKey: string) => {
        if (fromKey === toKey) return;
        setPostings((prev) => {
            const fromIdx = prev.findIndex((p) => p.key === fromKey);
            const toIdx = prev.findIndex((p) => p.key === toKey);
            if (fromIdx < 0 || toIdx < 0) return prev;
            const next = [...prev];
            const [moved] = next.splice(fromIdx, 1);
            if (moved === undefined) return prev;
            next.splice(toIdx, 0, moved);
            return next;
        });
    }, []);

    // The keyboard path. Expressed as a swap rather than by reusing
    // reorderPostings: "move up one" has to mean exactly one slot in both
    // directions, which the splice semantics above do NOT give you (dropping
    // onto the row above lands before it — one slot — but dropping onto the
    // row below lands after it, which is also one slot only because the
    // neighbour is adjacent; the two agree here and diverge everywhere else).
    // A swap is the same thing for adjacent rows and cannot drift.
    const movePosting = useCallback((key: string, delta: -1 | 1) => {
        setPostings((prev) => {
            const idx = prev.findIndex((p) => p.key === key);
            if (idx < 0) return prev;
            const target = idx + delta;
            if (target < 0 || target >= prev.length) return prev;
            const next = [...prev];
            const a = next[idx]!;
            const b = next[target]!;
            next[idx] = b;
            next[target] = a;
            return next;
        });
    }, []);

    return {
        draft: { payee, headerMemo, checkNumber, postedAt, transactedAt, tags, postings },
        setPayee,
        setHeaderMemo,
        setCheckNumber,
        setPostedAt,
        setTransactedAt,
        setTags,
        patchPosting,
        addPosting,
        removePosting,
        reorderPostings,
        movePosting,
        focusKey,
        setFocusKey,
    };
}
