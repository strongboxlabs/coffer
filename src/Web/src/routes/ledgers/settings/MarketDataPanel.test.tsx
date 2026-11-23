import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import * as apiModule from '@/lib/api';

import { MarketDataPanel } from './MarketDataPanel';

// The quote providers' switches each save the whole list, built from the list
// last read. They are one group (pending.ts): a change of any holds them all
// from the change until its save has landed, so a second change made before
// the pending state renders never drops the first. Provider names are invented.

const LEDGER = 'ledger-1';

type Ledgers = Awaited<ReturnType<typeof apiModule.fetchVisibleLedgers>>;

function renderPanel(role: 'owner' | 'editor' | 'viewer' = 'owner') {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    // The reader's role, from the read the panel asks it of.
    const ledgers = [{ id: LEDGER, name: 'Ledger', role }] as Ledgers;
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    client.setQueryData(['ledgers'], ledgers);
    render(
        <QueryClientProvider client={client}>
            <MarketDataPanel ledgerId={LEDGER} />
        </QueryClientProvider>,
    );
}

/** Microtasks only: the save has been called, the pending state has not rendered. */
const ticks = async () => { for (let i = 0; i < 10; i += 1) await Promise.resolve(); };

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchQuoteProviders').mockResolvedValue([
        { key: 'a', displayName: 'Provider A', host: null },
        { key: 'b', displayName: 'Provider B', host: null },
    ]);
    vi.spyOn(apiModule, 'fetchQuotesPrefs').mockResolvedValue({ enabledProviders: [] });
    vi.spyOn(apiModule, 'fetchSchedule').mockReturnValue(new Promise(() => {}));
    vi.spyOn(apiModule, 'fetchBenchmarks').mockResolvedValue([]);
});

describe('MarketDataPanel: the provider switches', () => {
    it('sends one save for two providers switched on back to back, and it keeps the first', async () => {
        const save = vi.spyOn(apiModule, 'saveQuotesPrefs').mockReturnValue(new Promise(() => {}));
        renderPanel();

        fireEvent.click(await screen.findByRole('checkbox', { name: 'Enable Provider A' }));
        fireEvent.click(screen.getByRole('checkbox', { name: 'Enable Provider B' }));
        await ticks();

        expect(save).toHaveBeenCalledTimes(1);
        expect(save).toHaveBeenCalledWith(LEDGER, { enabledProviders: ['a'] });
    });

    it('sends one save for the same switch pressed twice', async () => {
        const save = vi.spyOn(apiModule, 'saveQuotesPrefs').mockReturnValue(new Promise(() => {}));
        renderPanel();

        const a = await screen.findByRole('checkbox', { name: 'Enable Provider A' });
        fireEvent.click(a);
        fireEvent.click(a);
        await ticks();

        expect(save).toHaveBeenCalledTimes(1);
    });

    it('refreshes the rates view and the benchmark list once a switch is saved', async () => {
        vi.spyOn(apiModule, 'saveQuotesPrefs').mockResolvedValue({ enabledProviders: ['a'] });
        const invalidated = vi.spyOn(QueryClient.prototype, 'invalidateQueries');
        renderPanel();

        fireEvent.click(await screen.findByRole('checkbox', { name: 'Enable Provider A' }));

        // The benchmark list's availability reads the reader's Yahoo switch (ADR-0114 D1).
        const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
        await waitFor(() => expect(keys()).toContainEqual(['benchmarks', LEDGER]));
        expect(keys()).toContainEqual(['fx-rates', LEDGER]);
    });
});

describe('MarketDataPanel: where each provider sends', () => {
    it('names the host each known provider sends to, as the install configured it', async () => {
        vi.spyOn(apiModule, 'fetchQuoteProviders').mockResolvedValue([
            { key: 'yahoo', displayName: 'Yahoo Finance', host: 'query1.finance.yahoo.com' },
            { key: 'frankfurter', displayName: 'Frankfurter exchange rates', host: 'rates.example.test' },
        ]);
        renderPanel();
        expect(await screen.findByText(
            'End-of-day prices from Yahoo Finance (query1.finance.yahoo.com) for holdings that have a public ticker.',
        )).toBeInTheDocument();
        expect(screen.getByText(/^Daily exchange rates from Frankfurter \(rates\.example\.test\), so totals/)).toBeInTheDocument();
    });

    it('names no host it was not given', async () => {
        vi.spyOn(apiModule, 'fetchQuoteProviders').mockResolvedValue([
            { key: 'frankfurter', displayName: 'Frankfurter exchange rates', host: null },
        ]);
        renderPanel();
        expect(await screen.findByText(/^Daily exchange rates from Frankfurter, so totals/)).toBeInTheDocument();
    });
});

describe('MarketDataPanel: by role (ADR-0023 §L)', () => {
    it('gives a viewer the provider switches, their own choice, and no refresh schedule', async () => {
        renderPanel('viewer');
        expect(await screen.findByRole('checkbox', { name: 'Enable Provider A' })).toBeInTheDocument();
        expect(screen.queryByText('Refresh prices and exchange rates automatically each day')).toBeNull();
    });

    it.each(['owner', 'editor'] as const)('keeps the refresh schedule for an %s', async (role) => {
        renderPanel(role);
        expect(await screen.findByRole('checkbox', { name: 'Enable Provider A' })).toBeInTheDocument();
        expect(screen.getByText('Refresh prices and exchange rates automatically each day')).toBeInTheDocument();
    });
});
