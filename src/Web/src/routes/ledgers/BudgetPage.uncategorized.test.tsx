import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';

import { categoryChipVariant } from '@/lib/categoryChip';

import { BudgetPage } from './BudgetPage';
import { account, CHECKING, homeStub, LEDGER } from './home/homeTestFixtures';

/**
 * The Budget page draws the system Uncategorized category's dot neutral: the
 * system flag comes from the page's own accounts read. Mocked at `fetch`.
 */

const coloured = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .filter((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default');
const SYSTEM_ID = coloured[0]!;
const OWN_ID = coloured[1]!;

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

function renderBudget() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const budget = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/budget', component: BudgetPage });
    const register = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/accounts/$accountId', component: () => <main /> });
    const router = createRouter({
        routeTree: root.addChildren([budget, register]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/budget`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('the Budget page\'s Uncategorized dot', () => {
    it('is neutral for the system category, and another category keeps its colour', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
                account({ id: SYSTEM_ID, name: 'Uncategorized', accountType: 'category', categoryKind: 'expense', isSystem: true }),
                account({ id: OWN_ID, name: 'Own category', accountType: 'category', categoryKind: 'expense' }),
            ],
            budget: {
                actualTotal: 100,
                rows: [
                    { categoryId: SYSTEM_ID, name: 'Uncategorized', parentId: null, actual: 60, typical: 10, target: null, targetCurrencyCode: null, mark: 10 },
                    { categoryId: OWN_ID, name: 'Own category', parentId: null, actual: 40, typical: 10, target: null, targetCurrencyCode: null, mark: 10 },
                ],
            },
        });
        renderBudget();

        const system = (await screen.findByText('Uncategorized', { selector: 'tr *' })).closest('tr')!;
        const own = (await screen.findByText('Own category', { selector: 'tr *' })).closest('tr')!;
        expect(system.querySelector('.size-dot-sm')).toHaveClass('bg-chip-neutral-text');
        expect(own.querySelector('.size-dot-sm')).not.toBeNull();
        expect(own.querySelector('.size-dot-sm')).not.toHaveClass('bg-chip-neutral-text');
    });
});
