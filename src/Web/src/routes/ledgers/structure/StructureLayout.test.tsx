import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import type { AccountSummary } from '@/lib/types';

import { StructureLayout } from './StructureLayout';

// Structure (ADR-0111): the three lists that describe a ledger, one page with a tab for each.
// This layout owns what the tabs share, so these cover that and nothing a tab owns: the
// trail, the heading, the reader's role, the tab strip and what it counts.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID, parentId: null, accountType: 'bank', categoryKind: null,
        currencyCode: 'USD', isActive: true, isSystem: false, feedConnectionId: null,
        needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false, ...over,
    };
}

function category(id: string): AccountSummary {
    return account({ id, name: `Category ${id}`, accountType: 'category', categoryKind: 'expense' });
}

type Seed = 'full' | 'none' | 'empty';

function renderLayout({
    role = 'owner', initialPath = `/ledgers/${LEDGER_ID}/accounts`, seed = 'full', seedLedgers = true,
}: { role?: string; initialPath?: string; seed?: Seed; seedLedgers?: boolean } = {}) {
    const queryClient = new QueryClient({
        // Seeded data is stale the moment it lands, and jsdom has no fetch: without
        // `staleTime: Infinity` the counts' reads would refetch, fail, and drop the numbers
        // out from under an assertion.
        defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    });
    if (seedLedgers) {
        queryClient.setQueryData(['ledgers'], [{ id: LEDGER_ID, name: 'Household', role }]);
    }
    if (seed === 'full') {
        // The accounts read also carries the categories: the Accounts tab lists the first two,
        // the Categories tab the other three, and the strip counts them that way.
        queryClient.setQueryData(['accounts', LEDGER_ID, { includeInactive: false }], [
            account({ id: 'a1', name: 'One' }),
            account({ id: 'a2', name: 'Two', accountType: 'credit_card' }),
            category('c1'), category('c2'), category('c3'),
        ]);
        queryClient.setQueryData(['tags', LEDGER_ID], [{ id: 't1' }]);
    } else if (seed === 'empty') {
        queryClient.setQueryData(['accounts', LEDGER_ID, { includeInactive: false }], []);
        queryClient.setQueryData(['tags', LEDGER_ID], []);
    }

    const root = createRootRoute();
    const ledgerHome = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>overview</main>,
    });
    const layout = createRoute({ getParentRoute: () => root, id: '_structure', component: StructureLayout });
    const tabs = ['accounts', 'categories', 'tags'].map((tab) => createRoute({
        getParentRoute: () => layout,
        path: `/ledgers/$ledgerId/${tab}`,
        component: () => <section>{tab} list</section>,
    }));
    const router = createRouter({
        routeTree: root.addChildren([ledgerHome, layout.addChildren(tabs)]),
        history: createMemoryHistory({ initialEntries: [initialPath] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('StructureLayout', () => {
    it('titles the page Structure, with the ledger as the first step of the trail', async () => {
        renderLayout();
        expect(await screen.findByRole('heading', { level: 1, name: 'Structure' })).toBeInTheDocument();
        const trail = screen.getByRole('navigation', { name: 'Breadcrumb' });
        expect(trail).toHaveTextContent('Household');
        expect(trail).toHaveTextContent('Structure');
        expect(screen.getByRole('link', { name: 'Household' })).toBeInTheDocument();
    });

    it('offers the three tabs as links to the URLs the pages had before they were joined', async () => {
        renderLayout();
        const strip = await screen.findByRole('navigation', { name: 'Structure sections' });
        const hrefs = Array.from(strip.querySelectorAll('a')).map((a) => a.getAttribute('href'));
        expect(hrefs).toEqual([
            `/ledgers/${LEDGER_ID}/accounts`,
            `/ledgers/${LEDGER_ID}/categories`,
            `/ledgers/${LEDGER_ID}/tags`,
        ]);
    });

    it('marks the tab it is showing, to a reader and to the eye, and only that one', async () => {
        renderLayout({ initialPath: `/ledgers/${LEDGER_ID}/categories` });
        expect(await screen.findByText('categories list')).toBeInTheDocument();
        const strip = screen.getByRole('navigation', { name: 'Structure sections' });
        const current = Array.from(strip.querySelectorAll('a[aria-current="page"]'));
        expect(current).toHaveLength(1);
        expect(current[0]).toHaveTextContent('Categories');
        // The router sets aria-current on its own, so it cannot say whether the underline and the
        // weight, which are what a sighted reader sees, are drawn.
        expect(current[0]).toHaveClass('border-accent', 'font-semibold');
        const others = Array.from(strip.querySelectorAll('a')).filter((a) => a !== current[0]);
        expect(others).toHaveLength(2);
        for (const a of others) {
            expect(a).toHaveClass('border-transparent');
            expect(a).not.toHaveClass('border-accent');
        }
    });

    it('names the list that is showing in the heading outline, since the title is the same on all three', async () => {
        renderLayout({ initialPath: `/ledgers/${LEDGER_ID}/categories` });
        expect(await screen.findByRole('heading', { level: 2, name: 'Categories' })).toBeInTheDocument();
        expect(screen.queryByRole('heading', { level: 2, name: 'Accounts' })).not.toBeInTheDocument();
        await userEvent.click(screen.getByRole('link', { name: /^Tags/ }));
        expect(await screen.findByRole('heading', { level: 2, name: 'Tags' })).toBeInTheDocument();
        expect(screen.queryByRole('heading', { level: 2, name: 'Categories' })).not.toBeInTheDocument();
    });

    it('switches the list under the strip when a tab is chosen', async () => {
        renderLayout();
        expect(await screen.findByText('accounts list')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('link', { name: /^Tags/ }));
        expect(await screen.findByText('tags list')).toBeInTheDocument();
        expect(screen.queryByText('accounts list')).not.toBeInTheDocument();
        // The heading and the strip are the layout's, so they survive the change.
        expect(screen.getByRole('heading', { level: 1, name: 'Structure' })).toBeInTheDocument();
    });

    it('opens the next tab at the top, not at the offset the last one was scrolled to', async () => {
        renderLayout({ initialPath: `/ledgers/${LEDGER_ID}/categories` });
        expect(await screen.findByText('categories list')).toBeInTheDocument();
        // The pane outlives the change of tab, since it belongs to the layout. jsdom does not lay
        // out, so give the element a scroll offset it keeps and a way to see it set.
        const pane = document.querySelector('main') as HTMLElement;
        let offset = 640;
        Object.defineProperty(pane, 'scrollTop', { configurable: true, get: () => offset, set: (v: number) => { offset = v; } });

        await userEvent.click(screen.getByRole('link', { name: /^Tags/ }));
        expect(await screen.findByText('tags list')).toBeInTheDocument();

        expect(offset).toBe(0);
    });

    it('counts what each tab will list, taking the categories from the same read as the accounts', async () => {
        renderLayout();
        // Two real accounts and three categories in one accounts read, and one tag. The category
        // rows are not accounts on the Accounts tab, so they are not counted there.
        expect(await screen.findByRole('link', { name: /^Accounts\s*2$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Categories\s*3$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Tags\s*1$/ })).toBeInTheDocument();
    });

    it('says none when a list has been read and is empty, as a zero, not as a missing count', async () => {
        renderLayout({ seed: 'empty' });
        expect(await screen.findByRole('link', { name: /^Accounts\s*0$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Categories\s*0$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Tags\s*0$/ })).toBeInTheDocument();
    });

    it('shows no count for a tab whose list has not been read, rather than a zero', async () => {
        renderLayout({ seed: 'none' });
        // Anchor on the tab, which renders at once; the counts then have nothing to show.
        expect(await screen.findByRole('link', { name: /^Accounts$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Categories$/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^Tags$/ })).toBeInTheDocument();
    });

    it('says once that a reader who cannot edit can only look', async () => {
        renderLayout({ role: 'viewer' });
        expect(await screen.findByText('View only')).toBeInTheDocument();
    });

    it.each(['owner', 'editor'])('says nothing of the sort to an %s', async (role) => {
        renderLayout({ role });
        // Anchor on the heading, so the absence is of a rendered page and not of an empty one.
        expect(await screen.findByRole('heading', { level: 1, name: 'Structure' })).toBeInTheDocument();
        expect(screen.queryByText('View only')).not.toBeInTheDocument();
    });

    it('does not tell anyone they can only look before it knows who they are', async () => {
        // The ledger list has not been read. Until it has, the role is unknown, which counts as
        // "cannot edit"; the chip must wait for the ledger, or an owner would see it flash.
        renderLayout({ seedLedgers: false });
        expect(await screen.findByRole('heading', { level: 1, name: 'Structure' })).toBeInTheDocument();
        expect(screen.queryByText('View only')).not.toBeInTheDocument();
    });
});
