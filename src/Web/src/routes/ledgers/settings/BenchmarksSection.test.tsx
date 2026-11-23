import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import * as apiModule from '@/lib/api';
import type { Benchmark } from '@/lib/types';

import { MarketDataPanel } from './MarketDataPanel';

// Settings → Quotes: the one provider list with Frankfurter as its second row, and the
// ledger's benchmarks under it (ADR-0114 D1, D2, D8). Every fund, symbol and ledger
// here is invented.

const LEDGER = 'ledger-1';

type Ledgers = Awaited<ReturnType<typeof apiModule.fetchVisibleLedgers>>;

const WORLD: Benchmark = { id: 'b-1', symbol: 'SWF', name: 'Sample World Fund', currencyCode: 'USD', position: 1, availability: null };
const BOND: Benchmark = { id: 'b-2', symbol: 'SBF', name: 'Sample Bond Fund', currencyCode: 'USD', position: 2, availability: null };
const NORTH: Benchmark = { id: 'b-3', symbol: 'SNE.TO', name: 'Sample North Equity', currencyCode: 'CAD', position: 3, availability: null };

function renderPanel({ role = 'owner', yahoo = true, benchmarks = [WORLD, BOND] }: {
    role?: 'owner' | 'editor' | 'viewer';
    yahoo?: boolean;
    benchmarks?: Benchmark[] | Promise<Benchmark[]>;
} = {}) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const ledgers = [{ id: LEDGER, name: 'Sample Ledger', role, currencyCode: 'USD' }] as Ledgers;
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    client.setQueryData(['ledgers'], ledgers);
    vi.spyOn(apiModule, 'fetchQuotesPrefs').mockResolvedValue({ enabledProviders: yahoo ? ['yahoo'] : [] });
    const list = vi.spyOn(apiModule, 'fetchBenchmarks');
    if (benchmarks instanceof Promise) list.mockReturnValue(benchmarks);
    else list.mockResolvedValue(benchmarks);
    render(
        <QueryClientProvider client={client}>
            <MarketDataPanel ledgerId={LEDGER} />
        </QueryClientProvider>,
    );
    return { client };
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchQuoteProviders').mockResolvedValue([
        { key: 'yahoo', displayName: 'Yahoo Finance', host: 'query1.finance.yahoo.com' },
        { key: 'frankfurter', displayName: 'Frankfurter (exchange rates)', host: 'api.frankfurter.dev' },
    ]);
    vi.spyOn(apiModule, 'fetchSchedule').mockReturnValue(new Promise(() => {}));
});

/** The benchmarks list, once it is drawn. */
const list = () => screen.findByRole('list', { name: 'Benchmarks' });

describe('Quotes: the provider list', () => {
    it('lists Frankfurter as a second row, off by default, saying what it sends', async () => {
        renderPanel();
        const frankfurter = await screen.findByRole('checkbox', { name: 'Enable Frankfurter (exchange rates)' });
        expect(frankfurter).not.toBeChecked();
        expect(screen.getByText('Sent: currency codes and dates only. No amounts and no account data.')).toBeInTheDocument();
        expect(screen.getByText(/benchmark funds for the Returns report/)).toBeInTheDocument();
        const rows = screen.getAllByRole('checkbox').filter((c) => c.getAttribute('aria-label')?.startsWith('Enable '));
        expect(rows.map((c) => c.getAttribute('aria-label'))).toEqual([
            'Enable Yahoo Finance', 'Enable Frankfurter (exchange rates)',
        ]);
    });

    it('turns Frankfurter on beside Yahoo, keeping Yahoo', async () => {
        const save = vi.spyOn(apiModule, 'saveQuotesPrefs').mockResolvedValue({ enabledProviders: ['yahoo', 'frankfurter'] });
        renderPanel();
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Enable Frankfurter (exchange rates)' }));
        await waitFor(() => expect(save).toHaveBeenCalledWith(LEDGER, { enabledProviders: ['yahoo', 'frankfurter'] }));
    });
});

