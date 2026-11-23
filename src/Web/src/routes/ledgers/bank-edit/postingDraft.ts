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

import { amountText } from '@/lib/money';

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
    /** The text `amount` was seeded with from a stored or copied figure; absent on a
     *  line that started blank. A save rounds the amount to its currency's places only
     *  when the text differs from it, so a stored figure finer than its currency is sent
     *  as it was when the line was left alone (ADR-0114 D15). */
    seededAmount?: string;
    /**
     * The second amount's magnitude, as typed or seeded: the counterparty's own figure on
     * a posting between real accounts in different currencies (ADR-0114 D13, D14). Its
     * sign is always opposite Amount's, so the field holds no sign and a save applies one
     * (lib/secondAmount). Absent or '' while there is none: the field shows only while
     * the counterparty is a real account in another currency, and changing the
     * counterparty drops it (see `withCounterparty`).
     */
    otherAmount?: string;
    /** True once the second amount is typed in or seeded from a stored or copied figure:
     *  it is never refilled from a rate then. Absent: it follows Amount and the date. */
    otherFixed?: boolean;
    /** The text `otherAmount` was seeded with, as for `seededAmount`: a save rounds the
     *  figure at the other currency's places only when the text differs from it. */
    seededOtherAmount?: string;
    /** The line's own memo on a split. On a one-posting transaction the Memo
     *  field is the side's, not this. */
    legMemo: string;
    /** The line's own tags on a split (ADR-0103). Distinct from the
     *  transaction's, and not a subset of them: the transaction's say what it
     *  was, these say what the line was. */
    tags: readonly string[];
    /** How many tags this line held when it was loaded: the size the tag limit
     *  measures growth from. 0 for a new line. */
    tagBaseline: number;
    /** What this line holds in storage, which a save compares against to send
     *  only what changed; null for a line nothing is stored for (a new line, or
     *  any line of a create). Carried on the line, not looked up by leg id, so
     *  a stored line seeded without one is still compared. `tags` is null when
     *  the source did not say what the line holds, which is not the same as
     *  none; the reminder template read always says. */
    stored: { memo: string; tags: readonly string[] | null } | null;
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
    /** Optional so older callers still satisfy this structurally; absent reads
     *  as "no tags", which is what a posting with none returns anyway. */
    legTags?: readonly string[];
    /** The counterparty leg's own figure on a transfer between currencies (ADR-0114 D13),
     *  signed; null or absent on a mirrored pair. */
    counterpartyAmount?: number | null;
}

/** The minimum a DUPLICATE-prefill posting must carry. Note `legMemo` is
 *  optional here, matching `PostingPrefill` — a prefill may omit it. */
export interface PostingPrefillLike {
    /** A stored line's source leg; read only by the stored path below. */
    legId?: string | null;
    counterpartyAccountId: string | null;
    amount: number;
    legMemo?: string | null;
    legTags?: readonly string[];
    /** The counterparty leg's own figure, signed, as for {@link PostingSeedLike}. */
    counterpartyAmount?: number | null;
}

let postingKeyCounter = 0;

/** Next editor-local row key. Not pure, deliberately — see the module note. */
export function nextKey(): string {
    postingKeyCounter += 1;
    return `p_${postingKeyCounter}`;
}

/**
 * The second amount a stored or copied counterparty figure seeds (ADR-0114 D14): its
 * magnitude at the other currency's places, fixed, so it is never refilled from a rate.
 * Nothing for a mirrored pair, whose field, if the editor shows one, follows Amount.
 */
function seededOther(
    counterpartyAmount: number | null | undefined,
    otherCurrency: string | null,
): Pick<PostingDraft, 'otherAmount' | 'otherFixed' | 'seededOtherAmount'> {
    if (counterpartyAmount === null || counterpartyAmount === undefined) return {};
    const text = amountText(Math.abs(counterpartyAmount), otherCurrency);
    return { otherAmount: text, otherFixed: true, seededOtherAmount: text };
}

