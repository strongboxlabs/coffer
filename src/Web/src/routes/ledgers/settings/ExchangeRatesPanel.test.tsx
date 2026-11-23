import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import * as apiModule from '@/lib/api';
import { NO_RATE_LABEL } from '@/lib/conversion';
import type { FxPair, FxRate, FxRatePage, FxRatesView, LedgerSummary } from '@/lib/types';

import { ExchangeRatesPanel } from './ExchangeRatesPanel';

// Settings → Exchange rates (ADR-0114 D9, D10): the pairs a ledger needs, how far
// each is covered, each pair's stored rates newest first, and a typed rate for a day
// with none. Every ledger, rate and day here is invented.

const LEDGER = 'ledger-1';

const CAD_USD: FxPair = {
    fromCurrency: 'CAD', toCurrency: 'USD', neededFor: ['net-worth'],
    neededFrom: '2026-03-03', fetchedFromDate: '2026-03-03', fetchedThroughDate: '2026-11-10',
    latestManualDate: '2026-11-07', coveredThroughDate: '2026-11-10', rateCount: 3,
    firstRateDate: '2026-03-03', missingDays: [],
    neededBy: [{ neededFor: 'net-worth', id: 'acct-b', name: 'Brokerage B', symbol: null, accountId: null, accountName: null }],
};
const EUR_CAD: FxPair = {
    fromCurrency: 'EUR', toCurrency: 'CAD', neededFor: ['position'],
    neededFrom: '2026-09-25', fetchedFromDate: null, fetchedThroughDate: null,
    latestManualDate: '2026-10-30', coveredThroughDate: '2026-10-30', rateCount: 1,
    // Before the first entry, and after it from the day coverage ends through today.
    firstRateDate: '2026-10-30',
    missingDays: [
        { fromDate: '2026-09-25', throughDate: '2026-10-29' },
        { fromDate: '2026-10-31', throughDate: '2026-11-12' },
    ],
    neededBy: [{ neededFor: 'position', id: 'sec-eueq', name: 'Euro Equity Fund', symbol: 'EUEQ', accountId: 'acct-b', accountName: 'Brokerage B' }],
};
const GBP_USD: FxPair = {
    fromCurrency: 'GBP', toCurrency: 'USD', neededFor: ['net-worth', 'benchmark'],
    neededFrom: null, fetchedFromDate: null, fetchedThroughDate: null,
    latestManualDate: null, coveredThroughDate: null, rateCount: 0,
    firstRateDate: null, missingDays: [{ fromDate: '2026-11-12', throughDate: '2026-11-12' }],
    neededBy: [
        { neededFor: 'net-worth', id: 'acct-s', name: 'Savings', symbol: null, accountId: null, accountName: null },
        { neededFor: 'benchmark', id: 'bm-1', name: 'Global Fund', symbol: 'GLBF', accountId: null, accountName: null },
    ],
};

const rate = (date: string, value: number, source: FxRate['source'] = 'frankfurter'): FxRate =>
    ({ date, rate: value, source, upstream: source === 'frankfurter' ? 'SRC' : null, enteredByUserId: null });

/** Each pair's stored rates, newest first. */
const STORED: Record<string, FxRate[]> = {
    'CAD-USD': [rate('2026-11-09', 0.72), rate('2026-11-07', 0.7185, 'manual'), rate('2026-11-06', 0.719)],
    'EUR-CAD': [rate('2026-10-30', 1.465, 'manual')],
    'GBP-USD': [],
};

/** The server's paging: rates before `before`, newest first, `limit` of them. */
function page(from: string, to: string, opts: { before?: string; limit?: number } = {}): FxRatePage {
    const all = (STORED[`${from}-${to}`] ?? []).filter((r) => opts.before === undefined || r.date < opts.before);
    const take = opts.limit ?? 100;
    const rates = all.slice(0, take);
    return { fromCurrency: from, toCurrency: to, rates, nextBefore: all.length > take ? rates[rates.length - 1]!.date : null };
}

