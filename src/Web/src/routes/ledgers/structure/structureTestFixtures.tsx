import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

// What the tests of Structure's three lists share: a ledger id, a query client that keeps what it was
// seeded with, and a router, because a list's names are links and its menus navigate.

export const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

/**
 * A client whose seeded data stays fresh. Seeded data is stale the moment it lands, and jsdom has no
 * fetch: without `staleTime: Infinity` a list would refetch on mount, fail, and replace itself with its
 * error state under an assertion.
 */
export function testQueryClient(): QueryClient {
    return new QueryClient({
        defaultOptions: {
            queries: { retry: false, staleTime: Infinity },
            mutations: { retry: false },
        },
    });
}

/** The reader's role, which decides whether a list draws any control at all. */
export function seedRole(queryClient: QueryClient, role: string): void {
    queryClient.setQueryData(['ledgers'], [{ id: LEDGER_ID, name: 'Ledger', role }]);
}

const REGISTER_PATH = '/ledgers/$ledgerId/accounts/$accountId';
const TAG_PATH = '/ledgers/$ledgerId/tags/$tagId';
const TAGS_PATH = '/ledgers/$ledgerId/tags';

/**
 * Render `ui` in a router that also knows the places a list links to, so a click can be
 * followed: an account's register, a tag's page, and the Tags tab. Each shows a marker saying where
 * it landed. A place that `ui` is itself mounted at is not registered twice.
 *
 * By default `ui` is at `/`. A page that reads the ledger or the tag from the URL, as the Accounts
 * tab and the tag's page do, is given the path it has in the app instead, so `useParams` answers as
 * it would there.
 */
export function renderInRouter(
    ui: ReactElement,
    queryClient: QueryClient,
    where: { path: string; location: string } = { path: '/', location: '/' },
) {
    const root = createRootRoute();
    const home = createRoute({ getParentRoute: () => root, path: where.path, component: () => ui });
    const markers: Array<[path: string, text: string]> = [
        [REGISTER_PATH, 'register page'],
        [TAG_PATH, 'tag page'],
        [TAGS_PATH, 'tags page'],
    ];
    const others = markers
        .filter(([path]) => path !== where.path)
        .map(([path, text]) => createRoute({ getParentRoute: () => root, path, component: () => <div>{text}</div> }));
    const router = createRouter({
        routeTree: root.addChildren([home, ...others]),
        history: createMemoryHistory({ initialEntries: [where.location] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}
