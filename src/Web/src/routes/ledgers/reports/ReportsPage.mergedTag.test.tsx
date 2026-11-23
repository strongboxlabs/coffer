import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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
import { ReportView } from './ReportView';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';

/**
 * A tag merge repoints every saved report that filtered on the merged-away tag
 * (server side). What the client owes is to open such a report on the tag its
 * stored spec now names, without reinterpreting it: the spec is forwarded as it
 * is stored. Mocked at `fetch`.
 */

const LEDGER = 'ledger-1';
const SURVIVOR = '6f1c2a9e-0000-4000-8000-0000000000aa';

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER, name: 'Personal', role: 'owner' }])
        .on('GET', /\/reports\/saved$/, [{
            id: 'rep-1',
            name: 'Trips by month',
            // As the server leaves it after the merge: the survivor's id.
            spec: { measure: 'Spending', groupBy: 'Category', bucket: 'Month', dateMode: 'Sliding', windowCount: 12, windowUnit: 'Month', windowAlign: true, tagId: SURVIVOR },
            visibility: 'private',
            ownerUserId: 'user-1',
            createdAt: '2026-10-01T00:00:00Z',
            updatedAt: '2026-10-01T00:00:00Z',
        }])
        .on('GET', /\/reports\/pins$/, [])
        .on('GET', /\/reports\/summary$/, { rows: [], total: 0, fromUtc: '2025-11-01T00:00:00Z', toUtc: '2026-11-01T00:00:00Z', currencyCode: 'USD', periodTotals: [], groupTotals: [] });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('a saved report filtered on a merged tag', () => {
    it('opens filtered on the surviving tag', async () => {
        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const root = createRootRoute();
        const router = createRouter({
            routeTree: root.addChildren([
                createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports', component: ReportsPage }),
                createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId/reports/view', component: ReportView }),
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

        await userEvent.click(await screen.findByRole('link', { name: /Trips by month/ }));

        await waitFor(() => expect(stub.callsTo('GET', /\/reports\/summary$/).length).toBeGreaterThan(0));
        const summary = stub.callsTo('GET', /\/reports\/summary$/).at(-1)!;
        expect(summary.search.get('tagId')).toBe(SURVIVOR);
    });
});
