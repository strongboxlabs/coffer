import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import { checkLedgerConsistency, fetchConsistencyState, repairProjection } from './ledger';

/**
 * The consistency check records its verdict, so it is a POST; the server answers
 * nothing else on its path. Asserted on what crosses the wire, because the panel
 * tests mock these functions and could not see a GET that the server refuses.
 */
describe('consistency calls on the wire', () => {
    let calls: Array<{ url: string; init: RequestInit }>;

    beforeEach(() => {
        calls = [];
        vi.stubGlobal('fetch', vi.fn((url: string, init: RequestInit) => {
            calls.push({ url, init });
            return Promise.resolve(new Response('{}', {
                status: 200,
                headers: { 'Content-Type': 'application/json' },
            }));
        }));
    });

    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('posts the check', async () => {
        await checkLedgerConsistency('l1');
        expect(calls).toHaveLength(1);
        expect(calls[0].init.method).toBe('POST');
        expect(new URL(calls[0].url, 'http://coffer.test').pathname)
            .toBe('/api/ledgers/l1/balances/consistency');
    });

    it('posts a repair', async () => {
        await repairProjection('l1', 'trade_prices');
        expect(calls).toHaveLength(1);
        expect(calls[0].init.method).toBe('POST');
        expect(new URL(calls[0].url, 'http://coffer.test').pathname)
            .toBe('/api/ledgers/l1/balances/consistency/trade_prices/repair');
    });

    it('reads the stored verdict with a GET', async () => {
        await fetchConsistencyState('l1');
        expect(calls).toHaveLength(1);
        expect(calls[0].init.method ?? 'GET').toBe('GET');
    });
});
