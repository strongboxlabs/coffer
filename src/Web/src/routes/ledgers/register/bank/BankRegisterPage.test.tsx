import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { BankRegisterPage } from './BankRegisterPage';
import { ApiError } from '@/lib/api';
import * as apiModule from '@/lib/api';
import type {
    AccountSummary,
    BankRow,
    LedgerSummary,
    RegisterEntry,
} from '@/lib/types';

// Smoke tests for the register page. We don't try to test the
// list virtualization directly — jsdom has no layout, so virtuoso
// can't compute scroll offsets meaningfully and `startReached` /
// `endReached` never fire. What we DO lock down:
//
//   * empty state renders the explanatory copy
//   * populated state renders the footer wiring with the
//     correct row count
//   * API failure surfaces an alert with ApiError.detail
//   * Scheduled filter button reflects the future-dated count
//     and toggles aria-pressed
//   * breadcrumbs link to the landing and parent-ledger pages
//
// The bidirectional sliding-window pagination (`startReached` /
// `endReached` on virtuoso) is exercised by the integration tests
// at the API layer + the spike smoke test in the browser; testing
// it here would require pinning virtuoso internals against a
// layout-less DOM, which is more brittle than valuable.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000100';
const FOCUS_ID = '00000000-0000-0000-0000-0000000000f0';

const TEST_LEDGER: LedgerSummary = {
    id: LEDGER_ID,
    name: 'Personal',
    role: 'owner',
};
const TEST_ACCOUNT: AccountSummary = {
    id: ACCOUNT_ID,
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Checking',
    accountType: 'bank',
    categoryKind: null,
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: null,
        isTradeCommission: false,
};

function makeTxn(
    overrides: Partial<BankRow> & { id: string },
): BankRow {
    const defaults: BankRow = {
        kind: 'bank',
        id: '',
        accountId: ACCOUNT_ID,
        payee: 'Coffee Shop',
        memo: null,
        amount: -4.5,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: 100,
        origin: 'manual',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: '00000000-0000-0000-0000-000000000999',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        headerId: '00000000-0000-0000-0000-000000000000',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-01T12:00:00Z',
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
    };
    return { ...defaults, ...overrides };
}

/** Wrap a single transaction into a single-txn register entry. */
function entryOf(t: BankRow): RegisterEntry {
    return { kind: 'txn', txn: t, groupId: null, legs: null };
}

