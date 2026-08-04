import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    Outlet,
    RouterProvider,
} from '@tanstack/react-router';

import { AuthedSidebar } from './AuthedSidebar';
import * as authModule from '@/lib/auth';
import * as apiModule from '@/lib/api';
import type { AccountSummary, LedgerSummary } from '@/lib/types';

// Smoke tests for the persistent authed sidebar. Behaviour locked down:
//
//   * the display name (or username fallback) appears
//   * the logout icon button posts to /api/auth/logout, clears the
//     QueryClient cache (so a subsequent login can't see the
//     previous user's data), and navigates to /login
//   * if the logout API call fails, the local state clear +
//     redirect still happens (user's intent honoured)
//   * accounts on the active ledger are grouped by type, one section
//     per type (Banking / Cash / Credit cards / Investments / Assets /
//     Liabilities / Loans) — system + hidden accounts are
//     filtered out so the rail isn't dominated by per-security
//     Holdings sub-accounts

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderInRouter(opts: {
    initialPath?: string;
    accounts?: AccountSummary[];
    ledgers?: LedgerSummary[];
    isAdmin?: boolean;
} = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    queryClient.setQueryData(['me'], {
        id: '00000000-0000-0000-0000-000000000001',
        username: 'alice',
        displayName: 'Alice Z.',
        isAdmin: opts.isAdmin ?? false,
    });
    queryClient.setQueryData(
        ['ledgers'],
        opts.ledgers ?? [
            { id: LEDGER_ID, name: 'Personal', role: 'owner' },
        ],
    );
    if (opts.accounts) {
        queryClient.setQueryData(['accounts', LEDGER_ID], opts.accounts);
    }

    const root = createRootRoute();
    const homeRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => (
            <>
                <AuthedSidebar />
                <Outlet />
            </>
        ),
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => (
            <>
                <AuthedSidebar />
                <main>ledger</main>
            </>
        ),
    });
    const loginRoute = createRoute({
        getParentRoute: () => root,
        path: '/login',
        component: () => <main>login</main>,
    });
    // The register route also renders AuthedSidebar so the sidebar
    // is present on every authed URL we test (matches the production
    // AuthedOutlet shell).
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => (
            <>
                <AuthedSidebar />
                <main>register</main>
            </>
        ),
    });
    // Stub the System settings target so the sidebar gear's navigation resolves.
    const systemRoute = createRoute({
        getParentRoute: () => root,
        path: '/system',
        component: () => (
            <>
                <AuthedSidebar />
                <main>system</main>
            </>
        ),
    });
    const router = createRouter({
        routeTree: root.addChildren([
            homeRoute,
            ledgerRoute,
            loginRoute,
            registerRoute,
            systemRoute,
        ]),
        history: createMemoryHistory({
            initialEntries: [opts.initialPath ?? '/'],
        }),
        context: { queryClient },
    });

    return {
        queryClient,
        view: render(
            <QueryClientProvider client={queryClient}>
                {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
                <RouterProvider router={router as any} />
            </QueryClientProvider>,
        ),
    };
}

function makeAccount(
    overrides: Partial<AccountSummary> & { id: string; name: string },
): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        accountType: 'bank',
        categoryKind: null,
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...overrides,
    };
}

