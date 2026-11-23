import { StrictMode } from 'react';
import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { stubFetch } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';
import { maxWidthFor, WIDTH_VAR } from '@/lib/sidebarRail';
import type {
    AccountBalance, AccountFolder, AccountSubtotals, AccountSummary, LedgerSummary, RateUnavailable,
} from '@/lib/types';
import type { LedgerAttention, SystemAttention } from '@/lib/types/notification';

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
//   * balances, folders (nested sums, drag and drop, the menu's moves,
//     role gating) and the rail's collapse and width: the second
//     describe below

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderInRouter(opts: {
    initialPath?: string;
    accounts?: AccountSummary[];
    /** The ledger list in the cache; null when it has not arrived yet. */
    ledgers?: LedgerSummary[] | null;
    isAdmin?: boolean;
    attention?: LedgerAttention[];
    /** What the INSTALLATION needs, behind the System gear (admins only). */
    systemAttention?: SystemAttention;
    /** Render under StrictMode, as main.tsx does, which runs each effect twice on mount. */
    strict?: boolean;
} = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    queryClient.setQueryData(['me'], {
        id: '00000000-0000-0000-0000-000000000001',
        username: 'alice',
        displayName: 'User A',
        isAdmin: opts.isAdmin ?? false,
    });
    if (opts.ledgers !== null) {
        queryClient.setQueryData(
            ['ledgers'],
            opts.ledgers ?? [
                { id: LEDGER_ID, name: 'Personal', role: 'owner' },
            ],
        );
    }
    if (opts.accounts) {
        queryClient.setQueryData(['accounts', LEDGER_ID], opts.accounts);
    }
    // Seeded even when empty, so the rail's indicator query never reaches for the
    // network in a test that is not about it.
    queryClient.setQueryData(['ledger-attention'], opts.attention ?? []);
    // The same for the gear, so an admin test that is not about it makes no request either.
    queryClient.setQueryData(
        ['system-attention'],
        opts.systemAttention ?? { worst: null, subjects: [], upgradeVersion: null },
    );

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
    // The three tabs of Structure, so the rail can be checked against each URL it should and
    // should not light up for.
    const tabRoutes = ['accounts', 'categories', 'tags'].map((tab) => createRoute({
        getParentRoute: () => root,
        path: `/ledgers/$ledgerId/${tab}`,
        component: () => (
            <>
                <AuthedSidebar />
                <main>{tab} tab</main>
            </>
        ),
    }));
    // A tag's page, which is outside the Structure layout and reached from its Tags tab.
    const tagPageRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/tags/$tagId',
        component: () => (
            <>
                <AuthedSidebar />
                <main>tag page</main>
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
            ...tabRoutes,
            tagPageRoute,
            systemRoute,
        ]),
        history: createMemoryHistory({
            initialEntries: [opts.initialPath ?? '/'],
        }),
        context: { queryClient },
    });

    const tree = (
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>
    );
    return {
        queryClient,
        view: render(opts.strict ? <StrictMode>{tree}</StrictMode> : tree),
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
        // The rail's shape is per browser; jsdom's storage outlives a test.
        localStorage.clear();
        document.documentElement.style.removeProperty(WIDTH_VAR);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchAccountBalances').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchAccountSubtotals').mockResolvedValue({ currencyCode: 'USD', types: [], folders: [] });
        vi.spyOn(apiModule, 'fetchAccountFolders').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' },
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
        expect(await screen.findByText('User A')).toBeInTheDocument();
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

    it('shows Structure as one destination in place of Accounts, Categories and Tags (ADR-0111)', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}` });
        // Anchor on the entry that should be there, so the absences below cannot pass on an
        // empty rail.
        expect(await screen.findByRole('link', { name: /^structure$/i })).toBeInTheDocument();
        // The three lists are tabs of it, not destinations of their own. The exact names matter:
        // a register link carries an account's name, and "Accounts" is also the rail's section.
        expect(screen.queryByRole('link', { name: /^accounts$/i })).not.toBeInTheDocument();
        expect(screen.queryByRole('link', { name: /^categories$/i })).not.toBeInTheDocument();
        expect(screen.queryByRole('link', { name: /^tags$/i })).not.toBeInTheDocument();
        // Activity lives in Settings, so it is not a top-level destination link.
        expect(
            screen.queryByRole('link', { name: /^activity$/i }),
        ).not.toBeInTheDocument();
    });

    it.each([
        ['accounts', 'accounts tab'],
        ['categories', 'categories tab'],
        ['tags', 'tags tab'],
    ])('lights Structure on the %s tab', async (tab, marker) => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/${tab}` });
        expect(await screen.findByText(marker)).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^structure$/i })).toHaveAttribute('aria-current', 'page');
    });

    it('lights Structure on a tag’s page, which is reached from the Tags tab and sits outside it', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/tags/t1` });
        expect(await screen.findByText('tag page')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^structure$/i })).toHaveAttribute('aria-current', 'page');
    });

    it('lights Structure on a tag’s page in the collapsed rail too', async () => {
        localStorage.setItem('coffer.sidebar.collapsed', 'true');
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/tags/t1` });
        expect(await screen.findByText('tag page')).toBeInTheDocument();
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Structure' })).toHaveAttribute('aria-current', 'page');
    });

    it('does not light Structure on an account register, though it shares the /accounts prefix', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/accounts/a1` });
        expect(await screen.findByText('register')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^structure$/i })).not.toHaveAttribute('aria-current');
    });

    it('does not mark the Overview as the current page on a page under it', async () => {
        // Overview's path is the prefix of every other page in the ledger, so the router's own
        // idea of "active" would claim it on all of them unless the link asks for an exact match.
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/categories` });
        expect(await screen.findByText('categories tab')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /overview/i })).not.toHaveAttribute('aria-current');
    });

    it('opens Structure on its Accounts tab', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}` });
        expect(await screen.findByRole('link', { name: /^structure$/i }))
            .toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts`);
    });

    // The collapsed rail draws its own Links, so the exact-match rule and the current-page rule are
    // written twice; the tests above only see the expanded one. Its icons are named by aria-label.
    it.each([
        ['accounts', 'accounts tab'],
        ['categories', 'categories tab'],
        ['tags', 'tags tab'],
    ])('lights Structure on the %s tab in the collapsed rail too', async (tab, marker) => {
        localStorage.setItem('coffer.sidebar.collapsed', 'true');
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/${tab}` });
        expect(await screen.findByText(marker)).toBeInTheDocument();
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Structure' })).toHaveAttribute('aria-current', 'page');
    });

    it('lights neither Structure nor the Overview on a register in the collapsed rail', async () => {
        localStorage.setItem('coffer.sidebar.collapsed', 'true');
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/accounts/a1` });
        expect(await screen.findByText('register')).toBeInTheDocument();
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Structure' })).not.toHaveAttribute('aria-current');
        expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current');
    });

    it('does not light Structure on the Overview', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}` });
        expect(await screen.findByText('ledger')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /^structure$/i })).not.toHaveAttribute('aria-current');
    });

    it('the ⌄ chevron opens the switch-ledger dropdown', async () => {
        const ledgers: LedgerSummary[] = [
            { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' },
            { id: '00000000-0000-0000-0000-000000000020', name: 'Demo', role: 'owner', currencyCode: 'USD' },
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
                name: 'Example Bank Checking',
                accountType: 'bank',
            }),
            makeAccount({
                id: 'a2',
                name: 'Rewards Card',
                accountType: 'credit_card',
            }),
            makeAccount({
                id: 'a3',
                name: 'Workplace 401(k)',
                accountType: 'investment',
            }),
            // Loan — grouped under its own "Loans" section, not a
            // catch-all "Other" bucket.
            makeAccount({
                id: 'a5',
                name: 'Home Mortgage',
                accountType: 'loan',
            }),
            // System Holdings sub-account — should be filtered out.
            makeAccount({
                id: 'a4',
                name: 'Holdings · IDX',
                accountType: 'holding',
                isSystem: true,
            }),
            // No inactive account here: the rail always fetches them (its
            // totals count them) and "Show inactive" only decides whether
            // their rows are drawn. That is covered in the balances and
            // folders tests below.
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
        // Each type gets its own labelled section; there is no generic
        // "Other" bucket.
        expect(screen.getByText('Loans')).toBeInTheDocument();
        expect(screen.queryByText('Other')).not.toBeInTheDocument();

        // The visible-account names render.
        expect(screen.getByText('Example Bank Checking')).toBeInTheDocument();
        expect(screen.getByText('Rewards Card')).toBeInTheDocument();
        expect(screen.getByText('Workplace 401(k)')).toBeInTheDocument();
        expect(screen.getByText('Home Mortgage')).toBeInTheDocument();

        // System Holdings sub-account is filtered out.
        expect(screen.queryByText(/Holdings · IDX/)).not.toBeInTheDocument();
        // Categories are filtered out of the rail.
        expect(screen.queryByText('Groceries')).not.toBeInTheDocument();
    });

    it('marks the active account row with aria-current=page', async () => {
        const accounts: AccountSummary[] = [
            makeAccount({ id: 'a1', name: 'Example Bank Checking' }),
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
            name: /example bank checking/i,
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

        // The account's own leading dot is the mark: blue, with the count
        // as its title and in the link's accessible name, in the words the home
        // header, the Accounts page and To review use.
        const flagged = await screen.findByRole('link', { name: /has review.*5 to review/i });
        const dot = flagged.querySelector('[title="5 to review"]')!;
        expect(dot).toHaveClass('bg-accent');

        // The other account keeps the quiet dot and says nothing.
        const quiet = screen.getByRole('link', { name: /^no review$/i });
        expect(quiet.querySelector('.bg-text-subtle')).not.toBeNull();
        expect(quiet.querySelector('.bg-accent')).toBeNull();
        expect(screen.queryAllByText(/to review/i)).toHaveLength(1);
        expect(screen.queryByText(/transactions?/i)).toBeNull();
    });

    it('says one row to review without a plural, in the title and to a screen reader alike', async () => {
        const accounts: AccountSummary[] = [makeAccount({ id: 'a1', name: 'One Row', needsReviewCount: 1 })];
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(accounts);

        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}`, accounts });

        const flagged = await screen.findByRole('link', { name: /one row.*1 to review/i });
        expect(flagged.querySelector('[title="1 to review"]')).toHaveClass('bg-accent');
        expect(within(flagged).getByText('1 to review')).toHaveClass('sr-only');
        expect(screen.queryByText(/transactions?/i)).toBeNull();
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
        // Fresh install: a dead "No ledger selected" <span> would leave no way
        // in from the rail.
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

    // Outside a ledger the rail takes the first visible ledger, which it does not
    // have until the ledger list arrives. Until then it has no ledger id, and a
    // read it sends without one goes to `/api/ledgers//…`, which the server can
    // only refuse.
    describe('outside a ledger, before the ledger list arrives', () => {
        afterEach(() => vi.unstubAllGlobals());

        it.each(['/', '/system'])('sends no ledger read without a ledger id on %s', async (initialPath) => {
            const stub = stubFetch().on('GET', /\/reports\/pins$/, []);
            renderInRouter({ initialPath, ledgers: null });

            // Settled once the rail has the ledger and asks for its pins: any read
            // made without the ledger was made before this one.
            await waitFor(() =>
                expect(stub.callsTo('GET', new RegExp(`^/api/ledgers/${LEDGER_ID}/reports/pins$`))).toHaveLength(1));
            expect(stub.calls.filter((c) => c.path.includes('//')).map((c) => `${c.method} ${c.path}`)).toEqual([]);
        });
    });

    // ------------------------------------------------------------------
    // Problem indicators (FU-049)
    //
    // These assert DELIVERY, not the dot. AttentionDot.test.tsx already proves the
    // component draws and labels itself; what can still be wrong — and would leave
    // every one of those tests green — is the rail never rendering one.
    // ------------------------------------------------------------------

    it('shows no indicator anywhere when nothing is outstanding', async () => {
        renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}`, attention: [] });

        // Anchored on the rail actually being up: without this, an empty query would
        // "pass" on a component that failed to render at all.
        expect(await screen.findByText('Coffer')).toBeInTheDocument();
        expect(screen.queryByText(/needs attention/i)).not.toBeInTheDocument();
    });

    // ------------------------------------------------------------------
    // The System gear: what the INSTALLATION needs (ADR-0102, amended for ADR-0110).
    // The wordmark answers for the ledgers; the gear answers for the install — a failed
    // backup, a job switched off, a release waiting — by the same dot. An admin reads it;
    // nobody else is asked.
    // ------------------------------------------------------------------

    const SINCE = '2026-11-02T12:00:00Z';

    it('marks the System gear for an available release, naming it', async () => {
        renderInRouter({
            isAdmin: true,
            systemAttention: {
                worst: 'warning',
                subjects: [{ subject: 'upgrade', severity: 'warning', since: SINCE }],
                upgradeVersion: '0.131.0',
            },
        });

        expect(
            await screen.findByRole('button', { name: 'System settings: Update available: 0.131.0' }),
        ).toBeInTheDocument();
    });

    it('marks the System gear for a deployment problem, counting subjects not events', async () => {
        renderInRouter({
            isAdmin: true,
            systemAttention: {
                worst: 'critical',
                subjects: [{ subject: 'backup', severity: 'critical', since: SINCE }],
                upgradeVersion: null,
            },
        });

        expect(
            await screen.findByRole('button', { name: 'System settings: 1 problem outstanding on this install' }),
        ).toBeInTheDocument();
    });

    it('says both when a problem and a release are outstanding, in one dot', async () => {
        renderInRouter({
            isAdmin: true,
            systemAttention: {
                worst: 'critical',
                subjects: [
                    { subject: 'backup', severity: 'critical', since: SINCE },
                    { subject: 'upgrade', severity: 'warning', since: SINCE },
                ],
                upgradeVersion: '0.131.0',
            },
        });

        expect(
            await screen.findByRole('button', {
                name: 'System settings: 1 problem outstanding on this install, and update available: 0.131.0',
            }),
        ).toBeInTheDocument();
    });

    it('leaves the gear plain when nothing is outstanding', async () => {
        renderInRouter({ isAdmin: true });

        expect(await screen.findByRole('button', { name: 'System settings' })).toBeInTheDocument();
    });

    it('never marks the gear for someone who is not an admin, whatever the cache holds', async () => {
        // The route is RequireAdmin, so the request is never made for anyone else. A cache that
        // somehow holds an answer (a previous session on a shared browser) must not draw it either.
        renderInRouter({
            isAdmin: false,
            systemAttention: {
                worst: 'warning',
                subjects: [{ subject: 'upgrade', severity: 'warning', since: SINCE }],
                upgradeVersion: '0.131.0',
            },
        });

        expect(await screen.findByRole('button', { name: 'System settings' })).toBeInTheDocument();
    });

    it('keeps the wordmark for problems in ledgers: a release waiting does not light it', async () => {
        renderInRouter({
            isAdmin: true,
            attention: [],
            systemAttention: {
                worst: 'warning',
                subjects: [{ subject: 'upgrade', severity: 'warning', since: SINCE }],
                upgradeVersion: '0.131.0',
            },
        });

        await screen.findByRole('button', { name: /^System settings: Update available/ });
        expect(screen.queryByText(/^Coffer needs attention/)).not.toBeInTheDocument();
    });

    it('marks the app root when any ledger has a problem', async () => {
        renderInRouter({
            initialPath: '/',
            attention: [{ ledgerId: LEDGER_ID, severity: 'warning', count: 1 }],
        });

        expect(
            await screen.findByText('Coffer needs attention: problems outstanding'),
        ).toBeInTheDocument();
    });

    it('marks Settings for the ledger in view, with its count', async () => {
        renderInRouter({
            initialPath: `/ledgers/${LEDGER_ID}`,
            attention: [{ ledgerId: LEDGER_ID, severity: 'critical', count: 2 }],
        });

        const settings = await screen.findByRole('link', { name: /settings/i });
        expect(
            within(settings).getByText(
                'This ledger needs attention: 2 critical problems outstanding',
            ),
        ).toBeInTheDocument();
    });

    it('does not mark Settings for a problem on a DIFFERENT ledger', async () => {
        // The trail has to point somewhere true. A per-ledger dot fed by the aggregate
        // would send the reader into a ledger that is fine, and the app root would be
        // the only honest indicator left.
        renderInRouter({
            initialPath: `/ledgers/${LEDGER_ID}`,
            attention: [
                { ledgerId: '00000000-0000-0000-0000-0000000000ff', severity: 'critical', count: 1 },
            ],
        });

        // The root still lights up — that is the anchor proving the data arrived.
        expect(
            await screen.findByText(
                'Coffer needs attention: critical problems outstanding',
            ),
        ).toBeInTheDocument();

        const settings = screen.getByRole('link', { name: /settings/i });
        expect(within(settings).queryByText(/needs attention/i)).not.toBeInTheDocument();
    });

});

// ----------------------------------------------------------------------
// Balances, folders, collapse and width (FU-006, FU-051, FU-052)
//
// The fetches are mocked at the API module, as above. Folder writes go to a
// small fake server, so the refetch that follows every write returns what the
// server would, and a test can tell an optimistic edit that stuck from one that
// was put back.
// ----------------------------------------------------------------------

const ACCOUNTS: AccountSummary[] = [
    makeAccount({ id: 'chk', name: 'Checking' }),
    makeAccount({ id: 'sav', name: 'Savings' }),
    makeAccount({ id: 'emg', name: 'Emergency fund' }),
    makeAccount({ id: 'card', name: 'Travel card', accountType: 'credit_card' }),
    makeAccount({ id: 'ira', name: 'Retirement account', accountType: 'investment' }),
    makeAccount({ id: 'plan', name: 'Workplace plan', accountType: 'investment' }),
];

const BALANCES: AccountBalance[] = [
    { accountId: 'chk', balance: 100, currencyCode: 'USD' },
    { accountId: 'sav', balance: 250.5, currencyCode: 'USD' },
    { accountId: 'emg', balance: 1000, currencyCode: 'USD' },
    { accountId: 'card', balance: -1240.5, currencyCode: 'USD' },
    { accountId: 'ira', balance: 19200, currencyCode: 'USD' },
    { accountId: 'plan', balance: 52100, currencyCode: 'USD' },
];

const FOLDERS: AccountFolder[] = [
    { id: 'everyday', accountType: 'bank', parentId: null, name: 'Everyday', accountIds: ['chk', 'sav'] },
    { id: 'long', accountType: 'investment', parentId: null, name: 'Long term', accountIds: ['plan'] },
    { id: 'tax', accountType: 'investment', parentId: 'long', name: 'Tax free', accountIds: ['ira'] },
];

const usd = (n: number) => formatCurrency(n, 'USD');
const dataTransfer = () => ({ setData: vi.fn(), effectAllowed: '', dropEffect: '' });

/**
 * The server's totals over `folders`: each type's over its accounts, and each folder's
 * over every account beneath it at any depth. The fixtures' balances are all in one
 * currency, so these sums stand in for the server's conversion; a test of conversion
 * passes its own totals to {@link fakeServer}.
 */
function serverSubtotals(folders: readonly AccountFolder[]): AccountSubtotals {
    const balanceOf = new Map(BALANCES.map((b) => [b.accountId, b.balance]));
    const sum = (ids: readonly string[]) => ids.reduce((s, id) => s + (balanceOf.get(id) ?? 0), 0);
    const beneath = (id: string): string[] => [
        ...(folders.find((f) => f.id === id)?.accountIds ?? []),
        ...folders.filter((f) => f.parentId === id).flatMap((f) => beneath(f.id)),
    ];
    const types = [...new Set(ACCOUNTS.map((a) => a.accountType))];
    return {
        currencyCode: 'USD',
        types: types.map((t) => ({
            accountType: t, amount: sum(ACCOUNTS.filter((a) => a.accountType === t).map((a) => a.id)), rateUnavailable: [],
        })),
        folders: folders.map((f) => ({ folderId: f.id, accountIds: beneath(f.id), amount: sum(beneath(f.id)), rateUnavailable: [] })),
    };
}

/** The folder list as a server would keep it, behind the mocked API, with the totals over it. */
function fakeServer(
    initial: AccountFolder[] = FOLDERS,
    totals: (folders: readonly AccountFolder[]) => AccountSubtotals = serverSubtotals,
) {
    let folders = initial;
    vi.spyOn(apiModule, 'fetchAccountFolders').mockImplementation(async () => folders);
    const subtotals = vi.spyOn(apiModule, 'fetchAccountSubtotals').mockImplementation(async () => totals(folders));
    const setFolder = vi.spyOn(apiModule, 'setAccountFolder').mockImplementation(
        async (_ledger, accountId, folderId) => {
            folders = folders.map((f) => {
                const without = f.accountIds.filter((id) => id !== accountId);
                return { ...f, accountIds: f.id === folderId ? [...without, accountId] : without };
            });
        },
    );
    const moveFolder = vi.spyOn(apiModule, 'moveAccountFolder').mockImplementation(
        async (_ledger, folderId, parentId) => {
            folders = folders.map((f) => (f.id === folderId ? { ...f, parentId } : f));
        },
    );
    const create = vi.spyOn(apiModule, 'createAccountFolder').mockImplementation(
        async (_ledger, request) => {
            const made: AccountFolder = {
                id: `new-${request.name.toLowerCase()}`,
                accountType: request.accountType,
                parentId: request.parentId ?? null,
                name: request.name,
                accountIds: [],
            };
            folders = [...folders, made];
            return made;
        },
    );
    const rename = vi.spyOn(apiModule, 'renameAccountFolder').mockImplementation(
        async (_ledger, folderId, request) => {
            folders = folders.map((f) => (f.id === folderId ? { ...f, name: request.name } : f));
            return folders.find((f) => f.id === folderId)!;
        },
    );
    const remove = vi.spyOn(apiModule, 'deleteAccountFolder').mockImplementation(
        async (_ledger, folderId) => {
            const gone = folders.find((f) => f.id === folderId)!;
            // What it held moves up one level, as the API does it.
            folders = folders
                .filter((f) => f.id !== folderId)
                .map((f) => (f.parentId === folderId ? { ...f, parentId: gone.parentId } : f))
                .map((f) => (f.id === gone.parentId ? { ...f, accountIds: [...f.accountIds, ...gone.accountIds] } : f));
        },
    );
    return { setFolder, moveFolder, create, rename, remove, subtotals };
}

function renderRail(opts: {
    role?: string;
    accounts?: AccountSummary[];
    balances?: AccountBalance[];
    attention?: LedgerAttention[];
    strict?: boolean;
} = {}) {
    const ledgers: LedgerSummary[] = [{ id: LEDGER_ID, name: 'Personal', role: opts.role ?? 'owner', currencyCode: 'USD' }];
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(opts.accounts ?? ACCOUNTS);
    vi.spyOn(apiModule, 'fetchAccountBalances').mockResolvedValue(opts.balances ?? BALANCES);
    return renderInRouter({
        initialPath: `/ledgers/${LEDGER_ID}`,
        ledgers,
        attention: opts.attention,
        strict: opts.strict,
    });
}

const folderRow = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}`) });
const accountRow = (name: string) => screen.getByRole('link', { name: new RegExp(`^${name}`) });
/** A section heading, the drop target for the top level of its section. */
const heading = (label: string) => screen.getByText(label).closest('div')!;

