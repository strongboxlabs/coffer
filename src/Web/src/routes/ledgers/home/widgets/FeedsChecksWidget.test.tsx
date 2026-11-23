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

import type { StubbedCall } from '@/lib/annotations/fetchStub';
import type { FeedConnectionSummary } from '@/lib/types';

import { FeedConnectionsPanel } from '../../settings/FeedConnectionsPanel';
import { HomePage } from '../HomePage';
import { homeStub, LEDGER, schedule } from '../homeTestFixtures';

beforeEach(() => {
    // The home screen reads the user's local date once per render.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const connection = (name: string | null): FeedConnectionSummary => ({
    id: 'conn-1', ledgerId: LEDGER, provider: 'simplefin', institutionName: name,
    status: 'active', lastSyncedAt: null, createdAt: '2026-03-01T00:00:00Z',
});

/**
 * The home screen and Settings' Feeds panel on one page, sharing one cache, as the
 * app shares it between the two pages. The home screen stays mounted through the
 * rename, so it shows the new name only if the rename wrote the cache they share: no
 * remount reads the list again.
 */
function renderHomeBesideFeeds() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const stubPage = () => <main>elsewhere</main>;
    const paths = [
        '/ledgers/$ledgerId/settings', '/ledgers/$ledgerId/reminders', '/ledgers/$ledgerId/accounts',
        '/ledgers/$ledgerId/accounts/$accountId', '/ledgers/$ledgerId/budget', '/ledgers/$ledgerId/reports/view',
        '/ledgers/$ledgerId/securities', '/ledgers/$ledgerId/categories', '/imports/moneydance', '/system',
    ];
    const both = () => (
        <>
            <HomePage />
            <FeedConnectionsPanel ledgerId={LEDGER} />
        </>
    );
    const home = createRoute({ getParentRoute: () => root, path: '/ledgers/$ledgerId', component: both });
    const others = paths.map((path) => createRoute({ getParentRoute: () => root, path, component: stubPage }));
    const router = createRouter({
        routeTree: root.addChildren([home, ...others]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER}`] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('Feeds & checks', () => {
    it('names a connection renamed in Settings by its new name', async () => {
        let stored = 'Feed A';
        homeStub()
            .on('GET', /\/feed-connections$/, () => [connection(stored)])
            .on('GET', /\/feed-connections\/conn-1\/accounts$/, [])
            .on('GET', /\/schedules\/feed-sync$/, schedule())
            .on('PATCH', /\/feed-connections\/conn-1$/, (call: StubbedCall) => {
                stored = (call.body as { name: string }).name;
                return connection(stored);
            });
        renderHomeBesideFeeds();
        const user = userEvent.setup();

        const widget = (await screen.findByRole('heading', { name: 'Feeds & checks' }))
            .closest('[id^="home-widget-"]') as HTMLElement;
        expect(await within(widget).findByRole('link', { name: 'Feed A' })).toBeInTheDocument();

        await user.click(await screen.findByRole('button', { name: 'Rename connection' }));
        await user.clear(screen.getByRole('textbox', { name: 'Connection name' }));
        await user.keyboard('Feed B{Enter}');

        expect(await within(widget).findByRole('link', { name: 'Feed B' })).toBeInTheDocument();
        expect(within(widget).queryByRole('link', { name: 'Feed A' })).toBeNull();
        await waitFor(() => expect(screen.getByRole('button', { name: 'Rename connection' }))
            .not.toHaveAttribute('aria-busy'));
        expect(within(widget).getByRole('link', { name: 'Feed B' })).toBeInTheDocument();
    });
});
