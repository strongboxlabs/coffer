import type { PostingDraft } from './postingDraft';

/**
 * The bank editor's client-side validation. Pure, so it can be tested without
 * rendering 1,600 lines of editor.
 *
 * TWO SHAPES OVER ONE RULE SET. `postingIssues` is the structured form — one
 * entry per defect, carrying the posting's key and row number — and
 * `validatePostings` renders it as the sentence list the editor has always
 * shown. The rules live in ONE place on purpose: a split row marking itself
 * red while the Save button's tooltip disagrees about why is the exact failure
 * two parallel validators produce.
 *
 * WHY THE SENTENCE LIST IS A STRING LIST AND NOT A KEYED MAP — a deliberate
 * deviation from `investment-edit/validation.ts`, which returns
 * `Partial<Record<Key, string>>`. Two reasons, both structural:
 *
 *   1. The shell renders these VERBATIM as joined text, twice: `join(' · ')` in
 *      the single-posting warning strip and `join('\n')` in the Save button's
 *      title. The messages are index-prefixed (`Posting 2: amount is
 *      required.`) because on the split branch the strip was, until the
 *      redesign, the only place a reader learned WHICH posting was wrong.
 *   2. One posting can contribute TWO messages — a blank amount and a missing
 *      counterparty. A map keyed by field collapses those into one, and a map
 *      keyed by field loses construction order.
 *
 * Converting to a keyed map is therefore a user-visible text change, not a
 * refactor. Recorded here because it will otherwise read as an oversight next
 * to the sibling folder's convention.
 *
 * ZERO IS LEGAL, and that is not an oversight either: a paycheck split
 * routinely carries $0 lines, the database has no non-zero constraint, and the
 * server accepts them. Only an unparseable or blank amount is rejected. There
 * is also NO cross-posting balance rule anywhere — ADR-0025 explicitly
 * rejected auto-balance and sum warnings, so the running total the editor
 * shows is informational and nothing here may treat an "unbalanced" split as
 * invalid.
 */

/** What is wrong with one posting. One posting can produce both kinds. */
export type PostingIssueKind = 'amount' | 'counterparty';

export interface PostingIssue {
    /** The posting's stable draft key — how a row finds its own issues. */
    key: string;
    /** 1-based row number, as shown in the splits grid and in the message. */
    n: number;
    kind: PostingIssueKind;
    /** The sentence the editor shows, index-prefixed. */
    message: string;
    /** The short form, for a per-row marker's tooltip. */
    hint: string;
}

const RULES: readonly {
    kind: PostingIssueKind;
    broken: (p: PostingDraft) => boolean;
    message: (n: number) => string;
    hint: string;
}[] = [
    {
        kind: 'amount',
        // Blank or unparseable only. Zero is a legal amount — see above.
        broken: (p) => p.amount.trim().length === 0 || Number.isNaN(Number(p.amount)),
        message: (n) => `Posting ${n}: amount is required.`,
        hint: 'Enter an amount for this split',
    },
    {
        kind: 'counterparty',
        broken: (p) => p.counterpartyId === null,
        message: (n) => `Posting ${n}: pick a counterparty.`,
        hint: 'Pick a category for this split',
    },
];

/**
 * Every defect, in row order and — within a row — in rule order. The order is
 * part of the contract: the sentence list below is built by mapping over this,
 * and it has always read amount-then-counterparty for a posting missing both.
 */
export function postingIssues(postings: readonly PostingDraft[]): readonly PostingIssue[] {
    const issues: PostingIssue[] = [];
    for (let i = 0; i < postings.length; i++) {
        const p = postings[i]!;
        for (const rule of RULES) {
            if (!rule.broken(p)) continue;
            issues.push({
                key: p.key,
                n: i + 1,
                kind: rule.kind,
                message: rule.message(i + 1),
                hint: rule.hint,
            });
        }
    }
    return issues;
}

export function validatePostings(postings: readonly PostingDraft[]): readonly string[] {
    // The empty case has no posting to attach to, so it is not an issue and
    // cannot be one: it is a statement about the list itself. It is also
    // unreachable through the UI — removePosting refuses to drop the last
    // posting — and kept as a belt-and-braces guard on the Save path.
    const empty = postings.length === 0 ? ['Add at least one posting.'] : [];
    return [...empty, ...postingIssues(postings).map((i) => i.message)];
}
