import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { categoryChipVariant } from '@/lib/categoryChip';
import type { BudgetCategoryRow } from '@/lib/types';

import { CategoryRows } from './CategoryRows';

/** The Budget table's identity dot: neutral for the system Uncategorized category. */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
// Ids the hash gives a colour (it hashes the id), so neutral is the system flag's doing.
const coloured = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .filter((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default');
const SYSTEM_ID = coloured[0]!;
const OWN_ID = coloured[1]!;

const row = (categoryId: string, name: string): BudgetCategoryRow => ({
    categoryId, name, parentId: null, actual: 20, typical: 10, target: null, targetCurrencyCode: null, mark: 10,
});

function renderRows() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root, path: '/ledgers/$ledgerId/accounts/$accountId', component: () => <main />,
    });
    const budgetRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/budget',
        component: () => (
            <CategoryRows
                canEdit
                rows={[row(SYSTEM_ID, 'Uncategorized'), row(OWN_ID, 'Own category')]}
                accountPaths={new Map([[SYSTEM_ID, 'Uncategorized'], [OWN_ID, 'Own category']])}
                systemCategoryIds={new Set([SYSTEM_ID])}
                currency="USD"
                ledgerId={LEDGER_ID}
                monthKey="2026-04"
                expandedIds={new Set()}
                onToggle={() => {}}
                detailId={null}
                onToggleDetail={() => {}}
                onSetTarget={() => {}}
            />
        ),
    });
    const router = createRouter({
        routeTree: root.addChildren([budgetRoute, registerRoute]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/budget`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** The identity dot in the table row that names `label`. */
async function dotFor(label: string): Promise<Element | null> {
    const cell = await screen.findByText(label);
    return cell.closest('tr')!.querySelector('.size-dot-sm');
}

describe('the Budget table\'s Uncategorized dot', () => {
    it('is neutral for the system category, and another category keeps its colour', async () => {
        renderRows();

        expect(await dotFor('Uncategorized')).toHaveClass('bg-chip-neutral-text');
        const own = await dotFor('Own category');
        expect(own).not.toBeNull();
        expect(own).not.toHaveClass('bg-chip-neutral-text');
    });
});
