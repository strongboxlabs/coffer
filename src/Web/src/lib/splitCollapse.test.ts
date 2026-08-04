import { describe, it, expect } from 'vitest';

import {
    buildDisplayRows,
    canonicalLeg,
    groupAmount,
    groupBalanceAfter,
    regroupTargetSplits,
} from './splitCollapse';
import type { BankRow, RegisterEntry } from './types';

type GroupEntry = Extract<RegisterEntry, { kind: 'group' }>;

const ACCOUNT_A = '00000000-0000-0000-0000-0000000000aa';

// Split-collapse is domain-agnostic; the canonical case it serves is a
// multi-leg bank-domain paycheck, so the fixture is a `BankRow`
// (ADR-0030 §2 — bank rows carry no investment fields).
function txn(overrides: Partial<BankRow> & { id: string }): BankRow {
    return {
        kind: 'bank',
        accountId: ACCOUNT_A,
        payee: 'Payroll',
        memo: null,
        amount: 0,
        postedAt: '2026-05-04T12:00:00Z',
        transactedAt: null,
        status: 'cleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: 1000,
        origin: 'moneydance_import',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: '00000000-0000-0000-0000-000000000099',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        headerId: '00000000-0000-0000-0000-000000000000',
        clearedAt: '2026-05-04T12:00:00Z',
        clearedByUserId: null,
        createdAt: '2026-05-04T12:00:00Z',
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
        ...overrides,
    };
}

function entryTxn(t: BankRow): RegisterEntry {
    return { kind: 'txn', txn: t, groupId: null, legs: null };
}

function entryGroup(
    groupId: string,
    legs: readonly BankRow[],
): RegisterEntry {
    return { kind: 'group', txn: null, groupId, legs: [...legs] };
}

describe('buildDisplayRows', () => {
    it('passes through single-transaction entries unchanged', () => {
        const t1 = txn({ id: 't1', payee: 'Coffee' });
        const t2 = txn({ id: 't2', payee: 'Lunch' });
        const rows = buildDisplayRows([entryTxn(t1), entryTxn(t2)], new Set());
        expect(rows).toEqual([
            { kind: 'txn', txn: t1 },
            { kind: 'txn', txn: t2 },
        ]);
    });

    it('emits a split-parent for group entries (default: collapsed)', () => {
        const legs = [
            txn({ id: 't1', txnGroupId: 'g1', legIndex: 0, amount: -800 }),
            txn({ id: 't2', txnGroupId: 'g1', legIndex: 1, amount: -150 }),
            txn({ id: 't3', txnGroupId: 'g1', legIndex: 2, amount: -50 }),
        ];
        const rows = buildDisplayRows([entryGroup('g1', legs)], new Set());
        expect(rows).toHaveLength(1);
        expect(rows[0]).toMatchObject({
            kind: 'split-parent',
            groupId: 'g1',
            expanded: false,
        });
    });

    it('inlines legs after the parent when the group is expanded', () => {
        const legs = [
            txn({ id: 't1', txnGroupId: 'g1', legIndex: 0, amount: -800 }),
            txn({ id: 't2', txnGroupId: 'g1', legIndex: 1, amount: -150 }),
        ];
        const rows = buildDisplayRows([entryGroup('g1', legs)], new Set(['g1']));
        expect(rows).toHaveLength(3);
        expect(rows[0]!.kind).toBe('split-parent');
        expect(rows[1]).toEqual({ kind: 'split-leg', leg: legs[0] });
        expect(rows[2]).toEqual({ kind: 'split-leg', leg: legs[1] });
    });

    it('handles interleaved txn + group entries in order', () => {
        const single1 = txn({ id: 'a', payee: 'Solo 1' });
        const groupLegs = [
            txn({ id: 'b', txnGroupId: 'g1', legIndex: 0, amount: -10 }),
            txn({ id: 'c', txnGroupId: 'g1', legIndex: 1, amount: -20 }),
        ];
        const single2 = txn({ id: 'd', payee: 'Solo 2' });

        const rows = buildDisplayRows(
            [entryTxn(single1), entryGroup('g1', groupLegs), entryTxn(single2)],
            new Set(),
        );
        expect(rows.map((r) => r.kind)).toEqual([
            'txn',
            'split-parent',
            'txn',
        ]);
    });
});

describe('groupAmount', () => {
    it('prefers the stored headerAccountNetAmount when present', () => {
        // Trigger-populated value wins over leg-sum, even when the
        // stored value disagrees (the trigger is authoritative).
        const legs = [
            txn({ id: '1', amount: -800, headerAccountNetAmount: -1000 }),
            txn({ id: '2', amount: -150, headerAccountNetAmount: -1000 }),
            txn({ id: '3', amount: -50, headerAccountNetAmount: -1000 }),
        ];
        expect(groupAmount(legs)).toBe(-1000);
    });

    it('falls back to summing leg amounts when the stored value is missing', () => {
        // Transient state during ingest before the trigger fires.
        const legs = [
            txn({ id: '1', amount: -800 }),
            txn({ id: '2', amount: -150 }),
            txn({ id: '3', amount: -50 }),
        ];
        expect(groupAmount(legs)).toBe(-1000);
    });
});

