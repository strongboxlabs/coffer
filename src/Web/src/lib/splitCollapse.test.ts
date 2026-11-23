import { describe, it, expect } from 'vitest';

import {
    buildDisplayRows,
    canonicalLeg,
    groupAmount,
    groupBalanceAfter,
    groupBalanceRateUnavailable,
    groupCurrency,
} from './splitCollapse';
import { NO_NOTES } from './annotations/rowNotesFixture';
import type { BankRow, RegisterEntry } from './types';

const ACCOUNT_A = '00000000-0000-0000-0000-0000000000aa';

// Split-collapse is domain-agnostic; the canonical case it serves is a
// multi-leg bank-domain paycheck, so the fixture is a `BankRow`
// (ADR-0030 §2 — bank rows carry no investment fields).
function txn(overrides: Partial<BankRow> & { id: string }): BankRow {
    return {
        ...NO_NOTES,
        kind: 'bank',
        accountId: ACCOUNT_A,
        payee: 'Payroll',
        amount: 0,
        postedAt: '2026-05-04T12:00:00Z',
        transactedAt: null,
        status: 'cleared',
        isHidden: false,
        balanceAfter: 1000,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
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
        headerId: '00000000-0000-0000-0000-000000000000',
        clearedAt: '2026-05-04T12:00:00Z',
        clearedByUserId: null,
        createdAt: '2026-05-04T12:00:00Z',
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

    // A category's split can be paid from accounts in different currencies, each
    // line in its paying account's (ADR-0114 D7). No figure adds currencies, so the
    // group has no net: not the stored one, which adds them, nor a sum of the lines.
    it('has no net when the lines are in different currencies', () => {
        const stored = [
            txn({ id: '1', amount: 10, currencyCode: 'USD', headerAccountNetAmount: 30 }),
            txn({ id: '2', amount: 20, currencyCode: 'CAD', headerAccountNetAmount: 30 }),
        ];
        expect(groupAmount(stored)).toBeNull();
        expect(groupCurrency(stored)).toBeNull();

        const summed = stored.map((leg) => ({ ...leg, headerAccountNetAmount: null }));
        expect(groupAmount(summed)).toBeNull();
    });

    it('names the one currency the lines share', () => {
        const legs = [
            txn({ id: '1', amount: 10, currencyCode: 'CAD', headerAccountNetAmount: 30 }),
            txn({ id: '2', amount: 20, currencyCode: 'CAD', headerAccountNetAmount: 30 }),
        ];
        expect(groupCurrency(legs)).toBe('CAD');
        expect(groupAmount(legs)).toBe(30);
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

describe('groupBalanceRateUnavailable', () => {
    const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-10-08', reason: 'rate_unavailable' as const };

    it('returns the missing rates of the leg whose balance the group shows, the last', () => {
        const legs = [
            txn({ id: '1', legIndex: 0, balanceAfter: 900 }),
            txn({ id: '2', legIndex: 1, balanceAfter: null, balanceRateUnavailable: [missing] }),
        ];
        expect(groupBalanceAfter(legs)).toBeNull();
        expect(groupBalanceRateUnavailable(legs)).toEqual([missing]);
    });

    it('returns none when the last leg has its rate, whatever an earlier one carries', () => {
        const legs = [
            txn({ id: '1', legIndex: 1, balanceAfter: 750 }),
            txn({ id: '2', legIndex: 0, balanceAfter: null, balanceRateUnavailable: [missing] }),
        ];
        expect(groupBalanceRateUnavailable(legs)).toBeUndefined();
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

describe('a split seen from a line\'s other side', () => {
    // Two lines of one header that both land on this account: from here each
    // line is its own read-only row, and nothing clusters them back.
    const OPPOSITE = {
        accountPostingsOnHeader: 2,
        headerTotalPostings: 9,
        notesKind: 'splitOpposite' as const,
        isReadOnly: true,
    };

    it('renders one row per line, in the order the server sent them', () => {
        const rows = buildDisplayRows(
            [
                entryTxn(txn({ id: 'deferral', headerId: 'h1', legIndex: 0, ...OPPOSITE })),
                entryTxn(txn({ id: 'match', headerId: 'h1', legIndex: 1, ...OPPOSITE })),
            ],
            new Set(['h1']),
        );
        expect(rows.map((r) => r.kind)).toEqual(['txn', 'txn']);
        expect(rows.map((r) => (r.kind === 'txn' ? r.txn.id : null))).toEqual(['deferral', 'match']);
    });
});
