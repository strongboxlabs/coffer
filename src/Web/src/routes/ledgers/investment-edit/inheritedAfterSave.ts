import type { InheritedNotes } from '@/lib/annotations/notes';
import type { InvestmentRow } from '@/lib/types';

import type { InvestmentTxnDraft } from './validation';

/** A leg's posting role; null on a leg that has none (a feed row's, before it is classified). */
type PostingRole = InvestmentRow['postingRole'];

/** What the brokerage inherits as the row is stored, and the role of the leg it comes from. */
export interface InvestmentInheritedNotes extends InheritedNotes {
    /** The posting role of the stored leg the value comes from: null for a leg
     *  without one. Omitted when the host does not know it; the role is then
     *  not compared. */
    fromRole?: PostingRole;
}

/** The draft's one posting across from the brokerage: its account and the role the save gives it. */
export interface DraftOtherSide {
    accountId: string;
    role: Exclude<PostingRole, null>;
}

/**
 * The account and role across from the brokerage once the draft is saved,
 * when the save writes one posting that faces an account off the investment
 * side; null otherwise, and while the draft names no such account.
 *
 * It follows the postings the server builds for each action
 * (`InvestmentTransactionsRepository.BuildPostings`): a transfer is one posting
 * with its transfer account, in the transfer role; a cash dividend and a misc
 * entry one with their category, in the income role, and a second posting when
 * the draft names both a fee account and a fee amount. Every other action
 * writes several postings, or one with the brokerage's holdings account, whose
 * legs never carry a value.
 */
export function draftOtherSide(
    draft: Pick<InvestmentTxnDraft, 'action' | 'categoryAccountId' | 'transferAccountId' | 'feeAccountId' | 'feeAmount'>,
): DraftOtherSide | null {
    const withFee = draft.feeAccountId !== null && draft.feeAmount !== null;
    switch (draft.action) {
        case 'transfer':
            return draft.transferAccountId === null
                ? null
                : { accountId: draft.transferAccountId, role: 'transfer' };
        case 'dividend_cash':
        case 'misc':
            return withFee || draft.categoryAccountId === null
                ? null
                : { accountId: draft.categoryAccountId, role: 'income' };
        default:
            return null;
    }
}

/**
 * What the brokerage will inherit once the investment editor's draft is saved:
 * the value behind the Memo and Tags captions' "from X", the marker in each
 * field and the fields' placeholders.
 *
 * An investment save rebuilds the event's legs. A stored leg is kept only
 * where the new event has a leg on the same account in the same role, or, for
 * a stored leg without a role, on the same account (`ShapeChange.ByAccountAndRole`);
 * a kept leg keeps its memo and tags, a leg not kept goes with them, and a new
 * leg carries none (ADR-0108 D4). So the stored value is still inherited only
 * while the draft saves as one posting whose other leg keeps the stored one:
 * then it comes from the same account, the one the draft names. Across any
 * other account, or while the draft names no account, nothing is inherited,
 * and the editor shows nothing. Several postings inherit nothing either: the
 * event owns its memo and tags, and the save copies what the brokerage showed
 * onto them, a copy the editor leaves to the server, as the bank editor does
 * when a one-posting row is split.
 */
export function inheritedAfterSave(
    stored: InvestmentInheritedNotes | null,
    draft: Parameters<typeof draftOtherSide>[0],
): InvestmentInheritedNotes | null {
    if (stored === null || stored.fromAccountId === null) return null;
    const across = draftOtherSide(draft);
    if (across === null || across.accountId !== stored.fromAccountId) return null;
    if (stored.fromRole !== undefined && stored.fromRole !== null && stored.fromRole !== across.role) return null;
    return stored;
}
