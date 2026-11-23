import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { stubFetch } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';
import type { RateUnavailable } from '@/lib/types';
import type { ReportResult } from '@/types/report';

import { ReportView } from './ReportView';

// A report whose figures convert into the ledger's reporting currency (ADR-0114 D7),
// one of which needs a missing rate (ADR-0114 D9), through the real page with the
// server mocked at fetch. Every name and figure is invented.

const LEDGER = 'ledger-1';

afterEach(() => {
    vi.unstubAllGlobals();
});

const result = (over: Partial<ReportResult>): ReportResult => ({
    rows: [], total: 0, fromUtc: '2026-02-01T00:00:00Z', toUtc: '2026-04-01T00:00:00Z',
    periodTotals: [], groupTotals: [], currencyCode: 'USD', ...over,
});

const MISSING: RateUnavailable = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-09', reason: 'rate_unavailable' };

/** Category B has a posting with no rate, so it, its month and the total read "no rate". */
const byCategory = result({
    rows: [
        { period: '2026-03', groupId: 'cat-a', groupName: 'Category A', groupPath: ['Category A'], parentId: null, amount: 70, currencyCode: 'USD' },
        { period: '2026-03', groupId: 'cat-b', groupName: 'Category B', groupPath: ['Category B'], parentId: null, amount: null, currencyCode: 'USD', rateUnavailable: [MISSING] },
    ],
    total: null,
    rateUnavailable: [MISSING],
    periodTotals: [{ period: '2026-02', amount: 0 }, { period: '2026-03', amount: null, rateUnavailable: [MISSING] }],
});

const bank = (id: string, currencyCode: string, accountType = 'bank') => ({
    id, ledgerId: LEDGER, parentId: null, name: `Account ${id}`, accountType, categoryKind: null, currencyCode,
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
});

function renderReport(search: string, summary: ReportResult, accounts = [bank('a', 'USD')]) {
    stubFetch()
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Synthetic', role: 'owner', currencyCode: 'USD' }])
        .on('GET', /\/accounts$/, accounts)
        .on('GET', /\/reports\/summary$/, summary);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
        ]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/reports/view?${search}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('a report with a missing rate', () => {
    it('keeps every figure it has and reads each one that needs the rate "no rate"', async () => {
        renderReport('groupBy=Category&bucket=Month', byCategory);

        const table = await screen.findByRole('table');
        expect(within(table).getAllByText('$70.00')).toHaveLength(2);
        const row = within(table).getByText('Category B').closest('tr') as HTMLElement;
        expect(within(row).getAllByText('no rate')).toHaveLength(2);
        const total = within(table).getByText('Total', { selector: 'td' }).closest('tr') as HTMLElement;
        const marked = within(total).getAllByText('no rate');
        // March and the window; February still prints its zero.
        expect(marked).toHaveLength(2);
        for (const m of marked) expect(m).toHaveAttribute('title', 'No CAD to USD rate for Mar 9, 2026.');
        expect(within(total).getByText('$0.00')).toBeInTheDocument();
    });

    it('draws no chart, stacked or grouped, and names the missing rate in its place', async () => {
        for (const stack of ['stacked', 'grouped']) {
            const { unmount } = renderReport(`groupBy=Category&bucket=Month&view=chart&chartStack=${stack}`, byCategory);
            expect(await screen.findByText(/^No CAD to USD rate for Mar 9, 2026\./)).toBeInTheDocument();
            expect(screen.queryByRole('img')).toBeNull();
            unmount();
        }
    });
});

