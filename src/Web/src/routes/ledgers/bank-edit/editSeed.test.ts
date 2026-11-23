import { describe, expect, it } from 'vitest';

import type { RegisterEntryOf } from '@/lib/splitCollapse';
import type { BankRow } from '@/lib/types';

import { bankEditSeed } from './editSeed';
import { seedPostings } from './draft';

/**
 * Characterisation: the bank editor's edit seed is what the bank register built
 * inline when it opened a row, value for value. Each expected object is that
 * inline construction, written out: one posting from a plain row, every line
 * from a split, with the tax date, the inherited notes and the review state.
 */

const ACCOUNT = '00000000-0000-0000-0000-000000000100';
const OTHER_ACCOUNT = '00000000-0000-0000-0000-000000000101';
const CATEGORY_A = '00000000-0000-0000-0000-000000000300';
const CATEGORY_B = '00000000-0000-0000-0000-000000000301';
const HEADER = '00000000-0000-0000-0000-000000000aaa';

function row(over: Partial<BankRow>): BankRow {
    return {
        kind: 'bank',
        id: 'leg-1', accountId: ACCOUNT, payee: 'Payee One', amount: -12.5,
        postedAt: '2026-01-02T00:00:00.000Z', transactedAt: '2026-01-02T00:00:00.000Z',
        status: 'uncleared', isHidden: false, balanceAfter: 100, origin: 'manual',
        currencyCode: 'USD', balanceCurrencyCode: 'USD',
        isPending: false, externalId: null, checkNumber: null, counterpartyId: 'cp-1',
        txnGroupId: null, legIndex: 0, counterpartyAccountId: CATEGORY_A,
        counterpartyAccountName: 'Category A', counterpartyAccountType: 'category',
        headerId: HEADER, clearedAt: null, clearedByUserId: null,
        createdAt: '2026-01-02T00:00:00.000Z', onlineMatchFitid: null, onlineMatchFiId: null,
        needsReview: false, providerRawPayload: null, headerAccountNetAmount: null,
        providerKey: null, isMergeWinner: false, importSource: null, derivedAction: null,
        accountPostingsOnHeader: 1, headerTotalPostings: 2,
        memo: null, tags: [], memoInherited: false, tagsInherited: false,
        inheritedMemo: null, inheritedTags: [], inheritFromAccountId: null,
        headerMemo: null, headerTags: null, notesKind: 'onePosting', isReadOnly: false,
        ...over,
    };
}

/** The register's own entry shapes, which the seed takes as they are. */
const txnEntry = (txn: BankRow): RegisterEntryOf<BankRow> => ({ kind: 'txn', txn, groupId: null, legs: null });
const groupEntry = (legs: BankRow[]): RegisterEntryOf<BankRow> => ({ kind: 'group', txn: null, groupId: 'g-1', legs });

describe('bankEditSeed: a plain row', () => {
    it('seeds one posting, this side\'s own notes and the row\'s dates and review state', () => {
        const txn = row({
            id: 'leg-9', payee: 'Payee Two', amount: -40, checkNumber: '1001',
            transactedAt: '2025-12-30T00:00:00.000Z', balanceAfter: 60,
            memo: 'Own memo', tags: ['Tag A'], needsReview: true,
        });

        expect(bankEditSeed(txnEntry(txn))).toEqual({
            kind: 'edit',
            headerId: HEADER,
            sourceAccountId: ACCOUNT,
            postings: [{
                legId: 'leg-9',
                counterpartyAccountId: CATEGORY_A,
                counterpartyAccountName: 'Category A',
                amount: -40,
                legMemo: null,
                legTags: [],
            }],
            payee: 'Payee Two',
            memo: 'Own memo',
            checkNumber: '1001',
            postedAt: '2026-01-02T00:00:00.000Z',
            transactedAt: '2025-12-30T00:00:00.000Z',
            balanceAfter: 60,
            balanceCurrencyCode: 'USD',
            tags: ['Tag A'],
            inherited: { memo: null, tags: [], fromAccountId: null },
            needsReview: true,
        });
    });

    it('leaves an inherited memo and tags out of this side\'s fields and names their source', () => {
        const txn = row({
            memo: 'Other side memo', memoInherited: true,
            tags: ['Tag B'], tagsInherited: true,
            inheritedMemo: 'Other side memo', inheritedTags: ['Tag B'],
            inheritFromAccountId: OTHER_ACCOUNT,
        });

        const seed = bankEditSeed(txnEntry(txn));

        expect(seed.memo).toBeNull();
        expect(seed.tags).toEqual([]);
        expect(seed.inherited).toEqual({
            memo: 'Other side memo', tags: ['Tag B'], fromAccountId: OTHER_ACCOUNT,
        });
        expect(seed.needsReview).toBe(false);
    });
});

