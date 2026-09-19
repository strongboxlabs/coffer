/**
 * The bank editor's posting draft, and the pure inverters that build one.
 *
 * FIRST PIECE of the TxnRowEdit decomposition (follow-ups Slice 3), mirroring
 * the shape `investment-edit/` already has: pure `.ts` at the root of the
 * folder, so the extension alone tells a reader what may import React.
 *
 * WHY THE INPUTS ARE STRUCTURAL. The obvious extraction imports `PostingSeed`
 * and `PostingPrefill` from `TxnRowEdit.tsx` — and that is a cycle, because the
 * shell imports this module. The investment editor solved the same problem the
 * same way: its inverter takes a MINIMAL STRUCTURAL interface rather than the
 * register DTO, which is what let the reminders path (`reminderOccurrenceDraft`)
 * reuse it with no cast. The shell's exported DTOs satisfy these structurally,
 * so nothing at the call sites changes.
 *
 * WHY `nextKey` LIVES HERE AND IS NOT PURE. It is a module-level counter, and
 * that is load-bearing rather than sloppy. `key` is the React key for a leg row
 * and must be STABLE across a reorder — deriving it from array position or from
 * `legId` would remount rows mid-drag (a newly added posting has no `legId` at
 * all). It also means draft construction is not idempotent: calling an inverter
 * twice mints different keys, which is exactly why the draft hook must capture
 * its initial value ONCE with `useState` rather than recompute it. A recomputed
 * initial carries different keys from the live draft, which latches `dirty`
 * true forever and makes `reset()` remount every row, destroying focus and the
 * per-leg textarea heights.
 */

/** A posting as the editor holds it while being edited. */
export interface PostingDraft {
    /** Stable id within the editor's lifecycle — used as React
     *  key during reorder. Distinct from `legId`: a freshly added
     *  posting has `legId === null` but always a `key`. */
    key: string;
    /** Existing source-side leg id (PATCH-only; null for newly
     *  added postings the server will INSERT). */
    legId: string | null;
    /** The chosen counterparty account/category id (ADR-0043 —
     *  id-based via AccountCategoryPicker; null until picked). */
    counterpartyId: string | null;
    /** Free-text amount input. Parsed on save. */
    amount: string;
    legMemo: string;
}

/**
 * The minimum an EXISTING leg must carry to be inverted into a draft.
 * `TxnRowEdit`'s `PostingSeed` satisfies this and carries more
 * (`counterpartyAccountName`, which the picker resolves from the accounts map
 * instead).
 */
export interface PostingSeedLike {
    legId: string;
    counterpartyAccountId: string | null;
    amount: number;
    legMemo: string | null;
}

/** The minimum a DUPLICATE-prefill posting must carry. Note `legMemo` is
 *  optional here, matching `PostingPrefill` — a prefill may omit it. */
export interface PostingPrefillLike {
    counterpartyAccountId: string | null;
    amount: number;
    legMemo?: string | null;
}

let postingKeyCounter = 0;

/** Next editor-local row key. Not pure, deliberately — see the module note. */
export function nextKey(): string {
    postingKeyCounter += 1;
    return `p_${postingKeyCounter}`;
}

/** Invert an existing leg into a draft (the EDIT path). */
export function seedToDraft(s: PostingSeedLike): PostingDraft {
    return {
        key: nextKey(),
        legId: s.legId,
        // Id-based (ADR-0043): the picker resolves the display name
        // from the full accounts map, so an existing system-account
        // counterparty (e.g. Uncategorized) still round-trips even
        // though it isn't offered for a fresh pick.
        counterpartyId: s.counterpartyAccountId,
        amount: s.amount.toFixed(2),
        legMemo: s.legMemo ?? '',
    };
}

/** A blank posting — what "Add split" appends, and the NEW path's first leg. */
export function emptyDraft(): PostingDraft {
    return {
        key: nextKey(),
        legId: null,
        counterpartyId: null,
        amount: '',
        legMemo: '',
    };
}

/** Map one duplicate-prefill posting to a fresh draft (no legId). The
 *  edit path has `seedToDraft`; this is its new-mode twin. */
export function prefillToDraft(p: PostingPrefillLike): PostingDraft {
    return {
        key: nextKey(),
        legId: null,
        counterpartyId: p.counterpartyAccountId,
        amount: p.amount.toFixed(2),
        legMemo: p.legMemo ?? '',
    };
}