describe('a report by account, the accounts in their own currencies (ADR-0114 D7)', () => {
    // Account B's row is its own euro figure; the totals combine both accounts in USD.
    const byAccount = result({
        rows: [
            { period: '2026-03', groupId: 'a', groupName: 'Account a', groupPath: ['Account a'], parentId: null, amount: 70, currencyCode: 'USD' },
            { period: '2026-03', groupId: 'b', groupName: 'Account b', groupPath: ['Account b'], parentId: null, amount: 40, currencyCode: 'EUR' },
        ],
        total: 130,
        periodTotals: [{ period: '2026-02', amount: 0 }, { period: '2026-03', amount: 130 }],
    });
    const accounts = [bank('a', 'USD'), bank('b', 'EUR')];

    it('prints each account\'s row in its own currency, and the totals in the reporting one', async () => {
        renderReport('groupBy=Account&bucket=Month', byAccount, accounts);

        const table = await screen.findByRole('table');
        const row = within(table).getByText('Account b').closest('tr') as HTMLElement;
        expect(within(row).getAllByText(formatCurrency(40, 'EUR'))).toHaveLength(2);
        expect(row).not.toHaveTextContent('$');
        const total = within(table).getByText('Total', { selector: 'td' }).closest('tr') as HTMLElement;
        expect(within(total).getAllByText('$130.00')).toHaveLength(2);
    });

    it('draws no chart, stacked, grouped or ranked, and says why in its place', async () => {
        for (const search of ['bucket=Month&chartStack=stacked', 'bucket=Month&chartStack=grouped', 'bucket=None']) {
            const { unmount } = renderReport(`groupBy=Account&view=chart&${search}`, byAccount, accounts);
            expect(await screen.findByText(
                'No chart: each account here is in its own currency and the totals are in USD, so a chart '
                + 'would draw EUR and USD on one scale. The table shows each figure in its currency.',
            )).toBeInTheDocument();
            expect(screen.queryByRole('img')).toBeNull();
            expect(screen.queryByRole('list', { name: /series/ })).toBeNull();
            unmount();
        }
    });

    it('draws the chart when every account is in the reporting currency', async () => {
        renderReport('groupBy=Account&bucket=None&view=chart', result({
            rows: [{ period: null, groupId: 'a', groupName: 'Account a', groupPath: ['Account a'], parentId: null, amount: 70, currencyCode: 'USD' }],
            total: 70,
        }));

        const list = await screen.findByRole('list', { name: /series/ });
        expect(within(list).getByText('$70.00')).toBeInTheDocument();
        expect(screen.queryByText(/^No chart:/)).toBeNull();
    });
});

describe('the conversion note (ADR-0114 D7)', () => {
    const ONE = result({
        rows: [{ period: null, groupId: 'cat-a', groupName: 'Category A', groupPath: ['Category A'], parentId: null, amount: 70, currencyCode: 'USD' }],
        total: 70,
    });

    it('says above the result that totals are converted, when an account is in another currency', async () => {
        renderReport('groupBy=Category&bucket=None', ONE, [bank('a', 'USD'), bank('b', 'CAD')]);

        const note = await screen.findByText(/^Totals are in USD, with CAD accounts converted at the rate for each figure's date \(the latest rate from today on\)\./);
        expect(within(note).getByRole('link', { name: 'Exchange rates →' })).toBeInTheDocument();
        // Above the result, not inside it.
        expect(note.closest('table')).toBeNull();
    });

    it('carries none when every account is in the reporting currency', async () => {
        renderReport('groupBy=Category&bucket=None', ONE, [bank('a', 'USD'), bank('c', 'CAD', 'category')]);

        // Settled: the table has drawn the row.
        expect(await within(await screen.findByRole('table')).findByText('Category A')).toBeInTheDocument();
        expect(screen.queryByText(/^Totals are in/)).toBeNull();
    });
});

describe('a ledger in one currency other than dollars', () => {
    it('prints the chart\'s figures in that currency', async () => {
        renderReport('groupBy=Category&bucket=None&view=chart', result({
            currencyCode: 'EUR',
            rows: [{ period: null, groupId: 'cat-a', groupName: 'Category A', groupPath: ['Category A'], parentId: null, amount: 70, currencyCode: 'EUR' }],
            total: 70,
        }));

        const list = await screen.findByRole('list', { name: /series/ });
        expect(within(list).getByText(formatCurrency(70, 'EUR'))).toBeInTheDocument();
        expect(list.textContent).not.toContain('$');
    });
});
