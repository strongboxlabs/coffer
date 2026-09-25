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
const LOSER_HEADER = '00000000-0000-0000-0000-0000000010c3';
const WINNER_HEADER = '00000000-0000-0000-0000-000000001117';
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
    // A category register's breadcrumb links at the Categories destination;
    // TanStack throws on a <Link to> with no matching route.
    const categoriesRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/categories',
        component: () => <main>categories</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([
            registerRoute, landingRoute, detailRoute, categoriesRoute,
        ]),
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

    // The window is EMPTY while the month buckets still exist — they come from
    // their own query, not from the window, so this state is reachable in
    // normal use (a filter that matches nothing).
    //
    // This failed before the overlays moved out of RegisterShell. The shell
    // passes its children through RegisterStates, which REPLACES them with the
    // empty placeholder, so everything rendered inside it — the popover, the
    // delete confirm, the context menu — was unmounted by the list's own
    // state. RegisterDateJumpPopover's own doc comment promises "Mounted
    // always; visible only while open"; on this page that was false.
    it('keeps the date-jump popover reachable when the window is empty', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        // Two buckets minimum — the shortcut is deliberately suppressed below
        // that, so one bucket would make this pass for the wrong reason.
        vi.spyOn(apiModule, 'fetchIndexBuckets').mockResolvedValue([
            { yearMonth: '2026-01', count: 3, sampleHeaderId: 'h-jan' },
            { yearMonth: '2026-02', count: 5, sampleHeaderId: 'h-feb' },
        ]);

        renderRegister();
        await screen.findByText(/no transactions in this account/i);

        await userEvent.keyboard('{Control>}j{/Control}');

        expect(
            await screen.findByRole('dialog', { name: /jump to date/i }),
        ).toBeInTheDocument();
    });

    it('numbers rows from 1 and declares the total unknown', async () => {
        // `aria-rowindex` was fed straight from virtuoso's logical index,
        // which carries a 1,000,000 front-shift offset so rows can be
        // prepended without going negative — so this announced the first row
        // of the register as row 1,000,001. Its paired `aria-rowcount` was the
        // LOADED entry count, making the whole announcement "row 1000001 of
        // 87" on an account with tens of thousands of rows.
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

        const first = (await screen.findByText('Coffee Shop')).closest('[role="row"]');
        const second = screen.getByText('Paycheck').closest('[role="row"]');
        expect(first).toHaveAttribute('aria-rowindex', '1');
        expect(second).toHaveAttribute('aria-rowindex', '2');

        // -1 is ARIA's "not known", and it is the honest answer for a sliding
        // window: the rows in the DOM are a slice whose offset into the
        // account is not derivable here.
        expect(first!.closest('[role="grid"]')).toHaveAttribute('aria-rowcount', '-1');
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
        // Accept-as-is is a COMMAND with its own route now, not a PATCH whose
        // entire body was one flag. Spying on patchTransaction as well pins that
        // it is NOT used — otherwise this test would keep passing if the call
        // quietly went back to the old shape.
        const approveSpy = vi
            .spyOn(apiModule, 'approveTransaction')
            .mockResolvedValue(null);
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
            expect(approveSpy).toHaveBeenCalledWith(
                LEDGER_ID,
                txn.headerId,
                ACCOUNT_ID,
            );
        });
        expect(patchSpy).not.toHaveBeenCalled();
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

    // A CATEGORY register is the same component pointed at a different kind of
    // account, and every difference below was a place it looked like a bank
    // account and wasn't: it offered a statement import, a New-transaction
    // button that the editor cannot honour, a breadcrumb that named neither the
    // tree it lives in nor its parent, and — once widened to the subtree — rows
    // whose own sub-category appeared nowhere.
    describe('a category register', () => {
        const PARENT_ID = '00000000-0000-0000-0000-000000000200';
        const CHILD_ID = '00000000-0000-0000-0000-000000000201';
        const SIBLING_ID = '00000000-0000-0000-0000-000000000202';

        const category = (
            id: string,
            name: string,
            parentId: string | null,
        ): AccountSummary => ({
            ...TEST_ACCOUNT,
            id,
            name,
            parentId,
            accountType: 'category',
            categoryKind: 'expense',
        });
        const FOOD = category(PARENT_ID, 'Food', null);
        const GROCERIES = category(CHILD_ID, 'Groceries', PARENT_ID);
        const RESTAURANTS = category(SIBLING_ID, 'Restaurants', PARENT_ID);
        const TREE = [TEST_ACCOUNT, FOOD, GROCERIES, RESTAURANTS];

        function mockTree(entries: RegisterEntry[]) {
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries,
                cursorForOlder: null,
                cursorForNewer: null,
            });
            vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
            vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(TREE);
        }

        it('walks the category tree in the breadcrumb, not just the ledger', async () => {
            mockTree([]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            const breadcrumbs = await screen.findByRole('navigation');
            await waitFor(() => {
                expect(
                    within(breadcrumbs).getByRole('link', { name: 'Food' }),
                ).toBeInTheDocument();
            });
            const links = within(breadcrumbs).getAllByRole('link');
            expect(links.map((a) => a.getAttribute('href'))).toEqual([
                `/ledgers/${LEDGER_ID}`,
                `/ledgers/${LEDGER_ID}/categories`,
                `/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}`,
            ]);
            // The leaf is the current page — named, not linked.
            expect(
                within(breadcrumbs).getByText('Groceries'),
            ).toHaveAttribute('aria-current', 'page');
        });

        it('offers no New transaction, because a category cannot hold one', async () => {
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            await screen.findByRole('checkbox', {
                name: /select all transactions/i,
            });
            expect(
                screen.queryByRole('button', { name: /new transaction/i }),
            ).not.toBeInTheDocument();
        });

        it('tells a parent its money is on its children, and widens on request', async () => {
            mockTree([]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}`);

            expect(
                await screen.findByText(/nothing is filed directly under this category/i),
            ).toBeInTheDocument();
            // The empty state is the one with an action attached.
            const widen = screen.getByRole('button', { name: /include sub-categories/i });

            // Re-point the register at the subtree's rows before widening, so
            // the assertion is about the widened READ, not an empty re-render.
            vi.mocked(apiModule.fetchRegister).mockResolvedValue({
                entries: [entryOf(makeTxn({
                    id: 'g1', accountId: CHILD_ID, payee: 'Market',
                }))],
                cursorForOlder: null,
                cursorForNewer: null,
            });
            fireEvent.click(widen);

            // The scope lands on a PERSISTENT toggle, not a chip that vanished
            // with the button that set it: a register showing several
            // sub-categories' rows has to say so somewhere that is still there
            // once it has happened, and offer the way back.
            await waitFor(() => {
                expect(
                    screen.getByRole('checkbox', { name: /include sub-categories/i }),
                ).toBeChecked();
            });
            await waitFor(() => {
                expect(apiModule.fetchRegister).toHaveBeenCalledWith(
                    expect.objectContaining({
                        filter: expect.objectContaining({ includeSubcategories: true }),
                    }),
                );
            });

            // …and the arriving row names the sub-category it is filed under,
            // by FULL path. The category column holds the OTHER side of the
            // posting, so without this a widened register is a list of rows
            // with no way to tell Groceries from Restaurants.
            expect(await screen.findByText('Market')).toBeInTheDocument();
            expect(await screen.findByText('Food/Groceries')).toBeInTheDocument();
        });

        it('arrives from the budget with the subtree already in scope', async () => {
            // A budget row is a ROLLUP: the figure just read already contains
            // every descendant. The register it links to must open the same
            // way, or the number you clicked is not the number you land on.
            mockTree([entryOf(makeTxn({
                id: 'g1', accountId: CHILD_ID, payee: 'Market',
            }))]);
            renderRegister(
                `/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}?subcategories=true`,
            );

            expect(await screen.findByText('Market')).toBeInTheDocument();
            // On from the first read — not toggled on afterwards.
            expect(apiModule.fetchRegister).toHaveBeenCalledWith(
                expect.objectContaining({
                    filter: expect.objectContaining({ includeSubcategories: true }),
                }),
            );
            expect(
                screen.getByRole('checkbox', { name: /include sub-categories/i }),
            ).toBeChecked();
            // …and the rows say which sub-category they are filed under.
            expect(screen.getByText('Food/Groceries')).toBeInTheDocument();
        });

        it('offers the scope toggle on a parent, and not on a leaf', async () => {
            mockTree([entryOf(makeTxn({ id: 'p1', accountId: CHILD_ID }))]);
            const parent = renderRegister(
                `/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}`,
            );
            expect(
                await screen.findByRole('checkbox', { name: /include sub-categories/i }),
            ).not.toBeChecked();
            parent.unmount();

            // A leaf has no subtree, so the toggle would be an affordance for
            // nothing. Anchor on the register having rendered before asserting
            // the absence, or this passes on the pending frame.
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID, payee: 'Market' }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);
            expect(await screen.findByText('Market')).toBeInTheDocument();
            expect(
                screen.queryByRole('checkbox', { name: /include sub-categories/i }),
            ).not.toBeInTheDocument();
        });

        it('closes the keyboard route to the editor, not just the button', async () => {
            // Hiding the button while leaving `n` bound left the action
            // reachable by the exact route a regular would use — and the
            // editor it opens authors against the register's own account,
            // which here is a category.
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID, payee: 'Market' }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            expect(await screen.findByText('Market')).toBeInTheDocument();
            await userEvent.keyboard('n');

            // The editor announces itself with a Save control; nothing opened.
            expect(
                screen.queryByRole('button', { name: /^save$/i }),
            ).not.toBeInTheDocument();
        });

        it('offers Show raw data and renders the provider payload', async () => {
        // The modal lived inside InvestmentRegisterPage and was typed on its
        // row, so it was investment-only — never by decision. Most feed rows
        // land on a BANK register, so the side that needed "why did this import
        // like this?" was the side without it.
        const txn = makeTxn({
            id: 'raw1',
            payee: 'Synced Row',
            providerRawPayload: '{"id":"sf-42","description":"SYNCED ROW"}',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([]);

        renderRegister();

        fireEvent.contextMenu(await screen.findByText('Synced Row'));
        const item = await screen.findByRole('menuitem', { name: /show raw data/i });
        fireEvent.click(item);

        // The provider's own payload is the headline, pretty-printed — not the
        // SPA's view of the row, which is only the fallback.
        expect(await screen.findByText(/Raw provider data/i)).toBeInTheDocument();
        expect(await screen.findByText(/sf-42/)).toBeInTheDocument();
    });

    it('drops Duplicate from the row menu — the other door to the editor', async () => {
            // The builder is unit-tested; this asserts the WIRING, so the
            // option cannot sit there unpassed while the menu still offers it.
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID, payee: 'Market' }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            fireEvent.contextMenu(await screen.findByText('Market'));

            // Anchor on the menu actually being open before asserting absence.
            expect(await screen.findByRole('menu')).toBeInTheDocument();
            expect(
                screen.queryByRole('menuitem', { name: /duplicate/i }),
            ).not.toBeInTheDocument();
        });

        it('filters by ACCOUNT, because that is the other side of these rows', async () => {
            // The filter's `categoryId` has always meant "counterparty account
            // id". On a money account's register the other side is a category;
            // on a CATEGORY's register it is a money account. Offering
            // categories here filtered an axis these rows do not have — the
            // counterparty column reads "Checking", and nothing in the list
            // could ever match it.
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            await userEvent.click(await screen.findByRole('button', { name: /^filter/i }));

            const picker = await screen.findByRole('combobox', { name: /account/i });
            expect(picker).toHaveAttribute('placeholder', 'Any account');

            // Eligibility, not just the label: the money account is offered and
            // the categories are not.
            await userEvent.type(picker, 'e');
            const options = await screen.findAllByRole('option');
            const labels = options.map((o) => o.textContent ?? '');
            expect(labels.some((t) => t.includes('Checking'))).toBe(true);
            expect(labels.some((t) => t.includes('Groceries'))).toBe(false);
        });

        it('still filters by CATEGORY on an ordinary account register', async () => {
            // The mirror of the case above — the default must not drift.
            mockTree([entryOf(makeTxn({ id: 'b1' }))]);
            renderRegister();

            await userEvent.click(await screen.findByRole('button', { name: /^filter/i }));

            const picker = await screen.findByRole('combobox', { name: /category/i });
            expect(picker).toHaveAttribute('placeholder', 'Any category');

            await userEvent.type(picker, 'o');
            const labels = (await screen.findAllByRole('option')).map((o) => o.textContent ?? '');
            expect(labels.some((t) => t.includes('Groceries'))).toBe(true);
            expect(labels.some((t) => t.includes('Checking'))).toBe(false);
        });

        it('leaves an unwidened row unlabelled — its category is the breadcrumb', async () => {
            mockTree([entryOf(makeTxn({
                id: 'c1', accountId: CHILD_ID, payee: 'Market',
            }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            // Anchor on the row being present, THEN assert the absence —
            // a bare queryBy…toBeNull passes on the pending frame.
            expect(await screen.findByText('Market')).toBeInTheDocument();
            expect(screen.queryByText('Food/Groceries')).not.toBeInTheDocument();
        });
    });

    // The bank's merge SAVE path, which had never been tested. It has shipped
    // since #341 and works; the gap is why the INVESTMENT register could be
    // missing the same branch entirely and nobody noticed for as long.
    //
    // Direction is inverted: the edited row becomes the LOSER and the chosen
    // candidate survives, so the server returns the SURVIVOR's entry. Patching
    // that onto the edited header — the ordinary save path — would paint the
    // survivor's data onto the loser's row and leave two entries under one
    // header id.
    it('removes the loser and keeps the survivor once, on a merge save', async () => {
        const loser = makeTxn({
            id: 'l1', headerId: LOSER_HEADER, payee: 'imported dupe',
            // NO counterparty — an uncategorised imported row, which is what
            // most needs-review rows look like. Folding one used to be a
            // silent no-op: buildSaveBody validated the postings before it
            // reached the merge stamp.
            needsReview: true,
        });
        const winner = makeTxn({
            id: 'w1', headerId: WINNER_HEADER, payee: 'the keeper',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(loser), entryOf(winner)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchMergeCandidates').mockResolvedValue([
            {
                headerId: WINNER_HEADER,
                payee: 'the keeper',
                memo: null,
                postedAt: '2026-05-01T12:00:00Z',
                daysDelta: 0,
                tags: [],
                postings: [
                    {
                        counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
                        counterpartyAccountName: 'Groceries',
                        amount: 4.5,
                        legMemo: null,
                    },
                ],
            },
        ]);
        // The server folds the loser away and returns the SURVIVOR's entry.
        // Merging is a COMMAND with its own route; patchTransaction is spied too
        // so a silent return to merge-by-PATCH fails rather than passing.
        const mergeSpy = vi
            .spyOn(apiModule, 'mergeTransaction')
            .mockResolvedValue(entryOf(winner));
        const patchSpy = vi
            .spyOn(apiModule, 'patchTransaction')
            .mockResolvedValue(entryOf(winner));
        // Saving kicks off the in-place balance refresh. Unstubbed it reaches the
        // real fetch with a relative URL, which the test environment cannot parse
        // — today an ignored rejection, and a hard "unhandled error" failure on a
        // newer jsdom, which is how the dependency bump surfaced it.
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);

        renderRegister();

        const loserCell = await screen.findByText('imported dupe');
        fireEvent.dblClick(loserCell.closest('[role="row"]')!);

        // The chip is labelled by its summary — date · payee · counterparty.
        fireEvent.click(await screen.findByRole('button', { name: /the keeper.*Groceries/i }));
        fireEvent.click(
            await screen.findByRole('button', { name: /fold into selected/i }),
        );

        await waitFor(() => expect(mergeSpy).toHaveBeenCalled());
        // EXACT, not objectContaining: the body carries the merge stamp and
        // nothing else. It used to pair an `approve: true`, which put the
        // "a merged loser is not awaiting review" invariant in the caller —
        // so clients that did not send it left the row flagged. The server owns
        // it now, and the route has no approve field at all, which is a stronger
        // guarantee than pinning a body shape: there is nothing to creep back.
        //
        // The WINNER is the third argument — the direction is inverted, and
        // asserting it here is what catches a swap of loser and survivor.
        expect(mergeSpy.mock.calls[0]![1]).toBe(LOSER_HEADER);
        expect(mergeSpy.mock.calls[0]![2]).toBe(WINNER_HEADER);
        expect(patchSpy).not.toHaveBeenCalled();

        await waitFor(() => {
            expect(screen.queryByText('imported dupe')).not.toBeInTheDocument();
        });
        expect(screen.getAllByText('the keeper')).toHaveLength(1);
    });
});

describe('BankRegisterPage — focus anchoring', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
    });

    const ANCHOR = '00000000-0000-0000-0000-0000000000aa';

    it('asks the server to anchor on the ?focus= row', async () => {
        const wanted = makeTxn({ id: 'a1', headerId: ANCHOR, payee: 'Anchored' });
        const fetchSpy = vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(wanted)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderRegister(
            `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`,
        );

        await screen.findByText('Anchored');
        expect(fetchSpy.mock.calls[0]![0]).toEqual(
            expect.objectContaining({ startingAtHeaderId: ANCHOR }),
        );
    });

    it('focuses the anchored row when the server returns it', async () => {
        const wanted = makeTxn({ id: 'a1', headerId: ANCHOR, payee: 'Anchored' });
        const other = makeTxn({ id: 'b1', headerId: 'other-header', payee: 'Bystander' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(other), entryOf(wanted)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderRegister(
            `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`,
        );

        await screen.findByText('Anchored');
        await waitFor(() => {
            expect(
                document.querySelector(`[data-headerid="${ANCHOR}"]`)
                    ?.getAttribute('data-focused'),
            ).toBe('true');
        });
        // ...and NOT the row that merely happens to sit first.
        expect(
            document.querySelector('[data-headerid="other-header"]')
                ?.getAttribute('data-focused'),
        ).toBe('false');
    });

    /**
     * The regression that motivated the whole change.
     */
    it('focuses nothing when the server declines to anchor', async () => {
        // The server pins an anchor only when the row matches the active filter
        // and otherwise returns the ordinary most-recent page — saying nothing
        // about which it did. The hook used to read "anchor requested" as
        // "anchor is at index 0" and focus whatever sat there. With a status tab
        // active, saving a row out of that tab therefore focused and scrolled to
        // an unrelated row.
        const unrelated = makeTxn({ id: 'z1', headerId: 'not-the-anchor', payee: 'Unrelated' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(unrelated)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderRegister(
            `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`,
        );

        // Settled anchor: the page has rendered the row the server DID return,
        // so an absence of focus below is a real absence.
        await screen.findByText('Unrelated');
        expect(
            document.querySelector('[data-headerid="not-the-anchor"]')
                ?.getAttribute('data-focused'),
        ).toBe('false');
    });
});

