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

import { RegisterRouter } from './RegisterRouter';
import * as apiModule from '@/lib/api';
import type { AccountSummary, LedgerSummary } from '@/lib/types';

// Smoke tests for the router. Renders are dispatched purely by
// `account.accountType` — bank/credit/cash/asset/liability/loan
// all flow to <RegisterPage>; only `investment` flips to the
// dedicated <InvestmentRegisterPage>. We assert on each branch by
// stubbing the underlying account list and looking for a body
// fingerprint unique to each page.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK_ACCOUNT_ID = '00000000-0000-0000-0000-000000000100';
const INVESTMENT_ACCOUNT_ID = '00000000-0000-0000-0000-000000000200';

const TEST_LEDGER: LedgerSummary = {
    id: LEDGER_ID,
    name: 'Personal',
    role: 'owner',
};

function makeAccount(overrides: Partial<AccountSummary> & { id: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        name: 'Account',
        accountType: 'bank',
        categoryKind: null,
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...overrides,
    };
}

function renderRouter(accountId: string, accounts: AccountSummary[]) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    queryClient.setQueryData(['ledgers'], [TEST_LEDGER]);
    queryClient.setQueryData(['accounts', LEDGER_ID], accounts);

    const root = createRootRoute();
    const route = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: RegisterRouter,
    });
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <main>landing</main>,
    });
    const detailRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>detail</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([route, landingRoute, detailRoute]),
        history: createMemoryHistory({
            initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${accountId}`],
        }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('RegisterRouter', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        // Default to empty register / empty holdings so neither page
        // throws on its first fetch.
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            accountId: INVESTMENT_ACCOUNT_ID,
            accountName: 'Brokerage',
            currencyCode: 'USD',
            summary: {
                portfolioValue: 0,
                costBasis: 0,
                unrealizedGain: 0,
                percentChange: 0,
                cashBalance: 0,
                total: 0,
            },
            positions: [],
        });
    });

    it('dispatches an investment account to the investment register page', async () => {
        const investmentAccount = makeAccount({
            id: INVESTMENT_ACCOUNT_ID,
            name: 'Brokerage',
            accountType: 'investment',
        });
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([investmentAccount]);

        renderRouter(INVESTMENT_ACCOUNT_ID, [investmentAccount]);

        // The investment register has a "Security · Shares @ Price" column;
        // the bank register does not — the reliable page fingerprint now that
        // both pages share the controls bar (+ New transaction).
        expect(
            await screen.findByRole('columnheader', { name: /Security/i }),
        ).toBeInTheDocument();
    });

    it('dispatches a bank account to the bank register page', async () => {
        const bankAccount = makeAccount({
            id: BANK_ACCOUNT_ID,
            name: 'Checking',
            accountType: 'bank',
        });
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([bankAccount]);

        renderRouter(BANK_ACCOUNT_ID, [bankAccount]);

        // Both pages share the controls bar (+ New transaction) now, so
        // disambiguate on the column headers: the bank register has a
        // "Payee · memo" column and no "Security" column.
        expect(
            await screen.findByRole('columnheader', { name: /Payee/i }),
        ).toBeInTheDocument();
        expect(
            screen.queryByRole('columnheader', { name: /Security/i }),
        ).not.toBeInTheDocument();
    });
});