describe('AuthedSidebar — balances and folders', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        window.scrollTo = vi.fn();
        localStorage.clear();
        document.documentElement.style.removeProperty(WIDTH_VAR);
    });

    it('shows every balance, folder sum and section total, liabilities negative', async () => {
        fakeServer();
        renderRail();

        expect(await screen.findByText(usd(100))).toBeInTheDocument();
        expect(accountRow('Savings')).toHaveTextContent(usd(250.5));
        // Everyday holds Checking and Savings; Banking adds Emergency fund.
        expect(folderRow('Everyday')).toHaveTextContent(usd(350.5));
        expect(heading('Banking')).toHaveTextContent(usd(1350.5));
        // A card is owed, and shows negative, as the dashboard shows it.
        expect(accountRow('Travel card')).toHaveTextContent(usd(-1240.5));
        expect(heading('Credit cards')).toHaveTextContent(usd(-1240.5));
    });

    it('rolls a grandchild up through every folder above it', async () => {
        fakeServer();
        renderRail();

        await waitFor(() => expect(folderRow('Tax free')).toHaveTextContent(usd(19200)));
        expect(folderRow('Long term')).toHaveTextContent(usd(71300));
        expect(heading('Investments')).toHaveTextContent(usd(71300));
        // Names truncate; the full path is in the title.
        expect(accountRow('Retirement account')).toHaveAttribute(
            'title', 'Long term › Tax free › Retirement account',
        );
    });

    describe('accounts in more than one currency (ADR-0114 D7, D9)', () => {
        // Savings holds euros; the server converts them, so Everyday is Checking's dollars
        // plus Savings' euros at their rate, never the two figures added as they stand.
        const euroSavings = BALANCES.map((b) => (b.accountId === 'sav' ? { ...b, currencyCode: 'EUR' } : b));
        const converted = (folders: readonly AccountFolder[]): AccountSubtotals => {
            const totals = serverSubtotals(folders);
            return {
                ...totals,
                types: totals.types.map((t) => (t.accountType === 'bank' ? { ...t, amount: 1400 } : t)),
                folders: totals.folders.map((f) => (f.folderId === 'everyday' ? { ...f, amount: 400 } : f)),
            };
        };

        it('shows the server\'s converted totals, and each account in its own currency', async () => {
            fakeServer(FOLDERS, converted);
            renderRail({ balances: euroSavings });

            await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(400)));
            expect(folderRow('Everyday')).not.toHaveTextContent(usd(350.5));
            expect(folderRow('Everyday')).not.toHaveTextContent('mixed');
            expect(heading('Banking')).toHaveTextContent(usd(1400));
            expect(accountRow('Savings')).toHaveTextContent(formatCurrency(250.5, 'EUR'));
            expect(accountRow('Checking')).toHaveTextContent(usd(100));
        });

        it('reads a total that needs a missing rate "no rate", naming the rate, and keeps every other', async () => {
            const missing: RateUnavailable = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-11', reason: 'rate_unavailable' };
            fakeServer(FOLDERS, (folders) => {
                const totals = serverSubtotals(folders);
                const none = { amount: null, rateUnavailable: [missing] };
                return {
                    ...totals,
                    types: totals.types.map((t) => (t.accountType === 'bank' ? { ...t, ...none } : t)),
                    folders: totals.folders.map((f) => (f.folderId === 'everyday' ? { ...f, ...none } : f)),
                };
            });
            renderRail({ balances: euroSavings });

            await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent('no rate'));
            expect(within(folderRow('Everyday')).getByText('no rate')).toHaveAttribute('title', 'No EUR to USD rate for Mar 11, 2026.');
            expect(within(heading('Banking')).getByText('no rate')).toBeInTheDocument();
            expect(heading('Investments')).toHaveTextContent(usd(71300));
            expect(accountRow('Savings')).toHaveTextContent(formatCurrency(250.5, 'EUR'));
        });
    });

    it('never shows a folder the old total while a move into it is unanswered', async () => {
        const server = fakeServer();
        renderRail();
        await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(350.5)));

        // The move is held, so the rail draws it before the server has it.
        let finish: () => void = () => {};
        server.setFolder.mockImplementation(() => new Promise<void>((resolve) => { finish = resolve; }));
        fireEvent.dragStart(accountRow('Emergency fund'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(folderRow('Everyday'), { dataTransfer: dataTransfer() });
        fireEvent.drop(folderRow('Everyday'), { dataTransfer: dataTransfer() });

        await waitFor(() =>
            expect(accountRow('Emergency fund')).toHaveAttribute('title', 'Everyday › Emergency fund'));
        // The server's total is over Checking and Savings alone: not this folder's now.
        expect(folderRow('Everyday')).not.toHaveTextContent('$');
        expect(heading('Banking')).toHaveTextContent(usd(1350.5));

        finish();
        await waitFor(() => expect(server.subtotals).toHaveBeenCalledTimes(2));
    });

    describe('review mark on a folder', () => {
        // Retirement account sits in Tax free, inside Long term.
        const flagged = ACCOUNTS.map((a) => (a.id === 'ira' ? { ...a, needsReviewCount: 2 } : a));
        const icon = (name: string) => folderRow(name).querySelector('svg:nth-of-type(2)')!;

        it('tints a collapsed folder, and every collapsed folder above the account', async () => {
            fakeServer();
            renderRail({ accounts: flagged });
            await screen.findByText('Tax free');

            // Open, the accounts say it themselves; the folders stay quiet.
            expect(icon('Long term')).not.toHaveClass('text-accent');
            expect(icon('Tax free')).not.toHaveClass('text-accent');

            fireEvent.click(folderRow('Tax free'));
            expect(icon('Tax free')).toHaveClass('text-accent');
            expect(folderRow('Tax free')).toHaveTextContent('contains accounts to review');
            expect(icon('Long term')).not.toHaveClass('text-accent');

            fireEvent.click(folderRow('Long term'));
            expect(icon('Long term')).toHaveClass('text-accent');
        });

        it('leaves a collapsed folder quiet when nothing inside needs review', async () => {
            fakeServer();
            renderRail({ accounts: flagged });
            await screen.findByText('Everyday');

            fireEvent.click(folderRow('Everyday'));
            expect(icon('Everyday')).not.toHaveClass('text-accent');
            expect(folderRow('Everyday')).not.toHaveTextContent('to review');
        });
    });

    it('has no tab strip', async () => {
        fakeServer();
        renderRail();
        expect(await screen.findByText('Everyday')).toBeInTheDocument();
        expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /new tab/i })).not.toBeInTheDocument();
    });

    it('drags an account into a folder', async () => {
        const server = fakeServer();
        renderRail();
        await screen.findByText('Everyday');

        fireEvent.dragStart(accountRow('Emergency fund'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(folderRow('Everyday'), { dataTransfer: dataTransfer() });
        expect(folderRow('Everyday')).toHaveClass('bg-accent-soft');
        fireEvent.drop(folderRow('Everyday'), { dataTransfer: dataTransfer() });

        await waitFor(() => expect(server.setFolder).toHaveBeenCalledWith(LEDGER_ID, 'emg', 'everyday'));
        await waitFor(() =>
            expect(accountRow('Emergency fund')).toHaveAttribute('title', 'Everyday › Emergency fund'));
        // The server's total over the folder's accounts now, refreshed by the move.
        await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(1350.5)));
    });

    it('drags a folder onto its section heading, to the top level', async () => {
        const server = fakeServer();
        renderRail();
        await screen.findByText('Tax free');

        fireEvent.dragStart(folderRow('Tax free'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(heading('Investments'), { dataTransfer: dataTransfer() });
        expect(heading('Investments')).toHaveClass('bg-accent-soft');
        fireEvent.drop(heading('Investments'), { dataTransfer: dataTransfer() });

        await waitFor(() => expect(server.moveFolder).toHaveBeenCalledWith(LEDGER_ID, 'tax', null));
        await waitFor(() =>
            expect(accountRow('Retirement account')).toHaveAttribute('title', 'Tax free › Retirement account'));
    });

    it('refuses a target of another account type, and a folder dropped into its own sub-folder', async () => {
        const server = fakeServer();
        renderRail();
        await screen.findByText('Tax free');

        fireEvent.dragStart(accountRow('Checking'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(folderRow('Long term'), { dataTransfer: dataTransfer() });
        expect(folderRow('Long term')).toHaveClass('cursor-not-allowed');
        fireEvent.drop(folderRow('Long term'), { dataTransfer: dataTransfer() });
        fireEvent.dragEnd(accountRow('Checking'));

        fireEvent.dragStart(folderRow('Long term'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(folderRow('Tax free'), { dataTransfer: dataTransfer() });
        expect(folderRow('Tax free')).toHaveClass('cursor-not-allowed');
        fireEvent.drop(folderRow('Tax free'), { dataTransfer: dataTransfer() });

        expect(server.setFolder).not.toHaveBeenCalled();
        expect(server.moveFolder).not.toHaveBeenCalled();
        expect(folderRow('Tax free')).not.toHaveClass('cursor-not-allowed');
    });

    it('puts a refused move back and says why', async () => {
        fakeServer();
        vi.spyOn(apiModule, 'setAccountFolder').mockRejectedValue(new Error('The folder holds another account type.'));
        const { queryClient } = renderRail();
        await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(350.5)));

        // From here the refetch that follows every move hangs, so what is on
        // screen after the refusal is the rollback's doing alone, not the
        // server's list coming back.
        let release: (folders: AccountFolder[]) => void = () => {};
        const refetch = vi.spyOn(apiModule, 'fetchAccountFolders').mockImplementation(
            () => new Promise<AccountFolder[]>((resolve) => { release = resolve; }),
        );
        refetch.mockClear();

        fireEvent.dragStart(accountRow('Emergency fund'), { dataTransfer: dataTransfer() });
        fireEvent.dragOver(folderRow('Everyday'), { dataTransfer: dataTransfer() });
        fireEvent.drop(folderRow('Everyday'), { dataTransfer: dataTransfer() });

        expect(await screen.findByRole('alert')).toHaveTextContent(
            'Could not move Emergency fund: The folder holds another account type.',
        );
        await waitFor(() => expect(refetch).toHaveBeenCalled());
        expect(accountRow('Emergency fund')).toHaveAttribute('title', 'Emergency fund');
        expect(folderRow('Everyday')).toHaveTextContent(usd(350.5));

        // And the server's answer, when it lands, agrees.
        release(FOLDERS);
        await waitFor(() =>
            expect(queryClient.getQueryState(['account-folders', LEDGER_ID])?.fetchStatus).toBe('idle'));
        expect(accountRow('Emergency fund')).toHaveAttribute('title', 'Emergency fund');
        expect(folderRow('Everyday')).toHaveTextContent(usd(350.5));
    });

    it('hides an inactive account until asked, and counts it in every total either way', async () => {
        fakeServer();
        renderRail({
            accounts: ACCOUNTS.map((a) => (a.id === 'sav' ? { ...a, isActive: false } : a)),
        });

        // Anchored on both the account list and the figures being in: Checking
        // is drawn from the one, the folder's sum from the other.
        expect(await screen.findByText('Checking')).toBeInTheDocument();
        await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(350.5)));
        // Savings is inactive: no row, but its balance is in the folder and the section.
        expect(screen.queryByText('Savings')).not.toBeInTheDocument();
        expect(heading('Banking')).toHaveTextContent(usd(1350.5));

        await userEvent.setup().click(screen.getByRole('checkbox', { name: 'Show inactive accounts' }));
        expect(await screen.findByText('Savings')).toHaveClass('line-through');
        expect(accountRow('Savings')).toHaveTextContent(usd(250.5));
        // Ticking the box changes which rows are drawn, never a total.
        expect(folderRow('Everyday')).toHaveTextContent(usd(350.5));
        expect(heading('Banking')).toHaveTextContent(usd(1350.5));
    });

    it('refreshes the accounts and the home screen\'s reads when an account is deactivated or reactivated', async () => {
        fakeServer();
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        const { queryClient } = renderRail({
            accounts: ACCOUNTS.map((a) => (a.id === 'sav' ? { ...a, isActive: false } : a)),
        });
        const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
        const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
        // The rail's own totals with them: they are the overview's balances summed.
        const homeReads = [
            ['home', LEDGER_ID], ['overview', LEDGER_ID], ['budget-progress', LEDGER_ID],
            ['account-folders', LEDGER_ID, 'subtotals'],
        ];
        const user = userEvent.setup();
        await screen.findByText('Checking');

        fireEvent.contextMenu(accountRow('Checking'));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Deactivate' }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Deactivate' }));
        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'chk', false));
        await waitFor(() => expect(keys()).toContainEqual(['accounts', LEDGER_ID]));
        for (const key of homeReads) expect(keys()).toContainEqual(key);

        invalidated.mockClear();
        await user.click(screen.getByRole('checkbox', { name: 'Show inactive accounts' }));
        fireEvent.contextMenu(accountRow('Savings'));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Reactivate' }));
        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'sav', true));
        await waitFor(() => expect(keys()).toContainEqual(['accounts', LEDGER_ID]));
        for (const key of homeReads) expect(keys()).toContainEqual(key);
    });

    describe('focus when a deactivated account leaves the rail', () => {
        // ADR-0023 §L: a row its own menu takes away hands focus to the same
        // control on the next row drawn, else the previous one, never to the
        // page; with inactive accounts hidden, a deactivate takes the row away.
        function serveActive(initial: AccountSummary[] = ACCOUNTS) {
            let accounts = initial;
            vi.spyOn(apiModule, 'fetchAccounts').mockImplementation(async () => accounts);
            let release: () => void = () => {};
            const held = new Promise<void>((resolve) => { release = resolve; });
            vi.spyOn(apiModule, 'setAccountActive').mockImplementation(async (_ledger, id, active) => {
                await held;
                accounts = accounts.map((a) => (a.id === id ? { ...a, isActive: active } : a));
            });
            return { release };
        }

        async function deactivate(user: ReturnType<typeof userEvent.setup>) {
            await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Deactivate' }));
            await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Deactivate' }));
        }

        it('gives focus to the next drawn row\'s link, and keeps it on the opener while the request is in flight', async () => {
            fakeServer();
            renderRail();
            const { release } = serveActive();
            const user = userEvent.setup();
            await screen.findByText('Checking');

            const checking = accountRow('Checking');
            checking.focus();
            fireEvent.contextMenu(checking);
            await deactivate(user);
            await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
            expect(checking).toHaveFocus();

            release();
            await waitFor(() => expect(screen.queryByRole('link', { name: /^Checking/ })).toBeNull());
            expect(accountRow('Savings')).toHaveFocus();
        });

        it('gives focus to the previous drawn row\'s link when the last row drawn goes', async () => {
            // Only the Everyday folder and its two accounts are drawn.
            fakeServer([FOLDERS[0]!]);
            renderRail({ accounts: ACCOUNTS.filter((a) => a.id !== 'emg' && a.accountType === 'bank') });
            const { release } = serveActive(ACCOUNTS.filter((a) => a.id !== 'emg' && a.accountType === 'bank'));
            const user = userEvent.setup();
            await screen.findByText('Savings');

            const savings = accountRow('Savings');
            savings.focus();
            fireEvent.contextMenu(savings);
            await deactivate(user);
            release();

            await waitFor(() => expect(screen.queryByRole('link', { name: /^Savings/ })).toBeNull());
            expect(accountRow('Checking')).toHaveFocus();
        });

        it('hands the selected row\'s kebab\'s focus to the next drawn row\'s link', async () => {
            fakeServer();
            vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([{ id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' }]);
            vi.spyOn(apiModule, 'fetchAccountBalances').mockResolvedValue(BALANCES);
            const { release } = serveActive();
            renderInRouter({ initialPath: `/ledgers/${LEDGER_ID}/accounts/chk`, ledgers: [{ id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' }] });
            const user = userEvent.setup();

            const kebab = await screen.findByRole('button', { name: 'Actions for Checking' });
            kebab.focus();
            await user.keyboard('{Enter}');
            await deactivate(user);
            await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
            expect(kebab).toHaveFocus();

            release();
            await waitFor(() => expect(screen.queryByRole('button', { name: 'Actions for Checking' })).toBeNull());
            expect(accountRow('Savings')).toHaveFocus();
        });
    });

    it("offers an account's moves on its menu, by path, and nothing that makes a folder", async () => {
        const server = fakeServer();
        renderRail();
        await screen.findByText('Tax free');

        fireEvent.contextMenu(accountRow('Workplace plan'));
        const menu = await screen.findByRole('menu');
        const labels = within(menu).getAllByRole('menuitem').map((i) => i.textContent);
        expect(labels).toEqual(['Move to: Long term › Tax free', 'Move to top level', 'Deactivate']);
        expect(labels.some((l) => /new folder/i.test(l ?? ''))).toBe(false);

        await userEvent.setup().click(within(menu).getByRole('menuitem', { name: 'Move to: Long term › Tax free' }));
        await waitFor(() => expect(server.setFolder).toHaveBeenCalledWith(LEDGER_ID, 'plan', 'tax'));
        // The row remounted in its new place, and focus went with it.
        await waitFor(() =>
            expect(accountRow('Workplace plan')).toHaveAttribute('title', 'Long term › Tax free › Workplace plan'));
        expect(accountRow('Workplace plan')).toHaveFocus();
    });

    it("offers a folder's edits and moves on its menu", async () => {
        const server = fakeServer();
        renderRail();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for folder Tax free' }));
        const menu = await screen.findByRole('menu');
        expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual([
            'Rename', 'New folder inside…', 'Move to top level', 'Delete folder',
        ]);
        await user.click(within(menu).getByRole('menuitem', { name: 'Move to top level' }));
        await waitFor(() => expect(server.moveFolder).toHaveBeenCalledWith(LEDGER_ID, 'tax', null));
        await waitFor(() =>
            expect(accountRow('Retirement account')).toHaveAttribute('title', 'Tax free › Retirement account'));
        expect(folderRow('Tax free')).toHaveFocus();

        await user.click(screen.getByRole('button', { name: 'Actions for folder Everyday' }));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Delete folder' }));
        await waitFor(() => expect(server.remove).toHaveBeenCalledWith(LEDGER_ID, 'everyday'));
        // Its accounts move up to the top level, and so does focus: to the
        // section's new-folder button, since the row it was on is gone.
        await waitFor(() => expect(accountRow('Checking')).toHaveAttribute('title', 'Checking'));
        expect(screen.queryByRole('button', { name: /^Everyday/ })).not.toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'New folder in Banking' })).toHaveFocus();
    });

    it('hands focus back after naming a folder from the keyboard, and on to a new folder when it arrives', async () => {
        fakeServer();
        renderRail();
        const user = userEvent.setup();
        const plus = await screen.findByRole('button', { name: 'New folder in Banking' });

        // Escape: back on the button that opened it.
        await user.click(plus);
        expect(screen.getByRole('textbox', { name: 'New folder name' })).toHaveFocus();
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('textbox', { name: 'New folder name' })).not.toBeInTheDocument();
        expect(plus).toHaveFocus();

        // Enter: on the opener at once, then on the new folder's row.
        await user.click(plus);
        await user.keyboard('Bills{Enter}');
        await waitFor(() => expect(folderRow('Bills')).toHaveFocus());

        // Rename from a folder's menu: back on that folder's row.
        await user.click(screen.getByRole('button', { name: 'Actions for folder Everyday' }));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Rename' }));
        // The old name is selected on open, so typing replaces it.
        await user.keyboard('Daily{Enter}');
        await waitFor(() => expect(folderRow('Daily')).toHaveFocus());
    });

    it('leaves focus where the reader sent it when naming ends on a blur', async () => {
        const server = fakeServer();
        renderRail();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New folder in Banking' }));
        await user.keyboard('Bills');
        const elsewhere = screen.getByRole('link', { name: 'Overview' });
        await user.click(elsewhere);
        await waitFor(() => expect(server.create).toHaveBeenCalled());
        await screen.findByRole('button', { name: /^Bills/ });
        expect(screen.getByRole('button', { name: 'New folder in Banking' })).not.toHaveFocus();
        expect(folderRow('Bills')).not.toHaveFocus();
    });

    it('names a new folder in place from the section heading, and renames one the same way', async () => {
        fakeServer();
        const create = vi.spyOn(apiModule, 'createAccountFolder').mockResolvedValue({
            id: 'bills', accountType: 'bank', parentId: null, name: 'Bills', accountIds: [],
        });
        const rename = vi.spyOn(apiModule, 'renameAccountFolder').mockResolvedValue({
            ...FOLDERS[0]!, name: 'Daily',
        });
        renderRail();
        const user = userEvent.setup();

        // The folder-plus, named for its section.
        await user.click(await screen.findByRole('button', { name: 'New folder in Banking' }));
        await user.type(screen.getByRole('textbox', { name: 'New folder name' }), 'Bills{Enter}');
        await waitFor(() => expect(create).toHaveBeenCalledWith(LEDGER_ID, {
            accountType: 'bank', name: 'Bills', parentId: null,
        }));

        await user.click(screen.getByRole('button', { name: 'Actions for folder Everyday' }));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Rename' }));
        const input = screen.getByRole('textbox', { name: 'Folder name' });
        expect(input).toHaveValue('Everyday');
        await user.clear(input);
        await user.type(input, 'Daily{Enter}');
        await waitFor(() => expect(rename).toHaveBeenCalledWith(LEDGER_ID, 'everyday', { name: 'Daily' }));
    });

    it('creates a sub-folder from "New folder inside…"', async () => {
        fakeServer();
        const create = vi.spyOn(apiModule, 'createAccountFolder').mockResolvedValue({
            id: 'deep', accountType: 'investment', parentId: 'tax', name: 'Deep', accountIds: [],
        });
        renderRail();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for folder Tax free' }));
        await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'New folder inside…' }));
        await user.type(screen.getByRole('textbox', { name: 'New folder name' }), 'Deep{Enter}');
        await waitFor(() => expect(create).toHaveBeenCalledWith(LEDGER_ID, {
            accountType: 'investment', name: 'Deep', parentId: 'tax',
        }));
    });

    it('offers a viewer no folder edit, no move and no drag', async () => {
        fakeServer();
        renderRail({ role: 'viewer' });

        // Anchor on the data being up: the folder and its figure.
        await waitFor(() => expect(folderRow('Everyday')).toHaveTextContent(usd(350.5)));
        expect(screen.queryByRole('button', { name: /^new folder in/i })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^actions for folder/i })).not.toBeInTheDocument();
        expect(accountRow('Checking')).not.toHaveAttribute('draggable');
        expect(folderRow('Everyday')).not.toHaveAttribute('draggable');

        fireEvent.contextMenu(accountRow('Emergency fund'));
        const menu = await screen.findByRole('menu');
        expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['No actions available']);
    });

    it('lets an editor do everything an owner can', async () => {
        fakeServer();
        renderRail({ role: 'editor' });
        expect(await screen.findByRole('button', { name: 'New folder in Banking' })).toBeInTheDocument();
        expect(accountRow('Checking')).toHaveAttribute('draggable', 'true');
    });

    it('remembers a closed folder in this browser', async () => {
        fakeServer();
        const first = renderRail();
        await screen.findByText('Checking');

        fireEvent.click(folderRow('Everyday'));
        expect(folderRow('Everyday')).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByText('Checking')).not.toBeInTheDocument();
        // Still summed while closed.
        expect(folderRow('Everyday')).toHaveTextContent(usd(350.5));
        first.view.unmount();

        renderRail();
        await waitFor(() => expect(folderRow('Everyday')).toHaveAttribute('aria-expanded', 'false'));
        expect(screen.queryByText('Checking')).not.toBeInTheDocument();
        expect(screen.getByText('Emergency fund')).toBeInTheDocument();
    });
});

