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

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatCurrency, formatPrice } from '@/lib/money';
import type { QuoteRunOutcome, SecuritySummary } from '@/lib/types';

import { SecuritiesCatalogPage } from './SecuritiesCatalogPage';

/** The securities catalog against the wire, answered by the stub. */

const LEDGER = '00000000-0000-0000-0000-0000000000aa';

const OUTCOME: QuoteRunOutcome = {
    providerKeys: ['provider-a'], pricesInserted: 1, pricesUpdated: 0, securitiesUnresolved: [], errors: [],
    currenciesConfirmed: 0, rowsRestamped: 0, ratesInserted: 0, benchmarkDaysInserted: 0,
};

let stub: FetchStub;

function renderPage() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    const root = createRootRoute();
    const page = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/securities',
        component: SecuritiesCatalogPage,
    });
    const ledger = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const detail = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/securities/$securityId',
        component: () => <main>detail</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([page, ledger, detail]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/securities`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return { keys: () => invalidated.mock.calls.map((c) => c[0]?.queryKey) };
}

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

const security = (over: Partial<SecuritySummary> & { id: string; name: string }): SecuritySummary => ({
    ticker: null, cusip: null, assetClass: null, exchange: null, isActive: true,
    totalQuantity: 0, latestPrice: null, latestPriceAsOf: null, priceCurrencyCode: 'USD', ...over,
});

describe('SecuritiesCatalogPage: figures', () => {
    it('prints each row\'s price and amount in that security\'s price currency', async () => {
        stub
            .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role: 'owner' }])
            .on('GET', /\/securities$/, [
                security({ id: 's1', name: 'Security A', totalQuantity: 10, latestPrice: 12.5, priceCurrencyCode: 'EUR' }),
                security({ id: 's2', name: 'Security B', totalQuantity: 4, latestPrice: 3, priceCurrencyCode: 'GBP' }),
            ]);
        renderPage();

        const a = (await screen.findByText('Security A')).closest('tr')!;
        expect(within(a).getByText(formatPrice(12.5, 'EUR'))).toBeInTheDocument();
        expect(within(a).getByText(formatCurrency(125, 'EUR'))).toBeInTheDocument();
        const b = screen.getByText('Security B').closest('tr')!;
        expect(within(b).getByText(formatPrice(3, 'GBP'))).toBeInTheDocument();
        expect(within(b).getByText(formatCurrency(12, 'GBP'))).toBeInTheDocument();
        // Neither security is priced in dollars, so no figure wears the dollar sign.
        expect(screen.getByRole('table').textContent).not.toContain('$');
    });
});

describe('SecuritiesCatalogPage: opening a security', () => {
    it('opens a security from the keyboard through its name, as a click on the row does', async () => {
        stub
            .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role: 'owner' }])
            .on('GET', /\/securities$/, [security({ id: 's1', name: 'Security A' })]);
        renderPage();
        const user = userEvent.setup();

        const link = await screen.findByRole('link', { name: 'Security A' });
        expect(link).toHaveAttribute('href', `/ledgers/${LEDGER}/securities/s1`);
        link.focus();
        await user.keyboard('{Enter}');

        expect(await screen.findByText('detail')).toBeInTheDocument();
    });
});

describe('SecuritiesCatalogPage: Refresh prices', () => {
    it('refreshes the catalog, the accounts, the home screen\'s reads and the converted reads', async () => {
        stub
            .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role: 'owner' }])
            .on('GET', /\/securities$/, [])
            .on('POST', /\/quotes\/refresh$/, OUTCOME);
        const { keys } = renderPage();

        await userEvent.click(await screen.findByRole('button', { name: 'Refresh prices' }));

        await waitFor(() => expect(stub.callsTo('POST', /\/quotes\/refresh$/)).toHaveLength(1));
        await waitFor(() => expect(keys()).toContainEqual(['securities', LEDGER]));
        for (const key of [
            ['accounts', LEDGER], ['home', LEDGER], ['overview', LEDGER], ['budget-progress', LEDGER],
            // The run fetches rates and benchmark history too (ADR-0114 D11), which the
            // converted reads, the rates view and the benchmark list's availability read.
            ['fx-rates', LEDGER], ['benchmarks', LEDGER], ['returns-series', LEDGER],
        ]) {
            expect(keys()).toContainEqual(key);
        }
        expect(stub.unrouted).toEqual([]);
    });

    it('says what a run stored when it stored rates and benchmark days but no prices (ADR-0114 D11)', async () => {
        stub
            .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role: 'owner' }])
            .on('GET', /\/securities$/, [])
            .on('POST', /\/quotes\/refresh$/, { ...OUTCOME, pricesInserted: 0, ratesInserted: 12, benchmarkDaysInserted: 1 });
        renderPage();

        await userEvent.click(await screen.findByRole('button', { name: 'Refresh prices' }));

        expect(await screen.findByText('No prices updated. Stored 12 rate days · 1 benchmark day.')).toBeInTheDocument();
        expect(screen.queryByText('Prices are already up to date.')).toBeNull();
    });
});

describe('SecuritiesCatalogPage: by role (ADR-0023 §L)', () => {
    const serveAs = (role: string, rows: SecuritySummary[]) => stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Ledger A', role }])
        .on('GET', /\/securities$/, rows);

    it('gives a viewer the catalog and no refresh or new security', async () => {
        serveAs('viewer', [security({ id: 's1', name: 'Security A' })]);
        renderPage();
        await screen.findByText('Ledger A');
        expect(await screen.findByText('Security A')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Refresh prices' })).toBeNull();
        expect(screen.queryByRole('button', { name: '+ Add security' })).toBeNull();
    });

    it('tells a viewer of an empty catalog nothing to add', async () => {
        serveAs('viewer', []);
        renderPage();
        await screen.findByText('Ledger A');
        expect(await screen.findByText('No securities yet.')).toBeInTheDocument();
        expect(screen.queryByText('Add one manually or import from Moneydance.')).toBeNull();
    });

    it.each(['owner', 'editor'])('keeps refresh and new security for an %s', async (role) => {
        serveAs(role, [security({ id: 's1', name: 'Security A' })]);
        renderPage();
        expect(await screen.findByText('Security A')).toBeInTheDocument();
        expect(await screen.findByRole('button', { name: 'Refresh prices' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: '+ Add security' })).toBeInTheDocument();
    });
});
