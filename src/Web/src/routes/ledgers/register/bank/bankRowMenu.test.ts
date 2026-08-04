import { describe, it, expect } from 'vitest';
import type { BankRow } from '@/lib/types';
import { buildBankRowMenuItems } from './bankRowMenu';

// Minimal BankRow factory (no coercion — every field set).
function makeRow(overrides: Partial<BankRow> & { id: string }): BankRow {
    const defaults: BankRow = {
        kind: 'bank',
        id: '',
        accountId: '00000000-0000-0000-0000-0000000000a1',
        payee: 'Paycheck',
        memo: null,
        amount: 1436.82,
        postedAt: '2026-04-07T12:00:00Z',
        transactedAt: null,
        status: 'cleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: 1436.82,
        origin: 'manual',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: '00000000-0000-0000-0000-000000000999',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        headerId: '00000000-0000-0000-0000-0000000000bb',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-04-07T12:00:00Z',
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
    };
    return { ...defaults, ...overrides };
}

const noopActions = {
    onApprove: () => {},
    onDuplicate: () => {},
    onCreateReminder: () => {},
    onShowOtherSide: () => {},
    onRequestDelete: () => {},
};

const ids = (row: BankRow, opts?: { originatingSplit?: boolean }) =>
    buildBankRowMenuItems(row, noopActions, opts).map((i) => i.id);

describe('buildBankRowMenuItems — originating split-parent', () => {
    // An originating split-parent's canonical leg has txnGroupId set
    // (every split header does). Without the flag the counter-side guard
    // would mark it read-only (only "show-other-side"); the flag treats
    // it as the editable origin and offers Delete (+ Accept if flagged).
    it('offers Duplicate + Delete for a clean originating split', () => {
        const row = makeRow({
            id: 'split',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            accountPostingsOnHeader: 9,
            headerTotalPostings: 9,
        });
        expect(ids(row, { originatingSplit: true })).toEqual(['duplicate', 'create-reminder', 'delete']);
    });

    it('offers Accept + Duplicate + Delete when the originating split needs review', () => {
        const row = makeRow({
            id: 'split-rev',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            needsReview: true,
        });
        expect(ids(row, { originatingSplit: true })).toEqual([
            'accept',
            'duplicate',
            'create-reminder',
            'delete',
        ]);
    });

    it('without the flag, a split counter-side stays read-only (show-other-side only)', () => {
        // Contrast: same txnGroupId-bearing row, but NOT flagged as an
        // originating parent → the existing ADR-0036 counter-side guard
        // makes it read-only. Locks that this change is opt-in.
        const row = makeRow({
            id: 'counter',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
        });
        expect(ids(row)).toEqual(['show-other-side']);
    });
});

describe('buildBankRowMenuItems — single editable row', () => {
    it('offers Duplicate + Create reminder + Show other side + Delete', () => {
        const row = makeRow({
            id: 'plain',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
        });
        expect(ids(row)).toEqual([
            'duplicate',
            'create-reminder',
            'show-other-side',
            'delete',
        ]);
    });

    it('prepends Accept when the row needs review', () => {
        const row = makeRow({
            id: 'rev',
            needsReview: true,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
        });
        expect(ids(row)).toEqual([
            'accept',
            'duplicate',
            'create-reminder',
            'show-other-side',
            'delete',
        ]);
    });
});
