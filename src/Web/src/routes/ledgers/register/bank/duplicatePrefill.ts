import { splitHeaderNotes } from '@/lib/annotations/splitHeader';
import type { BankRow } from '@/lib/types';

import type { TxnRowNewPrefill } from '../../TxnRowEdit';

/**
 * Build a new-transaction prefill (Duplicate path) from one OR MORE
 * source rows. A single-row duplicate passes `[row]` → one posting; a
 * split duplicate passes all the group's legs → N postings. Header
 * fields come from the first row; posted_at defaults to today in the
 * editor.
 *
 * The copy takes what THIS register shows as its own: a one-split's memo
 * and tags even when they are inherited from the other side, and a split's
 * transaction values plus each line's. Duplicate is offered only on rows
 * this register can edit, so the source is always the side that will own
 * the copy.
 */
export function rowsToDuplicatePrefill(
    rows: readonly BankRow[],
): TxnRowNewPrefill {
    const head = rows[0]!;
    if (rows.length === 1) {
        return {
            payee: head.payee,
            memo: head.memo,
            tags: head.tags,
            checkNumber: head.checkNumber,
            postings: [{
                counterpartyAccountId: head.counterpartyAccountId,
                amount: head.amount,
                // A copy carries both amounts of a transfer between currencies
                // (ADR-0114 D14); null on a mirrored pair.
                counterpartyAmount: head.counterpartyAmount ?? null,
            }],
        };
    }
    const header = splitHeaderNotes(rows);
    return {
        payee: head.payee,
        memo: header.memo,
        tags: header.tags,
        checkNumber: head.checkNumber,
        postings: rows.map((r) => ({
            counterpartyAccountId: r.counterpartyAccountId,
            amount: r.amount,
            counterpartyAmount: r.counterpartyAmount ?? null,
            // Duplicating a split and losing which project each posting was
            // for would defeat the point of having recorded it.
            legMemo: r.memo,
            legTags: r.tags,
        })),
    };
}
