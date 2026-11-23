import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch } from '@/lib/annotations/fetchStub';

import { DERIVED_RATE_DEBOUNCE_MS, useDerivedRate, type DerivedRateArgs } from './marketSeriesQueries';

// The editors ask for the derived rate as Amount is typed (ADR-0114 D14). One request per
// keystroke would race and flood the server, so the amount is debounced: only the value it
// settles on is asked for. Mocked at fetch, so the real client builds the request.

function wrapper(queryClient: QueryClient) {
    return function Wrapper({ children }: { children: ReactNode }) {
        return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    };
}

function client() {
    return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

const answer = {
    fromCurrency: 'EUR', toCurrency: 'JPY', date: '2026-03-12', reportingCurrency: 'USD',
    rate: 2, rateDate: '2026-03-12', legs: [], converted: 0, rateUnavailable: [],
};

const base: DerivedRateArgs = {
    ledgerId: 'ledger-1', fromCurrency: 'EUR', toCurrency: 'JPY', date: '2026-03-12', amount: 1,
};

/** Resolves after `ms` on the real clock. */
function pause(ms: number): Promise<void> {
    return new Promise((resolve) => { setTimeout(resolve, ms); });
}

describe('useDerivedRate', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('asks once for the amount the typing settles on, never for the ones in between', async () => {
        const stub = stubFetch().on('GET', /\/derived$/, answer);
        const { rerender } = renderHook((args: DerivedRateArgs) => useDerivedRate(args), {
            wrapper: wrapper(client()),
            initialProps: base,
        });
        await waitFor(() => expect(stub.callsTo('GET', /\/derived$/)).toHaveLength(1));

        rerender({ ...base, amount: 12 });
        rerender({ ...base, amount: 123 });
        rerender({ ...base, amount: 1234 });
        await waitFor(() => expect(stub.callsTo('GET', /\/derived$/)).toHaveLength(2));
        await pause(DERIVED_RATE_DEBOUNCE_MS * 2);

        const amounts = stub.callsTo('GET', /\/derived$/).map((c) => c.search.get('amount'));
        expect(amounts).toEqual(['1', '1234']);
    });

    // A save or a prefill inside the debounce window must not read the previous amount's
    // converted figure as the answer for the new one.
    it('never answers a new amount with the previous amount\'s figure while the typing settles', async () => {
        stubFetch().on('GET', /\/derived$/, (call: { search: URLSearchParams }) => ({
            ...answer, converted: Number(call.search.get('amount')) * 2,
        }));
        const { result, rerender } = renderHook((args: DerivedRateArgs) => useDerivedRate(args), {
            wrapper: wrapper(client()),
            initialProps: base,
        });
        // Anchor: the first amount's answer has arrived and is current.
        await waitFor(() => expect(result.current.data?.converted).toBe(2));

        rerender({ ...base, amount: 1234 });
        // At once, before the debounce ends: no answer yet, not the one for the old amount.
        expect(result.current.data).toBeUndefined();

        await waitFor(() => expect(result.current.data?.converted).toBe(2468));
    });

    it.each([
        ['the two currencies are the same', { toCurrency: 'EUR' }],
        ['a currency is not known yet', { fromCurrency: null }],
        ['the editor has no date', { date: null }],
        ['the second amount is not prefilled', { enabled: false }],
    ])('asks for nothing when %s', async (_why, patch: Partial<DerivedRateArgs>) => {
        const stub = stubFetch().on('GET', /\/derived$/, answer);
        const { result } = renderHook(() => useDerivedRate({ ...base, ...patch }), { wrapper: wrapper(client()) });

        await pause(DERIVED_RATE_DEBOUNCE_MS * 2);

        // Anchored on the settled frame: the query is idle, not on its way.
        expect(result.current.fetchStatus).toBe('idle');
        expect(stub.calls).toEqual([]);
    });
});
