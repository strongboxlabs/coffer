import { describe, it, expect, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';

import { invalidateConvertedReads, invalidateHomeReads, invalidateLedgerRegister } from './registerInvalidation';

// ADR-0079 as amended 2026-11-03: a precise writer refreshes the home screen's
// reads without touching the register's window key.
describe('invalidateHomeReads', () => {
    it('invalidates the home, overview, budget-progress and rail-total reads for the ledger, and nothing else', () => {
        const queryClient = new QueryClient();
        const spy = vi.spyOn(queryClient, 'invalidateQueries');
        const removeSpy = vi.spyOn(queryClient, 'removeQueries');

        invalidateHomeReads(queryClient, 'led-1');

        const keys = spy.mock.calls.map((call) => call[0]?.queryKey);
        expect(keys).toEqual([
            ['home', 'led-1'],
            ['overview', 'led-1'],
            ['budget-progress', 'led-1'],
            // The sidebar's section and folder totals: the overview's balances summed
            // and converted on the server (ADR-0114 D7).
            ['account-folders', 'led-1', 'subtotals'],
        ]);
        expect(removeSpy).not.toHaveBeenCalled();
    });
});

// ADR-0079: a wholesale writer (feed sync, reminder-fire, restore, balance heal,
// rename) must refresh the whole register surface with one call — the canonical
// ['register', …] row key (the controller's sentinel reloads the bespoke window
// off it) PLUS the sibling queries the register reads.
describe('invalidateLedgerRegister', () => {
    it('invalidates the canonical register key + buckets + accounts + holdings + folders + account-currency reads for the ledger', () => {
        const queryClient = new QueryClient();
        const spy = vi.spyOn(queryClient, 'invalidateQueries');

        invalidateLedgerRegister(queryClient, 'led-1');

        const keys = spy.mock.calls.map((call) => call[0]?.queryKey);
        expect(keys).toContainEqual(['register', 'led-1']);
        expect(keys).toContainEqual(['register-index-buckets', 'led-1']);
        expect(keys).toContainEqual(['accounts', 'led-1']);
        expect(keys).toContainEqual(['holdings', 'led-1']);
        // A snapshot restore replaces the ledger's folders along with its
        // accounts, and the rail must not keep drawing the old tree.
        expect(keys).toContainEqual(['account-folders', 'led-1']);
        // Reads that carry a currency taken from the accounts (ADR-0109 D9): the
        // Categories tab's display currency, a security's hero and trades, and the
        // securities catalog. An account writer reaches them only through here.
        expect(keys).toContainEqual(['categories', 'led-1']);
        expect(keys).toContainEqual(['security', 'led-1']);
        expect(keys).toContainEqual(['security-transactions', 'led-1']);
        expect(keys).toContainEqual(['securities', 'led-1']);
        // A brokerage's currency edit re-prices its trade days in the new currency,
        // which a security's price list prints; a tag's and a budget category's
        // transaction lists print each line in its account's currency.
        expect(keys).toContainEqual(['security-prices', 'led-1']);
        expect(keys).toContainEqual(['tag-transactions', 'led-1']);
        expect(keys).toContainEqual(['budget-transactions', 'led-1']);
        // The home screen reads the same ledger: its own reads, the overview's
        // balances and this month's spending.
        expect(keys).toContainEqual(['home', 'led-1']);
        expect(keys).toContainEqual(['overview', 'led-1']);
        expect(keys).toContainEqual(['budget-progress', 'led-1']);
    });

    it('drops the investment editor per-header leg-seed caches for the ledger', () => {
        const queryClient = new QueryClient();
        const removeSpy = vi.spyOn(queryClient, 'removeQueries');

        invalidateLedgerRegister(queryClient, 'led-1');

        // The editor captures its ['header-legs', …] seed once, so an external /
        // wholesale writer reaching this seam must DROP it (not just invalidate),
        // else a reopen re-seeds stale.
        const keys = removeSpy.mock.calls.map((call) => call[0]?.queryKey);
        expect(keys).toContainEqual(['header-legs', 'led-1']);
    });

    it('scopes every invalidation to the given ledger (no cross-ledger blast)', () => {
        const queryClient = new QueryClient();
        const spy = vi.spyOn(queryClient, 'invalidateQueries');

        invalidateLedgerRegister(queryClient, 'led-2');

        for (const call of spy.mock.calls) {
            expect(call[0]?.queryKey?.[1]).toBe('led-2');
        }
    });
});

// ADR-0114 D7, D9: a typed rate, or a new reporting currency, moves every converted
// figure, the ledger list's currency and the rates view with it.
describe('invalidateConvertedReads', () => {
    it('refreshes the register surface, the home reads, the ledger list, the rates and the report reads', () => {
        const queryClient = new QueryClient();
        const spy = vi.spyOn(queryClient, 'invalidateQueries');

        invalidateConvertedReads(queryClient, 'led-1');

        const keys = spy.mock.calls.map((call) => call[0]?.queryKey);
        expect(keys).toContainEqual(['register', 'led-1']);
        expect(keys).toContainEqual(['accounts', 'led-1']);
        expect(keys).toContainEqual(['categories', 'led-1']);
        expect(keys).toContainEqual(['overview', 'led-1']);
        expect(keys).toContainEqual(['budget-progress', 'led-1']);
        expect(keys).toContainEqual(['ledgers']);
        expect(keys).toContainEqual(['fx-rates', 'led-1']);
        expect(keys).toContainEqual(['benchmarks', 'led-1']);
        expect(keys).toContainEqual(['report-summary', 'led-1']);
        expect(keys).toContainEqual(['returns-series', 'led-1']);
    });
});
