import { describe, it, expect } from 'vitest';

import { bankRow } from '../../home/homeTestFixtures';
import { shouldRefreshOnSignal, shouldReseatAfterWrite, withFreshBalance } from './useRegisterController';

// After a save the loaded rows take the server's fresh balances in place. On a
// category a balance can be null for a missing rate (ADR-0114 D9), and the rates it
// needed travel with it.
describe('withFreshBalance', () => {
    const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-10-08', reason: 'rate_unavailable' as const };

    it('carries a null balance\'s missing rates onto the row', () => {
        const entry = { kind: 'txn' as const, txn: bankRow({ balanceAfter: 40 }), groupId: null, legs: null };
        const patched = withFreshBalance(entry, { headerId: 'header-1', balanceAfter: null, netAmount: -10, balanceRateUnavailable: [missing] });
        expect(patched.kind === 'txn' && patched.txn.balanceAfter).toBeNull();
        expect(patched.kind === 'txn' && patched.txn.balanceRateUnavailable).toEqual([missing]);
    });

    it('drops the missing rates a balance carried once it has its rate again, on every leg of a group', () => {
        const leg = bankRow({ balanceAfter: null, balanceRateUnavailable: [missing] });
        const entry = { kind: 'group' as const, txn: null, groupId: 'group-1', legs: [leg, { ...leg, id: 'leg-2', legIndex: 1 }] };
        const patched = withFreshBalance(entry, { headerId: 'header-1', balanceAfter: 55, netAmount: -10 });
        expect(patched.kind === 'group' && patched.legs.map((l) => [l.balanceAfter, l.balanceRateUnavailable])).toEqual([[55, undefined], [55, undefined]]);
    });
});

// After a saved write the row is patched in place (preserving the user's
// scroll position) UNLESS its sort key moved — then the row must relocate to
// its new slot, so the controller re-seeds the window. Under the date sort the
// key is the full posted timestamp (then the immutable insertion sequence), so
// a same-day time change moves the row among that day's rows and re-orders
// their running balances. This guards that decision.
describe('shouldReseatAfterWrite', () => {
    it('re-seats when the calendar date changed', () => {
        expect(
            shouldReseatAfterWrite('2026-06-01T12:00:00Z', '2026-07-15T00:00:00Z'),
        ).toBe(true);
    });

    it('re-seats on a same-day time change, which moves the row within the day', () => {
        // An Accept writes the posted date at midnight over a feed row's time.
        expect(
            shouldReseatAfterWrite('2026-06-01T18:00:00Z', '2026-06-01T00:00:00.000Z'),
        ).toBe(true);
    });

    it('does NOT re-seat when the posted timestamp is unchanged', () => {
        expect(
            shouldReseatAfterWrite('2026-06-01T12:00:00Z', '2026-06-01T12:00:00Z'),
        ).toBe(false);
    });

    it('compares instants, not spellings', () => {
        expect(
            shouldReseatAfterWrite('2026-06-01T12:00:00Z', '2026-06-01T12:00:00.000Z'),
        ).toBe(false);
    });

    it('does NOT re-seat when the old date is unknown (row not loaded)', () => {
        expect(shouldReseatAfterWrite(undefined, '2026-07-15T00:00:00Z')).toBe(false);
    });

    it('re-seats under a column sort, whose key any write can change', () => {
        expect(
            shouldReseatAfterWrite('2026-06-01T12:00:00Z', '2026-06-01T12:00:00Z', 'amount'),
        ).toBe(true);
    });
});

// ADR-0079: the controller subscribes to the canonical ['register', …] key via a
// sentinel query and reloads the bespoke row window when it refetches. This
// guards the skip-initial logic — the tricky part — so an external invalidation
// reloads the rows while the window's own first load does not double-fetch.
describe('shouldRefreshOnSignal', () => {
    it('does NOT refresh before the sentinel has settled (updatedAt 0)', () => {
        expect(shouldRefreshOnSignal(null, 0)).toEqual({ refresh: false, nextSeen: null });
    });

    it('records the first settle WITHOUT refreshing (the initial window load is already fresh)', () => {
        expect(shouldRefreshOnSignal(null, 1000)).toEqual({ refresh: false, nextSeen: 1000 });
    });

    it('refreshes when the signal bumps — a writer invalidated the canonical key', () => {
        expect(shouldRefreshOnSignal(1000, 2000)).toEqual({ refresh: true, nextSeen: 2000 });
    });

    it('does NOT refresh on an unchanged signal (a re-render, same settle)', () => {
        expect(shouldRefreshOnSignal(2000, 2000)).toEqual({ refresh: false, nextSeen: 2000 });
    });
});
