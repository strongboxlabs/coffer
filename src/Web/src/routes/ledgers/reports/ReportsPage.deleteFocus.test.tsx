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

import { ReportsPage } from './ReportsPage';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';

/**
 * A saved report deleted from its card takes the card's Delete with it (ADR-0023
 * §L). The saved reports have no add control (a report is saved from its own
 * view), so with no card left focus goes to the saved reports' own region,
 * which stays on the page: named, and no Tab stop of its own. Mocked at `fetch`.
 */

const LEDGER = 'ledger-1';
const SAVED = {
    id: 'rep-1',
    name: 'Monthly spending',
    spec: { measure: 'Spending', groupBy: 'Category', bucket: 'Month', dateMode: 'Sliding', windowCount: 12, windowUnit: 'Month', windowAlign: true },
    visibility: 'private',
    ownerUserId: 'user-1',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
};

let stub: FetchStub;
let deleted = false;
beforeEach(() => {
    deleted = false;
    stub = stubFetch();
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Personal', role: 'owner' }])
        .on('GET', /\/reports\/saved$/, () => (deleted ? [] : [SAVED]))
        .on('DELETE', /\/reports\/saved\/rep-1$/, () => { deleted = true; return undefined; })
        .on('GET', /\/reports\/pins$/, []);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderPage() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    const root = createRootRoute();
    const router = createRouter({
        routeTree: root.addChildren([
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports', component: ReportsPage }),
            createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId', component: () => <main>ledger</main> }),
        ]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}/reports`] }),
        context: { queryClient: qc },
    });
    render(
        <QueryClientProvider client={qc}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('deleting the last saved report', () => {
    it('gives focus to the saved reports\' region', async () => {
        renderPage();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Delete Monthly spending' }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }));

        expect(await screen.findByText('Open a report, adjust it, and save it to keep it.')).toBeInTheDocument();
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        const saved = screen.getByRole('group', { name: 'Saved reports' });
        expect(saved).toHaveAttribute('tabindex', '-1');
        await waitFor(() => expect(saved).toHaveFocus());
    });
});