describe('Quotes: benchmarks, as a reader who can write', () => {
    it('lists each benchmark with its symbol and currency, and how many of three are used', async () => {
        renderPanel({ benchmarks: [WORLD, NORTH] });
        const rows = within(await list()).getAllByRole('listitem');
        expect(rows).toHaveLength(2);
        expect(rows[0]).toHaveTextContent('Sample World Fund');
        expect(rows[0]).toHaveTextContent('SWF');
        expect(rows[1]).toHaveTextContent('CAD');
        expect(screen.getByText('Funds and ETFs only · 2 of 3 used.')).toBeInTheDocument();
    });

    it('says Loading… while the list is read', async () => {
        renderPanel({ benchmarks: new Promise(() => {}) });
        await screen.findByRole('checkbox', { name: 'Enable Yahoo Finance' });
        const section = screen.getByRole('heading', { name: 'Benchmarks' }).closest('div')!.parentElement!;
        expect(within(section).getByText('Loading…')).toBeInTheDocument();
    });

    it('says when there are none yet', async () => {
        renderPanel({ benchmarks: [] });
        expect(await screen.findByText(/No benchmarks yet\. Add a fund or ETF below/)).toBeInTheDocument();
        expect(screen.getByText('Funds and ETFs only · 0 of 3 used.')).toBeInTheDocument();
    });

    it('says the list could not be read', async () => {
        renderPanel({ benchmarks: Promise.reject(new apiModule.ApiError(500, 'Server error.')) });
        expect(await screen.findByText('Server error.')).toBeInTheDocument();
        expect(screen.queryByLabelText('Add a benchmark')).toBeNull();
    });

    it('adds the symbol typed, upper-cased, checking while Yahoo answers, then clears the field', async () => {
        let answer!: (b: Benchmark) => void;
        const add = vi.spyOn(apiModule, 'addBenchmark').mockReturnValue(new Promise((r) => { answer = r; }));
        renderPanel({ benchmarks: [WORLD] });
        const user = userEvent.setup();
        const field = await screen.findByLabelText('Add a benchmark');
        await waitFor(() => expect(field).toBeEnabled());
        await user.type(field, ' sbf ');
        await user.click(screen.getByRole('button', { name: 'Add' }));

        expect(add).toHaveBeenCalledWith(LEDGER, 'SBF');
        const checking = await screen.findByRole('button', { name: 'Checking…' });
        expect(checking).toHaveAttribute('aria-busy', 'true');
        // Remove waits on the add too: one group.
        expect(screen.getByRole('button', { name: 'Remove SWF' })).toHaveAttribute('aria-disabled', 'true');

        answer(BOND);
        await waitFor(() => expect(field).toHaveValue(''));
        expect(screen.queryByRole('alert')).toBeNull();
    });

    it.each([
        ['benchmark-is-index', '^SIX', '^SIX is an index, not a fund. An index has no dividend-adjusted close, so it cannot show total return. Add a fund or ETF that tracks it.', true],
        ['benchmark-no-adjusted-close', 'SMM', 'Yahoo Finance returned no dividend-adjusted close for SMM, so its total return cannot be measured. A benchmark needs a fund or ETF that has one.', true],
        ['benchmark-instrument-unsupported', 'SEQ', 'Yahoo Finance does not list SEQ as a fund or ETF. A benchmark is a fund or ETF, so its total return can be measured.', true],
        ['benchmark-symbol-invalid', 'BAD SYM', 'Enter a Yahoo Finance symbol for a fund or ETF, such as VT.', true],
        ['benchmark-already-added', 'SWF', 'SWF is already one of this ledger\'s benchmarks.', true],
        ['benchmark-limit-reached', 'SBF', 'A ledger keeps up to three benchmarks. Remove one to add another.', false],
        ['currency-missing', 'SNC', 'Yahoo Finance names no currency for SNC, so its line could not be converted. It cannot be a benchmark.', true],
        ['currency-unknown', 'SUC', 'Yahoo Finance quotes SUC in a currency Coffer does not recognise, so it cannot be a benchmark.', true],
        ['ticker-not-resolved', 'NOPE', 'Yahoo Finance found nothing for NOPE. Check the symbol as Yahoo Finance writes it, such as VT or a listing with its exchange suffix.', true],
        ['rate-limited', 'SBF', 'Yahoo Finance is limiting requests right now, so SBF could not be checked. Try again in a few minutes.', false],
        ['fetch-failed', 'SBF', 'Coffer could not reach Yahoo Finance to check SBF. Nothing was added. Try again later.', false],
        ['parse-failed', 'SBF', 'Yahoo Finance answered with something Coffer could not read, so SBF was not checked. Nothing was added. Try again later.', false],
    ] as const)('says %s in words, keeping the symbol', async (code, symbol, words, invalid) => {
        vi.spyOn(apiModule, 'addBenchmark').mockRejectedValue(new apiModule.ApiError(422, 'server detail', code));
        renderPanel({ benchmarks: [WORLD] });
        const user = userEvent.setup();
        const field = await screen.findByLabelText('Add a benchmark');
        await waitFor(() => expect(field).toBeEnabled());
        await user.type(field, symbol);
        await user.click(screen.getByRole('button', { name: 'Add' }));

        expect(await screen.findByRole('alert')).toHaveTextContent(words);
        expect(field).toHaveValue(symbol);
        if (invalid) expect(field).toHaveAttribute('aria-invalid', 'true');
        else expect(field).not.toHaveAttribute('aria-invalid');
    });

    it('says a code it does not know with the server\'s words', async () => {
        vi.spyOn(apiModule, 'addBenchmark').mockRejectedValue(new apiModule.ApiError(422, 'Something new went wrong.', 'brand-new-code'));
        renderPanel({ benchmarks: [WORLD] });
        const user = userEvent.setup();
        const field = await screen.findByLabelText('Add a benchmark');
        await waitFor(() => expect(field).toBeEnabled());
        await user.type(field, 'SBF');
        await user.click(screen.getByRole('button', { name: 'Add' }));
        expect(await screen.findByRole('alert')).toHaveTextContent('Something new went wrong.');
    });

    it('answers the server\'s Yahoo-off refusal with the switch to turn on', async () => {
        vi.spyOn(apiModule, 'addBenchmark').mockRejectedValue(new apiModule.ApiError(422, 'detail', 'benchmark-yahoo-off'));
        renderPanel({ benchmarks: [WORLD] });
        const user = userEvent.setup();
        const field = await screen.findByLabelText('Add a benchmark');
        await waitFor(() => expect(field).toBeEnabled());
        await user.type(field, 'SBF');
        await user.click(screen.getByRole('button', { name: 'Add' }));
        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent('Adding a benchmark checks the symbol with Yahoo Finance, which is off for this ledger.');
        await user.click(within(alert).getByRole('button', { name: 'Turn on Yahoo Finance above' }));
        expect(screen.getByRole('checkbox', { name: 'Enable Yahoo Finance' })).toHaveFocus();
    });

    it('with Yahoo off, disables the field and Add before anything is typed, with the reason and the way to the switch', async () => {
        renderPanel({ yahoo: false });
        await list();
        const field = await screen.findByLabelText('Add a benchmark');
        await waitFor(() => expect(screen.getByText(/which is off for this ledger/)).toBeInTheDocument());
        expect(field).toBeDisabled();
        expect(screen.getByRole('button', { name: 'Add' })).toBeDisabled();
        // The reason stands where the hint goes, and the count with it is gone.
        expect(screen.queryByText(/of 3 used/)).toBeNull();
        expect(within(await list()).getAllByRole('listitem')).toHaveLength(2);

        await userEvent.click(screen.getByRole('button', { name: 'Turn on Yahoo Finance above' }));
        expect(screen.getByRole('checkbox', { name: 'Enable Yahoo Finance' })).toHaveFocus();
    });

    it('with three, disables the field and Add and says how to make room', async () => {
        renderPanel({ benchmarks: [WORLD, BOND, NORTH] });
        expect(await screen.findByText('3 of 3 used. Remove one to add another.')).toBeInTheDocument();
        expect(screen.getByLabelText('Add a benchmark')).toBeDisabled();
        const add = screen.getByRole('button', { name: 'Add' });
        expect(add).toBeDisabled();
        expect(add).toHaveAttribute('title', 'A ledger keeps up to three benchmarks.');
    });

    it('asks before removing, saying what happens to saved reports, then removes', async () => {
        const remove = vi.spyOn(apiModule, 'removeBenchmark').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: 'Remove SWF' }));

        const dialog = await screen.findByRole('dialog', { name: 'Remove “Sample World Fund (SWF)”?' });
        expect(dialog).toHaveTextContent(
            'Saved Returns reports that compare with SWF will show no benchmark until SWF is added again.',
        );
        expect(remove).not.toHaveBeenCalled();
        await user.click(within(dialog).getByRole('button', { name: 'Remove' }));
        await waitFor(() => expect(remove).toHaveBeenCalledWith(LEDGER, 'b-1'));
    });

    // ADR-0023 §L: the last row's Remove hands focus to the add control, never the page.
    // With Yahoo off the symbol field is disabled, so the way to the switch takes it.
    it('with Yahoo off, gives focus to the way to the switch when the last benchmark goes', async () => {
        const remove = vi.spyOn(apiModule, 'removeBenchmark').mockResolvedValue(undefined);
        renderPanel({ yahoo: false, benchmarks: [WORLD] });
        const user = userEvent.setup();
        await waitFor(() => expect(screen.getByText(/which is off for this ledger/)).toBeInTheDocument());
        vi.mocked(apiModule.fetchBenchmarks).mockResolvedValue([]);

        await user.click(await screen.findByRole('button', { name: 'Remove SWF' }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove' }));

        await waitFor(() => expect(remove).toHaveBeenCalledWith(LEDGER, 'b-1'));
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        await screen.findByText(/No benchmarks yet/);
        expect(screen.getByRole('button', { name: 'Turn on Yahoo Finance above' })).toHaveFocus();
    });

    it('removes nothing when the confirm is cancelled', async () => {
        const remove = vi.spyOn(apiModule, 'removeBenchmark').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: 'Remove SBF' }));
        const dialog = await screen.findByRole('dialog');
        await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect(remove).not.toHaveBeenCalled();
    });

    it('says a remove the server refused', async () => {
        vi.spyOn(apiModule, 'removeBenchmark').mockRejectedValue(
            new apiModule.ApiError(422, 'That benchmark is not one of this ledger\'s.', 'benchmark-not-found'),
        );
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: 'Remove SWF' }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove' }));
        expect(await screen.findByText('That benchmark is not one of this ledger\'s.')).toBeInTheDocument();
    });

    it('lets an editor add and remove too', async () => {
        renderPanel({ role: 'editor' });
        expect(await screen.findByRole('button', { name: 'Remove SWF' })).toBeInTheDocument();
        expect(screen.getByLabelText('Add a benchmark')).toBeInTheDocument();
    });
});

describe('Quotes: benchmarks, as a viewer', () => {
    it('shows the list with no Remove and no way to add', async () => {
        renderPanel({ role: 'viewer' });
        // Settled: the viewer's list, with its rows, is drawn.
        const rows = within(await list()).getAllByRole('listitem');
        expect(rows[0]).toHaveTextContent('Sample World Fund');
        expect(screen.queryByRole('button', { name: /^Remove/ })).toBeNull();
        expect(screen.queryByLabelText('Add a benchmark')).toBeNull();
        expect(screen.queryByRole('button', { name: 'Add' })).toBeNull();
    });

    it('says there are none without inviting an add', async () => {
        renderPanel({ role: 'viewer', benchmarks: [] });
        const empty = await screen.findByText(/^No benchmarks yet\./);
        expect(empty).toHaveTextContent(/^No benchmarks yet\.$/);
    });
});
