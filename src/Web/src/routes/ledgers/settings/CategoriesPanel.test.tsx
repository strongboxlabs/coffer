import { describe, expect, it } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { CategoriesPanel } from './CategoriesPanel';
import type { CategoryNode } from '@/lib/types';

// The panel had NO visible controls — `grep -c '<Button|<IconButton'` returned
// zero — so Add sub-category / Rename / Move / Merge / Delete were reachable
// only by right-clicking a row, advertised by a title tooltip that does not
// exist on touch and is not announced as an affordance.
//
// These cover the visible path and the one branch that is easy to get wrong:
// SYSTEM categories have no actions, so they must not get a button — and must
// still line up with the rows that do.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function node(over: Partial<CategoryNode> & { id: string; name: string }): CategoryNode {
    return {
        parentId: null,
        isSystem: false,
        isActive: true,
        categoryKind: 'expense',
        transactionCount: 0,
        childCount: 0,
        total: 0,
        ...over,
    } as CategoryNode;
}

function renderPanel(categories: CategoryNode[]) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    // The real key carries the showInactive flag as a third element; seeding
    // ['categories', id] alone leaves the panel loading forever.
    queryClient.setQueryData(['categories', LEDGER_ID, false], categories);
    queryClient.setQueryData(['accounts', LEDGER_ID], []);

    const root = createRootRoute();
    const home = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <CategoriesPanel ledgerId={LEDGER_ID} />,
    });
    const account = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <div>account</div>,
    });
    const router = createRouter({
        routeTree: root.addChildren([home, account]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('CategoriesPanel row actions', () => {
    it('gives an editable category a visible actions button', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' }))
            .toBeInTheDocument();
    });

    it('opens the action menu from the button', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        // Anchor first: absent before the click, so the assertion after cannot
        // pass on a menu that was always rendered.
        expect(screen.queryByText('Rename')).not.toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));

        expect(await screen.findByText('Rename')).toBeInTheDocument();
        expect(screen.getByText('Add sub-category')).toBeInTheDocument();
        expect(screen.getByText('Delete')).toBeInTheDocument();
    });

    it('gives a system category no actions button', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Opening Balance', isSystem: true }),
        ]);
        // Anchor on the row that SHOULD have one, so this cannot pass because
        // nothing rendered at all.
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' }))
            .toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for Opening Balance' }))
            .not.toBeInTheDocument();
    });
});
