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

import { InvestmentRegisterPage } from './InvestmentRegisterPage';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import * as apiModule from '@/lib/api';
import { formatLedgerDateLong } from '@/lib/dates';
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
    role: 'owner', currencyCode: 'USD',
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
        ...NO_NOTES,
        kind: 'investment',
        id: '',
        accountId: ACCOUNT_ID,
        payee: null,
        amount: -1000,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 5000,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
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
        headerId: '00000000-0000-0000-0000-000000000aaa',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-01T12:00:00Z',
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
        // "unhandled error" run failure on a newer one.
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        // useSelection fires a debounced selection-summary query the
        // moment a row is checked. Stub it so the count/Σ readout is
        // deterministic and no network call escapes the test.
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1,
            totalCount: 1,
            readOnlyCount: 0,
            sumOnAccount: -1000, currencyCode: 'USD',
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

    it('labels the action column for the check# it also carries', async () => {
        const txn = makeTxn({ id: 't1' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        await screen.findByText(/^ETFA/);
        expect(
            screen.getByRole('columnheader', { name: 'Action · Check #' }),
        ).toBeInTheDocument();
    });

    it('renders an unset action as a neutral dash chip, keeping the check# on line 2', async () => {
        // With no action and no ingest hint, a collapsing chip line would move
        // the check number to line 1 and the column would read ragged. The dash
        // is the same chip an action label uses, so the two rows line up.
        const unset = makeTxn({
            id: 't1',
            headerId: '00000000-0000-0000-0000-000000000ab1',
            investmentAction: null,
            derivedAction: null,
            ingestActionHint: null,
            checkNumber: '4021',
        });
        const bought = makeTxn({
            id: 't2',
            headerId: '00000000-0000-0000-0000-000000000ab2',
            checkNumber: '4022',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(unset), entryOf(bought)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const unsetCheck = await screen.findByText('4021');
        const unsetCell = unsetCheck.parentElement as HTMLElement;
        const dash = within(unsetCell).getByTitle('No action');
        expect(dash).toHaveTextContent(/^—$/);
        // The chip leads the cell and the check number follows it.
        expect(unsetCell.firstElementChild).toContainElement(dash);
        expect(
            dash.compareDocumentPosition(unsetCheck) & Node.DOCUMENT_POSITION_FOLLOWING,
        ).toBeTruthy();

        // Same chip, same classes as a set action's label.
        const boughtCell = screen.getByText('4022').parentElement as HTMLElement;
        const buyChip = within(boughtCell).getByText('Buy');
        expect(dash.className).toBe(buyChip.className);
    });

    it('shows a set action by its own label and no dash', async () => {
        const txn = makeTxn({ id: 't1', checkNumber: '4022' });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const checkNumber = await screen.findByText('4022');
        const actionCell = checkNumber.parentElement as HTMLElement;
        const buyChip = within(actionCell).getByText('Buy');
        expect(
            buyChip.compareDocumentPosition(checkNumber) & Node.DOCUMENT_POSITION_FOLLOWING,
        ).toBeTruthy();
        expect(screen.queryByTitle('No action')).not.toBeInTheDocument();
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
            totalCount: 2,
            readOnlyCount: 0,
            sumOnAccount: -2000, currencyCode: 'USD',
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
        // All-mode bulk delete is not gated client-side: the
        // server restricts all-mode ops to headers this account
        // ORIGINATES (ADR-0036), so the Delete button is enabled once
        // the header checkbox flips the selection into 'all' mode. A
        // large server count (> 100) still gates Confirm behind the
        // typed-confirm input.
        const t1 = makeTxn({ id: 't1', headerId: '00000000-0000-0000-0000-0000000000a1' });
        const t2 = makeTxn({ id: 't2', headerId: '00000000-0000-0000-0000-0000000000a2' });
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 250,
            totalCount: 250,
            readOnlyCount: 0,
            sumOnAccount: -250000, currencyCode: 'USD',
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

    describe('the footer count', () => {
        // The count element is found by its own text inside the footer region
        // and compared whole, so a missing or extra read-only part fails.
        async function expectFooterCount(text: string) {
            const region = await screen.findByRole('region', { name: /bulk actions/i });
            await waitFor(() => {
                expect(within(region).getByText(/\d+ selected/).textContent).toBe(text);
            });
        }

        const OWN = makeTxn({ id: 'f-own', headerId: '00000000-0000-0000-0000-0000000000f1' });
        // One line of a split another account owns, seen from here.
        const SPLIT_SIDE = makeTxn({
            id: 'f-split',
            headerId: '00000000-0000-0000-0000-0000000000f2',
            accountPostingsOnHeader: 1,
            headerTotalPostings: 2,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000cc',
            notesKind: 'splitOpposite',
            isReadOnly: true,
        });

        function mockRows() {
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(OWN), entryOf(SPLIT_SIDE)],
                cursorForOlder: null,
                cursorForNewer: null,
            });
        }

        it('names the read-only part of an explicit selection from its loaded rows while the summary is in flight', async () => {
            mockRows();
            // Never answers: until it does, the read-only part comes from the loaded rows.
            vi.spyOn(apiModule, 'fetchSelectionSummary').mockReturnValue(new Promise(() => {}));
            renderPage();

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${OWN.id}` }));
            await expectFooterCount('1 selected');

            await user.click(screen.getByRole('checkbox', { name: `Select transaction ${SPLIT_SIDE.id}` }));
            await expectFooterCount('2 selected · 1 read-only');
        });

        it('counts a select-all from the server, read-only rows included; the delete still names what it deletes', async () => {
            mockRows();
            vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
                count: 12,
                totalCount: 15,
                readOnlyCount: 3,
                sumOnAccount: -12000, currencyCode: 'USD',
            });
            renderPage();

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
                sumOnAccount: -2000, currencyCode: 'USD',
            });
            renderPage();

            const user = userEvent.setup();
            await user.click(await screen.findByRole('checkbox', {
                name: /select all transactions in this account/i,
            }));
            await expectFooterCount('2 selected');
        });
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

    it('disables bulk delete when a read-only row is selected', async () => {
        // The server marks the row read-only: one line of a split owned by
        // another account.
        const target = makeTxn({
            id: 't-target',
            accountPostingsOnHeader: 1,
            headerTotalPostings: 2,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000cc',
            notesKind: 'splitOpposite',
            isReadOnly: true,
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

    it('disables bulk unhide when a read-only row is selected, as it does delete', async () => {
        // Two hidden rows: one line of a split owned by another account, read-only
        // here, and an ordinary row this register can change.
        const target = makeTxn({
            id: 't-hidden-target',
            headerId: '00000000-0000-0000-0000-0000000000a1',
            accountPostingsOnHeader: 1,
            headerTotalPostings: 2,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000cc',
            notesKind: 'splitOpposite',
            isReadOnly: true,
            isHidden: true,
        });
        const own = makeTxn({
            id: 't-hidden-own',
            headerId: '00000000-0000-0000-0000-0000000000a2',
            isHidden: true,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockImplementation((args) =>
            Promise.resolve({
                entries: (args.hidden ? [target, own] : [makeTxn({ id: 't-visible' })]).map(entryOf),
                cursorForOlder: null,
                cursorForNewer: null,
            }),
        );

        renderPage();

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

    // ── A split's lines seen from this account ──────────────────────
    // Two lines of one paycheck land on this workplace-plan cash sleeve: a
    // deferral and an employer match. The split belongs to the paycheck's
    // account, so from here each line is its own read-only row; nothing
    // clusters them into an expandable parent.
    const SPLIT_HEADER = '00000000-0000-0000-0000-0000000000f1';
    function oppositeLineEntries(
        overrides: Partial<InvestmentRow> = {},
    ): RegisterEntry[] {
        const common = {
            headerId: SPLIT_HEADER,
            payee: 'Paycheck',
            investmentAction: null,
            derivedAction: 'Xfr' as const,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000c9',
            counterpartyAccountName: 'Paycheck',
            counterpartyAccountType: 'bank',
            balanceAfter: 1400.75,
            accountPostingsOnHeader: 2,
            headerTotalPostings: 9,
            notesKind: 'splitOpposite' as const,
            isReadOnly: true,
            ...overrides,
        };
        const deferral = makeTxn({
            ...common,
            id: 'leg-deferral',
            legIndex: 0,
            amount: 1100.25,
            memo: 'Deferral',
            memoInherited: true,
            inheritedMemo: 'Deferral',
        });
        const match = makeTxn({
            ...common,
            id: 'leg-match',
            legIndex: 1,
            amount: 300.50,
            memo: 'Employer match',
            memoInherited: true,
            inheritedMemo: 'Employer match',
        });
        return [entryOf(deferral), entryOf(match)];
    }

    it('shows each line of a split as its own row, with no splits toggle', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: oppositeLineEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Each line, with its own memo and amount, on its own row.
        const deferral = (await screen.findByText('Deferral')).closest('[role="row"]') as HTMLElement;
        const match = screen.getByText('Employer match').closest('[role="row"]') as HTMLElement;
        expect(deferral).not.toBe(match);
        expect(deferral).toHaveTextContent('$1,100.25');
        expect(match).toHaveTextContent('$300.50');
        // Both read-only, badged as a split's line, memo marked as inherited.
        expect(within(deferral).getByText('↗ Split')).toBeInTheDocument();
        expect(within(match).getByText('↗ Split')).toBeInTheDocument();
        expect(within(deferral).getByRole('img', { name: /^From / })).toBeInTheDocument();
        // No clustering affordance anywhere on the page.
        expect(screen.queryByRole('button', { name: /\d+ splits/i })).not.toBeInTheDocument();
        expect(document.querySelectorAll('[data-split-leg="true"]')).toHaveLength(0);
    });

    it('puts a split line\'s badge in the slot after the date, above its action chip, not in the payee cell', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: oppositeLineEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const row = (await screen.findByText('Deferral')).closest('[role="row"]') as HTMLElement;
        const { payee, afterDate } = badgeSlots(row, 'Deferral', formatLedgerDateLong('2026-05-01T12:00:00Z'));
        const badge = within(afterDate).getByText('↗ Split');
        expect(badge).toHaveAttribute(
            'title', 'One line of a split transaction · edit it in the split’s own register',
        );
        const chip = within(afterDate).getByText('Xfr');
        expect(badge.compareDocumentPosition(chip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(within(payee).getByText('Paycheck')).toBeInTheDocument();
        expect(within(payee).queryByText('↗ Split')).toBeNull();
    });

    it('offers neither Edit nor Duplicate on a split\'s line seen from here', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: oppositeLineEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const row = (await screen.findByText('Deferral')).closest('[role="row"]') as HTMLElement;
        fireEvent.contextMenu(row);

        const menu = await screen.findByRole('menu');
        expect(
            within(menu).getByRole('menuitem', { name: /show other side/i }),
        ).toBeInTheDocument();
        expect(
            within(menu).queryByRole('menuitem', { name: /^Duplicate/ }),
        ).not.toBeInTheDocument();
        expect(
            within(menu).queryByRole('menuitem', { name: /^Edit/ }),
        ).not.toBeInTheDocument();
    });

    it('opens no editor on a double-click of a read-only line', async () => {
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: oppositeLineEntries(),
            cursorForOlder: null,
            cursorForNewer: null,
        });
        const legsSpy = vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([]);

        renderPage();

        const row = (await screen.findByText('Deferral')).closest('[role="row"]') as HTMLElement;
        fireEvent.doubleClick(row);

        // The row is still a row, and no editor asked for the event's legs.
        expect(screen.getByText('Deferral')).toBeInTheDocument();
        expect(document.querySelector('[data-editing="true"]')).toBeNull();
        expect(legsSpy).not.toHaveBeenCalled();
    });

    it('marks a funding brokerage\'s counterpart row read-only and offers no Duplicate', async () => {
        // A buy funded from this brokerage: the event lives in the other
        // brokerage, and this row is its counterpart.
        const counterpart = makeTxn({
            id: 't-funding',
            investmentAction: 'buyx',
            memo: 'Quarterly rebalance',
            memoInherited: true,
            inheritedMemo: 'Quarterly rebalance',
            notesKind: 'eventOpposite',
            isReadOnly: true,
            counterpartyAccountId: '00000000-0000-0000-0000-0000000000cc',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(counterpart)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const row = (await screen.findByText('Quarterly rebalance')).closest('[role="row"]') as HTMLElement;
        expect(within(row).getByText('↗ Investment')).toBeInTheDocument();
        // The badge and the action chip share the slot after the date, badge
        // first; the payee cell carries neither.
        const { payee, afterDate } = badgeSlots(row, 'Quarterly rebalance', formatLedgerDateLong('2026-05-01T12:00:00Z'));
        const badge = within(afterDate).getByText('↗ Investment');
        expect(badge).toHaveAttribute(
            'title', 'Part of an investment transaction in another account · view + edit it there',
        );
        const chip = within(afterDate).getByText('BuyXfr');
        expect(badge.compareDocumentPosition(chip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(within(payee).queryByText('↗ Investment')).toBeNull();
        fireEvent.contextMenu(row);
        const menu = await screen.findByRole('menu');
        expect(within(menu).getByRole('menuitem', { name: /show other side/i })).toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Duplicate/ })).not.toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Edit/ })).not.toBeInTheDocument();
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
        // The row badge must use the RESOLVED status (future-dated →
        // scheduled), not the raw persisted recon status, as the bank register
        // does. Otherwise a future-dated investment row would show the hollow
        // "uncleared" ring even though it counts toward the Scheduled tab.
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

    // The grid's announced position. `aria-rowindex` was fed straight from
    // virtuoso's logical index, which carries a 1000000 front-shift offset
    // so rows could be prepended without going negative — so a screen reader
    // was told the first row of the register was row 1000001. The paired
    // `aria-rowcount` was the LOADED count, which made the announcement "row
    // 1000001 of 87".
    describe('announced row positions', () => {
        it('numbers rows from 1 and declares the total unknown', async () => {
            const rows = [
                makeTxn({ id: 'a1', payee: 'first row' }),
                makeTxn({ id: 'a2', payee: 'second row' }),
            ];
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: rows.map(entryOf),
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            const first = (await screen.findByText('first row')).closest('[role="row"]');
            const second = screen.getByText('second row').closest('[role="row"]');
            expect(first).toHaveAttribute('aria-rowindex', '1');
            expect(second).toHaveAttribute('aria-rowindex', '2');

            // -1 is ARIA's "not known". The window is a slice of the account,
            // so any concrete total here would be one the index cannot be read
            // against.
            const grid = first!.closest('[role="grid"]');
            expect(grid).toHaveAttribute('aria-rowcount', '-1');
        });
    });

    // Two things the register said that were not true of it.
    describe('what the rows and the header claim', () => {
        it('says the select-all checkbox is filter-scoped, because it is', async () => {
            // It said "Select all transactions in this account". This register
            // has the same filter bar as bank's, so the checkbox selects the
            // FILTERED set — a screen-reader user was told they were about to
            // act on every transaction in the account.
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(makeTxn({ id: 's1', payee: 'a row' }))],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            expect(await screen.findByText('a row')).toBeInTheDocument();
            expect(
                screen.getByRole('checkbox', { name: /matching the current filter/i }),
            ).toBeInTheDocument();
        });

        it('shows a pointer cursor, because double-click opens the editor', async () => {
            // The rows wire onDoubleClickEdit, and showed an arrow — the one
            // cursor that says "nothing happens here".
            const txn = makeTxn({ id: 'c1', payee: 'clickable' });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            const row = (await screen.findByText('clickable')).closest('[role="row"]')!;
            expect(row.className).toContain('cursor-pointer');
        });
    });

    // Tags are header-level (ADR-0009) and were blanked on this register — the
    // API replaced the row's array with an empty one on the way out, so a
    // tagged brokerage row looked untagged while the tag FILTER still matched
    // it. Nothing here had a fixture with a non-empty `tags`.
    describe('header tags', () => {
        it('renders the header tags on an investment row', async () => {
            const txn = makeTxn({
                id: 'tagged1',
                payee: 'tagged buy',
                tags: ['roth', 'long-hold'],
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            // Asserted on the title rather than the text: TagLine interleaves a
            // colour stripe before each name, so the line is several nodes. The
            // title is the full list and is what a truncated line falls back to,
            // which makes it the thing worth pinning anyway.
            expect(await screen.findByTitle('roth, long-hold')).toBeInTheDocument();
            // And the count beside them, which is the total rather than a
            // "+N hidden" — how many fit depends on the column's pixel width.
            expect(screen.getByLabelText('2 tags')).toBeInTheDocument();
        });

        it('renders tags on a row with no category, transfer or fee chip', async () => {
            // Slot 6's early-out ran on the three account chips alone. A plain
            // buy populates none of them, so a tagged one would have rendered
            // an empty cell — the commonest shape on a brokerage register.
            const txn = makeTxn({
                id: 'tagged2',
                payee: 'plain buy',
                investmentAction: 'buy',
                counterpartyAccountName: null,
                tags: ['roth'],
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            // Anchor on the row first: a bare queryBy would pass on the frame
            // before the register has rendered anything at all.
            expect(await screen.findByText('plain buy')).toBeInTheDocument();
            expect(screen.getByText('roth')).toBeInTheDocument();
        });

        it('shows a tag on this register the same way the bank register does', async () => {
            // Was "colours a tag chip from the ledger palette". ADR-0077 D4 had
            // said coloured chips were "a bank-register concern only" — which
            // followed from this register rendering no chips at all, not from
            // anything about colour — so the guard was that the same tag must
            // not look different depending on which register you were on.
            //
            // The COLOUR half of that is gone, with the chips: the register now
            // renders one truncating comma line (TagLine), and the palette hexes
            // are chip fills, not legible as foreground text. The guard that
            // remains is the one that still has meaning — the tag appears, on
            // both registers, rendered the same way.
            vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([
                { id: 'tag-1', name: 'roth', color: '#336699', usageCount: 1, transactionCount: 1 },
            ]);
            const txn = makeTxn({ id: 'coloured', payee: 'coloured buy', tags: ['roth'] });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            // Anchored on the row first — a bare queryBy would pass on the
            // frame before the register has rendered anything at all.
            expect(await screen.findByText('coloured buy')).toBeInTheDocument();
            expect(screen.getByText(/roth/)).toBeInTheDocument();
        });

        it('renders no tag chips on an untagged row', async () => {
            const txn = makeTxn({ id: 'untagged', payee: 'no tags here', tags: [] });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            expect(await screen.findByText('no tags here')).toBeInTheDocument();
            expect(screen.queryByText('roth')).toBeNull();
        });
    });

    // A cash-neutral event nets to ZERO on the brokerage sleeve by
    // construction — a reinvestment is income +X and security −X — so ADR-0028's
    // Amount is a true and useless zero. Every reinvestment is in that class,
    // so these pin what a reader actually sees for one.
    describe('cash-neutral events show the settled amount', () => {
        it('shows what a reinvestment settled for, beneath its $0.00 net', async () => {
            const txn = makeTxn({
                id: 'divr1',
                investmentAction: 'dividend_reinvest',
                amount: 0,
                settledAmount: 250.40,
                feeAmount: null,
            });
            vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
                entries: [entryOf(txn)],
                cursorForOlder: null,
                cursorForNewer: null,
            });

            renderPage();

            expect(await screen.findByText(/settled \$250\.40/)).toBeInTheDocument();
        });

        it('does NOT print the fee twice when a fee consumed the proceeds', async () => {
            // The trap: on these the settled amount EQUALS the fee, so a naive
            // "put settled on the subtitle line" renders the fee twice under a
            // zero. The shape occurs across sell, sellx and buy.
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

    // The merge SAVE path. Without a test here, the register can lack the
    // merge branch entirely while every other test stays green.
    //
    // On a merge the edited row becomes the LOSER and the server returns the
    // SURVIVOR's entry, so the ordinary save path (patch the saved entry onto
    // the edited header) would paint the survivor's data onto the loser's row
    // and leave two entries under one header id — a duplicate virtuoso key.
    //
    // Note the fetchHeaderLegs mock. editingContext bails on an EMPTY leg set,
    // so the shared stub's `[]` means the editor can never mount, and no test
    // over that stub drives it.
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
                derivedAction: 'buy',
                securityTicker: 'ETFA',
                shares: 10,
                unitPrice: 100,
                amount: -1000,
                payee: 'the keeper',
                counterpartyAccountId: null,
                counterpartyAccountName: null,
                origin: 'manual',
            },
        ]);
        // Merging is a COMMAND with its own route, mirroring bank. The PATCH
        // spy stays so a silent return to merge-by-PATCH fails the test.
        const mergeSpy = vi
            .spyOn(apiModule, 'mergeInvestmentTransaction')
            .mockResolvedValue([entryOf(survivorRow)]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([entryOf(survivorRow)]);

        renderPage();

        const loserCell = await screen.findByText('imported dupe');
        fireEvent.dblClick(loserCell.closest('[role="row"]')!);

        // Arm the merge by picking the candidate chip, then fold.
        // The chip reads like the candidate's register row — date · action ·
        // payee · ticker · shares · amount.
        fireEvent.click(await screen.findByRole('button', { name: /ETFA.*10 sh/i }));
        fireEvent.click(
            await screen.findByRole('button', { name: /fold into selected/i }),
        );

        await waitFor(() => expect(mergeSpy).toHaveBeenCalled());
        // Direction is inverted: the URL names the LOSER, the body the SURVIVOR.
        // Asserting both positions is what catches a swap of the two.
        expect(mergeSpy.mock.calls[0]![1]).toBe(LOSER_HEADER);
        expect(mergeSpy.mock.calls[0]![2]).toBe(SURVIVOR_HEADER);
        expect(patchSpy).not.toHaveBeenCalled();

        // The loser is gone from the window…
        await waitFor(() => {
            expect(screen.queryByText('imported dupe')).not.toBeInTheDocument();
        });
        // …and the survivor appears exactly once, not duplicated onto the
        // loser's row.
        expect(screen.getAllByText('the keeper')).toHaveLength(1);
    });

    it('keeps the selection when Escape on the editor\'s Save button cancels the edit', async () => {
        // The page's Escape clears the selection, and the editor's cancels the
        // edit. One Escape on a button inside the editor is the editor's alone.
        const edited = makeTxn({
            id: 't-edited', headerId: '00000000-0000-0000-0000-0000000000e1', payee: 'Edited row',
        });
        const picked = makeTxn({
            id: 't-picked', headerId: '00000000-0000-0000-0000-0000000000e2', payee: 'Selected row',
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(edited), entryOf(picked)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([edited]);
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
        const user = userEvent.setup();

        renderPage();

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

        // With no editor open, the page's Escape still clears it.
        expect(document.activeElement?.tagName).not.toBe('INPUT');
        await user.keyboard('{Escape}');
        await waitFor(() => {
            expect(screen.getByRole('checkbox', { name: `Select transaction ${picked.id}` })).not.toBeChecked();
        });
    });

    it('closes only Add security on Escape, the edit behind it keeps its draft, and focus goes back to the security field', async () => {
        // Add security opens from inside the editor. Its Escape is its own: the
        // editor's cancel-on-Escape must not see it, or the whole edit is lost
        // with the dialog.
        // No security yet, so the one typed in is new and Add security is offered.
        const txn = makeTxn({
            id: 't-edit', payee: 'Payee Original',
            securityId: null, securityTicker: null, securityName: null,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([txn]);
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({
            accounts: [],
            categories: [],
        });
        const user = userEvent.setup();

        renderPage();

        const cell = await screen.findByText('Payee Original');
        fireEvent.doubleClick(cell.closest('[role="row"]')!);
        const payee = await screen.findByRole('combobox', { name: 'Payee' });
        await user.clear(payee);
        await user.type(payee, 'Payee Edited');

        await user.type(screen.getByRole('combobox', { name: 'Security' }), 'ZZQQ');
        await user.click(await screen.findByText('+ Create new security "ZZQQ"'));
        const dialog = await screen.findByRole('dialog', { name: 'Add security' });
        // Focus is on the field the dialog's content focuses, Name: the ticker
        // comes filled in from the security field (ADR-0023 §L).
        expect(within(dialog).getByPlaceholderText('e.g. ABCDX')).toHaveValue('ZZQQ');
        expect(within(dialog).getByPlaceholderText('e.g. Broad Market Index Fund')).toHaveFocus();

        await user.keyboard('{Escape}');

        await waitFor(() => {
            expect(screen.queryByRole('dialog', { name: 'Add security' })).toBeNull();
        });
        // The editor is still open, with what was typed into it.
        expect(screen.getByRole('combobox', { name: 'Payee' })).toHaveValue('Payee Edited');
        expect(document.querySelector('[data-editing="true"]')).not.toBeNull();
        // Focus is back in the field the dialog was opened from (ADR-0023 §L),
        // not left on the page by the dialog's own autofocused field.
        expect(screen.getByRole('combobox', { name: 'Security' })).toHaveFocus();
    });

    it('creates the security on Enter in Add security, without saving the edit behind it', async () => {
        // Enter in the editor saves the transaction from any field. A field of
        // a dialog the editor opened is not one of its fields: Enter there
        // submits that dialog.
        const txn = makeTxn({
            id: 't-edit', payee: 'Payee Original',
            securityId: null, securityTicker: null, securityName: null,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([txn]);
        vi.spyOn(apiModule, 'fetchSecurities').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSimilarPayees').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchFrequentCounterparties').mockResolvedValue({
            accounts: [],
            categories: [],
        });
        const createSpy = vi
            .spyOn(apiModule, 'createSecurity')
            .mockResolvedValue({ securityId: '00000000-0000-0000-0000-0000000005e1' });
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([]);
        const user = userEvent.setup();

        renderPage();

        const cell = await screen.findByText('Payee Original');
        fireEvent.doubleClick(cell.closest('[role="row"]')!);
        await user.type(await screen.findByRole('combobox', { name: 'Security' }), 'ZZQQ');
        await user.click(await screen.findByText('+ Create new security "ZZQQ"'));
        const dialog = await screen.findByRole('dialog', { name: 'Add security' });
        expect(within(dialog).getByPlaceholderText('e.g. ABCDX')).toHaveValue('ZZQQ');

        await user.type(
            within(dialog).getByPlaceholderText('e.g. Broad Market Index Fund'),
            'Synthetic Fund{Enter}',
        );

        await waitFor(() => expect(createSpy).toHaveBeenCalledTimes(1));
        expect(patchSpy).not.toHaveBeenCalled();
        expect(document.querySelector('[data-editing="true"]')).not.toBeNull();
    });

    it('offers Accept on an unreviewed row, and it reaches the dedicated route', async () => {
        // Bank has offered this from the row menu since slice 2c; investment
        // never did, so accepting an imported row meant opening the editor and
        // re-saving every field through a wholesale PATCH — which writes back
        // whatever the client reconstructed, when accepting should change
        // nothing.
        const txn = makeTxn({
            id: 't-accept',
            headerId: '00000000-0000-0000-0000-0000000000e1',
            payee: 'imported buy',
            needsReview: true,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        const approveSpy = vi
            .spyOn(apiModule, 'approveInvestmentTransaction')
            .mockResolvedValue([]);
        const patchSpy = vi
            .spyOn(apiModule, 'patchInvestmentTransaction')
            .mockResolvedValue([]);
        const invalidateSpy = vi.spyOn(QueryClient.prototype, 'invalidateQueries');

        renderPage();

        const cell = await screen.findByText(txn.payee!);
        fireEvent.contextMenu(cell.closest('[role="row"]') as HTMLElement);
        fireEvent.click(await screen.findByRole('menuitem', { name: /^Accept$/ }));

        await waitFor(() => {
            expect(approveSpy).toHaveBeenCalledWith(
                LEDGER_ID, '00000000-0000-0000-0000-0000000000e1', ACCOUNT_ID,
            );
        });
        // Accepting must not re-save the row's fields.
        expect(patchSpy).not.toHaveBeenCalled();
        // A precise write: the home screen's reads refresh (ADR-0079 as amended
        // 2026-11-03), and the register's window key is left alone.
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['home', LEDGER_ID] });
        });
        const keys = invalidateSpy.mock.calls.map((c) => c[0]?.queryKey);
        expect(keys).toContainEqual(['overview', LEDGER_ID]);
        expect(keys).toContainEqual(['budget-progress', LEDGER_ID]);
        expect(keys.filter((k) => k?.[0] === 'register')).toEqual([]);
    });

    it('does not offer Accept on a row that is already accepted', async () => {
        const txn = makeTxn({
            id: 't-done',
            headerId: '00000000-0000-0000-0000-0000000000e2',
            payee: 'already accepted',
            needsReview: false,
        });
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(txn)],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        const cell = await screen.findByText(txn.payee!);
        fireEvent.contextMenu(cell.closest('[role="row"]') as HTMLElement);
        // Anchored on the menu being open before asserting the absence — a bare
        // queryBy would pass on the frame before the menu renders at all.
        expect(await screen.findByRole('menuitem', { name: /^Edit$/ })).toBeInTheDocument();
        expect(screen.queryByRole('menuitem', { name: /^Accept$/ })).toBeNull();
    });

    it('hides Import and Sync on a retired brokerage', async () => {
        // Deactivating an account is a statement that nothing more should land
        // in it. The bank register has gated both on isActive; this one invited
        // new rows into an account the user had deliberately closed, and the
        // import would have succeeded.
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([
            { ...TEST_ACCOUNT, isActive: false, feedConnectionId: 'fc-1' },
        ]);
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [],
            cursorForOlder: null,
            cursorForNewer: null,
        });

        renderPage();

        // Anchor on the page having rendered before asserting absence.
        expect(await screen.findByText(/Brokerage/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^Import$/ })).toBeNull();
        expect(screen.queryByRole('button', { name: /sync account/i })).toBeNull();
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
            totalCount: 1,
            readOnlyCount: 0,
            sumOnAccount: -1000, currencyCode: 'USD',
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
        // or from any Security Detail row — must focus the anchored row, which
        // means this page reads ?focus=; otherwise it lands at the newest rows
        // with nothing highlighted.
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
        // A refresh has the anchor in the first render; a client-side
        // navigation delivers it afterwards. Reading it only on load would make
        // "Show other side" onto a brokerage land at the top, with only a
        // browser refresh focusing the row.
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
        // "whatever is at index 0" is what this guards against.
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

/**
 * The row list is the register's focus home (ADR-0023 §D, §E, §L), as on the
 * bank register: a control that takes itself away hands focus to the row list,
 * never to the page, and the register's keys work from there.
 */
describe('InvestmentRegisterPage — the row list is focus\'s home', () => {
    const FIRST = makeTxn({ id: 't-first', headerId: '00000000-0000-0000-0000-0000000000e1', payee: 'First row' });
    const SECOND = makeTxn({ id: 't-second', headerId: '00000000-0000-0000-0000-0000000000e2', payee: 'Second row' });

    beforeEach(() => {
        vi.restoreAllMocks();
        window.scrollTo = vi.fn();
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
        vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue([TEST_ACCOUNT]);
        vi.spyOn(apiModule, 'fetchBalancesForHeaders').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({
            count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: -1000, currencyCode: 'USD',
        });
        vi.spyOn(apiModule, 'fetchHeaderLegs').mockResolvedValue([FIRST]);
        vi.spyOn(apiModule, 'fetchInvestmentMergeCandidates').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
            entries: [entryOf(FIRST), entryOf(SECOND)],
            cursorForOlder: null,
            cursorForNewer: null,
        });
    });

    const rowList = () => screen.getByRole('grid', { name: 'Transactions' });
    const focusedRow = () => document.querySelector('[data-focused="true"]')?.getAttribute('data-headerid') ?? null;
    const editorClosed = () => expect(document.querySelector('[data-editing="true"]')).toBeNull();

    async function openEditor() {
        fireEvent.dblClick((await screen.findByText('First row')).closest('[role="row"]')!);
        await screen.findByRole('button', { name: /^Save/ });
    }

    it('is a named grid that takes focus without being a Tab stop', async () => {
        renderPage();
        await screen.findByText('First row');

        expect(rowList()).toHaveAttribute('tabindex', '-1');
    });

    it('takes focus when Clear selection takes itself away', async () => {
        renderPage();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${SECOND.id}` }));

        await user.click(await screen.findByRole('button', { name: 'Clear selection' }));

        await waitFor(() => expect(screen.queryByRole('button', { name: 'Clear selection' })).toBeNull());
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when Escape cancels an inline edit', async () => {
        renderPage();
        const user = userEvent.setup();
        await openEditor();

        await user.keyboard('{Escape}');

        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when Cancel closes an inline edit', async () => {
        renderPage();
        const user = userEvent.setup();
        await openEditor();

        await user.click(screen.getByRole('button', { name: /^Cancel/ }));

        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();
    });

    it('takes focus when Escape closes the new-row form', async () => {
        renderPage();
        const user = userEvent.setup();
        await screen.findByText('First row');
        await user.click(screen.getByRole('button', { name: /\+ New transaction/i }));
        await screen.findByRole('button', { name: /^Cancel$/i });

        await user.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('button', { name: /^Cancel$/i })).toBeNull());
        expect(rowList()).toHaveFocus();
    });

    it('works the register\'s keys with focus on it: the arrows, Enter and N', async () => {
        renderPage();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('checkbox', { name: `Select transaction ${SECOND.id}` }));
        await user.click(await screen.findByRole('button', { name: 'Clear selection' }));
        await waitFor(() => expect(rowList()).toHaveFocus());

        await user.keyboard('{ArrowDown}');
        expect(focusedRow()).toBe(FIRST.headerId);
        expect(rowList()).toHaveFocus();

        await user.keyboard('{Enter}');
        expect(await screen.findByRole('button', { name: /^Save/ })).toBeInTheDocument();
        await user.keyboard('{Escape}');
        await waitFor(editorClosed);
        expect(rowList()).toHaveFocus();

        await user.keyboard('n');
        expect(await screen.findByRole('button', { name: /^Cancel$/i })).toBeInTheDocument();
    });

    it('gives focus to + New transaction when Escape closes the new-row form in an empty account', async () => {
        // An empty register draws a placeholder instead of the list: focus goes
        // to the register's add control, never to the page.
        vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({ entries: [], cursorForOlder: null, cursorForNewer: null });
        renderPage();
        const user = userEvent.setup();
        const add = await screen.findByRole('button', { name: /\+ New transaction/i });
        await waitFor(() => expect(add).toBeEnabled());
        await user.click(add);
        await screen.findByRole('button', { name: /^Cancel$/i });

        await user.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('button', { name: /^Cancel$/i })).toBeNull());
        expect(add).toHaveFocus();
    });
});

/**
 * The two cells a read-only row's badge is judged by, found from the row's own
 * data: the payee cell is the one holding `anchor` (the row's memo), and the
 * slot is the cell right after the one showing the row's date.
 */
function badgeSlots(r: HTMLElement, anchor: string, dateText: string) {
    const cells = Array.from(r.children) as HTMLElement[];
    const payee = cells.find((c) => within(c).queryByText(anchor) !== null);
    const dateAt = cells.findIndex((c) => c.textContent?.startsWith(dateText) ?? false);
    const afterDate = dateAt >= 0 ? cells[dateAt + 1] : undefined;
    if (payee === undefined || afterDate === undefined) {
        throw new Error(`row has no cell holding "${anchor}" or no cell after "${dateText}"`);
    }
    expect(afterDate).not.toBe(payee);
    return { payee, afterDate };
}
