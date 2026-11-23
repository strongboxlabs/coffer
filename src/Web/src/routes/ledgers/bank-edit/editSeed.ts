import { ownMemo, ownTags } from '@/lib/annotations/notes';
import { splitHeaderNotes } from '@/lib/annotations/splitHeader';
import type { RegisterEntryOf } from '@/lib/splitCollapse';
import type { BankRow } from '@/lib/types';

import type { TxnRowMode } from '../TxnRowEdit';

/** The bank editor's edit seed. */
export type BankEditSeed = Extract<TxnRowMode, { kind: 'edit' }>;

/** A register entry as far as the seed reads it: a plain row, or a split's
 *  lines. A `RegisterEntryOf<BankRow>` is one, and so is a split-parent row. */
export type BankEditSource =
    | Pick<Extract<RegisterEntryOf<BankRow>, { kind: 'txn' }>, 'kind' | 'txn'>
    | { kind: 'group'; legs: readonly BankRow[] };

/**
 * The bank editor's seed for one register entry, as the register opens it: a
 * plain row is one posting whose memo and tags are the Memo and Tags fields; a
 * split is every source-side line, each with its own memo and tags, under the
 * whole transaction's.
 *
 * One function, so the register and any other host (the home screen's Review)
 * open an entry with the same values. Every field the editor sends back on save
 * is seeded here, `transactedAt` above all: the editor sends it on every save,
 * so an unseeded one would clear a tax date the transaction already had.
 *
 * The caller keeps its own read-only gate: a row the server marks `isReadOnly`
 * opens no editor, and this does not check.
 */
export function bankEditSeed(entry: BankEditSource): BankEditSeed {
    if (entry.kind === 'group') {
        const canonical = entry.legs[0]!;
        const header = splitHeaderNotes(entry.legs);
        return {
            kind: 'edit',
            headerId: canonical.headerId,
            sourceAccountId: canonical.accountId,
            postings: entry.legs.map((leg) => ({
                legId: leg.id,
                counterpartyAccountId: leg.counterpartyAccountId,
                counterpartyAccountName: leg.counterpartyAccountName,
                amount: leg.amount,
                legMemo: ownMemo(leg),
                legTags: ownTags(leg),
                // Both amounts of a line between currencies, as stored: a saved
                // transaction is never refilled (ADR-0114 D14).
                counterpartyAmount: leg.counterpartyAmount,
            })),
            payee: canonical.payee,
            memo: header.memo,
            checkNumber: canonical.checkNumber,
            postedAt: canonical.postedAt,
            transactedAt: canonical.transactedAt,
            balanceAfter: canonical.balanceAfter,
            balanceCurrencyCode: canonical.balanceCurrencyCode,
            ...(canonical.balanceRateUnavailable ? { balanceRateUnavailable: canonical.balanceRateUnavailable } : {}),
            tags: header.tags,
            // A split's transaction values are its own: nothing to inherit,
            // so no "from" caption.
            inherited: null,
            // Captured when the editor opens: a row awaiting review saves as
            // its Accept.
            needsReview: canonical.needsReview,
        };
    }
    const txn = entry.txn;
    return {
        kind: 'edit',
        headerId: txn.headerId,
        sourceAccountId: txn.accountId,
        // One posting. Its memo and tags are the Memo and Tags fields, not the
        // posting's.
        postings: [{
            legId: txn.id,
            counterpartyAccountId: txn.counterpartyAccountId,
            counterpartyAccountName: txn.counterpartyAccountName,
            amount: txn.amount,
            legMemo: null,
            legTags: [],
            counterpartyAmount: txn.counterpartyAmount,
        }],
        payee: txn.payee,
        // THIS side's own memo and tags: blank while it inherits, so an
        // untouched save sends nothing and the inherited value is never copied
        // onto this side.
        memo: ownMemo(txn),
        checkNumber: txn.checkNumber,
        postedAt: txn.postedAt,
        transactedAt: txn.transactedAt,
        balanceAfter: txn.balanceAfter,
        balanceCurrencyCode: txn.balanceCurrencyCode,
        ...(txn.balanceRateUnavailable ? { balanceRateUnavailable: txn.balanceRateUnavailable } : {}),
        tags: ownTags(txn),
        inherited: {
            memo: txn.inheritedMemo,
            tags: txn.inheritedTags,
            fromAccountId: txn.inheritFromAccountId,
        },
        needsReview: txn.needsReview,
    };
}