describe('groupBalanceAfter', () => {
    it('returns the balance after the highest-leg_index leg', () => {
        const legs = [
            txn({ id: '1', legIndex: 0, balanceAfter: 900 }),
            txn({ id: '2', legIndex: 2, balanceAfter: 750 }), // final leg
            txn({ id: '3', legIndex: 1, balanceAfter: 850 }),
        ];
        expect(groupBalanceAfter(legs)).toBe(750);
    });

    it('returns null when no leg has a balance_after', () => {
        const legs = [
            txn({ id: '1', legIndex: 0, balanceAfter: null }),
            txn({ id: '2', legIndex: 1, balanceAfter: null }),
        ];
        expect(groupBalanceAfter(legs)).toBeNull();
    });
});

describe('canonicalLeg', () => {
    it('returns the first leg (server sorts by leg_index ASC)', () => {
        const legs = [
            txn({ id: 'first', legIndex: 0 }),
            txn({ id: 'mid', legIndex: 1 }),
            txn({ id: 'last', legIndex: 2 }),
        ];
        expect(canonicalLeg(legs).id).toBe('first');
    });
});

describe('regroupTargetSplits', () => {
    // A header whose postings partly land on this account: 2 of 9 postings
    // here → two leg-keyed target entries that must fold into one cluster.
    const TARGET = { accountPostingsOnHeader: 2, headerTotalPostings: 9 };

    it('folds a contiguous target-split run into one group entry', () => {
        const deferral = txn({ id: 'deferral', headerId: 'h1', legIndex: 0, amount: 1137.48, ...TARGET });
        const match = txn({ id: 'match', headerId: 'h1', legIndex: 1, amount: 299.34, ...TARGET });

        const out = regroupTargetSplits([entryTxn(deferral), entryTxn(match)]);

        expect(out).toHaveLength(1);
        expect(out[0]!.kind).toBe('group');
        const g = out[0] as GroupEntry;
        expect(g.groupId).toBe('h1');
        expect(g.legs.map((l) => l.id)).toEqual(['deferral', 'match']);
    });

    it('sorts folded legs by leg_index even when they arrive out of order', () => {
        const b = txn({ id: 'b', headerId: 'h1', legIndex: 1, ...TARGET });
        const a = txn({ id: 'a', headerId: 'h1', legIndex: 0, ...TARGET });

        const out = regroupTargetSplits([entryTxn(b), entryTxn(a)]);

        const g = out[0] as GroupEntry;
        expect(g.legs.map((l) => l.id)).toEqual(['a', 'b']);
    });

    it('passes a single-posting target through as a flat txn (no one-leg parent)', () => {
        const solo = txn({ id: 'solo', headerId: 'h1', accountPostingsOnHeader: 1, headerTotalPostings: 9 });

        const out = regroupTargetSplits([entryTxn(solo)]);

        expect(out).toHaveLength(1);
        expect(out[0]!.kind).toBe('txn');
    });

    it('leaves ordinary single-posting txns untouched', () => {
        const a = entryTxn(txn({ id: 'a' })); // accountPostingsOnHeader=1, total=1
        expect(regroupTargetSplits([a])).toEqual([a]);
    });

    it('passes originating group entries straight through', () => {
        const legs = [
            txn({ id: 't1', headerId: 'g1', legIndex: 0, accountPostingsOnHeader: 2, headerTotalPostings: 2 }),
            txn({ id: 't2', headerId: 'g1', legIndex: 1, accountPostingsOnHeader: 2, headerTotalPostings: 2 }),
        ];
        const g = entryGroup('g1', legs);
        expect(regroupTargetSplits([g])).toEqual([g]);
    });

    it('does not merge legs from different headers', () => {
        const out = regroupTargetSplits([
            entryTxn(txn({ id: 'h1a', headerId: 'h1', legIndex: 0, ...TARGET })),
            entryTxn(txn({ id: 'h1b', headerId: 'h1', legIndex: 1, ...TARGET })),
            entryTxn(txn({ id: 'h2a', headerId: 'h2', legIndex: 0, ...TARGET })),
            entryTxn(txn({ id: 'h2b', headerId: 'h2', legIndex: 1, ...TARGET })),
        ]);

        expect(out).toHaveLength(2);
        expect(out.every((e) => e.kind === 'group')).toBe(true);
        expect((out[0] as GroupEntry).groupId).toBe('h1');
        expect((out[1] as GroupEntry).groupId).toBe('h2');
    });

    it('preserves order with interleaved singles and clusters', () => {
        const out = regroupTargetSplits([
            entryTxn(txn({ id: 's1' })),
            entryTxn(txn({ id: 'c1', headerId: 'h1', legIndex: 0, ...TARGET })),
            entryTxn(txn({ id: 'c2', headerId: 'h1', legIndex: 1, ...TARGET })),
            entryTxn(txn({ id: 's2' })),
        ]);

        expect(out.map((e) => e.kind)).toEqual(['txn', 'group', 'txn']);
    });
});