function renderPanel({ role = 'owner', view, enabledProviders = [] }: {
    role?: 'owner' | 'editor' | 'viewer';
    view?: FxRatesView | Promise<FxRatesView>;
    /** The reader's opt-in providers; none, as a ledger starts (ADR-0114 D8). */
    enabledProviders?: string[];
} = {}) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const ledgers: LedgerSummary[] = [{ id: LEDGER, name: 'Sample Ledger', role, currencyCode: 'USD' }];
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    vi.spyOn(apiModule, 'fetchQuotesPrefs').mockResolvedValue({ enabledProviders });
    const read = vi.spyOn(apiModule, 'fetchFxRatesView');
    const shown = view ?? { reportingCurrency: 'USD', pairs: [CAD_USD, EUR_CAD, GBP_USD] };
    if (shown instanceof Promise) read.mockReturnValue(shown);
    else read.mockResolvedValue(shown);
    const pages = vi.spyOn(apiModule, 'fetchFxRatePage')
        .mockImplementation(async (_l, from, to, opts) => page(from, to, opts));

    const root = createRootRoute();
    const settings = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <ExchangeRatesPanel ledgerId={LEDGER} />,
    });
    const tab = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/settings',
        component: () => <main>settings</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([settings, tab]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    return { pages, invalidated };
}

/** The pairs table's row for a pair, once drawn. */
async function row(name: string) {
    const cell = await screen.findByRole('cell', { name });
    return cell.closest('tr')!;
}

beforeEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
    // "Today" on the server (UTC) is Nov 12, 2026, so Nov 13 has not begun.
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-12T15:00:00Z') });
});
afterEach(() => {
    vi.useRealTimers();
});