describe('bankEditSeed: a split', () => {
    it('seeds every line with its own notes under the whole transaction\'s', () => {
        const legs = [
            row({
                id: 'leg-1', legIndex: 0, amount: -8, counterpartyAccountId: CATEGORY_A,
                counterpartyAccountName: 'Category A', memo: 'Line one', tags: ['Tag A'],
                headerMemo: 'Whole memo', headerTags: ['Tag C'], txnGroupId: 'g-1',
                transactedAt: '2025-12-31T00:00:00.000Z', balanceAfter: 92, needsReview: true,
                checkNumber: '2002',
            }),
            row({
                id: 'leg-2', legIndex: 1, amount: -4, counterpartyAccountId: CATEGORY_B,
                counterpartyAccountName: 'Category B', memo: null, tags: [],
                headerMemo: 'Whole memo', headerTags: ['Tag C'], txnGroupId: 'g-1',
                balanceAfter: 88,
            }),
        ];

        expect(bankEditSeed(groupEntry(legs))).toEqual({
            kind: 'edit',
            headerId: HEADER,
            sourceAccountId: ACCOUNT,
            postings: [
                {
                    legId: 'leg-1', counterpartyAccountId: CATEGORY_A, counterpartyAccountName: 'Category A',
                    amount: -8, legMemo: 'Line one', legTags: ['Tag A'],
                },
                {
                    legId: 'leg-2', counterpartyAccountId: CATEGORY_B, counterpartyAccountName: 'Category B',
                    amount: -4, legMemo: null, legTags: [],
                },
            ],
            payee: 'Payee One',
            memo: 'Whole memo',
            checkNumber: '2002',
            postedAt: '2026-01-02T00:00:00.000Z',
            // The canonical (first) line's.
            transactedAt: '2025-12-31T00:00:00.000Z',
            balanceAfter: 92,
            balanceCurrencyCode: 'USD',
            tags: ['Tag C'],
            inherited: null,
            needsReview: true,
        });
    });

    it('drops a line\'s inherited notes, as for a plain row', () => {
        const legs = [
            row({ id: 'leg-1', memo: 'Shown', memoInherited: true, tags: ['Tag A'], tagsInherited: true }),
            row({ id: 'leg-2', legIndex: 1 }),
        ];

        const seed = bankEditSeed(groupEntry(legs));

        expect(seed.postings[0]).toMatchObject({ legMemo: null, legTags: [] });
        expect(seed.memo).toBeNull();
        expect(seed.tags).toEqual([]);
    });
});

describe('bankEditSeed: a category row\'s balance (ADR-0114 D7, D9)', () => {
    const missing = { fromCurrency: 'CAD', toCurrency: 'EUR', date: '2026-01-02', reason: 'rate_unavailable' as const };

    it('keeps the currency the balance is in, not the posting\'s, and the rates a null one needed', () => {
        const txn = row({ currencyCode: 'CAD', balanceCurrencyCode: 'EUR', balanceAfter: null, balanceRateUnavailable: [missing] });

        expect(bankEditSeed(txnEntry(txn))).toMatchObject({
            balanceAfter: null, balanceCurrencyCode: 'EUR', balanceRateUnavailable: [missing],
        });
        expect(bankEditSeed(groupEntry([txn, { ...txn, id: 'leg-2', legIndex: 1 }]))).toMatchObject({
            balanceAfter: null, balanceCurrencyCode: 'EUR', balanceRateUnavailable: [missing],
        });
    });
});

// ADR-0114 D14: a saved transaction is never refilled. Both amounts of a posting between
// currencies are seeded from what was stored, the second fixed so no rate refills it.
describe('bankEditSeed: a transfer between currencies', () => {
    const EUR_BANK = '00000000-0000-0000-0000-000000000200';
    const currencyOf = (id: string | null) => (id === EUR_BANK ? 'EUR' : 'USD');

    it('seeds a one-posting row\'s stored second amount, fixed', () => {
        const txn = row({ counterpartyAccountId: EUR_BANK, counterpartyAccountType: 'bank', amount: -100, counterpartyAmount: 90 });
        const [p] = seedPostings(bankEditSeed(txnEntry(txn)), 'USD', currencyOf);
        expect(p).toMatchObject({ otherAmount: '90.00', seededOtherAmount: '90.00', otherFixed: true });
    });

    it('seeds each split line\'s own, and nothing on a mirrored line', () => {
        const legs = [
            row({ id: 'leg-1', counterpartyAccountId: CATEGORY_A, amount: -40, counterpartyAmount: null }),
            row({ id: 'leg-2', counterpartyAccountId: EUR_BANK, counterpartyAccountType: 'bank', amount: -100, counterpartyAmount: 90.5, legIndex: 1 }),
        ];
        const [mirrored, across] = seedPostings(bankEditSeed(groupEntry(legs)), 'USD', currencyOf);
        expect(mirrored!.otherAmount).toBeUndefined();
        expect(mirrored!.otherFixed).toBeUndefined();
        expect(across).toMatchObject({ otherAmount: '90.50', otherFixed: true });
    });
});
