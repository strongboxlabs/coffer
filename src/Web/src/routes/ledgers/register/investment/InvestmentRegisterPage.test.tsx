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

import { InvestmentRegisterPage } from './InvestmentRegisterPage';
import * as apiModule from '@/lib/api';
import type {
    AccountSummary,
    InvestmentRow,
    LedgerSummary,
    RegisterEntry,
} from '@/lib/types';

// Smoke tests for the investment register page. We lock down:
//   * empty state copy
//   * a populated row renders with the action chip + security ticker
//   * status-badge click cycles recon status optimistically (no
//     server round-trip waited on)
//   * right-click → Delete → ConfirmDialog → confirm calls the
//     delete endpoint and removes the row in-place
//   * "+ New transaction" toolbar opens the editor
//   * double-click on a row opens the editor in edit mode (the
//     edit-on-row dispatch)
//
// The aggregator + edit-shape conversion (`legsToDraft`) have
// their own unit tests under tests/. Here we only verify the page
// wiring.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const LOSER_HEADER = '00000000-0000-0000-0000-00000000105e';
const SURVIVOR_HEADER = '00000000-0000-0000-0000-0000000005ff';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000200';
const HOLDINGS_SIBLING_ID = '00000000-0000-0000-0000-000000000201';

const TEST_LEDGER: LedgerSummary = {
    id: LEDGER_ID,
    name: 'Personal',
    role: 'owner',
};

const TEST_ACCOUNT: AccountSummary = {
    id: ACCOUNT_ID,
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Brokerage',
    accountType: 'investment',
    categoryKind: null,
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: HOLDINGS_SIBLING_ID,
    isTradeCommission: false,
};