describe('Exchange rates: the pairs', () => {
    it('says Loading… while the view is read', async () => {
        renderPanel({ view: new Promise(() => {}) });
        expect(await screen.findByText('Loading…')).toBeInTheDocument();
    });

    it('says the view could not be read', async () => {
        renderPanel({ view: Promise.reject(new apiModule.ApiError(500, 'Server error.')) });
        expect(await screen.findByRole('alert')).toHaveTextContent('Server error.');
    });

    it('says a ledger in one currency needs no rate', async () => {
        renderPanel({ view: { reportingCurrency: 'USD', pairs: [] } });
        expect(await screen.findByText(/Every account and price is in USD, so nothing converts/)).toBeInTheDocument();
        expect(screen.queryByRole('table')).toBeNull();
    });

    it('names the reporting currency and, for each pair, what needs it', async () => {
        renderPanel();
        expect(await screen.findByText('USD', { selector: 'span.font-mono' })).toBeInTheDocument();
        const cad = await row('CAD → USD');
        expect(within(cad).getByRole('link', { name: 'Brokerage B (CAD)' })).toHaveAttribute('href', '/ledgers/ledger-1/accounts/acct-b');
        const eur = await row('EUR → CAD');
        expect(within(eur).getByRole('link', { name: 'Euro Equity Fund (EUEQ)' })).toHaveAttribute('href', '/ledgers/ledger-1/securities/sec-eueq');
        expect(eur).toHaveTextContent('priced in EUR, held in Brokerage B (CAD)');
        const gbp = await row('GBP → USD');
        expect(within(gbp).getByRole('link', { name: 'Savings (GBP)' })).toBeInTheDocument();
        expect(within(gbp).getByRole('link', { name: 'Global Fund (GLBF)' })).toHaveAttribute('href', '/ledgers/ledger-1/settings?tab=quotes');
        expect(gbp).toHaveTextContent('benchmark, drawn against USD returns');
    });

    it('lists the days each pair has no rate for, and says how many pairs have some', async () => {
        renderPanel();
        const cad = await row('CAD → USD');
        expect(cad.cells[5]).toHaveTextContent(/^None$/);
        const eur = await row('EUR → CAD');
        // Sep 25 to Oct 29 is 35 days, Oct 31 to Nov 12 is 13.
        expect(eur.cells[5]).toHaveTextContent('48 days');
        expect(eur.cells[5]).toHaveTextContent('Sep 25 to Oct 29, 2026');
        expect(eur.cells[5]).toHaveTextContent('Oct 31 to Nov 12, 2026');
        const gbp = await row('GBP → USD');
        expect(gbp.cells[5]).toHaveTextContent('Today');
        expect(gbp.cells[5]).toHaveTextContent('only the latest rate is needed');
        expect(screen.getByRole('status')).toHaveTextContent('2 of 3 pairs have days with no rate.');
    });

    it('says a pair with no rate stored misses every day from the first one needed', async () => {
        renderPanel({ view: { reportingCurrency: 'USD', pairs: [
            { ...GBP_USD, neededFrom: '2025-03-03', missingDays: [{ fromDate: '2025-03-03', throughDate: '2026-11-12' }] },
        ] } });
        const gbp = await row('GBP → USD');
        expect(gbp.cells[5]).toHaveTextContent('Every day');
        expect(gbp.cells[5]).toHaveTextContent('from Mar 3, 2025');
        expect(screen.getByRole('status')).toHaveTextContent('The pair has days with no rate.');
    });

    // ADR-0114 D9: a missing day reads as the mark every screen prints. What can fill it
    // depends on who reads: a typed rate is an owner's or an editor's (ADR-0114 D10), and a
    // fetch comes only while the reader's Frankfurter switch is on (ADR-0114 D8).
    it.each([
        ['owner', ['frankfurter'], 'Add a rate for a missing day, or wait for the next fetch.'],
        ['owner', [], 'Add a rate for a missing day. Frankfurter is off for you on this ledger, so nothing fetches them.'],
        ['viewer', ['frankfurter'], 'Wait for the next fetch, or ask an owner or editor to add a rate for a missing day.'],
        ['viewer', [], 'An owner or editor can add a rate for a missing day. Frankfurter is off for you on this ledger, so nothing fetches them.'],
    ] as const)('the summary tells a reader (%s, providers %j) what the days read and what can fill them', async (role, enabledProviders, fill) => {
        renderPanel({ role, enabledProviders: [...enabledProviders] });
        const words = `On those days a total that combines the pair's currencies reads "${NO_RATE_LABEL}" instead of a figure. ${fill}`;
        // The whole line, exactly: the frame before the role and the switch are read
        // says less than any of these, and a clause the reader cannot act on fails it.
        await waitFor(() => {
            const line = screen.getByRole('status').querySelectorAll('p')[1]!;
            expect(line.textContent!.replace(/\s+/g, ' ').trim()).toBe(words);
        });
    });

    it('draws no summary when every pair is covered', async () => {
        renderPanel({ view: { reportingCurrency: 'USD', pairs: [CAD_USD] } });
        const cad = await row('CAD → USD');
        expect(within(cad).getByText('None')).toBeInTheDocument();
        expect(screen.queryByRole('status')).toBeNull();
    });

    it('says where each pair\'s rates come from and how far it is covered', async () => {
        renderPanel();
        const cad = await row('CAD → USD');
        expect(cad).toHaveTextContent('Frankfurter');
        expect(within(cad).getByText('Manual')).toBeInTheDocument();
        expect(cad).toHaveTextContent('Nov 10, 2026');
        expect(cad).toHaveTextContent('fetched through');

        const eur = await row('EUR → CAD');
        expect(eur).not.toHaveTextContent('Frankfurter');
        expect(within(eur).getByText('Manual')).toBeInTheDocument();
        expect(eur).toHaveTextContent('Oct 30, 2026');
        expect(eur).toHaveTextContent('latest entry');

        const gbp = await row('GBP → USD');
        expect(gbp).toHaveTextContent('Not fetched yet');
        expect(gbp).toHaveTextContent('—');
    });

    it('shows each pair\'s newest stored rate, or that none is stored', async () => {
        renderPanel();
        const cad = await row('CAD → USD');
        await waitFor(() => expect(cad).toHaveTextContent('1 CAD = 0.7200 USD'));
        expect(cad).toHaveTextContent('Nov 9, 2026');
        const gbp = await row('GBP → USD');
        await waitFor(() => expect(gbp).toHaveTextContent('No rate stored'));
    });
});