/**
 * The fields a counterparty change writes: the new counterparty, and the second amount
 * dropped (ADR-0114 D14). A field that comes back, for another account in another
 * currency, is prefilled afresh, as for a new entry. Unchanged when the id is the same.
 */
export function withCounterparty(p: PostingDraft, counterpartyId: string | null): PostingDraft {
    if (p.counterpartyId === counterpartyId) return p;
    const { otherAmount: _a, otherFixed: _f, seededOtherAmount: _s, ...rest } = p;
    return { ...rest, counterpartyId };
}

/**
 * Invert an existing leg into a draft (the EDIT path). `currency` is the posting's:
 * the amount is seeded at its places, with every digit a stored figure carries beyond
 * them (ADR-0114 D15). Null pads nothing. `otherCurrency` is the counterparty's, the
 * places a stored second amount is seeded at.
 */
export function seedToDraft(s: PostingSeedLike, currency: string | null, otherCurrency: string | null = null): PostingDraft {
    const amount = amountText(s.amount, currency);
    return {
        ...seededOther(s.counterpartyAmount, otherCurrency),
        key: nextKey(),
        legId: s.legId,
        // Id-based (ADR-0043): the picker resolves the display name
        // from the full accounts map, so an existing system-account
        // counterparty (e.g. Uncategorized) still round-trips even
        // though it isn't offered for a fresh pick.
        counterpartyId: s.counterpartyAccountId,
        amount,
        seededAmount: amount,
        legMemo: s.legMemo ?? '',
        tags: s.legTags ?? [],
        tagBaseline: s.legTags?.length ?? 0,
        stored: { memo: s.legMemo ?? '', tags: s.legTags ?? [] },
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
        tags: [],
        tagBaseline: 0,
        stored: null,
    };
}

/** Map one duplicate-prefill posting to a fresh draft (no legId unless
 *  stored, below). The
 *  edit path has `seedToDraft`; this is its new-mode twin.
 *
 *  `isStored`: the prefill is a stored line being edited through the create
 *  form (a reminder template's), so its values are what a save compares
 *  against and what the tag limit measures growth from, and its leg id is
 *  what the server matches the rebuilt line to the stored one by. `currency` is the
 *  posting's, as for `seedToDraft`: a copied figure is seeded with every digit it has,
 *  and is rounded only once the user changes it, so a copy of a figure finer than its
 *  currency is refused by the API rather than silently changed. A copied second amount
 *  (`otherCurrency` is the counterparty's) is seeded the same way, and never refilled:
 *  Duplicate and a reminder template carry both amounts (ADR-0114 D14). */
export function prefillToDraft(
    p: PostingPrefillLike,
    currency: string | null,
    isStored = false,
    otherCurrency: string | null = null,
): PostingDraft {
    const tags = p.legTags ?? [];
    const amount = amountText(p.amount, currency);
    const other = seededOther(p.counterpartyAmount, otherCurrency);
    if (isStored) {
        return {
            ...other,
            key: nextKey(),
            legId: p.legId ?? null,
            counterpartyId: p.counterpartyAccountId,
            amount,
            seededAmount: amount,
            legMemo: p.legMemo ?? '',
            tags,
            tagBaseline: tags.length,
            stored: { memo: p.legMemo ?? '', tags: p.legTags ?? null },
        };
    }
    return {
        ...other,
        key: nextKey(),
        legId: null,
        counterpartyId: p.counterpartyAccountId,
        amount,
        seededAmount: amount,
        legMemo: p.legMemo ?? '',
        // A duplicate copies the posting's tags along with its memo and amount:
        // duplicating a split and losing which project each posting was for
        // would defeat the point of having recorded it.
        tags,
        // A copy is a new line: nothing is stored yet, so the limit applies
        // from zero, as the server applies it to a create.
        tagBaseline: 0,
        stored: null,
    };
}
