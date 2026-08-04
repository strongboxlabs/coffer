import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { LedgerDetailPage } from './LedgerDetailPage';
import { ApiError } from '@/lib/api';
import * as apiModule from '@/lib/api';
import type { LedgerOverview, LedgerSummary } from '@/lib/types';

// Smoke tests for the per-ledger Overview (ADR-0056 slice 1).
//
// Locked-down behaviour:
//   * pending state renders a loading message
//   * the net-worth strip + account rows render from the overview aggregate
//   * each account row links to its register (the Overview absorbs the old
//     Hub's navigation)
//   * API failure surfaces an alert with the ApiError detail
//   * the header reads the ledger name from the cached ['ledgers'] query

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000100';

function overview(partial: Partial<LedgerOverview> = {}): LedgerOverview {
    return {
        netWorth: 1000,
        totalAssets: 1000,
        totalLiabilities: 0,
        investmentsValue: 0,
        currencyCode: 'USD',
        mixedCurrency: false,
        accountGroups: [
            {
                accountType: 'bank',
                subtotal: 1000,
                accounts: [
                    {
                        id: ACCOUNT_ID,
                        name: 'Checking',
                        accountType: 'bank',
                        currencyCode: 'USD',
                        balance: 1000,
                    },
                ],
            },
        ],
        portfolio: { value: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0 },
        ...partial,
    };
}

function stubQueries() {
    vi.spyOn(apiModule, 'fetchUpcomingReminders').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([]);
    // Unconfigured layout → canonical default (all widgets visible).
    vi.spyOn(apiModule, 'fetchDashboardPrefs').mockResolvedValue({ widgets: [] });
}

function renderDetail(opts: { cachedLedgers?: LedgerSummary[] } = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    if (opts.cachedLedgers) {
        queryClient.setQueryData(['ledgers'], opts.cachedLedgers);
    }

    const root = createRootRoute();
    const detailRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: LedgerDetailPage,
    });
    const stub = (path: string) =>
        createRoute({
            getParentRoute: () => root,
            path,
            component: () => <main>stub</main>,
        });
    const router = createRouter({
        routeTree: root.addChildren([
            detailRoute,
            stub('/'),
            stub('/ledgers/$ledgerId/accounts/$accountId'),
            stub('/ledgers/$ledgerId/accounts'),
            stub('/ledgers/$ledgerId/securities'),
            stub('/ledgers/$ledgerId/reminders'),
            stub('/ledgers/$ledgerId/settings'),
        ]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}`] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('LedgerDetailPage (Overview)', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        stubQueries();
    });

    it('renders the ledger name from the cached landing query', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOverview').mockResolvedValue(overview());
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'owner' },
        ]);

        renderDetail({
            cachedLedgers: [{ id: LEDGER_ID, name: 'Personal', role: 'owner' }],
        });

        expect(
            await screen.findByRole('heading', { level: 1, name: /personal/i }),
        ).toBeInTheDocument();
        // ADR-0090: no "All ledgers" root crumb. The breadcrumb states where you
        // are; ledger management is reached from "Manage ledgers…" in the ledger
        // dropdown, not from a crumb pressed into service as navigation.
        expect(
            screen.queryByRole('link', { name: /all ledgers/i }),
        ).not.toBeInTheDocument();
    });

    it('renders the net-worth strip and account rows linking to the register', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOverview').mockResolvedValue(overview());
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);

        renderDetail();

        // Net-worth KPI label + the account row.
        expect(await screen.findByText(/net worth/i)).toBeInTheDocument();
        const checkingLink = await screen.findByRole('link', { name: /checking/i });
        expect(checkingLink).toHaveAttribute(
            'href',
            `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}`,
        );
    });

    it('renders the empty-state copy when the ledger has no accounts', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOverview').mockResolvedValue(
            overview({ accountGroups: [] }),
        );
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);

        renderDetail();

        expect(await screen.findByText(/no accounts yet/i)).toBeInTheDocument();
    });

    it('surfaces the ApiError detail when the overview query fails', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOverview').mockRejectedValue(
            new ApiError(422, 'Ledger not found or not visible to this user.', 'ledger-not-visible'),
        );
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);

        renderDetail();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/ledger not found or not visible/i);
    });
});