describe('AuthedSidebar — collapse and width', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        window.scrollTo = vi.fn();
        localStorage.clear();
        document.documentElement.style.removeProperty(WIDTH_VAR);
        fakeServer();
    });

    it('collapses to the icon rail, names each type with its total, and remembers it', async () => {
        const first = renderRail();
        const user = userEvent.setup();
        await screen.findByText('Everyday');

        await user.click(screen.getByRole('button', { name: 'Collapse sidebar' }));
        // The button that was pressed went with the shape; focus is on its counterpart.
        expect(screen.getByRole('button', { name: 'Expand sidebar' })).toHaveFocus();
        expect(localStorage.getItem('coffer.sidebar.collapsed')).toBe('true');
        const banking = screen.getByRole('button', { name: `Banking, ${usd(1350.5)}` });
        expect(banking.getAttribute('title')).toContain(`Everyday, ${usd(350.5)}`);
        expect(screen.getByRole('button', { name: `Credit cards, ${usd(-1240.5)}` })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Overview' })).toBeInTheDocument();
        expect(screen.queryByText('Everyday')).not.toBeInTheDocument();
        first.view.unmount();

        renderRail();
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: `Banking, ${usd(1350.5)}` }));
        expect(await screen.findByText('Everyday')).toBeInTheDocument();
        expect(localStorage.getItem('coffer.sidebar.collapsed')).toBe('false');
    });

    it('keeps the logo and every destination icon in one column when something needs attention', async () => {
        localStorage.setItem('coffer.sidebar.collapsed', 'true');
        renderRail({ attention: [{ ledgerId: LEDGER_ID, severity: 'critical', count: 2 }] });

        // Delivered on the collapsed rail too, for the ledger and for the app.
        const settings = await screen.findByRole('link', { name: 'Settings' });
        const dot = within(settings).getByText('This ledger needs attention: 2 critical problems outstanding');
        // Laid over the icon's corner, not beside it, so the icon is not pushed
        // out of the column the other icons are centred in.
        expect(dot.parentElement).toHaveClass('absolute');
        const logo = screen.getByRole('link', { name: 'Coffer' });
        expect(within(logo).getByText(/^Coffer needs attention/).parentElement).toHaveClass('absolute');
    });

    it('takes no focus when it mounts, even under StrictMode', async () => {
        renderRail({ strict: true });
        const collapse = await screen.findByRole('button', { name: 'Collapse sidebar' });
        await screen.findByText('Everyday');
        expect(collapse).not.toHaveFocus();
        expect(document.body).toHaveFocus();
    });

    it('toggles on Ctrl+\\ from anywhere but a text field', async () => {
        renderRail();
        await screen.findByText('Everyday');

        fireEvent.keyDown(window, { key: '\\', ctrlKey: true });
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();

        const field = document.createElement('input');
        document.body.appendChild(field);
        try {
            fireEvent.keyDown(field, { key: '\\', ctrlKey: true });
            expect(screen.getByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
        } finally {
            field.remove();
        }

        fireEvent.keyDown(window, { key: '\\', ctrlKey: true });
        expect(await screen.findByRole('button', { name: 'Collapse sidebar' })).toBeInTheDocument();
    });

    it('resizes from the keyboard on the handle, and keeps its keys from the register', async () => {
        renderRail();
        const handle = await screen.findByRole('separator', { name: 'Resize sidebar' });
        expect(handle).toHaveAttribute('aria-valuenow', '224');
        const outer = vi.fn();
        document.addEventListener('keydown', outer);
        try {
            fireEvent.keyDown(handle, { key: 'ArrowRight' });
            fireEvent.keyDown(handle, { key: 'n' });
        } finally {
            document.removeEventListener('keydown', outer);
        }
        expect(outer).not.toHaveBeenCalled();
        expect(handle).toHaveAttribute('aria-valuenow', '240');
        expect(localStorage.getItem('coffer.sidebar.width')).toBe('240');
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('240px');

        // Double-click goes back to the default, which is stored as nothing.
        fireEvent.doubleClick(handle);
        expect(handle).toHaveAttribute('aria-valuenow', '224');
        expect(localStorage.getItem('coffer.sidebar.width')).toBeNull();
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('');

        fireEvent.keyDown(handle, { key: 'Enter' });
        expect(await screen.findByRole('button', { name: 'Expand sidebar' })).toBeInTheDocument();
    });

    it('saves a drag on pointer-up, not on every move', async () => {
        renderRail();
        const handle = await screen.findByRole('separator', { name: 'Resize sidebar' });

        fireEvent.pointerDown(handle, { button: 0, clientX: 224, pointerId: 1 });
        fireEvent.pointerMove(handle, { clientX: 280, pointerId: 1 });
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('280px');
        expect(localStorage.getItem('coffer.sidebar.width')).toBeNull();
        fireEvent.pointerUp(handle, { clientX: 280, pointerId: 1 });
        expect(localStorage.getItem('coffer.sidebar.width')).toBe('280');
        expect(handle).toHaveAttribute('aria-valuenow', '280');
    });

    it('opens at the remembered width', async () => {
        localStorage.setItem('coffer.sidebar.width', '300');
        renderRail();
        expect(await screen.findByRole('separator', { name: 'Resize sidebar' }))
            .toHaveAttribute('aria-valuenow', '300');
    });

    it('draws the width it announces after the window narrows, and keeps the saved one', async () => {
        // Chosen on a wider window and applied at that page load by
        // theme-init.js; this window cannot hold it.
        localStorage.setItem('coffer.sidebar.width', '470');
        document.documentElement.style.setProperty(WIDTH_VAR, '470px');
        const fits = maxWidthFor(window.innerWidth);
        expect(fits).toBeLessThan(470);

        renderRail();
        const handle = await screen.findByRole('separator', { name: 'Resize sidebar' });
        expect(handle).toHaveAttribute('aria-valuenow', String(fits));
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe(`${fits}px`);
        expect(localStorage.getItem('coffer.sidebar.width')).toBe('470');

        // And again when the window narrows with the rail open.
        const wide = window.innerWidth;
        Object.defineProperty(window, 'innerWidth', { configurable: true, value: 600 });
        try {
            fireEvent(window, new Event('resize'));
            expect(handle).toHaveAttribute('aria-valuenow', String(maxWidthFor(600)));
            expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe(`${maxWidthFor(600)}px`);
        } finally {
            Object.defineProperty(window, 'innerWidth', { configurable: true, value: wide });
            fireEvent(window, new Event('resize'));
        }
        expect(handle).toHaveAttribute('aria-valuenow', String(fits));
        expect(localStorage.getItem('coffer.sidebar.width')).toBe('470');
    });
});
