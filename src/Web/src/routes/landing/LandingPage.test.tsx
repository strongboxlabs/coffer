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

import { LandingPage } from './LandingPage';
import { ApiError } from '@/lib/api';
import * as apiModule from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';

// Smoke tests for the post-auth landing. Behaviour we lock down:
//
//   * the populated list renders one entry per ledger with the
//     ledger's name + role
//   * the empty-state copy appears when the API returns []
//   * an API failure surfaces the alert region

function renderLanding() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: LandingPage,
    });
    // Stub destination route so the Link's typed `to` resolves.
    // We don't render its component in these tests (we only check the
    // <a href>); the route just needs to exist in the tree.
    const ledgerDetailRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>detail</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([landingRoute, ledgerDetailRoute]),
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

describe('LandingPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('renders one entry per ledger with name + role, each as a link to the detail route', async () => {
        const ledgers: LedgerSummary[] = [
            { id: '00000000-0000-0000-0000-000000000010', name: 'Personal', role: 'owner' },
            { id: '00000000-0000-0000-0000-000000000011', name: 'Household', role: 'owner' },
        ];
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);

        renderLanding();

        // Each ledger name appears.
        expect(await screen.findByText('Personal')).toBeInTheDocument();
        expect(screen.getByText('Household')).toBeInTheDocument();

        // Role appears next to the name (twice — once per ledger).
        expect(screen.getAllByText(/owner/i).length).toBe(2);

        // Each entry is a link to its detail page. The href is what
        // we lock down — actually navigating is a router concern
        // covered by the LedgerDetailPage's own tests.
        const personalLink = screen.getByRole('link', { name: /personal/i });
        expect(personalLink).toHaveAttribute(
            'href',
            '/ledgers/00000000-0000-0000-0000-000000000010',
        );
        const householdLink = screen.getByRole('link', { name: /household/i });
        expect(householdLink).toHaveAttribute(
            'href',
            '/ledgers/00000000-0000-0000-0000-000000000011',
        );

        // The empty-state copy is absent.
        expect(
            screen.queryByText(/you don't have any ledgers yet/i),
        ).not.toBeInTheDocument();
    });

    it('renders the empty-state copy when the user has no ledgers', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([]);

        renderLanding();

        expect(
            await screen.findByText(/you don't have any ledgers yet/i),
        ).toBeInTheDocument();
    });

    it('surfaces an alert when the API fails', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockRejectedValue(
            new ApiError(500, 'Internal error'),
        );

        renderLanding();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/could not load your ledgers/i);
    });
});
