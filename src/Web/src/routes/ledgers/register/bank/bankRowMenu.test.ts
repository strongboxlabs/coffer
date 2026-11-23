import { describe, it, expect } from 'vitest';
import type { BankRow } from '@/lib/types';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { buildBankRowMenuItems } from './bankRowMenu';

// Minimal BankRow factory (no coercion — every field set).
function makeRow(overrides: Partial<BankRow> & { id: string }): BankRow {
    const defaults: BankRow = {
        ...NO_NOTES,
        kind: 'bank',
        id: '',
        accountId: '00000000-0000-0000-0000-0000000000a1',
        payee: 'Paycheck',
        amount: 1000.00,
        postedAt: '2026-04-07T12:00:00Z',
        transactedAt: null,
        status: 'cleared',
        isHidden: false,
        balanceAfter: 1000.00,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
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
        headerId: '00000000-0000-0000-0000-0000000000bb',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-04-07T12:00:00Z',
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
    onEdit: () => {},
    onDuplicate: () => {},
    onCreateReminder: () => {},
    onShowOtherSide: () => {},
    onRequestDelete: () => {},
    onShowRawData: () => {},
};

const ids = (row: BankRow, opts?: { originatingSplit?: boolean; noAuthoring?: boolean }) =>
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
        expect(ids(row, { originatingSplit: true })).toEqual(['edit', 'duplicate', 'create-reminder', 'delete']);
    });

    it('offers Accept + Duplicate + Delete when the originating split needs review', () => {
        const row = makeRow({
            id: 'split-rev',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            needsReview: true,
        });
        expect(ids(row, { originatingSplit: true })).toEqual([
            'accept',
            // Edit sits after Accept and before Duplicate: Accept is the
            // one-click resolution a flagged row is asking for, so it stays
            // first. Edit is a split parent's only menu route to its own
            // legs.
            'edit',
            'duplicate',
            'create-reminder',
            'delete',
        ]);
    });

    it('a split line seen from its other side offers nothing that MUTATES it', () => {
        // "Read-only" is about mutation, not about the item COUNT: the two
        // items here navigate and inspect, and "Show raw data" is offered on
        // every row precisely because "why did this import like this?" is
        // asked most often of a row you cannot edit from here. The investment
        // register puts its raw item outside the same guard.
        const row = makeRow({
            id: 'counter',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
            notesKind: 'splitOpposite',
            isReadOnly: true,
        });
        expect(ids(row)).toEqual(['show-other-side', 'raw']);
        // The assertion that carries the meaning: nothing here changes the row.
        for (const mutating of ['edit', 'accept', 'duplicate', 'create-reminder', 'delete']) {
            expect(ids(row)).not.toContain(mutating);
        }
    });
});

describe('buildBankRowMenuItems — the server says which rows are read-only', () => {
    // The menu reads `isReadOnly`; it never re-derives read-only-ness from
    // posting counts or a group id.
    it.each([
        ['a one-posting investment counterpart (an Xfr seen from the bank)', 'eventCounterpartOne'],
        ['a multi-posting investment counterpart', 'eventOpposite'],
        ['a split line seen from its other side', 'splitOpposite'],
    ] as const)('offers neither Edit nor Duplicate on %s', (_name, notesKind) => {
        const row = makeRow({
            id: `ro-${notesKind}`,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
            notesKind,
            isReadOnly: true,
        });
        const items = ids(row);
        expect(items).toContain('show-other-side');
        expect(items).not.toContain('edit');
        expect(items).not.toContain('duplicate');
    });

    it('a row with a group id that the server shows as editable keeps Edit', () => {
        // The old guard treated any txnGroupId as a counter-side; the symmetric
        // shape on its owning account carries one and is editable.
        const row = makeRow({
            id: 'owned',
            txnGroupId: '00000000-0000-0000-0000-0000000000bb',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
            notesKind: 'onePosting',
            isReadOnly: false,
        });
        expect(ids(row)).toContain('edit');
        expect(ids(row)).toContain('duplicate');
    });
});

describe('buildBankRowMenuItems — single editable row', () => {
    it('offers Edit + Duplicate + Create reminder + Show other side + Raw + Delete', () => {
        // Edit was missing here and present on BOTH the split-parent branch
        // above and the investment register — so the commonest row in the app
        // was the one row whose menu did not name the action its double-click
        // and its Enter key both perform.
        const row = makeRow({
            id: 'plain',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
        });
        expect(ids(row)).toEqual([
            'edit',
            'duplicate',
            'create-reminder',
            'show-other-side',
            'raw',
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
            'edit',
            'duplicate',
            'create-reminder',
            'show-other-side',
            'raw',
            'delete',
        ]);
    });
});

describe('a category register offers no authoring', () => {
    // Duplicate opens the NEW-transaction editor, prefilled. That editor
    // authors against the register's own account, which on a category
    // register is a category — a transaction is created against a money
    // account and categorised, never authored inside one. The "+ New
    // transaction" button is hidden for the same reason; this is the other
    // door into the same room.
    it('drops Duplicate on a single row', () => {
        const row = makeRow({ id: 'r1' });
        expect(ids(row)).toContain('duplicate');
        expect(ids(row, { noAuthoring: true })).not.toContain('duplicate');
    });

    it('drops Duplicate on an originating split parent too', () => {
        const row = makeRow({ id: 'r2', txnGroupId: 'g1' });
        expect(ids(row, { originatingSplit: true })).toContain('duplicate');
        const without = ids(row, { originatingSplit: true, noAuthoring: true });
        expect(without).not.toContain('duplicate');
        // …and it removes ONLY that: Edit and Delete still have to work.
        expect(without).toContain('edit');
        expect(without).toContain('delete');
    });
});