describe('Exchange rates: one pair\'s stored rates', () => {
    it('opens a pair\'s rates newest first, manual ones marked, with how far it is covered', async () => {
        renderPanel();
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.click(await screen.findByRole('button', { name: 'Show CAD to USD rates' }));

        const section = (await screen.findByRole('button', { name: /CAD → USD/, expanded: true })).closest('div.bg-surface')!;
        const body = within(section as HTMLElement);
        expect(await body.findByText('1 CAD = 0.7200 USD')).toBeInTheDocument();
        const dates = body.getAllByRole('row').slice(1).map((r) => r.querySelector('td')!.textContent);
        expect(dates).toEqual(['Nov 9, 2026', 'Nov 7, 2026', 'Nov 6, 2026']);
        expect(body.getAllByRole('row')[2]).toHaveTextContent('Manual');
        expect(section).toHaveTextContent('Fetched from Frankfurter for Mar 3, 2026 through Nov 10, 2026.');
        expect(section).toHaveTextContent('Latest rate entered by hand: Nov 7, 2026.');
        expect(section).toHaveTextContent('Needed from Mar 3, 2026.');
        expect(section).toHaveTextContent('A stored rate is never changed, whatever its source.');
        // No stored rate has an edit control.
        expect(body.queryByRole('button', { name: /edit/i })).toBeNull();
    });

    it('pages older rates in with Load more', async () => {
        STORED['CAD-USD'] = Array.from({ length: 60 }, (_, i) =>
            rate(`2026-${String(10 - Math.floor(i / 28)).padStart(2, '0')}-${String(28 - (i % 28)).padStart(2, '0')}`, 0.7 + i / 1000));
        try {
            const { pages } = renderPanel();
            const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
            await user.click(await screen.findByRole('button', { name: 'Show CAD to USD rates' }));
            const more = await screen.findByRole('button', { name: 'Load more' });
            await user.click(more);
            await waitFor(() => expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull());
            const lastPage = pages.mock.calls.at(-1)!;
            expect(lastPage[3]).toEqual(expect.objectContaining({ before: expect.any(String), limit: 50 }));
        } finally {
            STORED['CAD-USD'] = [rate('2026-11-09', 0.72), rate('2026-11-07', 0.7185, 'manual'), rate('2026-11-06', 0.719)];
        }
    });

    it('says a pair with nothing stored has no rate yet', async () => {
        renderPanel();
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.click(await screen.findByRole('button', { name: 'Show GBP to USD rates' }));
        expect(await screen.findByText('No rate is stored for this pair yet.')).toBeInTheDocument();
        expect(screen.getByText(/Only the latest rate is needed\./)).toBeInTheDocument();
    });
});

describe('Exchange rates: by role', () => {
    it.each(['owner', 'editor'] as const)('offers an %s + Add rate', async (role) => {
        renderPanel({ role });
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.click(await screen.findByRole('button', { name: 'Show EUR to CAD rates' }));
        expect(await screen.findByRole('button', { name: 'Add a EUR to CAD rate' })).toBeInTheDocument();
    });

    it('shows a viewer the same rates with no way to add one', async () => {
        renderPanel({ role: 'viewer' });
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.click(await screen.findByRole('button', { name: 'Show EUR to CAD rates' }));
        // Settled: the pair's own rate is drawn in its open section.
        expect(await screen.findByText('1 EUR = 1.4650 CAD', { selector: 'td' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /Add a .* rate/ })).toBeNull();
        expect(screen.queryByText('+ Add rate')).toBeNull();
    });
});

