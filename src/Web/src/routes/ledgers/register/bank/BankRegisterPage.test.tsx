import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
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
    role: 'owner', currencyCode: 'USD',
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
        ...NO_NOTES,
        kind: 'bank',
        id: '',
        accountId: ACCOUNT_ID,
        payee: 'Coffee Shop',
        amount: -4.5,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 100,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
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
        headerId: '00000000-0000-0000-0000-000000000000',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-01T12:00:00Z',
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

/**
 * A precise write refreshes the home screen's reads (ADR-0079 as amended
 * 2026-11-03) and leaves the register's window key alone, which would re-seed
 * the window and move the user off their row.
 */
function expectHomeRefreshedNotRegister(spy: { mock: { calls: unknown[][] } }) {
    const keys = spy.mock.calls.map((c) => (c[0] as { queryKey?: unknown[] } | undefined)?.queryKey);
    expect(keys).toContainEqual(['home', LEDGER_ID]);
    expect(keys).toContainEqual(['overview', LEDGER_ID]);
    expect(keys).toContainEqual(['budget-progress', LEDGER_ID]);
    expect(keys.filter((k) => k?.[0] === 'register')).toEqual([]);
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
    // A category register's breadcrumb links at the Categories tab of Structure;
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

/**
 * The bulk footer's count reads exactly `text`. The count element is found by
 * its own text inside the footer region and compared whole, so a read-only part
 * that should be absent, or present, fails it.
 */
async function expectFooterCount(text: string) {
    const region = await screen.findByRole('region', { name: /bulk actions/i });
    await waitFor(() => {
        expect(within(region).getByText(/\d+ selected/).textContent).toBe(text);
    });
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

    it('names the counterparty column Category on an ACCOUNT register', async () => {
        // The other half of the category-register assertion further down.
        // Without this pair, a change that renamed the column everywhere
        // would pass one of them and nothing would notice.
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(makeTxn({ id: 't1' }))],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        expect(
            await screen.findByRole('columnheader', { name: /Category . tags/i }),
        ).toBeInTheDocument();
        expect(
            screen.queryByRole('columnheader', { name: /Account . tags/i }),
        ).not.toBeInTheDocument();
    });

    it('numbers rows from 1 and declares the total unknown', async () => {
        // `aria-rowindex` was fed straight from virtuoso's logical index,
        // which carries a 1000000 front-shift offset so rows can be
        // prepended without going negative — so this announced the first row
        // of the register as row 1000001. Its paired `aria-rowcount` was the
        // LOADED entry count, making the whole announcement "row 1000001 of
        // <one loaded page>" on an account far longer than one page.
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

    it('disables bulk unhide when a read-only row is selected, as it does delete', async () => {
        // Two hidden rows: one line of a split owned by another account, read-only
        // here, and an ordinary row this register can change.
        const target = makeTxn({
            id: 'h-target',
            headerId: '00000000-0000-0000-0000-0000000000b1',
            payee: 'Split line',
            isReadOnly: true,
            isHidden: true,
        });
        const own = makeTxn({
            id: 'h-own',
            headerId: '00000000-0000-0000-0000-0000000000b2',
            payee: 'Mis-imported',
            isHidden: true,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockImplementation((args) =>
            Promise.resolve({
                entries: (args.hidden ? [target, own] : [makeTxn({ id: 'v1', payee: 'Coffee Shop' })]).map(entryOf),
                cursorForOlder: null,
                cursorForNewer: null,
            }),
        );
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

        renderRegister();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Hidden/i }));

        await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${target.id}` }));
        const unhide = await screen.findByRole('button', { name: /^Unhide$/ });
        await waitFor(() => {
            expect(unhide).toBeDisabled();
        });
        expect(unhide).toHaveAttribute('title', expect.stringMatching(/canonical owner is elsewhere/i));

        // The ordinary row alone may be un-hidden: the gate is the read-only row.
        await user.click(screen.getByRole('checkbox', { name: `Select transaction ${target.id}` }));
        await user.click(screen.getByRole('checkbox', { name: `Select transaction ${own.id}` }));
        await waitFor(() => {
            expect(screen.getByRole('button', { name: /^Unhide$/ })).toBeEnabled();
        });
        expect(screen.getByRole('button', { name: /^Unhide$/ })).toHaveAttribute('title', 'Unhide the selection');
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

    it('keeps the selection when Escape on the editor\'s Save button cancels the edit', async () => {
        // The page's Escape clears the selection, and the editor's cancels the
        // edit. One Escape on a button inside the editor is the editor's alone.
        const edited = makeTxn({
            id: 't-edited',
            headerId: '00000000-0000-0000-0000-0000000000e1',
            payee: 'Edited row',
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
            counterpartyAccountName: 'Category A',
        });
        const picked = makeTxn({
            id: 't-picked',
            headerId: '00000000-0000-0000-0000-0000000000e2',
            payee: 'Selected row',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(edited), entryOf(picked)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: -4.5, currencyCode: 'USD',
        });

        renderRegister();
        const user = userEvent.setup();

        const checkbox = await screen.findByRole('checkbox', { name: `Select transaction ${picked.id}` });
        await user.click(checkbox);
        expect(checkbox).toBeChecked();

        fireEvent.dblClick((await screen.findByText('Edited row')).closest('[role="row"]')!);
        const save = await screen.findByRole('button', { name: /^Save/ });
        act(() => save.focus());
        expect(save).toHaveFocus();

        await user.keyboard('{Escape}');

        await waitFor(() => expect(document.querySelector('[data-editing="true"]')).toBeNull());
        expect(screen.getByText('Edited row')).toBeInTheDocument();
        expect(screen.getByRole('checkbox', { name: `Select transaction ${picked.id}` })).toBeChecked();
        await expectFooterCount('1 selected');

        // With no editor open, the page's Escape still clears it.
        expect(document.activeElement?.tagName).not.toBe('INPUT');
        await user.keyboard('{Escape}');
        await waitFor(() => {
            expect(screen.getByRole('checkbox', { name: `Select transaction ${picked.id}` })).not.toBeChecked();
        });
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
            totalCount: 1,
            readOnlyCount: 0,
            sumOnAccount: -4.5, currencyCode: 'USD',
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
        expectHomeRefreshedNotRegister(invalidateSpy);
    });

    it('all-mode delete is enabled and a large count shows the typed-confirm', async () => {
        // All-mode bulk delete is not gated client-side: the
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
            totalCount: 250,
            readOnlyCount: 0,
            sumOnAccount: -250000, currencyCode: 'USD',
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

    describe('the footer count', () => {
        const EDITABLE = makeTxn({
            id: 'f-editable',
            headerId: '00000000-0000-0000-0000-0000000000d1',
            payee: 'Own row',
        });
        const READ_ONLY = makeTxn({
            id: 'f-read-only',
            headerId: '00000000-0000-0000-0000-0000000000d2',
            payee: 'Split line',
            notesKind: 'splitOpposite',
            isReadOnly: true,
        });

        function mockRows(accounts: AccountSummary[] = [TEST_ACCOUNT]) {
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(EDITABLE), entryOf(READ_ONLY)],
                cursorForOlder: null,
                cursorForNewer: null,
            });
            vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
            vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(accounts);
        }

        it('names the read-only part of an explicit selection from its loaded rows while the summary is in flight', async () => {
            mockRows();
            // Never answers: until it does, the read-only part comes from the loaded rows.
            vi.spyOn(apiModule, 'fetchSelectionSummary').mockReturnValue(new Promise(() => {}));
            renderRegister();

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${EDITABLE.id}` }));
            await expectFooterCount('1 selected');

            await user.click(screen.getByRole('checkbox', { name: `Select transaction ${READ_ONLY.id}` }));
            await expectFooterCount('2 selected · 1 read-only');
        });

        it('counts a select-all from the server, read-only rows included; the delete still names what it deletes', async () => {
            mockRows();
            vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
                count: 12,
                totalCount: 15,
                readOnlyCount: 3,
                sumOnAccount: -120, currencyCode: 'USD',
            });
            renderRegister();

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', {
                name: /select all transactions in this account/i,
            }));
            await expectFooterCount('15 selected · 3 read-only');

            await user.click(await screen.findByRole('button', { name: /^Delete$/ }));
            expect(await screen.findByRole('dialog')).toHaveTextContent(/Delete 12 transactions\?/i);
        });

        it('reads "N selected" for a select-all with no read-only row', async () => {
            mockRows();
            vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
                count: 2,
                totalCount: 2,
                readOnlyCount: 0,
                sumOnAccount: -9, currencyCode: 'USD',
            });
            renderRegister();

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', {
                name: /select all transactions in this account/i,
            }));
            await expectFooterCount('2 selected');
        });
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
        // Accept-as-is is a COMMAND with its own route, not a PATCH whose entire
        // body is one flag. Spying on patchTransaction as well pins that it is
        // NOT used — otherwise this test would keep passing if the call quietly
        // became a PATCH.
        const approveSpy = vi
            .spyOn(apiModule, 'approveTransaction')
            .mockResolvedValue([]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchTransaction')
            .mockResolvedValue([]);
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
        expectHomeRefreshedNotRegister(invalidateSpy);
    });

    it('Accept from the editor refreshes the home screen\'s reads, and not the register window', async () => {
        // A counterparty, so the editor's Accept is enabled.
        const txn = makeTxn({
            id: 't1', payee: 'Synced', needsReview: true,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
            counterpartyAccountName: 'Category A',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchTransaction')
            .mockResolvedValue([entryOf({ ...txn, needsReview: false })]);
        const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries');

        renderRegister();

        fireEvent.dblClick((await screen.findByText('Synced')).closest('[role="row"]')!);
        fireEvent.click(await screen.findByRole('button', { name: 'Accept' }));

        await waitFor(() => expect(patchSpy).toHaveBeenCalled());
        expect(patchSpy.mock.calls[0]![2]).toMatchObject({ approve: true });
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['accounts', LEDGER_ID] });
        });
        expectHomeRefreshedNotRegister(invalidateSpy);
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

        it('names the counterparty column Account, because that is what is in it', async () => {
            // The column holds the OTHER side. On an account's register that
            // is a category; on a category's register it is a money account,
            // and the cells read "Checking" under a heading that said
            // "Category" — the wrong word for the column's contents.
            mockTree([entryOf(makeTxn({ id: 'c1', accountId: CHILD_ID }))]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            expect(
                await screen.findByRole('columnheader', { name: /Account . tags/i }),
            ).toBeInTheDocument();
            expect(
                screen.queryByRole('columnheader', { name: /Category . tags/i }),
            ).not.toBeInTheDocument();
        });

        it('offers Move to CATEGORY, because that is what the move does here', async () => {
            // The move re-points the leg sitting on the register's own
            // account. On a category's register that account IS the category,
            // so the operation is a recategorise — and the button said "Move
            // to account", which named an operation that destroyed the
            // category leg outright until the server guard landed.
            const txn = makeTxn({ id: 't-cat-move', accountId: CHILD_ID });
            mockTree([entryOf(txn)]);
            renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', {
                name: `Select transaction ${txn.id}`,
            }));

            expect(
                await screen.findByRole('button', { name: /Move to category/i }),
            ).toBeInTheDocument();
            // The pair matters: a blanket rename would satisfy the line above
            // while breaking every account register.
            expect(
                screen.queryByRole('button', { name: /Move to account/i }),
            ).not.toBeInTheDocument();
        });

        describe('the footer count', () => {
            // A category's read-only rows: a split's lines seen from the
            // category, and a dividend's category leg (the event is the
            // brokerage's).
            const OWN = makeTxn({
                id: 'cf-own',
                headerId: '00000000-0000-0000-0000-0000000000e1',
                accountId: CHILD_ID,
            });
            const SPLIT_SIDE = makeTxn({
                id: 'cf-split',
                headerId: '00000000-0000-0000-0000-0000000000e2',
                accountId: CHILD_ID,
                notesKind: 'splitOpposite',
                isReadOnly: true,
            });
            const EVENT_SIDE = makeTxn({
                id: 'cf-event',
                headerId: '00000000-0000-0000-0000-0000000000e3',
                accountId: CHILD_ID,
                notesKind: 'eventOpposite',
                isReadOnly: true,
            });

            it('names the read-only part of an explicit selection from its loaded rows while the summary is in flight', async () => {
                mockTree([OWN, SPLIT_SIDE, EVENT_SIDE].map(entryOf));
                // Never answers: until it does, the read-only part comes from the loaded rows.
                vi.spyOn(apiModule, 'fetchSelectionSummary').mockReturnValue(new Promise(() => {}));
                renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

                const user = userEvent.setup();
                await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${OWN.id}` }));
                await expectFooterCount('1 selected');

                await user.click(screen.getByRole('checkbox', { name: `Select transaction ${SPLIT_SIDE.id}` }));
                await user.click(screen.getByRole('checkbox', { name: `Select transaction ${EVENT_SIDE.id}` }));
                await expectFooterCount('3 selected · 2 read-only');
            });

            it('counts a select-all from the server, read-only rows included', async () => {
                mockTree([OWN, SPLIT_SIDE, EVENT_SIDE].map(entryOf));
                vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
                    count: 1,
                    totalCount: 3,
                    readOnlyCount: 2,
                    sumOnAccount: 4.5, currencyCode: 'USD',
                });
                renderRegister(`/ledgers/${LEDGER_ID}/accounts/${CHILD_ID}`);

                const user = userEvent.setup();
                await user.click(await screen.findByRole('checkbox', {
                    name: /select all transactions/i,
                }));
                await expectFooterCount('3 selected · 2 read-only');
            });
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
        // The rows wait for the account, whose currency they are printed in.
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);

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

    // The bank's merge SAVE path. Without a test, a register can lack this
    // branch entirely while every other test stays green.
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
            // most needs-review rows look like. Folding one must not be a
            // silent no-op, which it would be if buildSaveBody validated the
            // postings before it reached the merge stamp.
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
                postedAt: '2026-05-01T12:00:00Z',
                postings: [
                    {
                        counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
                        counterpartyAccountName: 'Groceries',
                    },
                ],
                origin: 'manual',
            },
        ]);
        // The server folds the loser away and returns the SURVIVOR's entries.
        // Merging is a COMMAND with its own route; patchTransaction is spied too
        // so a silent return to merge-by-PATCH fails rather than passing.
        const mergeSpy = vi
            .spyOn(apiModule, 'mergeTransaction')
            .mockResolvedValue([entryOf(winner)]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchTransaction')
            .mockResolvedValue([entryOf(winner)]);
        // Saving kicks off the in-place balance refresh. Unstubbed it reaches the
        // real fetch with a relative URL, which the test environment cannot parse
        // — today an ignored rejection, and a hard "unhandled error" failure on a
        // newer jsdom.
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries');

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
        // nothing else. Pairing it with `approve: true` would put the "a merged
        // loser is not awaiting review" invariant in the caller, so clients that
        // did not send it would leave the row flagged. The server owns it, and
        // the route has no approve field at all, which is a stronger guarantee
        // than pinning a body shape: there is nothing to creep back.
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
        expectHomeRefreshedNotRegister(invalidateSpy);
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
     * The central case for anchoring.
     */
    it('focuses nothing when the server declines to anchor', async () => {
        // The server pins an anchor only when the row matches the active filter
        // and otherwise returns the ordinary most-recent page — saying nothing
        // about which it did. Reading "anchor requested" as "anchor is at index
        // 0" would focus whatever sat there: with a status tab active, saving a
        // row out of that tab would focus and scroll to an unrelated row.
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

/**
 * The row list is the register's focus home (ADR-0023 §D, §E, §L). Rows never
 * take focus; a control that takes itself away (the bulk bar's Clear selection
 * and a bulk action that clears the selection, the inline editor and the
 * new-row form however they close) hands focus to the row list, never to the
 * page. From there the register's keys work as from anywhere.
 */
describe('BankRegisterPage — the row list is focus\'s home', () => {
    const FIRST = makeTxn({
        id: 't-first', headerId: '00000000-0000-0000-0000-0000000000e1', payee: 'First row',
        counterpartyAccountId: '00000000-0000-0000-0000-0000000000c1',
        counterpartyAccountName: 'Category A',
    });
    const SECOND = makeTxn({ id: 't-second', headerId: '00000000-0000-0000-0000-0000000000e2', payee: 'Second row' });

    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: -4.5, currencyCode: 'USD',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(FIRST), entryOf(SECOND)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
    });

    const rowList = () => screen.getByRole('grid', { name: 'Transactions' });
    const focusedRow = () => document.querySelector('[data-focused="true"]')?.getAttribute('data-headerid') ?? null;

    async function selectSecond(user: ReturnType<typeof userEvent.setup>) {
        const checkbox = await screen.findByRole('checkbox', { name: `Select transaction ${SECOND.id}` });
        await user.click(checkbox);
        expect(checkbox).toBeChecked();
    }

    async function openEditor() {
        fireEvent.dblClick((await screen.findByText('First row')).closest('[role="row"]')!);
        await screen.findByRole('button', { name: /^Save/ });
        expect(document.querySelector('[data-editing="true"]')).not.toBeNull();
    }

    const editorClosed = () => expect(document.querySelector('[data-editing="true"]')).toBeNull();

    it('is a named grid that takes focus without being a Tab stop', async () => {
        renderRegister();
        await screen.findByText('First row');

        expect(rowList()).toHaveAttribute('tabindex', '-1');
        expect(rowList().className).toContain('focus-visible:ring');
    });

    it('takes focus when Clear selection takes itself away', async () => {
        renderRegister();
        const user = userEvent.setup();
        await selectSecond(user);

        await user.click(await screen.findByRole('button', { name: 'Clear selection' }));

        await waitFor(() => expect(screen.queryByRole('button', { name: 'Clear selection' })).toBeNull());
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when a bulk delete clears the bar', async () => {
        vi.spyOn(apiModule, 'bulkDeleteTransactions').mockResolvedValue({ hardDeleted: 1, softHidden: 0 });
        renderRegister();
        const user = userEvent.setup();
        await selectSecond(user);

        await user.click(await screen.findByRole('button', { name: /^Delete$/ }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /^Delete$/ }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        await waitFor(() => expect(screen.queryByRole('button', { name: 'Clear selection' })).toBeNull());
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when Escape cancels an inline edit', async () => {
        renderRegister();
        const user = userEvent.setup();
        await openEditor();

        await user.keyboard('{Escape}');

        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();
        // Focus the keyboard handed it is ringed; only focus a press gave it is not.
        expect(rowList()).not.toHaveAttribute('data-pressed');
    });

    it('takes focus when Cancel closes an inline edit', async () => {
        renderRegister();
        const user = userEvent.setup();
        await openEditor();

        await user.click(screen.getByRole('button', { name: /^Cancel/ }));

        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when Save closes an inline edit', async () => {
        vi.spyOn(apiModule, 'patchTransaction').mockResolvedValue([entryOf(FIRST)]);
        renderRegister();
        const user = userEvent.setup();
        await openEditor();

        await user.click(screen.getByRole('button', { name: /^Save/ }));

        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();
    });

    it.each([
        ['Escape', async (user: ReturnType<typeof userEvent.setup>) => { await user.keyboard('{Escape}'); }],
        ['Cancel', async (user: ReturnType<typeof userEvent.setup>) => {
            await user.click(screen.getByRole('button', { name: /^Cancel/ }));
        }],
    ])('takes focus when %s closes the new-row form', async (_how, close) => {
        renderRegister();
        const user = userEvent.setup();
        await screen.findByText('First row');
        await user.click(screen.getByRole('button', { name: /\+ New transaction/i }));
        await screen.findByRole('button', { name: /^Save/ });

        await close(user);

        await waitFor(() => expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull());
        expect(rowList()).toHaveFocus();
    });

    it('leaves focus where a press outside the editor put it', async () => {
        renderRegister();
        const user = userEvent.setup();
        await openEditor();
        const search = screen.getByRole('searchbox', { name: 'Search transactions' });

        await user.click(search);

        await waitFor(editorClosed);
        expect(search).toHaveFocus();
    });

    it('leaves focus the user moved elsewhere while the save ran', async () => {
        let finish: (value: RegisterEntry[]) => void = () => {};
        vi.spyOn(apiModule, 'patchTransaction').mockReturnValue(
            new Promise<RegisterEntry[]>((resolve) => { finish = resolve; }),
        );
        renderRegister();
        const user = userEvent.setup();
        await openEditor();
        await user.click(screen.getByRole('button', { name: /^Save/ }));
        // A press outside does not cancel an editor whose save is in flight.
        const search = screen.getByRole('searchbox', { name: 'Search transactions' });
        await user.click(search);
        expect(document.querySelector('[data-editing="true"]')).not.toBeNull();

        await act(async () => finish([entryOf(FIRST)]));

        await waitFor(editorClosed);
        expect(search).toHaveFocus();
    });

    it('works the register\'s keys with focus on it: the arrows, Enter and N', async () => {
        renderRegister();
        const user = userEvent.setup();
        await selectSecond(user);
        await user.click(await screen.findByRole('button', { name: 'Clear selection' }));
        await waitFor(() => expect(rowList()).toHaveFocus());

        await user.keyboard('{ArrowDown}');
        expect(focusedRow()).toBe(FIRST.headerId);
        await user.keyboard('{ArrowDown}');
        expect(focusedRow()).toBe(SECOND.headerId);
        expect(rowList()).toHaveFocus();

        await user.keyboard('{ArrowUp}{Enter}');
        expect(await screen.findByRole('button', { name: /^Save/ })).toBeInTheDocument();
        await user.keyboard('{Escape}');
        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();

        await user.keyboard('n');
        expect(await screen.findByRole('button', { name: /^Save/ })).toBeInTheDocument();
    });

    // An empty register draws a placeholder instead of the list: focus goes to
    // "+ New transaction", the register's add control, never to the page.
    it.each([
        ['Escape', async (user: ReturnType<typeof userEvent.setup>) => { await user.keyboard('{Escape}'); }],
        ['Cancel', async (user: ReturnType<typeof userEvent.setup>) => {
            await user.click(screen.getByRole('button', { name: /^Cancel/ }));
        }],
    ])('gives focus to + New transaction when %s closes the new-row form in an empty account', async (_how, close) => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({ entries: [], cursorForOlder: null, cursorForNewer: null });
        renderRegister();
        const user = userEvent.setup();
        await screen.findByText('No transactions in this account.');
        const add = screen.getByRole('button', { name: /\+ New transaction/i });
        await user.click(add);
        await screen.findByRole('button', { name: /^Save/ });

        await close(user);

        await waitFor(() => expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull());
        expect(add).toHaveFocus();
    });

    it('gives focus to + New transaction when a bulk delete takes the last rows away', async () => {
        let entries: RegisterEntry[] = [entryOf(SECOND)];
        vi.spyOn(apiModule, 'fetchRegister').mockImplementation(async () => ({ entries, cursorForOlder: null, cursorForNewer: null }));
        vi.spyOn(apiModule, 'bulkDeleteTransactions').mockImplementation(async () => {
            entries = [];
            return { hardDeleted: 1, softHidden: 0 };
        });
        renderRegister();
        const user = userEvent.setup();
        await selectSecond(user);

        await user.click(await screen.findByRole('button', { name: /^Delete$/ }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /^Delete$/ }));

        await screen.findByText('No transactions in this account.');
        await waitFor(() => expect(screen.getByRole('button', { name: /\+ New transaction/i })).toHaveFocus());
    });
});

