import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchDerivedRate } from './marketSeries';
import { stubFetch } from '@/lib/annotations/fetchStub';
import type { DerivedRate } from '../types/marketSeries';

// The derived rate is read at one route the server serves (ADR-0114 D13, D14). The path
// and the query names are the contract: a misspelt query name is ignored by the server,
// so the read would quietly answer for another day or without the amount.
describe('fetchDerivedRate', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    const answer: DerivedRate = {
        fromCurrency: 'EUR',
        toCurrency: 'JPY',
        date: '2026-03-12',
        reportingCurrency: 'USD',
        rate: 2,
        rateDate: '2026-03-11',
        legs: [
            { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-12', rate: 4 },
            { fromCurrency: 'JPY', toCurrency: 'USD', date: '2026-03-11', rate: 2 },
        ],
        converted: 6,
        rateUnavailable: [],
    };

    it('asks the derived route for the pair, the day and the amount', async () => {
        const stub = stubFetch().on('GET', /\/fx-rates\/EUR\/JPY\/derived$/, answer);

        const got = await fetchDerivedRate('ledger-1', 'EUR', 'JPY', '2026-03-12', 3);

        const call = stub.callsTo('GET', /\/derived$/)[0]!;
        expect(call.path).toBe('/api/ledgers/ledger-1/fx-rates/EUR/JPY/derived');
        expect([...call.search.entries()].sort()).toEqual([['amount', '3'], ['date', '2026-03-12']]);
        expect(stub.unrouted).toEqual([]);
        expect(got.rate).toBe(2);
        expect(got.rateDate).toBe('2026-03-11');
        expect(got.converted).toBe(6);
    });

    it('sends no amount when only the rate is wanted', async () => {
        const stub = stubFetch().on('GET', /\/derived$/, { ...answer, converted: null });

        await fetchDerivedRate('ledger-1', 'EUR', 'JPY', '2026-03-12');

        expect([...stub.callsTo('GET', /\/derived$/)[0]!.search.entries()]).toEqual([['date', '2026-03-12']]);
    });

    it('hands back the missing stored legs as the server names them', async () => {
        const missing = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-12', reason: 'rate_unavailable' as const };
        stubFetch().on('GET', /\/derived$/, {
            ...answer, rate: null, rateDate: null, legs: [], converted: null, rateUnavailable: [missing],
        });

        const got = await fetchDerivedRate('ledger-1', 'EUR', 'JPY', '2026-03-12', 3);

        expect(got.rate).toBeNull();
        expect(got.rateUnavailable).toEqual([missing]);
    });
});