function makeTxn(
    overrides: Partial<InvestmentRow> & { id: string },
): InvestmentRow {
    const defaults: InvestmentRow = {
        kind: 'investment',
        id: '',
        accountId: ACCOUNT_ID,
        payee: null,
        memo: null,
        amount: -1000,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: 5000,
        origin: 'manual',
        isPending: false,
        investmentAction: 'buy',
        externalId: null,
        checkNumber: null,
        counterpartyId: '00000000-0000-0000-0000-000000000999',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        headerId: '00000000-0000-0000-0000-000000000aaa',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-01T12:00:00Z',
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        securityId: '00000000-0000-0000-0000-000000000sec',
        securityTicker: 'ETFA',
        securityName: 'Index ETF A',
        quantity: 10,
        unitPrice: 100,
        postingRole: 'security',
        ingestActionHint: null,
        ingestSecurityId: null,
        ingestShares: null,
        ingestUnitPrice: null,
        ingestFee: null,
        ingestAmount: null,
        ingestSecurityTickerHint: null,
        categoryAccountId: null,
        categoryAccountName: null,
        categoryAccountType: null,
        transferAccountId: null,
        transferAccountName: null,
        transferAccountType: null,
        feeAmount: null,
        feeCategoryId: null,
        feeCategoryName: null,
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

function entryOf(t: InvestmentRow): RegisterEntry {
    return { kind: 'txn', txn: t, groupId: null, legs: null };
}

function renderPage(
    initialUrl = `/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}`,
) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    queryClient.setQueryData(['ledgers'], [TEST_LEDGER]);
    queryClient.setQueryData(['accounts', LEDGER_ID], [TEST_ACCOUNT]);

    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: InvestmentRegisterPage,
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

    // The router is returned so a test can drive a CLIENT-SIDE navigation —
    // the path where an arrival anchor shows up after mount rather than in the
    // first render.
    return renderWithRouter(router, queryClient);
}

function renderWithRouter(
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    router: any,
    queryClient: QueryClient,
) {
    const result = render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return { ...result, router };
}

describe('InvestmentRegisterPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        // TanStack Router's scroll restoration calls window.scrollTo on
        // navigation; jsdom doesn't implement it. Stub so the new
        // expand / select interactions (which can trigger a focus
        // scroll) don't spew "Not implemented" noise.
        window.scrollTo = vi.fn();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        // Any save or merge kicks off the in-place balance refresh. Unstubbed it
        // reaches the real fetch with a relative URL, which this environment
        // cannot parse — an ignored rejection on today's jsdom and a hard
        // "unhandled error" run failure on a newer one, which is how the last
        // one of these surfaced: as a red dependency-bump PR rather than as a
        // red test.
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        // useSelection fires a debounced selection-summary query the
        // moment a row is checked. Stub it so the count/Σ readout is
        // deterministic and no network call escapes the test.
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1,
            sumOnAccount: -1000,
        });
        vi.spyOn(apiModule, 'fetchHoldings').mockResolvedValue({
            accountId: ACCOUNT_ID,
            accountName: 'Brokerage',
            currencyCode: 'USD',
            summary: {
                portfolioValue: 0,
                costBasis: 0,
                unrealizedGain: 0,
                percentChange: 0,
                cashBalance: 0,
                total: 0,
            },
            positions: [],
        });
        // ADR-0080: the register window returns collapsed events, so edit /
        // Duplicate / raw-data fetch the full leg set from the /legs endpoint.
        // Default to empty; tests that assert on inverted drafts override it.
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([]);
    });

    it('renders the empty-state copy when the account has no transactions', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        expect(
            await screen.findByText(/no transactions in this account/i),
        ).toBeInTheDocument();
    });

    it('renders a populated row with the security ticker visible', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // The aggregator collapses single-posting "buy" actions to
        // a single row that surfaces the ticker via the
        // investmentStrategy slot 7 renderer. The cell renders
        // "ETFA · Index ETF A" as one span; match on the prefix.
        expect(
            await screen.findByText(/^ETFA · Index ETF A$/),
        ).toBeInTheDocument();
    });

    it('stacks the check# under the Action and the tax date under the Date', async () => {
        // Line 2 of the date cell belongs to the date. The check number is an MD
        // marker qualifying the ACTION ('Auto' / 'EXfr' / 'Xfr' / a cheque number),
        // so it hangs off the action chip instead. Nothing pinned the previous
        // arrangement, which is why the swap was invisible to the suite.
        const txn = makeTxn({
            id: 't1',
            checkNumber: 'EXfr',
            postedAt: '2026-05-01T12:00:00Z',
            transactedAt: '2026-04-28T12:00:00Z',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const checkMarker = await screen.findByText('EXfr');
        const taxDate = await screen.findByText(/^tax /);

        // The check# shares a cell with the action chip; the tax date with the date.
        const actionCell = checkMarker.parentElement;
        expect(actionCell?.textContent).toContain('Buy');
        expect(actionCell?.textContent).not.toMatch(/^tax /);

        const dateCell = taxDate.parentElement;
        expect(dateCell?.textContent).toContain('May');
        expect(dateCell?.textContent).not.toContain('EXfr');
    });

    it('omits the tax-date line when the tax date is the posted day', async () => {
        // The common case by far — mig 189 stores transacted_at = posted_at rather
        // than null, so without this filter every row would carry a redundant line.
        const txn = makeTxn({
            id: 't1',
            postedAt: '2026-05-01T12:00:00Z',
            transactedAt: '2026-05-01T12:00:00Z',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        await screen.findByText(/^ETFA/);
        expect(screen.queryByText(/^tax /)).not.toBeInTheDocument();
    });

    it('clicking the status badge cycles the recon status optimistically', async () => {
        const txn = makeTxn({ id: 't1', status: 'uncleared' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        const setStatusSpy = vi
            .spyOn(apiModule, 'setReconStatus')
            .mockResolvedValue(undefined);

        renderPage();

        const statusButton = await screen.findByRole('button', {
            name: /Cycle reconciliation status \(currently uncleared\)/i,
        });

        const user = userEvent.setup();
        await user.click(statusButton);

        // The optimistic patch fires synchronously through `mutateEntries`,
        // so the aria-label should flip before the await on the
        // mutation result.
        await waitFor(() => {
            expect(
                screen.getByRole('button', {
                    name: /Cycle reconciliation status \(currently reconciling\)/i,
                }),
            ).toBeInTheDocument();
        });
        expect(setStatusSpy).toHaveBeenCalledWith(
            LEDGER_ID,
            txn.headerId,
            { status: 'reconciling', accountId: ACCOUNT_ID },
        );
    });

    it('opens the New-transaction editor when the toolbar button is clicked', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const newButton = await screen.findByRole('button', {
            name: /\+ New transaction/i,
        });

        const user = userEvent.setup();
        await user.click(newButton);

        // Editor surface is open: the Save / Cancel buttons appear.
        expect(
            await screen.findByRole('button', { name: /^Cancel$/i }),
        ).toBeInTheDocument();
    });

    // ── Bulk selection (ADR-0024) ──────────────────────────────────
    // Parity with the bank register: row toggle, select-all tri-state,
    // bulk recon-status, bulk-delete typed-confirm, read-only gating.

    it('toggling a row checkbox reveals the bulk action bar with the server count', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        expect(rowCheckbox).not.toBeChecked();

        const user = userEvent.setup();
        await user.click(rowCheckbox);

        // Checkbox flips on; the bulk action bar appears with the
        // server-summary count (mocked to 1).
        await waitFor(() => {
            expect(rowCheckbox).toBeChecked();
        });
        const actionBar = await screen.findByRole('region', {
            name: /bulk actions/i,
        });
        await waitFor(() => {
            expect(actionBar).toHaveTextContent(/1 selected/i);
        });
    });

    it('header select-all is tri-state and selects every visible row', async () => {
        const t1 = makeTxn({ id: 't1', headerId: '00000000-0000-0000-0000-0000000000a1' });
        const t2 = makeTxn({ id: 't2', headerId: '00000000-0000-0000-0000-0000000000a2' });
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 2,
            sumOnAccount: -2000,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(t1), entryOf(t2)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const selectAll = await screen.findByRole('checkbox', {
            name: /select all transactions in this account/i,
        });
        expect(selectAll).not.toBeChecked();

        const user = userEvent.setup();
        await user.click(selectAll);

        // All visible row checkboxes flip on; header reads fully checked.
        await waitFor(() => {
            expect(
                screen.getByRole('checkbox', { name: `Select transaction ${t1.id}` }),
            ).toBeChecked();
        });
        expect(
            screen.getByRole('checkbox', { name: `Select transaction ${t2.id}` }),
        ).toBeChecked();
        expect(selectAll).toBeChecked();
        // Indeterminate is false when ALL are selected.
        expect((selectAll as HTMLInputElement).indeterminate).toBe(false);

        // Unchecking one row drops the header into the indeterminate
        // (some-but-not-all) state.
        await user.click(
            screen.getByRole('checkbox', { name: `Select transaction ${t1.id}` }),
        );
        await waitFor(() => {
            expect((selectAll as HTMLInputElement).indeterminate).toBe(true);
        });
        expect(selectAll).not.toBeChecked();
    });

    it('all-mode delete is enabled and a large count shows the typed-confirm', async () => {
        // All-mode bulk delete is no longer gated client-side: the
        // server restricts all-mode ops to headers this account
        // ORIGINATES (ADR-0036), so the Delete button is enabled once
        // the header checkbox flips the selection into 'all' mode. A
        // large server count (> 100) still gates Confirm behind the
        // typed-confirm input.
        const t1 = makeTxn({ id: 't1', headerId: '00000000-0000-0000-0000-0000000000a1' });
        const t2 = makeTxn({ id: 't2', headerId: '00000000-0000-0000-0000-0000000000a2' });
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 250,
            sumOnAccount: -250000,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(t1), entryOf(t2)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

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

    it('bulk recon-status sends the selection and clears it', async () => {
        const txn = makeTxn({ id: 't1', status: 'uncleared' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        const bulkReconSpy = vi
            .spyOn(apiModule, 'bulkSetReconStatus')
            .mockResolvedValue({ updated: 1 });

        renderPage();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        const user = userEvent.setup();
        await user.click(rowCheckbox);

        const clearedButton = await screen.findByRole('button', {
            name: /✓ Cleared/i,
        });
        await user.click(clearedButton);

        await waitFor(() => {
            expect(bulkReconSpy).toHaveBeenCalledWith(
                LEDGER_ID,
                expect.objectContaining({ kind: 'explicit', headerIds: [txn.headerId] }),
                ACCOUNT_ID,
                'cleared',
            );
        });
        // Selection clears on success → the footer and its action buttons
        // drop away. The register footer now surfaces only for an active
        // selection or while loading (parity with bank), so once the
        // selection clears the whole bar — including "✓ Cleared" — is gone.
        await waitFor(() => {
            expect(
                screen.queryByRole('button', { name: /✓ Cleared/i }),
            ).not.toBeInTheDocument();
        });
    });

    it('bulk delete opens a confirm dialog and calls the bulk endpoint on confirm', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        const bulkDeleteSpy = vi
            .spyOn(apiModule, 'bulkDeleteTransactions')
            .mockResolvedValue({ hardDeleted: 1, softHidden: 0 });
        // Spy on the per-ledger accounts invalidation that resets the
        // sidebar's green "needs review" dot live after a bulk delete.
        const invalidateSpy = vi.spyOn(
            QueryClient.prototype,
            'invalidateQueries',
        );

        renderPage();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        const user = userEvent.setup();
        await user.click(rowCheckbox);

        // Delete in the action bar is ENABLED for an explicit selection
        // of an editable (non-target) row.
        const deleteButton = await screen.findByRole('button', { name: /^Delete$/ });
        expect(deleteButton).toBeEnabled();
        await user.click(deleteButton);

        // Confirm dialog opens; small count → no typed-confirm input.
        const dialog = await screen.findByRole('dialog');
        expect(dialog).toHaveTextContent(/Delete 1 transaction\?/i);
        expect(
            within(dialog).queryByRole('textbox'),
        ).not.toBeInTheDocument();

        await user.click(
            within(dialog).getByRole('button', { name: /^Delete$/ }),
        );

        await waitFor(() => {
            expect(bulkDeleteSpy).toHaveBeenCalledWith(
                LEDGER_ID,
                expect.objectContaining({
                    kind: 'explicit',
                    headerIds: [txn.headerId],
                }),
            );
        });
        // The accounts query is invalidated so the sidebar review-dot
        // refetches without a page reload.
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({
                queryKey: ['accounts', LEDGER_ID],
            });
        });
    });

    it('disables bulk delete when a read-only target-split row is selected', async () => {
        // accountPostingsOnHeader < headerTotalPostings → this row is
        // the counter-side of a multi-posting header (read-only here).
        const target = makeTxn({
            id: 't-target',
            accountPostingsOnHeader: 1,
            headerTotalPostings: 2,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000cc',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(target)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const rowCheckbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${target.id}`,
        });
        const user = userEvent.setup();
        await user.click(rowCheckbox);

        const deleteButton = await screen.findByRole('button', { name: /^Delete$/ });
        await waitFor(() => {
            expect(deleteButton).toBeDisabled();
        });
        expect(deleteButton).toHaveAttribute(
            'title',
            expect.stringMatching(/canonical owner is elsewhere/i),
        );
    });

    // ── Row context menu: Duplicate (parity with bank) ─────────────
    // A normal originating investment row offers Duplicate; a read-only
    // target-split (incl. the collapsed split-parent) does NOT — same
    // gating as Edit / Delete (ADR-0036).

    it('offers Duplicate in the context menu of a normal originating row', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Right-click the populated row to open its context menu.
        const cell = await screen.findByText(/^ETFA · Index ETF A$/);
        const row = cell.closest('[role="row"]') as HTMLElement;
        fireEvent.contextMenu(row);

        const menu = await screen.findByRole('menu');
        // Editable originating row → Edit + Duplicate + Delete present.
        expect(
            within(menu).getByRole('menuitem', { name: /^Duplicate/ }),
        ).toBeInTheDocument();
        expect(
            within(menu).getByRole('menuitem', { name: /^Edit/ }),
        ).toBeInTheDocument();
        expect(
            within(menu).getByRole('menuitem', { name: /^Delete/ }),
        ).toBeInTheDocument();
    });

    it('opens the new-transaction editor seeded when Duplicate is chosen', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({
            accounts: [],
            categories: [],
        });
        // Duplicate inverts the full leg set (ADR-0080 /legs fetch); the
        // row's own security leg is enough to seed the 'buy' draft.
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([txn]);

        renderPage();

        const cell = await screen.findByText(/^ETFA · Index ETF A$/);
        const row = cell.closest('[role="row"]') as HTMLElement;
        fireEvent.contextMenu(row);

        const menu = await screen.findByRole('menu');
        const user = userEvent.setup();
        await user.click(
            within(menu).getByRole('menuitem', { name: /^Duplicate/ }),
        );

        // The new-transaction editor opens (data-creating row) seeded
        // from the source: the action picker reflects the source's
        // 'buy' action, so its action-specific fields render.
        const editor = await screen.findByRole('row', {
            name: /new investment transaction/i,
        });
        expect(editor).toHaveAttribute('data-creating', 'true');
        const actionSelect = within(editor).getByRole('combobox', {
            name: /^Action$/,
        });
        expect((actionSelect as HTMLSelectElement).value).toBe('buy');
    });

    // ── Target-split collapse (ADR-0028 refinement 2026-06) ─────────
    // A bank-shape target-split cluster (2+ postings of one header
    // landing on this account) collapses into ONE expandable
    // split-parent row showing the NET amount + the REAL balance.
    // Expanding reveals the leg rows (own amount, blank balance).
    // Selecting the parent leaves bulk Delete disabled (read-only).

    // Two postings of one paycheck header landing on this 401(k) cash
    // sleeve: a deferral (+$1,137.48) and an employer match (+$299.34).
    // accountPostingsOnHeader=2 < headerTotalPostings=9 → target-split.
    const SPLIT_HEADER = '00000000-0000-0000-0000-0000000000f1';
    function targetClusterEntries(): RegisterEntry[] {
        const common = {
            headerId: SPLIT_HEADER,
            payee: 'Paycheck',
            investmentAction: null,
            derivedAction: 'Xfr' as const,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c9',
            counterpartyAccountName: 'Paycheck',
            counterpartyAccountType: 'bank',
            balanceAfter: 1436.82,
            accountPostingsOnHeader: 2,
            headerTotalPostings: 9,
        };
        const deferral = makeTxn({
            ...common,
            id: 'leg-deferral',
            legIndex: 0,
            amount: 1137.48,
        });
        const match = makeTxn({
            ...common,
            id: 'leg-match',
            legIndex: 1,
            amount: 299.34,
        });
        return [entryOf(deferral), entryOf(match)];
    }

    it('collapses a target-split cluster into ONE row with net amount + real balance, expandable to legs', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: targetClusterEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // ONE collapsed parent row with the "2 splits" affordance.
        const expandToggle = await screen.findByRole('button', {
            name: /2 splits/i,
        });
        expect(expandToggle).toHaveAttribute('aria-expanded', 'false');

        // Net amount (1137.48 + 299.34 = 1436.82) AND the real balance
        // (1436.82) both render — NOT the old fabricated per-leg step.
        const parentRow = expandToggle.closest('[role="row"]') as HTMLElement;
        expect(parentRow).toHaveTextContent('$1,436.82');

        // The leg amounts are NOT shown while collapsed.
        expect(screen.queryByText(/\$1,137\.48/)).not.toBeInTheDocument();
        expect(screen.queryByText(/\$299\.34/)).not.toBeInTheDocument();

        // Expand → the two leg rows appear with their own amounts.
        const user = userEvent.setup();
        await user.click(expandToggle);

        await waitFor(() => {
            expect(screen.getByText(/\$1,137\.48/)).toBeInTheDocument();
        });
        expect(screen.getByText(/\$299\.34/)).toBeInTheDocument();

        // Leg rows leave the Balance cell truly blank (no glyph), matching
        // the bank split-leg. Both leg rows are marked data-split-leg; the
        // trailing cell (balance, slot 9) is empty.
        const legRows = document.querySelectorAll('[data-split-leg="true"]');
        expect(legRows).toHaveLength(2);
        for (const legRow of Array.from(legRows)) {
            const cells = legRow.querySelectorAll('[role="cell"]');
            const balanceCell = cells[cells.length - 1];
            expect(balanceCell?.textContent).toBe('');
        }
    });

    it('omits Duplicate (and Edit / Delete) on a read-only split-parent context menu', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: targetClusterEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Right-click the collapsed split-parent row (located via its
        // expand toggle's enclosing row).
        const expandToggle = await screen.findByRole('button', {
            name: /2 splits/i,
        });
        const parentRow = expandToggle.closest('[role="row"]') as HTMLElement;
        fireEvent.contextMenu(parentRow);

        const menu = await screen.findByRole('menu');
        // Read-only target-split: no editable actions. Only the
        // read-only affordances (Show other side + Show raw data) remain.
        expect(
            within(menu).queryByRole('menuitem', { name: /^Duplicate/ }),
        ).not.toBeInTheDocument();
        expect(
            within(menu).queryByRole('menuitem', { name: /^Edit/ }),
        ).not.toBeInTheDocument();
        expect(
            within(menu).queryByRole('menuitem', { name: /^Delete/ }),
        ).not.toBeInTheDocument();
        expect(
            within(menu).getByRole('menuitem', { name: /show other side/i }),
        ).toBeInTheDocument();
    });

    it('selecting the collapsed split-parent leaves bulk Delete disabled (read-only)', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: targetClusterEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // The split-parent's checkbox toggles the cluster's HEADER
        // (header-level selection, ADR-0024).
        const parentCheckbox = await screen.findByRole('checkbox', {
            name: /select split transaction with 2 legs/i,
        });
        const user = userEvent.setup();
        await user.click(parentCheckbox);

        await waitFor(() => {
            expect(parentCheckbox).toBeChecked();
        });

        // Bulk Delete must stay disabled: the parent is a read-only
        // target-split (canonical owner is the paycheck's account).
        const deleteButton = await screen.findByRole('button', { name: /^Delete$/ });
        await waitFor(() => {
            expect(deleteButton).toBeDisabled();
        });
        expect(deleteButton).toHaveAttribute(
            'title',
            expect.stringMatching(/canonical owner is elsewhere/i),
        );
    });

    // ── Register consistency (shell extraction) ────────────────────
    // Investment gains the four shared register pieces: status-filter
    // tabs + counts, the checkbox-first row lead, the keyboard hint
    // chip, and the timeline sentinels. Bank already had all four;
    // these assert investment now matches.

    it('renders the status views in the shared Show dropdown', async () => {
        const txns = [
            makeTxn({
                id: 't-past',
                headerId: '00000000-0000-0000-0000-0000000000b1',
                postedAt: '2020-01-01T00:00:00Z',
                status: 'uncleared',
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Status views live in the compact "Show" dropdown (shared with bank).
        // Open it, then the views are listbox options.
        const showButton = await screen.findByRole('button', { name: /^Show:/i });
        fireEvent.click(showButton);
        expect(
            await screen.findByRole('option', { name: /^Scheduled/i }),
        ).toBeInTheDocument();
        expect(screen.getByRole('option', { name: /^Uncleared/i })).toBeInTheDocument();
        expect(screen.getByRole('option', { name: /^Reconciling/i })).toBeInTheDocument();
    });

    it('marks a future-dated row with the Scheduled status badge', async () => {
        // Regression: the row badge must use the RESOLVED status
        // (future-dated → scheduled), not the raw persisted recon status.
        // Previously a future-dated investment row showed the hollow
        // "uncleared" ring even though it counted toward the Scheduled tab
        // — the bank register resolved it, the investment one didn't.
        const txns = [
            makeTxn({
                id: 't-past',
                headerId: '00000000-0000-0000-0000-0000000000d1',
                postedAt: '2020-01-01T00:00:00Z',
                status: 'uncleared',
            }),
            makeTxn({
                id: 't-future',
                headerId: '00000000-0000-0000-0000-0000000000d2',
                postedAt: '2099-12-01T00:00:00Z',
                status: 'uncleared',
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Exactly one row resolves to scheduled (the 2099 row); the past
        // uncleared row keeps the uncleared badge.
        expect(
            await screen.findByRole('img', { name: 'Scheduled' }),
        ).toBeInTheDocument();
        expect(screen.getAllByRole('img', { name: 'Scheduled' })).toHaveLength(1);
    });

    it('clicking the Scheduled tab filters the list to future-dated rows', async () => {
        const txns = [
            makeTxn({
                id: 't-past',
                headerId: '00000000-0000-0000-0000-0000000000c1',
                payee: 'Past Trade',
                postedAt: '2020-01-01T00:00:00Z',
                securityTicker: 'PST',
                securityName: 'Past Co',
            }),
            makeTxn({
                id: 't-future',
                headerId: '00000000-0000-0000-0000-0000000000c2',
                payee: 'Future Trade',
                postedAt: '2099-12-01T00:00:00Z',
                securityTicker: 'FUT',
                securityName: 'Future Co',
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Both rows visible under the default "All" filter.
        expect(await screen.findByText(/^PST · Past Co$/)).toBeInTheDocument();
        expect(screen.getByText(/^FUT · Future Co$/)).toBeInTheDocument();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Scheduled/i }));

        // Only the future-dated (scheduled) row remains; the past row
        // is filtered out.
        await waitFor(() => {
            expect(screen.queryByText(/^PST · Past Co$/)).not.toBeInTheDocument();
        });
        expect(screen.getByText(/^FUT · Future Co$/)).toBeInTheDocument();
    });

    it('offers a Needs review view in the Show dropdown', async () => {
        // Two rows flagged needs_review + one not.
        const txns = [
            makeTxn({
                id: 't-rev-1',
                headerId: '00000000-0000-0000-0000-0000000000d1',
                needsReview: true,
            }),
            makeTxn({
                id: 't-rev-2',
                headerId: '00000000-0000-0000-0000-0000000000d2',
                needsReview: true,
            }),
            makeTxn({
                id: 't-ok',
                headerId: '00000000-0000-0000-0000-0000000000d3',
                needsReview: false,
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Needs review is one of the "Show" dropdown options.
        fireEvent.click(await screen.findByRole('button', { name: /^Show:/i }));
        expect(
            await screen.findByRole('option', { name: /^Needs review/i }),
        ).toBeInTheDocument();
    });

    it('clicking the Needs review tab filters to rows awaiting Accept', async () => {
        const txns = [
            makeTxn({
                id: 't-plain',
                headerId: '00000000-0000-0000-0000-0000000000e1',
                payee: 'Plain Trade',
                securityTicker: 'PLN',
                securityName: 'Plain Co',
                needsReview: false,
            }),
            makeTxn({
                id: 't-review',
                headerId: '00000000-0000-0000-0000-0000000000e2',
                payee: 'Review Trade',
                securityTicker: 'REV',
                securityName: 'Review Co',
                needsReview: true,
            }),
        ];
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: txns.map(entryOf),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        expect(await screen.findByText(/^PLN · Plain Co$/)).toBeInTheDocument();
        expect(screen.getByText(/^REV · Review Co$/)).toBeInTheDocument();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /^Show:/i }));
        await user.click(await screen.findByRole('option', { name: /^Needs review/i }));

        await waitFor(() => {
            expect(screen.queryByText(/^PLN · Plain Co$/)).not.toBeInTheDocument();
        });
        expect(screen.getByText(/^REV · Review Co$/)).toBeInTheDocument();
    });

    it('renders the combined controls bar with the Show dropdown and the New button in one row', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // The status "Show" dropdown and the "+ New transaction" button share
        // one controls row.
        const showButton = await screen.findByRole('button', { name: /^Show:/i });
        const newButton = screen.getByRole('button', { name: /\+ New transaction/i });
        const controlsBar = showButton.closest('div')?.parentElement as HTMLElement;
        expect(controlsBar).toContainElement(newButton);
        // The status views (incl. Needs review) live in the dropdown.
        fireEvent.click(showButton);
        expect(
            await screen.findByRole('option', { name: /^Needs review/i }),
        ).toBeInTheDocument();
    });

    it('opens each row with the checkbox before the status button (checkbox-first lead)', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const checkbox = await screen.findByRole('checkbox', {
            name: `Select transaction ${txn.id}`,
        });
        const statusButton = screen.getByRole('button', {
            name: /Cycle reconciliation status/i,
        });
        // Checkbox precedes the status button in document order — the
        // standardized [checkbox][status] lead shared with bank.
        expect(
            checkbox.compareDocumentPosition(statusButton)
                & Node.DOCUMENT_POSITION_FOLLOWING,
        ).toBeTruthy();
    });

    it('does not show the keyboard-hint chip in the dense controls bar', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        await screen.findByRole('button', { name: /\+ New transaction/i });
        // The verbose "N new · Enter edit selected" hint was dropped from the
        // combined controls bar in the Option-A redesign.
        expect(screen.queryByText(/edit selected/i)).not.toBeInTheDocument();
    });

    // A cash-neutral event nets to ZERO on the brokerage sleeve by
    // construction — a reinvestment is income +X and security −X — so ADR-0028's
    // Amount is a true and useless $0.00. 38.8% of dev's brokerage entries are
    // in that class, and none of them had any test at all: this file had no
    // DivReinvest fixture, so nothing pinned what a reader actually sees.
    describe('cash-neutral events show the settled amount', () => {
        it('shows what a reinvestment settled for, beneath its $0.00 net', async () => {
            const txn = makeTxn({
                id: 'divr1',
                investmentAction: 'dividend_reinvest',
                amount: 0,
                settledAmount: 316.29,
                feeAmount: null,
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            expect(await screen.findByText(/settled \$316\.29/)).toBeInTheDocument();
        });

        it('does NOT print the fee twice when a fee consumed the proceeds', async () => {
            // The trap: on these the settled amount EQUALS the fee, so a naive
            // "put settled on the subtitle line" renders $25.00 twice under a
            // zero. 478 dev entries across sell, sellx and buy.
            const txn = makeTxn({
                id: 'sell1',
                investmentAction: 'sell',
                amount: 0,
                settledAmount: 25,
                feeAmount: 25,
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            // Anchor on the fee actually rendering before asserting the absence.
            expect(await screen.findByText(/fee \$25\.00/)).toBeInTheDocument();
            expect(screen.queryByText(/settled/)).not.toBeInTheDocument();
        });

        it('leaves a row with a real net alone', async () => {
            // When the Amount cell already answers the question, a second
            // figure beneath it is noise.
            const txn = makeTxn({
                id: 'buy1',
                investmentAction: 'buy',
                amount: -1000,
                settledAmount: 1000,
                feeAmount: null,
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            expect(await screen.findByText(/^ETFA · Index ETF A$/)).toBeInTheDocument();
            expect(screen.queryByText(/settled/)).not.toBeInTheDocument();
        });
    });

    // The merge SAVE path, which neither register had a test for — the bank's
    // equivalent has shipped untested since #341, which is exactly how the
    // investment side could be missing the branch entirely and go unnoticed.
    //
    // On a merge the edited row becomes the LOSER and the server returns the
    // SURVIVOR's entry, so the ordinary save path (patch the saved entry onto
    // the edited header) painted the survivor's data onto the loser's row and
    // left two entries under one header id — a duplicate virtuoso key.
    //
    // Note the fetchHeaderLegs mock. editingContext bails on an EMPTY leg set,
    // so the shared stub's `[]` means the editor can never mount — which is why
    // nothing in this file had ever driven it.
    it('removes the loser and places the survivor when a merge is saved', async () => {
        const loser = makeTxn({
            id: 'loser', headerId: LOSER_HEADER, payee: 'imported dupe',
            needsReview: true,
        });
        const survivorRow = makeTxn({
            id: 'survivor', headerId: SURVIVOR_HEADER, payee: 'the keeper',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(loser), entryOf(survivorRow)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([loser]);
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([
            {
                headerId: SURVIVOR_HEADER,
                postedAt: '2026-05-01T12:00:00Z',
                dayDelta: 0,
                action: 'buy',
                securityTicker: 'ETFA',
                shares: 10,
                unitPrice: 100,
                amount: 1000,
                payee: 'the keeper',
            },
        ]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue(entryOf(survivorRow));

        renderPage();

        const loserCell = await screen.findByText('imported dupe');
        fireEvent.dblClick(loserCell.closest('[role="row"]')!);

        // Arm the merge by picking the candidate chip, then fold.
        // The chip is labelled by its SUMMARY — date · action · ticker · shares
        // · amount — not by the payee.
        fireEvent.click(await screen.findByRole('button', { name: /ETFA.*10 sh/i }));
        fireEvent.click(
            await screen.findByRole('button', { name: /fold into selected/i }),
        );

        await waitFor(() => expect(patchSpy).toHaveBeenCalled());
        // The PATCH carried the survivor as the merge target.
        expect(patchSpy.mock.calls[0]![2]).toEqual(
            expect.objectContaining({ mergeFromHeaderId: SURVIVOR_HEADER }),
        );

        // The loser is gone from the window…
        await waitFor(() => {
            expect(screen.queryByText('imported dupe')).not.toBeInTheDocument();
        });
        // …and the survivor appears exactly once, not duplicated onto the
        // loser's row.
        expect(screen.getAllByText('the keeper')).toHaveLength(1);
    });
});

describe('InvestmentRegisterPage — focus anchoring', () => {
    const ANCHOR = '00000000-0000-0000-0000-0000000000aa';

    beforeEach(() => {
        vi.restoreAllMocks();
        window.scrollTo = vi.fn();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1,
            sumOnAccount: -1000,
        });
        vi.spyOn(apiModule, 'fetchIndexBuckets').mockResolvedValue([]);
    });

    it('asks the server to anchor on the ?focus= row', async () => {
        const wanted = makeTxn({ id: 'a1', headerId: ANCHOR, payee: 'Anchored' });
        const fetchSpy = vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(wanted)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage(`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`);

        await waitFor(() => expect(fetchSpy).toHaveBeenCalled());
        expect(fetchSpy.mock.calls[0]![0]).toEqual(
            expect.objectContaining({ startingAtHeaderId: ANCHOR }),
        );
    });

    it('focuses the anchored row, not whatever came first', async () => {
        // "Show other side" onto a brokerage — from a bank row, from this
        // register (a transfer_shares counterparty is always another brokerage),
        // or from any Security Detail row — used to land at the newest rows with
        // nothing highlighted, because this page never read ?focus= at all.
        const bystander = makeTxn({ id: 'b1', headerId: 'other-header', payee: 'Bystander' });
        const wanted = makeTxn({ id: 'a1', headerId: ANCHOR, payee: 'Anchored' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(bystander), entryOf(wanted)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage(`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`);

        await waitFor(() => {
            expect(
                document.querySelector(`[data-headerid="${ANCHOR}"]`)
                    ?.getAttribute('data-focused'),
            ).toBe('true');
        });
        expect(
            document.querySelector('[data-headerid="other-header"]')
                ?.getAttribute('data-focused'),
        ).toBe('false');
    });

    it('focuses when the anchor ARRIVES BY NAVIGATION, not just on load', async () => {
        // The reported symptom: "Show other side" onto a brokerage lands at the
        // top, and only a browser refresh focuses the row. A refresh has the
        // anchor in the first render; a client-side navigation delivers it
        // afterwards.
        const bystander = makeTxn({ id: 'b1', headerId: 'other-header', payee: 'Bystander' });
        const wanted = makeTxn({ id: 'a1', headerId: ANCHOR, payee: 'Anchored' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(bystander), entryOf(wanted)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        // Arrive WITHOUT an anchor, as if from the sidebar...
        const { router } = renderPage();
        await screen.findByText('Anchored');
        expect(
            document.querySelector(`[data-headerid="${ANCHOR}"]`)
                ?.getAttribute('data-focused'),
        ).toBe('false');

        // ...then navigate to the same register WITH one.
        await router.navigate({
            to: '/ledgers/$ledgerId/accounts/$accountId',
            params: { ledgerId: LEDGER_ID, accountId: ACCOUNT_ID },
            search: { focus: ANCHOR },
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
        } as any);

        await waitFor(() => {
            expect(
                document.querySelector(`[data-headerid="${ANCHOR}"]`)
                    ?.getAttribute('data-focused'),
            ).toBe('true');
        });
    });

    it('focuses nothing when the server declines to anchor', async () => {
        // The server pins an anchor only when the row matches the active filter,
        // and otherwise returns the ordinary page without saying so. Focusing
        // "whatever is at index 0" is the bug this guards.
        const unrelated = makeTxn({ id: 'z1', headerId: 'not-the-anchor', payee: 'Unrelated' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(unrelated)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage(`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}?focus=${ANCHOR}`);

        // Settled anchor: the row the server DID return is on screen, so the
        // absence below is real rather than a frame that has not rendered.
        await waitFor(() => {
            expect(document.querySelector('[data-headerid="not-the-anchor"]')).not.toBeNull();
        });
        expect(
            document.querySelector('[data-headerid="not-the-anchor"]')
                ?.getAttribute('data-focused'),
        ).toBe('false');
    });
});