function renderRegister(
    initialUrl = `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}`,
) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    // Pre-seed the cached ledgers + accounts so the header reads
    // them without an extra fetch.
    queryClient.setQueryData(['ledgers'], [TEST_LEDGER]);
    queryClient.setQueryData(['accounts', LEDGER_ID], [TEST_ACCOUNT]);

    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <main>landing</main>,
    });
    const detailRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>detail</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, landingRoute, detailRoute]),
        history: createMemoryHistory({
            initialEntries: [initialUrl],
        }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('BankRegisterPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('renders the empty-state copy when the account has no transactions', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        // The cached ['ledgers']/['accounts'] reads still trigger
        // fetches behind the scenes for the queries that read them.
        // Mock the underlying fetchers so they don't hit the
        // network.
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        expect(
            await screen.findByText(/no transactions in this account/i),
        ).toBeInTheDocument();
    });

    it('renders the populated register without an idle row-count footer', async () => {
        // Assert on the table chrome rather than cell formatting: the
        // header select-all is enabled once entries load (disabled when
        // empty), so its enabled presence proves the populated branch.
        const txns = [
            makeTxn({ id: 't1', payee: 'Coffee Shop' }),
            makeTxn({ id: 't2', payee: 'Paycheck' }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        const selectAll = await screen.findByRole('checkbox', {
            name: /select all transactions/i,
        });
        expect(selectAll).toBeEnabled();
        // Populated, not the empty branch.
        expect(
            screen.queryByText(/no transactions in this account/i),
        ).not.toBeInTheDocument();
        // The always-on "N rows loaded" footer is gone — it now surfaces
        // only for an active selection or while older rows are loading.
        expect(screen.queryByText(/rows loaded/i)).not.toBeInTheDocument();
        // Post-virtuoso migration: no "Load more" button, no "End of
        // register" sentinel — loading is automatic at either scroll edge.
        expect(
            screen.queryByRole('button', { name: /load more/i }),
        ).not.toBeInTheDocument();
    });

    it('surfaces the ApiError detail when the register query fails', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockRejectedValue(
            new ApiError(422, 'Ledger not found or not visible to this user.', 'ledger-not-visible'),
        );
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        const alert = await screen.findByRole('alert');
        expect(alert).toHaveTextContent(/ledger not found or not visible/i);
    });

    it('initial fetch is made with no cursor and no direction', async () => {
        // The window's initial-load shape is the canonical first
        // page — no cursor, no direction, no starting_at. Locking
        // this down so a future hook refactor doesn't silently
        // start asking the server for a slice of history.
        const fetchSpy = vi
            .spyOn(apiModule, 'fetchRegister')
            .mockResolvedValue({
                entries: [entryOf(makeTxn({ id: 't1', payee: 'Coffee Shop' }))],
                cursorForOlder: null,
                cursorForNewer: null,
            });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        await waitFor(() => {
            expect(fetchSpy).toHaveBeenCalled();
        });
        expect(fetchSpy.mock.calls[0]![0]).toMatchObject({
            ledgerId: LEDGER_ID,
            accountId: ACCOUNT_ID,
        });
        const firstArgs = fetchSpy.mock.calls[0]![0];
        expect(firstArgs.cursor).toBeUndefined();
        expect(firstArgs.direction).toBeUndefined();
        expect(firstArgs.startingAtHeaderId).toBeUndefined();
    });

    it('clears the ?focus= anchor when the filter changes (ADR-0076)', async () => {
        // A focused/anchored row must not survive a freshly-applied filter and
        // hijack the top of the filtered list. Render with ?focus= set (initial
        // load anchors on it), change the filter (search), and assert the
        // re-seed drops the anchor and carries the filter instead.
        const fetchSpy = vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(makeTxn({ id: 't1', payee: 'Coffee Shop' }))],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchIndexBuckets').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchStatusCounts').mockResolvedValue({
            all: 1, cleared: 0, uncleared: 1, reconciling: 0,
            scheduled: 0, needsReview: 0, hidden: 0,
        });

        renderRegister(`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${FOCUS_ID}`);

        // Initial load anchors on the focus header.
        await waitFor(() => {
            expect(fetchSpy).toHaveBeenCalledWith(
                expect.objectContaining({ startingAtHeaderId: FOCUS_ID }),
            );
        });

        // Change the filter — the search box is debounced, so one change event
        // fires one filter update after the debounce window.
        fireEvent.change(await screen.findByLabelText(/search transactions/i), {
            target: { value: 'coffee' },
        });

        // The re-seed drops the anchor (focus cleared) and carries the filter.
        await waitFor(() => {
            const last = fetchSpy.mock.calls.at(-1)![0];
            expect(last.startingAtHeaderId).toBeUndefined();
            expect(last.filter?.search).toBe('coffee');
        });
    });

