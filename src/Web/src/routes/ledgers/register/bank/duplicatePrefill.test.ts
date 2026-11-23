import { describe, expect, it } from 'vitest';

import type { BankRow } from '@/lib/types';

import { rowsToDuplicatePrefill } from './duplicatePrefill';

// Duplicate carries both amounts of a transfer between currencies (ADR-0114 D14, board
// ED-3): the copy's second amount is the stored one, which the editor never refills.

const row = (over: Partial<BankRow>): BankRow => ({
    kind: 'bank',
    id: 'leg-1', accountId: 'usd-bank', payee: 'Payee One', amount: -100,
    postedAt: '2026-01-02T00:00:00.000Z', transactedAt: '2026-01-02T00:00:00.000Z',
    status: 'uncleared', isHidden: false, balanceAfter: 100, origin: 'manual',
    currencyCode: 'USD', balanceCurrencyCode: 'USD',
    isPending: false, externalId: null, checkNumber: null, counterpartyId: 'cp-1',
    txnGroupId: null, legIndex: 0, counterpartyAccountId: 'eur-bank',
    counterpartyAccountName: 'Test EUR Bank', counterpartyAccountType: 'bank',
    headerId: 'h1', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-01-02T00:00:00.000Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: false, providerRawPayload: null, headerAccountNetAmount: null,
    providerKey: null, isMergeWinner: false, importSource: null, derivedAction: null,
    accountPostingsOnHeader: 1, headerTotalPostings: 2,
    memo: null, tags: [], memoInherited: false, tagsInherited: false,
    inheritedMemo: null, inheritedTags: [], inheritFromAccountId: null,
    headerMemo: null, headerTags: null, notesKind: 'onePosting', isReadOnly: false,
    ...over,
});

describe('rowsToDuplicatePrefill', () => {
    it('copies a one-posting row\'s other-leg figure', () => {
        const prefill = rowsToDuplicatePrefill([row({ counterpartyAmount: 90, counterpartyCurrencyCode: 'EUR' })]);
        expect(prefill.postings).toEqual([{ counterpartyAccountId: 'eur-bank', amount: -100, counterpartyAmount: 90 }]);
    });

    it('copies each split line\'s own, and none for a mirrored line', () => {
        const prefill = rowsToDuplicatePrefill([
            row({ id: 'leg-1', counterpartyAccountId: 'groceries', counterpartyAccountType: 'category', amount: -40, counterpartyAmount: null }),
            row({ id: 'leg-2', amount: -100, counterpartyAmount: 90.5, legIndex: 1 }),
        ]);
        expect(prefill.postings!.map((p) => p.counterpartyAmount)).toEqual([null, 90.5]);
    });
});