describe('AuthedSidebar', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        // jsdom doesn't implement window.scrollTo; TanStack Router's
        // scroll-restoration calls it on every navigation, throwing an
        // unhandled "Not implemented" that can crash the worker mid-run
        // (flaky "no tests"). Stub it so the nav-driven tests are atomic.
        window.scrollTo = vi.fn();
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'owner' },
        ]);
        // Deliberately NOT mocking fetchCurrentUser: the userQuery
        // reads from the pre-seeded cache. If the cache is cleared
        // (logout flow), the unmocked refetch will fail and the
        // cache stays empty — that's the assertion shape the logout
        // tests rely on. Mocking it here would let the cache
        // repopulate post-clear and break those tests.
    });

    it("renders the user's display name", async () => {
        renderInRouter();
        expect(await screen.findByText('Alice Z.')).toBeInTheDocument();
    });

    it('the System settings gear navigates to /system', async () => {
        // System settings (About + admin Backups) replaced the (i) dialog and
        // the sidebar Admin section; it's a gear by the brand, shown to
        // everyone (the Backups tab self-gates by admin on the page).
        renderInRouter();
        const user = userEvent.setup();
        await user.click(
            await screen.findByRole('button', { name: /system settings/i }),
        );
        expect(await screen.findByText('system')).toBeInTheDocument();
    });

    it('logs out → clears the cache and navigates to /login', async () => {
        const logoutSpy = vi
            .spyOn(authModule, 'performLogout')
            .mockResolvedValue(undefined);

        const { queryClient } = renderInRouter();

        expect(queryClient.getQueryData(['me'])).toBeDefined();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /sign out/i }));

        await waitFor(() => {
            expect(logoutSpy).toHaveBeenCalledOnce();
            expect(screen.getByText('login')).toBeInTheDocument();
            expect(queryClient.getQueryData(['me'])).toBeUndefined();
        });
    });

    it('still clears state and routes to /login when the logout API fails', async () => {
        vi.spyOn(authModule, 'performLogout').mockRejectedValue(
            new Error('Network error'),
        );

        const { queryClient } = renderInRouter();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /sign out/i }));

        await waitFor(() => {
            expect(screen.getByText('login')).toBeInTheDocument();
            expect(queryClient.getQueryData(['me'])).toBeUndefined();
        });
    });

    it('the Overview destination opens this ledger\'s Hub', async () => {
        // Start on the register page so navigating to the Hub (via the
        // sidebar's Overview destination) is a visible change.
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/accounts/a1` });
        expect(await screen.findByText('register')).toBeInTheDocument();

        const user = userEvent.setup();
        await user.click(screen.getByRole('link', { name: /overview/i }));
        // The Hub route renders <main>ledger</main>.
        expect(await screen.findByText('ledger')).toBeInTheDocument();
    });

    it('shows Categories as a destination and not Activity (ADR-0069 nav swap)', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}` });
        // Categories graduated from a Settings tab to a top-level destination.
        expect(
            await screen.findByRole('link', { name: /categories/i }),
        ).toBeInTheDocument();
        // Activity moved the other way — into Settings — so it's no longer a
        // top-level destination link.
        expect(
            screen.queryByRole('link', { name: /^activity$/i }),
        ).not.toBeInTheDocument();
    });

    it('the ⌄ chevron opens the switch-ledger dropdown', async () => {
        const ledgers: LedgerSummary[] = [
            { id: LEDGER_ID, name: 'Personal', role: 'owner' },
            { id: '00000000-0000-0000-0000-000000000020', name: 'Demo', role: 'owner' },
        ];
        // Override the beforeEach single-ledger mock so a refetch keeps
        // both ledgers (the seeded cache alone would be overwritten).
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}`, ledgers });

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /switch ledger/i }));
        // The dropdown lists the OTHER ledger to switch to.
        expect(await screen.findByText('Demo')).toBeInTheDocument();
    });

    it('groups accounts by type and filters out system + hidden accounts', async () => {
        const accounts: AccountSummary[] = [
            makeAccount({
                id: 'a1',
                name: 'Eastbank Checking',
                accountType: 'bank',
            }),
            makeAccount({
                id: 'a2',
                name: 'Apple Card',
                accountType: 'credit_card',
            }),
            makeAccount({
                id: 'a3',
                name: 'Workplace 401(k)',
                accountType: 'investment',
            }),
            // Loan — previously lumped into a catch-all "Other" bucket;
            // now grouped under its own "Loans" section.
            makeAccount({
                id: 'a5',
                name: 'Home Mortgage',
                accountType: 'loan',
            }),
            // System Holdings sub-account — should be filtered out.
            makeAccount({
                id: 'a4',
                name: 'Holdings · AAPL',
                accountType: 'holding',
                isSystem: true,
            }),
            // Inactive account — not in this fixture because the
            // default fetchAccounts call (showInactive=false) wouldn't
            // include it. Inactive-account rendering is covered by
            // the dedicated "Show inactive" toggle tests below.
            // Category — not shown in the workflow rail.
            makeAccount({
                id: 'a6',
                name: 'Groceries',
                accountType: 'category',
                categoryKind: 'expense',
            }),
        ];
        // The beforeEach mock returns []; override so the eventual
        // refetch returns the same set as the cache (otherwise the
        // refetch overrides the seeded data on first render).
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(accounts);

        renderInRouter({
            initialPath: `/ledgers/${LEDGER_ID}`,
            accounts,
        });

        // One group heading per account type renders (labels mirror the
        // Ledger Hub's account sections).
        expect(await screen.findByText('Banking')).toBeInTheDocument();
        expect(screen.getByText('Credit cards')).toBeInTheDocument();
        expect(screen.getByText('Investments')).toBeInTheDocument();
        // Types that used to fall into a generic "Other" bucket now get
        // their own labelled section.
        expect(screen.getByText('Loans')).toBeInTheDocument();
        expect(screen.queryByText('Other')).not.toBeInTheDocument();

        // The visible-account names render.
        expect(screen.getByText('Eastbank Checking')).toBeInTheDocument();
        expect(screen.getByText('Apple Card')).toBeInTheDocument();
        expect(screen.getByText('Workplace 401(k)')).toBeInTheDocument();
        expect(screen.getByText('Home Mortgage')).toBeInTheDocument();

        // System Holdings sub-account is filtered out.
        expect(screen.queryByText(/Holdings · AAPL/)).not.toBeInTheDocument();
        // Categories are filtered out of the rail.
        expect(screen.queryByText('Groceries')).not.toBeInTheDocument();
    });

    it('marks the active account row with aria-current=page', async () => {
        const accounts: AccountSummary[] = [
            makeAccount({ id: 'a1', name: 'Eastbank Checking' }),
            makeAccount({ id: 'a2', name: 'Savings' }),
        ];
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(accounts);

        renderInRouter({
            initialPath: `/ledgers/${LEDGER_ID}/accounts/a2`,
            accounts,
        });

        // The active account's row is aria-current=page; the other
        // one isn't.
        const active = await screen.findByRole('link', { name: /savings/i });
        expect(active).toHaveAttribute('aria-current', 'page');

        const inactive = within(active.closest('nav')!).getByRole('link', {
            name: /eastbank checking/i,
        });
        expect(inactive).not.toHaveAttribute('aria-current');
    });

    it('shows a needs-review dot on accounts with needsReviewCount > 0 (slice 2c.2)', async () => {
        // ADR-0021: present-vs-absent signal, not a number on the UI.
        // The aria-label carries the count for screen readers and
        // keyboard hover.
        const accounts: AccountSummary[] = [
            makeAccount({
                id: 'a1',
                name: 'Has Review',
                needsReviewCount: 5,
            }),
            makeAccount({
                id: 'a2',
                name: 'No Review',
                needsReviewCount: 0,
            }),
        ];
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(accounts);

        renderInRouter({
            initialPath: `/ledgers/${LEDGER_ID}`,
            accounts,
        });

        // Dot present on the flagged account (aria-label is what
        // we can query — visually it's a 1.5x1.5 div).
        const dot = await screen.findByLabelText(/5 transactions to review/i);
        expect(dot).toBeInTheDocument();

        // The other account has no dot — no element with a
        // "transactions to review" aria-label.
        const allDots = screen.queryAllByLabelText(/transactions? to review/i);
        expect(allDots).toHaveLength(1);
    });

    // --- Getting to ledger management (ADR-0090) --------------------------
    //
    // The rail deliberately stays put on every authed page — the ledgerId
    // fallback keeps the destinations + account nav in place so the layout
    // doesn't reshuffle as you move around. What was missing was a labelled way
    // to ledger management: `/` was reachable only via an "All ledgers"
    // breadcrumb crumb, which is navigation smuggled into a location indicator.

    it('offers "Manage ledgers" in the ledger dropdown', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}` });

        const picker = await screen.findByRole('button', { name: /switch ledger/i });
        await userEvent.setup().click(picker);

        const menu = await screen.findByRole('menu');
        // Ledger MANAGEMENT sits with the ledgers, not beside the System gear —
        // it is not an install-wide setting.
        expect(
            within(menu).getByRole('menuitem', { name: /manage ledgers/i }),
        ).toBeInTheDocument();
        expect(within(menu).getByRole('menuitem', { name: /personal/i })).toBeInTheDocument();
    });

    it('offers the same dropdown when no ledger exists at all', async () => {
        // Fresh install: the picker used to render a dead "No ledger selected"
        // <span>, so there was no way in from the rail.
        renderInRouter({ initialPath: '/', ledgers: [] });

        const picker = await screen.findByRole('button', { name: /manage ledgers/i });
        await userEvent.setup().click(picker);

        expect(
            within(await screen.findByRole('menu')).getByRole('menuitem', {
                name: /manage ledgers/i,
            }),
        ).toBeInTheDocument();
    });

    it('links the wordmark to the ledger list', async () => {
        renderInRouter({ initialPath: '/system' });

        // Was an inert <span>, so the universal "click the logo" gesture did
        // nothing — and nothing else in the rail linked to `/`.
        const home = await screen.findByRole('link', { name: /^coffer$/i });
        expect(home).toHaveAttribute('href', '/');
    });
});
