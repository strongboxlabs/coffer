import { counterpartyWords, type CounterpartyKind, type CounterpartyWords } from '@/lib/counterparty';
import { secondAmountRequired, type SecondAmountSide } from '@/lib/secondAmount';

import type { PostingDraft } from './postingDraft';
import { pairAmountZero, secondAmountMissing } from './secondAmount';

/**
 * The bank editor's client-side validation. Pure, so it can be tested without
 * rendering 1600 lines of editor.
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
 * routinely carries zero-amount lines, the database has no non-zero constraint, and the
 * server accepts them. Only an unparseable or blank amount is rejected. There
 * is also NO cross-posting balance rule anywhere — ADR-0025 explicitly
 * rejected auto-balance and sum warnings, so the running total the editor
 * shows is informational and nothing here may treat an "unbalanced" split as
 * invalid.
 *
 * THE SECOND AMOUNT IS NOT A BALANCE RULE. A posting between real accounts in different
 * currencies carries the other account's own figure, which must be there and nonzero
 * (ADR-0114 D13). That is a rule about ONE posting's two legs, like a blank amount, never
 * a sum across postings, so the above still holds. Which postings carry one is decided
 * by the caller (`sideOf`, from ./secondAmount.ts) and is null for a category's. On such
 * a posting Amount is one side of the pair, so it cannot be zero either; that is the one
 * exception to ZERO IS LEGAL.
 */

/** What is wrong with one posting. One posting can produce several kinds. */
export type PostingIssueKind = 'amount' | 'counterparty' | 'second';

export interface PostingIssue {
    /** The posting's stable draft key — how a row finds its own issues. */
    key: string;
    /** 1-based row number, as shown in the splits grid and in the message. */
    n: number;
    kind: PostingIssueKind;
    /** The sentence the editor shows, index-prefixed. A second amount's is the one
     *  exception on a one-posting transaction: there it is the field's own sentence,
     *  which names its account, with no posting number. */
    message: string;
    /** The short form, for a per-row marker's tooltip. A second amount's is the field's
     *  own sentence, which also heads its group in the splits grid's strip. */
    hint: string;
}

const RULES: readonly {
    kind: PostingIssueKind;
    /** `side` is the posting's second amount, null when it has none. */
    broken: (p: PostingDraft, side: SecondAmountSide | null) => boolean;
    message: (n: number) => string;
    // The hint names the counterparty, so it depends on the register — "Pick a
    // category" on a category's own register asked for the one thing that
    // cannot go there. The message stays neutral ("counterparty") because the
    // Save button's tooltip shows it out of the editor's context.
    hint: (w: CounterpartyWords) => string;
}[] = [
    {
        kind: 'amount',
        // Blank or unparseable. Zero is a legal amount — see above — except on a
        // posting between currencies.
        broken: (p, side) => p.amount.trim().length === 0 || Number.isNaN(Number(p.amount))
            || pairAmountZero(p, side),
        message: (n) => `Posting ${n}: amount is required.`,
        hint: () => 'Enter an amount for this split',
    },
    {
        kind: 'counterparty',
        broken: (p) => p.counterpartyId === null,
        message: (n) => `Posting ${n}: pick a counterparty.`,
        hint: (w) => `Pick ${w.withArticle} for this split`,
    },
];

/**
 * Every defect, in row order and — within a row — in rule order. The order is
 * part of the contract: the sentence list below is built by mapping over this,
 * and it has always read amount-then-counterparty for a posting missing both.
 */
export function postingIssues(
    postings: readonly PostingDraft[],
    counterpartyKind: CounterpartyKind = 'category',
    sideOf: (p: PostingDraft) => SecondAmountSide | null = () => null,
): readonly PostingIssue[] {
    const words = counterpartyWords(counterpartyKind);
    // The editor's own split test (TxnRowEdit's isSplitLayout): more than one posting.
    const split = postings.length > 1;
    const issues: PostingIssue[] = [];
    for (let i = 0; i < postings.length; i++) {
        const p = postings[i]!;
        const side = sideOf(p);
        for (const rule of RULES) {
            if (!rule.broken(p, side)) continue;
            issues.push({
                key: p.key,
                n: i + 1,
                kind: rule.kind,
                message: rule.message(i + 1),
                hint: rule.hint(words),
            });
        }
        // Last in the row: the second amount is read only once Amount and the
        // counterparty are, so the sentence list keeps its amount-then-counterparty
        // order. A zero is refused with the same sentence (board ED-4).
        // The field's own sentence (board ED-4), which names the account and the
        // currency. A split line's carries its posting number, as the other split
        // messages do, since the Save button's title lists every line's; a one-posting
        // transaction's has none (ADR-0114 D14).
        if (side !== null && secondAmountMissing(p, side)) {
            const sentence = secondAmountRequired(side);
            issues.push({
                key: p.key,
                n: i + 1,
                kind: 'second',
                message: split
                    ? `Posting ${i + 1}: ${sentence.charAt(0).toLowerCase()}${sentence.slice(1)}.`
                    : `${sentence}.`,
                hint: sentence,
            });
        }
    }
    return issues;
}

export function validatePostings(
    postings: readonly PostingDraft[],
    sideOf?: (p: PostingDraft) => SecondAmountSide | null,
): readonly string[] {
    // The empty case has no posting to attach to, so it is not an issue and
    // cannot be one: it is a statement about the list itself. It is also
    // unreachable through the UI — removePosting refuses to drop the last
    // posting — and kept as a belt-and-braces guard on the Save path.
    const empty = postings.length === 0 ? ['Add at least one posting.'] : [];
    return [...empty, ...postingIssues(postings, 'category', sideOf).map((i) => i.message)];
}
