import { roundToMinorUnits } from '@/lib/money';
import {
    bankDirection,
    parsedMagnitude,
    signedSecondAmount,
    type SecondAmountSide,
} from '@/lib/secondAmount';
import type { AccountSummary } from '@/lib/types';

import type { PostingDraft } from './postingDraft';

/**
 * Which postings carry a second amount in the bank editor (ADR-0114 D13, D14), and what a
 * save sends for one. Pure, like the rest of the folder's root.
 *
 * ONE PREDICATE, decided once in the shell beside `counterpartyKind` and handed down: a
 * posting carries a second amount when its source is not a category, its counterparty is
 * a real account (not a category), and the two are in different currencies. A naive
 * currency comparison would ask for one on a category's own register, where the source
 * is the category and every leg mirrors the paying account (ADR-0114 D13, "a category leg
 * mirrors the real side"), and from a foreign-currency source to a category, whose leg
 * mirrors the source too.
 */
export interface SecondAmountContext {
    sourceAccountId: string;
    accounts: readonly AccountSummary[];
    /** The source posting's currency, the editor's. */
    currency: string;
    /** The name the editor shows for an account (its path). */
    accountName: (id: string) => string;
}

/** The other account a posting's second amount is in, or null when it has none. */
export function secondAmountSide(p: PostingDraft, ctx: SecondAmountContext): SecondAmountSide | null {
    if (p.counterpartyId === null) return null;
    const source = ctx.accounts.find((a) => a.id === ctx.sourceAccountId);
    if (source?.accountType === 'category') return null;
    const other = ctx.accounts.find((a) => a.id === p.counterpartyId);
    if (other === undefined || other.accountType === 'category') return null;
    if (other.currencyCode === ctx.currency) return null;
    return {
        accountId: other.id,
        accountName: ctx.accountName(other.id),
        currency: other.currencyCode,
        direction: bankDirection(p.amount),
    };
}

/**
 * The second amount a posting's save sends, signed opposite to Amount, or null when it
 * has none (a category or same-currency counterparty: the key is left out). A figure
 * the user changed is rounded at the other currency's places, as its blur would have; one
 * left as it was seeded is sent as stored (ADR-0114 D15).
 */
export function secondAmountToSend(p: PostingDraft, side: SecondAmountSide | null): number | null {
    if (side === null) return null;
    const text = p.otherAmount ?? '';
    const magnitude = parsedMagnitude(text);
    if (magnitude === null) return null;
    const exact = text === p.seededOtherAmount ? magnitude : roundToMinorUnits(magnitude, side.currency);
    return signedSecondAmount(exact, side.direction);
}

/**
 * True when a posting resends a stored pair exactly as it was seeded: it names a stored
 * leg, and both figures are unchanged. The server checks the nonzero rule only on a pair
 * a write changes, and it knows the stored pair by that leg's id (ADR-0114 D13), so a
 * mirrored zero pair a move landed across currencies still saves with an unrelated edit.
 * A copy is a new write with no stored leg: a Duplicate, or Post on a reminder, is
 * judged like a fresh entry, as the server judges it. A reminder template edited in
 * place carries its stored legs' ids, so it is exempt as an edit is.
 */
export function storedPairUntouched(p: PostingDraft): boolean {
    return p.legId !== null
        && p.seededOtherAmount !== undefined
        && (p.otherAmount ?? '') === p.seededOtherAmount
        && p.amount === p.seededAmount;
}

/**
 * True when a posting's second amount is missing or zero (ADR-0114 D13, "the second
 * amount is required"). A stored pair resent as it was seeded is not re-judged
 * ({@link storedPairUntouched}).
 */
export function secondAmountMissing(p: PostingDraft, side: SecondAmountSide | null): boolean {
    if (side === null) return false;
    const magnitude = parsedMagnitude(p.otherAmount ?? '');
    if (magnitude === null) return true;
    if (magnitude !== 0) return false;
    return !storedPairUntouched(p);
}

/**
 * True when a posting between currencies has a zero Amount: money that leaves one
 * account arrives in the other, so neither amount of the pair can be zero (ADR-0114 D13).
 * A stored pair resent as it was seeded is not re-judged ({@link storedPairUntouched}).
 * Zero is legal on every other posting.
 */
export function pairAmountZero(p: PostingDraft, side: SecondAmountSide | null): boolean {
    if (side === null) return false;
    if (p.amount.trim().length === 0 || Number(p.amount) !== 0) return false;
    return !storedPairUntouched(p);
}
