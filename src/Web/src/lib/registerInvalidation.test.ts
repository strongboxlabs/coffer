import { describe, it, expect, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';

import { invalidateLedgerRegister } from './registerInvalidation';

// ADR-0079: a wholesale writer (feed sync, reminder-fire, restore, balance heal,
// rename) must refresh the whole register surface with one call — the canonical
// ['register', …] row key (the controller's sentinel reloads the bespoke window
// off it) PLUS the sibling queries the register reads.
describe('invalidateLedgerRegister', () => {
    it('invalidates the canonical register key + buckets + accounts + holdings for the ledger', () => {
        const queryClient = new QueryClient();
        const spy = vi.spyOn(queryClient, 'invalidateQueries');

        invalidateLedgerRegister(queryClient, 'led-1');

        const keys = spy.mock.calls.map((call) => call[0]?.queryKey);
        expect(keys).toContainEqual(['register', 'led-1']);
        expect(keys).toContainEqual(['register-index-buckets', 'led-1']);
        expect(keys).toContainEqual(['accounts', 'led-1']);
        expect(keys).toContainEqual(['holdings', 'led-1']);
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
