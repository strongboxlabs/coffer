import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
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
import { currencyAffix } from '@/lib/money';
import type { BudgetCategoryRow } from '@/lib/types';

import { CategoryRows } from './CategoryRows';

// The target editor is a fixed-width money field that shows the reporting currency's
// symbol (ADR-0114 D14). The symbol's padding is added to the width, not taken out of it,
// so a three-character symbol does not cut an ordinary target short while it is typed.
// Every category and figure here is invented; nothing the panel reads is fetched, and
// fetch is stubbed so anything that were would be routed or fail.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ROW: BudgetCategoryRow = {
    categoryId: '00000000-0000-0000-0000-000000000200',
    name: 'Category A',
    parentId: null,
    actual: 10,
    typical: 20,
    target: null,
    targetCurrencyCode: null,
    mark: 20,
};

let stub: FetchStub;

beforeEach(() => {
    stub = stubFetch();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderRows(currency: string) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const budget = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => (
            <CategoryRows
                canEdit
                rows={[ROW]}
                accountPaths={new Map([[ROW.categoryId, ROW.name]])}
                systemCategoryIds={new Set()}
                currency={currency}
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
        routeTree: root.addChildren([budget]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

async function openTarget(): Promise<HTMLInputElement> {
    const user = userEvent.setup();
    const tr = (await screen.findByRole('button', { name: ROW.name })).closest('tr')!;
    await user.click(within(tr).getByTitle('Set a target'));
    return screen.getByLabelText(`Target for ${ROW.name}`) as HTMLInputElement;
}

describe('CategoryRows target editor width', () => {
    it('adds a three-character symbol\'s padding to the 96px figure width', async () => {
        // The case is only meaningful for a symbol of three characters (CA$).
        expect([...currencyAffix('CAD')!.symbol]).toHaveLength(3);
        renderRows('CAD');

        const input = await openTarget();

        // The style engine may reorder a calc's terms, so each is checked on its own.
        for (const term of ['calc(', 'var(--spacing-fixed-96px)', '0.75rem', '3ch']) {
            expect(input.style.width).toContain(term);
        }
        // No class sets a width that the inline one would have to fight.
        expect(input.className).not.toMatch(/(^|\s)w-/);
        expect(stub.unrouted).toEqual([]);
    });

    it('adds a one-character symbol\'s padding for the dollar', async () => {
        expect([...currencyAffix('USD')!.symbol]).toHaveLength(1);
        renderRows('USD');

        const input = await openTarget();

        for (const term of ['var(--spacing-fixed-96px)', '1ch']) {
            expect(input.style.width).toContain(term);
        }
    });
});
