import { toDateInputValue, todayInputValue } from '@/lib/dates';

import { emptyDraft, prefillToDraft, seedToDraft, type PostingDraft } from './postingDraft';
import type { TxnRowMode } from '../TxnRowEdit';

/**
 * How a bank-editor draft is SEEDED from a mode. Pure, one function per field.
 *
 * WHY PER-FIELD AND NOT ONE `modeToDraft`. The obvious shape is a single
 * constructor returning the whole draft, with the shell then reading
 * `draft.payee` everywhere. That is the right end state, but it means touching
 * every reference to seven state variables across 1,500 lines in the SAME diff
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
 * Slice 2c.6b: tag set. In edit mode seeds from the row's current tags; in new
 * mode starts empty. The save handler sends `tags: <this list>` so the server's
 * replace-semantics produces exactly this membership.
 */
export function seedTags(mode: TxnRowMode): readonly string[] {
    return mode.kind === 'new' ? [] : mode.tags;
}

/**
 * The postings list. NOT idempotent — every call mints fresh row keys, so it
 * must run exactly once per mount (see the module note).
 *
 * Duplicate seeds 1..N postings (single row = N=1, split = N); the form opens
 * with one empty posting when there is no prefill, because a single row is just
 * the N=1 case and there is no separate "convert to split" path.
 */
export function seedPostings(mode: TxnRowMode): PostingDraft[] {
    if (mode.kind === 'new') {
        const seeds = mode.prefill?.postings;
        return seeds && seeds.length > 0
            ? seeds.map((p) => prefillToDraft(p))
            : [emptyDraft()];
    }
    return mode.postings.map((s) => seedToDraft(s));
}
