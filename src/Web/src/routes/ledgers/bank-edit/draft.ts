import { toDateInputValue, todayInputValue } from '@/lib/dates';

import { emptyDraft, prefillToDraft, seedToDraft, type PostingDraft } from './postingDraft';
import type { NotesBaseline } from './notesBody';
import type { TxnRowMode } from '../TxnRowEdit';

/**
 * How a bank-editor draft is SEEDED from a mode. Pure, one function per field.
 *
 * WHY PER-FIELD AND NOT ONE `modeToDraft`. The obvious shape is a single
 * constructor returning the whole draft, with the shell then reading
 * `draft.payee` everywhere. That is the right end state, but it means touching
 * every reference to seven state variables across 1500 lines in the SAME diff
 * that moves the seeding logic — two risks at once, with the gate only able to
 * tell me that something broke, not which of the two.
 *
 * Per-field seeds are behaviour-identical and need no call-site changes: the
 * shell keeps its `useState(() => seedPayee(mode))` per field, exactly as
 * before. The logic becomes unit-testable now; consolidating the state is a
 * separate later step whose only job is moving references.
 *
 * WHY THE CALL SITES MUST STAY `useState` AND NOT BECOME `useMemo`.
 * `seedPostings` mints row keys from a module counter (see `postingDraft.ts`),
 * so seeding is NOT idempotent. `useMemo` is a cache hint, not a guarantee —
 * React may discard it, and StrictMode re-runs render — and a re-seeded
 * postings list carries different keys from the live draft, which remounts
 * every leg row and destroys focus plus the per-leg textarea heights. Each
 * seed must run exactly once per mount.
 *
 * The `import type` of `TxnRowMode` is erased at compile time, so it creates no
 * runtime cycle with the shell. The posting inverters still take structural
 * inputs (design choice 1); only this module — which exists precisely to
 * interpret the shell's mode contract — names the shell's type.
 */

export function seedPayee(mode: TxnRowMode): string {
    return mode.kind === 'new' ? (mode.prefill?.payee ?? '') : (mode.payee ?? '');
}

export function seedMemo(mode: TxnRowMode): string {
    return mode.kind === 'new' ? (mode.prefill?.memo ?? '') : (mode.memo ?? '');
}

export function seedCheckNumber(mode: TxnRowMode): string {
    return mode.kind === 'new'
        ? (mode.prefill?.checkNumber ?? '')
        : (mode.checkNumber ?? '');
}

export function seedPostedAt(mode: TxnRowMode): string {
    return mode.kind === 'new'
        ? (mode.postedAt ?? todayInputValue())
        : toDateInputValue(mode.postedAt);
}

/**
 * Empty string means "not set", which the payloads send as null. Deliberately
 * NOT defaulted to the posted date: writing transactedAt == postedAt on every
 * transaction would make the column meaningless and would permanently silence
 * taxDateSubLabel's noise filter, which only renders a second line when the two
 * dates differ.
 */
export function seedTransactedAt(mode: TxnRowMode): string {
    if (mode.kind === 'new' || !mode.transactedAt) return '';
    const tax = toDateInputValue(mode.transactedAt);
    // Same noise filter the register's taxDateSubLabel uses: a tax date on the
    // same calendar day as the posted date carries no information, so show the
    // field empty rather than echoing the posted date back. The Moneydance
    // importer sets transacted_at on EVERY row, so without this every imported
    // transaction would open with a redundant tax date filled in.
    //
    // Compared through toDateInputValue on BOTH sides, never as raw strings.
    // Two ISO stamps for one calendar day are rarely byte-identical, and a raw
    // `===` would still pass the existing same-day test — which feeds identical
    // strings — while being wrong for every real pair.
    //
    // Consequence, accepted: saving such a row untouched normalises the stored
    // value to null. Nothing is lost — same-day and null render identically and
    // mean the same thing — and it moves the data toward the cleaner of two
    // equivalent representations.
    return tax === toDateInputValue(mode.postedAt) ? '' : tax;
}

/**
 * The Tags field. In edit mode seeds from this side's own tags (a one-split)
 * or the transaction's (a split); in new mode from the Duplicate prefill, else
 * empty.
 */
export function seedTags(mode: TxnRowMode): readonly string[] {
    return mode.kind === 'new' ? (mode.prefill?.tags ?? []) : mode.tags;
}

/**
 * What a save compares against to decide which notes changed. Taken from the
 * mode, so it is the values the editor opened with, whatever the user then
 * typed. Idempotent, unlike `seedPostings`: it mints no keys.
 */
export function seedNotesBaseline(mode: TxnRowMode): NotesBaseline {
    return { memo: seedMemo(mode), tags: seedTags(mode) };
}

/**
 * True when nothing is stored for what the editor holds: a create, which the
 * server measures the tag limit from zero for and which sends every value that
 * is not blank. A stored template edited through the create form is not one.
 */
export function seedIsCreate(mode: TxnRowMode): boolean {
    return mode.kind === 'new' && mode.prefillIsStored !== true;
}

/**
 * The postings list. NOT idempotent — every call mints fresh row keys, so it
 * must run exactly once per mount (see the module note).
 *
 * Duplicate seeds 1..N postings (single row = N=1, split = N); the form opens
 * with one empty posting when there is no prefill, because a single row is just
 * the N=1 case and there is no separate "convert to split" path.
 *
 * `currency` is the source posting's (the editor's `currency`): each amount is seeded
 * at its places, keeping every digit a stored figure carries beyond them (ADR-0114 D15).
 * `currencyOf` gives a counterparty's currency, the places a stored or copied second
 * amount is seeded at (ADR-0114 D14); without it the second amount pads nothing.
 */
export function seedPostings(
    mode: TxnRowMode,
    currency: string | null,
    currencyOf: (accountId: string | null) => string | null = () => null,
): PostingDraft[] {
    if (mode.kind === 'new') {
        const seeds = mode.prefill?.postings;
        return seeds && seeds.length > 0
            ? seeds.map((p) => prefillToDraft(
                p, currency, mode.prefillIsStored === true, currencyOf(p.counterpartyAccountId)))
            : [emptyDraft()];
    }
    return mode.postings.map((s) => seedToDraft(s, currency, currencyOf(s.counterpartyAccountId)));
}