it('toggles the Scheduled filter', async () => {
        // Two future-dated transactions + one historical one.
        const txns = [
            makeTxn({
                id: 't-future-1',
                payee: 'City Utility',
                postedAt: '2099-12-01T00:00:00Z',
            }),
            makeTxn({
                id: 't-future-2',
                payee: 'Future-pay',
                postedAt: '2099-12-15T00:00:00Z',
            }),
            makeTxn({
                id: 't-past',
                payee: 'Coffee Shop',
                postedAt: '2020-01-01T00:00:00Z',
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        // Status views live in the "Show" dropdown. Open it, pick Scheduled,
        // and the trigger reflects the now-active view.
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Scheduled/i }));
        await waitFor(() => {
            // The trigger now reflects the active view ("Show: Scheduled").
            expect(
                screen.getByRole('button', { name: /Scheduled/i }),
            ).toBeInTheDocument();
        });
    });

    it('switches the register fetch to hidden rows when the Hidden tab is selected', async () => {
        // ADR-0072 D1: the Hidden tab re-seeds the windowed register with
        // hidden=true (hidden rows aren't in the default payload), so this
        // proves the flag threads fetchRegister → window → controller → page.
        const visible = [makeTxn({ id: 'v1', payee: 'Coffee Shop' })];
        const hidden = [
            makeTxn({ id: 'h1', payee: 'Mis-imported', isHidden: true }),
        ];
        const fetchSpy = vi
            .spyOn(apiModule, 'fetchRegister')
            .mockImplementation((args) =>
                Promise.resolve({
                    entries: (args.hidden ? hidden : visible).map(entryOf),
                    cursorForOlder: null,
                    cursorForNewer: null,
                }),
            );
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        // Default view: every fetch so far asked for visible rows.
        await screen.findByRole('checkbox', {
            name: /select all transactions/i,
        });
        expect(fetchSpy.mock.calls.every(([a]) => !a.hidden)).toBe(true);

        // Select Hidden from the "Show" dropdown → the window re-seeds with
        // hidden=true.
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Hidden/i }));
        await waitFor(() => {
            expect(
                fetchSpy.mock.calls.some(([a]) => a.hidden === true),
            ).toBe(true);
        });
    });

    it('offers Move to account (but not Unhide) in the bulk bar for a normal selection', async () => {
        // ADR-0072 D3: Move re-files any selection and is available in every
        // view; Unhide only appears in the Hidden view.
        const txn = makeTxn({ id: 't-move-1', payee: 'Coffee Shop' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        const user = userEvent.setup();
        await user.click(rowCheckbox);

        expect(
            await screen.findByRole('button', { name: /Move to account/i }),
        ).toBeInTheDocument();
        expect(
            screen.queryByRole('button', { name: /^Unhide$/ }),
        ).not.toBeInTheDocument();
    });

    it('toggles the Needs review filter tab', async () => {
        const txns = [
            makeTxn({ id: 't-rev-1', payee: 'Synced A', needsReview: true }),
            makeTxn({ id: 't-rev-2', payee: 'Synced B', needsReview: true }),
            makeTxn({ id: 't-ok', payee: 'Manual', needsReview: false }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Needs review/i }));
        await waitFor(() => {
            // The trigger now reflects the active view ("Show: Needs review").
            expect(
                screen.getByRole('button', { name: /Needs review/i }),
            ).toBeInTheDocument();
        });
    });

    it('renders the Show dropdown and the New-transaction button in one controls row', async () => {
        const txns = [makeTxn({ id: 't1', payee: 'Coffee Shop' })];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        // The status "Show" dropdown and the "+ New transaction" button live in
        // the same combined controls row.
        const showButton = await screen.findByRole('button', { name: /^Show:/ });
        const newButton = screen.getByRole('button', { name: /\+ New transaction/i });
        const controlsBar = showButton.closest('div')?.parentElement as HTMLElement;
        expect(controlsBar).toContainElement(newButton);
        // All status views live inside the dropdown (opened on click).
        fireEvent.click(showButton);
        expect(await screen.findByRole('option', { name: /^All/i })).toBeInTheDocument();
        for (const label of ['Cleared', 'Uncleared', 'Reconciling', 'Scheduled', 'Needs review']) {
            expect(
                screen.getByRole('option', { name: new RegExp(`^${label}`, 'i') }),
            ).toBeInTheDocument();
        }
    });

    it('bulk delete invalidates the per-ledger accounts query (resets the sidebar review-dot)', async () => {
        const txn = makeTxn({ id: 't1', payee: 'Synced', needsReview: true });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        // useSelection fires a debounced summary query the moment a row
        // is checked — stub it so the count/Σ readout is deterministic.
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1,
            sumOnAccount: -4.5,
        });
        const bulkDeleteSpy = vi
            .spyOn(apiModule, 'bulkDeleteTransactions')
            .mockResolvedValue({ hardDeleted: 1, softHidden: 0 });
        const invalidateSpy = vi.spyOn(
            QueryClient.prototype,
            'invalidateQueries',
        );

        renderRegister();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        const user = userEvent.setup();
        await user.click(rowCheckbox);

        const deleteButton = await screen.findByRole('button', { name: /^Delete$/ });
        await user.click(deleteButton);

        const dialog = await screen.findByRole('dialog');
        await user.click(
            within(dialog).getByRole('button', { name: /^Delete$/ }),
        );

        await waitFor(() => {
            // Explicit selections now also carry accountId (so the server can
            // compute the Σ); objectContaining tolerates it + any future dims.
            expect(bulkDeleteSpy).toHaveBeenCalledWith(
                LEDGER_ID,
                expect.objectContaining({
                    kind: 'explicit',
                    headerIds: [txn.headerId],
                }),
            );
        });
        // Bulk delete can drop the account's last needs_review row, so
        // the accounts query is invalidated → sidebar dot refetches
        // without a page reload.
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({
                queryKey: ['accounts', LEDGER_ID],
            });
        });
    });

    it('all-mode delete is enabled and a large count shows the typed-confirm', async () => {
        // All-mode bulk delete is no longer gated client-side: the
        // server restricts all-mode ops to headers this account
        // ORIGINATES (ADR-0036), so clicking the header checkbox (→
        // 'all' mode) leaves Delete enabled. A large server count
        // (> 100) still gates Confirm behind the typed-confirm input.
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(makeTxn({ id: 't1' }))],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 250,
            sumOnAccount: -250000,
        });

        renderRegister();

        const selectAll = await screen.findByRole('checkbox', {
            name: /select all transactions in this account/i,
        });
        const user = userEvent.setup();
        await user.click(selectAll);

        // Delete is ENABLED in all-mode (no client gate any more).
        const deleteButton = await screen.findByRole('button', { name: /^Delete$/ });
        await waitFor(() => {
            expect(deleteButton).toBeEnabled();
        });
        await user.click(deleteButton);

        // Large count → typed-confirm input appears in the dialog.
        const dialog = await screen.findByRole('dialog');
        expect(dialog).toHaveTextContent(/Delete 250 transactions\?/i);
        expect(within(dialog).getByRole('textbox')).toBeInTheDocument();
    });

    it('Accept (approve) invalidates the per-ledger accounts query (resets the sidebar review-dot)', async () => {
        const txn = makeTxn({ id: 't1', payee: 'Synced', needsReview: true });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchTransaction')
            .mockResolvedValue(null);
        const invalidateSpy = vi.spyOn(
            QueryClient.prototype,
            'invalidateQueries',
        );

        renderRegister();

        // Right-click the row to open the context menu, then Accept.
        const payeeCell = await screen.findByText('Synced');
        const user = userEvent.setup();
        await user.pointer({ keys: '[MouseRight]', target: payeeCell });

        const acceptItem = await screen.findByRole('menuitem', { name: /^Accept$/ });
        await user.click(acceptItem);

        await waitFor(() => {
            expect(patchSpy).toHaveBeenCalledWith(
                LEDGER_ID,
                txn.headerId,
                { approve: true },
                ACCOUNT_ID,
            );
        });
        // Approve clears the row's needs_review → invalidate accounts so
        // the sidebar dot resets live (no reload).
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({
                queryKey: ['accounts', LEDGER_ID],
            });
        });
    });

    it('breadcrumbs link to the parent ledger, with no "All ledgers" root', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        // Wait for the header to render with the ledger name resolved.
        const breadcrumbs = await screen.findByRole('navigation');
        const links = within(breadcrumbs).getAllByRole('link');
        // ADR-0090: the "All ledgers" root crumb is gone — `/` is ledger
        // MANAGEMENT, reached from "Manage ledgers…" in the ledger dropdown, not
        // by making a breadcrumb carry navigation. The first crumb link is now
        // the parent ledger.
        expect(links[0]).toHaveAttribute('href', `/ledgers/${LEDGER_ID}`);
        expect(
            within(breadcrumbs).queryByRole('link', { name: /all ledgers/i }),
        ).not.toBeInTheDocument();
    });
});
