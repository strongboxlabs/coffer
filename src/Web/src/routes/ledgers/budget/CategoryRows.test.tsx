import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { CategoryRows } from './CategoryRows';
import * as apiModule from '@/lib/api';
import type { BudgetCategoryRow, BudgetTransactionPage } from '@/lib/types';

// A budget row is a ROLLUP: its figure already contains every descendant. So
// the register it opens has to be a rollup too — scoped to the category AND its
// sub-categories — or the number you clicked is not the number you land on.
// That is one `search` prop on one Link, which is exactly the kind of thing
// that gets dropped in a refactor and noticed months later, so it is pinned
// here rather than left to a reader's eye.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const PARENT_ID = '00000000-0000-0000-0000-000000000200';
const MONTH = '2026-04';

const CHILD_ID = '00000000-0000-0000-0000-000000000201';

const ROW: BudgetCategoryRow = {
    categoryId: PARENT_ID,
    name: 'Food',
    parentId: null,
    actual: 120,
    typical: 100,
    target: null,
    mark: 100,
};
// A child, so the parent is a ROLLUP — which is the only shape where any of
// this matters: the per-line sub-category is shown only when there are
// sub-categories to distinguish.
const CHILD: BudgetCategoryRow = {
    categoryId: CHILD_ID,
    name: 'Groceries',
    parentId: PARENT_ID,
    actual: 120,
    typical: 100,
    target: null,
    mark: 100,
};

const PAGE: BudgetTransactionPage = {
    lines: [
        {
            headerId: '00000000-0000-0000-0000-0000000000a1',
            postedAt: '2026-04-03T12:00:00Z',
            payee: 'Market',
            accountId: '00000000-0000-0000-0000-000000000100',
            accountName: 'Checking',
            counterpartyAccountId: '00000000-0000-0000-0000-000000000201',
            counterpartyAccountName: 'Food/Groceries',
            amount: -120,
            memo: null,
            status: 'cleared',
        },
    ],
    limit: 200,
    offset: 0,
    hasMore: false,
};

function renderRows() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    // The Link needs a matching route or TanStack throws.
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <main>register</main>,
    });
    const budgetRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/budget',
        component: () => (
            <CategoryRows
                rows={[ROW, CHILD]}
                accountPaths={new Map([
                    [PARENT_ID, 'Food'],
                    [CHILD_ID, 'Food/Groceries'],
                ])}
                currency="USD"
                ledgerId={LEDGER_ID}
                monthKey={MONTH}
                expandedIds={new Set()}
                onToggle={() => {}}
                // Open on arrival — `detailId` is a prop, so the transactions
                // block renders without driving the row's gestures here.
                detailId={PARENT_ID}
                onToggleDetail={() => {}}
                onSetTarget={() => {}}
            />
        ),
    });
    const router = createRouter({
        routeTree: root.addChildren([budgetRoute, registerRoute]),
        history: createMemoryHistory({
            initialEntries: [`/ledgers/${LEDGER_ID}/budget`],
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

describe('CategoryRows — the register link', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('opens the register with the sub-category scope ON', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(PAGE);

        renderRows();

        const link = await screen.findByRole('link', { name: /open the register/i });
        const href = link.getAttribute('href') ?? '';
        expect(href).toContain(`/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}`);
        // The whole point: a rollup row opens a rollup register.
        expect(href).toMatch(/subcategories=true/);
    });

    it('asks the server for this category and month', async () => {
        const spy = vi
            .spyOn(apiModule, 'fetchBudgetTransactions')
            .mockResolvedValue(PAGE);

        renderRows();

        // Anchor on the rendered line before asserting the call, so a pending
        // frame cannot pass this by accident.
        expect(await screen.findByText('Market')).toBeInTheDocument();
        expect(spy).toHaveBeenCalledWith(LEDGER_ID, PARENT_ID, MONTH, 200);
    });

    it('names the sub-category each line is filed under', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(PAGE);

        renderRows();

        // The list spans the SUBTREE, so a line has to say which descendant it
        // belongs to — by full path, like every category shown outside a tree.
        // Matched loosely: the label is rendered with a leading separator.
        const row = (await screen.findByText('Market')).closest('tr');
        expect(row).not.toBeNull();
        expect(
            within(row!).getByText((_, el) => el?.textContent === '· Food/Groceries'),
        ).toBeInTheDocument();
    });
});