describe('Exchange rates: Add rate', () => {
    async function openAdd(pair = 'EUR to CAD') {
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.click(await screen.findByRole('button', { name: `Show ${pair} rates` }));
        await user.click(await screen.findByRole('button', { name: `Add a ${pair} rate` }));
        const dialog = await screen.findByRole('dialog', { name: 'Add rate' });
        return { user, dialog: within(dialog), element: dialog };
    }

    it('says a free day is free, and saves the rate typed as a manual one', async () => {
        const add = vi.spyOn(apiModule, 'addFxRate').mockResolvedValue(rate('2026-11-06', 1.47, 'manual'));
        const { invalidated } = renderPanel();
        const { user, dialog, element } = await openAdd();
        expect(element).toHaveTextContent('EUR → CAD');
        expect(element).toHaveTextContent('Saved as Manual. Once saved, a rate is never changed');

        await user.type(dialog.getByLabelText('Date *'), '2026-11-06');
        expect(await dialog.findByText('No rate is stored for Nov 6, 2026.')).toBeInTheDocument();
        await user.type(dialog.getByLabelText('Rate *'), '1.47');
        await user.click(dialog.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(add).toHaveBeenCalledWith(LEDGER, {
            fromCurrency: 'EUR', toCurrency: 'CAD', date: '2026-11-06', rate: 1.47,
        }));
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        // The rate can move any converted figure, and the pair's coverage.
        const keys = invalidated.mock.calls.map((c) => c[0]?.queryKey);
        expect(keys).toContainEqual(['fx-rates', LEDGER]);
        expect(keys).toContainEqual(['overview', LEDGER]);
        expect(keys).toContainEqual(['accounts', LEDGER]);
    });

    // ADR-0023 §L: the day and rate being saved refuse input until the save answers.
    it('holds the day and the rate while Save is pending', async () => {
        vi.spyOn(apiModule, 'addFxRate').mockReturnValue(new Promise(() => {}));
        renderPanel();
        const { user, dialog } = await openAdd();
        await user.type(dialog.getByLabelText('Date *'), '2026-11-06');
        await dialog.findByText('No rate is stored for Nov 6, 2026.');
        await user.type(dialog.getByLabelText('Rate *'), '1.47');
        await user.click(dialog.getByRole('button', { name: 'Save' }));

        const date = dialog.getByLabelText('Date *');
        const rateField = dialog.getByLabelText('Rate *');
        await waitFor(() => expect(date).toHaveAttribute('readonly'));
        expect(rateField).toHaveAttribute('readonly');
        expect(date).toHaveAttribute('aria-disabled', 'true');
        await user.type(rateField, '9');
        expect(rateField).toHaveValue(1.47);
    });

    it('names the rate a taken day already holds, and keeps Save off', async () => {
        const add = vi.spyOn(apiModule, 'addFxRate');
        renderPanel();
        const { user, dialog } = await openAdd('CAD to USD');
        await user.type(dialog.getByLabelText('Date *'), '2026-11-07');
        await user.type(dialog.getByLabelText('Rate *'), '0.718');

        expect(await dialog.findByRole('alert')).toHaveTextContent(
            'Nov 7, 2026 already has a rate: 1 CAD = 0.7185 USD, from an entry by hand. A stored rate is never changed or replaced, so pick a day with no rate.',
        );
        expect(dialog.getByLabelText('Date *')).toHaveAttribute('aria-invalid', 'true');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled();
        expect(add).not.toHaveBeenCalled();
    });

    it('refuses a day that has not begun on the server before it is sent', async () => {
        const add = vi.spyOn(apiModule, 'addFxRate');
        renderPanel();
        const { user, dialog } = await openAdd();
        expect(dialog.getByLabelText('Date *')).toHaveAttribute('max', '2026-11-12');
        await user.type(dialog.getByLabelText('Date *'), '2026-11-13');
        await user.type(dialog.getByLabelText('Rate *'), '1.47');
        expect(await dialog.findByRole('alert')).toHaveTextContent('Nov 13, 2026 has not begun yet in UTC');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled();
        expect(add).not.toHaveBeenCalled();
    });

    it('keeps Save off until a positive rate is typed', async () => {
        renderPanel();
        const { user, dialog } = await openAdd();
        await user.type(dialog.getByLabelText('Date *'), '2026-11-06');
        await dialog.findByText('No rate is stored for Nov 6, 2026.');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled();
        await user.type(dialog.getByLabelText('Rate *'), '0');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled();
        await user.clear(dialog.getByLabelText('Rate *'));
        await user.type(dialog.getByLabelText('Rate *'), '1.2');
        expect(dialog.getByRole('button', { name: 'Save' })).toBeEnabled();
    });

    it.each([
        ['fx-rate-day-taken', 'Nov 6, 2026 already has a EUR to CAD rate. A stored rate is never changed or replaced, so pick a day with no rate.'],
        ['fx-rate-date-in-future', 'Nov 6, 2026 has not begun yet in UTC, the server\'s day. A rate can be entered only for today or an earlier day.'],
        ['fx-rate-date-invalid', 'Enter the day the rate is for as a date.'],
        ['fx-rate-invalid', 'A rate is a positive number: how much one EUR is worth in CAD. This one is zero, negative or too large to store.'],
        ['fx-pair-not-needed', 'This ledger no longer converts EUR into CAD, so it takes no rate for it. Close this and the list shows the pairs it needs.'],
        ['fx-rate-currency-invalid', 'A rate converts one 3-letter currency into a different one.'],
        ['something-else', 'The server said no.'],
    ] as const)('says the server\'s %s refusal in words', async (code, words) => {
        vi.spyOn(apiModule, 'addFxRate').mockRejectedValue(new apiModule.ApiError(422, 'The server said no.', code));
        renderPanel();
        const { user, dialog } = await openAdd();
        await user.type(dialog.getByLabelText('Date *'), '2026-11-06');
        await dialog.findByText('No rate is stored for Nov 6, 2026.');
        await user.type(dialog.getByLabelText('Rate *'), '1.47');
        await user.click(dialog.getByRole('button', { name: 'Save' }));
        expect(await dialog.findByRole('alert')).toHaveTextContent(words);
        // The dialog stays, with what was typed.
        expect(dialog.getByLabelText('Rate *')).toHaveValue(1.47);
    });
});
