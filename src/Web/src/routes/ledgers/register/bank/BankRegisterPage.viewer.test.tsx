import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatSignedAmount } from '@/lib/money';
import type { AccountSummary, BankRow, LedgerSummary } from '@/lib/types';

/**
 * A viewer reads a bank register and is offered no write: no new transaction,
 * no editor by double-click or Enter, no write item in the row menu (Show
 * other side and Show raw data stay), no bulk action, no import or sync, no
 * status cycling. Each check runs for an editor too, which proves the viewer's
 * absence is the role's and not a missing fixture. Every negative waits first
 * for the ledger's role to arrive (the breadcrumb names the ledger, read from
 * the same response) and for the row. Mocked at `fetch`.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECKING = '00000000-0000-0000-0000-000000000100';
const CATEGORY = '00000000-0000-0000-0000-000000000302';
const OTHER = '00000000-0000-0000-0000-000000000101';

const ledger = (role: string): LedgerSummary => ({ id: LEDGER_ID, name: 'Ledger A', role, currencyCode: 'USD' });

const base = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: null, currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Account A', accountType: 'bank', feedConnectionId: 'feed-1' },
    { ...base, id: OTHER, name: 'Account B', accountType: 'bank' },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const ROW: BankRow = {
    ...NO_NOTES,
    kind: 'bank', id: 'leg-1', accountId: CHECKING, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, origin: 'simplefin', isPending: false, externalId: null, checkNumber: null,
    currencyCode: 'USD',
    balanceCurrencyCode: 'USD',
    counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0, counterpartyAccountId: CATEGORY,
    counterpartyAccountName: 'Category A', counterpartyAccountType: 'category',
    headerId: '00000000-0000-0000-0000-000000000e01', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: true, providerRawPayload: null, headerAccountNetAmount: null, providerKey: 'simplefin',
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 1,
};

let stub: FetchStub;

function serve(role: string) {
    stub
        .on('GET', /^\/api\/ledgers$/, [ledger(role)])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries: [{ kind: 'txn', txn: ROW, groupId: null, legs: null }], cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 1, cleared: 0, uncleared: 1, reconciling: 0, scheduled: 0, needsReview: 1, hidden: 0 })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', /\/transactions\/balances$/, [])
        .on('POST', /\/transactions\/selection-summary$/, { count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: -10 });
}

/** The selection's Σ as the bar draws it, from the summary served above. */
const SUM = `Σ ${formatSignedAmount(-10, 'USD')}`;

function renderRegister() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${CHECKING}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** The role has arrived (the breadcrumb names the ledger from the same read) and the row is drawn. */
async function settled(): Promise<HTMLElement> {
    await screen.findByRole('link', { name: 'Ledger A' });
    return (await screen.findByText('Payee One')).closest('[role="row"]') as HTMLElement;
}

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe.each([
    { role: 'viewer', writes: false },
    { role: 'editor', writes: true },
])('a bank register for an $role', ({ role, writes }) => {
    it(writes ? 'offers New transaction, Import and Sync' : 'offers no New transaction, Import or Sync', async () => {
        serve(role);
        renderRegister();
        await settled();

        for (const name of [/new transaction/i, /^import$/i, /sync account/i]) {
            if (writes) expect(screen.getByRole('button', { name })).toBeInTheDocument();
            else expect(screen.queryByRole('button', { name })).toBeNull();
        }
        // The N shortcut opens the same new-transaction editor.
        await userEvent.keyboard('n');
        if (writes) expect(await screen.findByLabelText('Payee')).toBeInTheDocument();
        else expect(screen.queryByLabelText('Payee')).toBeNull();
    });

    it(writes ? 'opens the editor on double-click and on Enter' : 'opens no editor on double-click or Enter', async () => {
        serve(role);
        renderRegister();
        const row = await settled();

        fireEvent.doubleClick(row);
        if (writes) {
            expect(await screen.findByLabelText('Payee')).toBeInTheDocument();
            await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
            await waitFor(() => expect(screen.queryByLabelText('Payee')).toBeNull());
        } else {
            expect(screen.queryByLabelText('Payee')).toBeNull();
        }

        // Focus the row the way a pointer user would, then use the keyboard.
        const user = userEvent.setup();
        await user.click((await screen.findByText('Payee One')).closest('[role="row"]')!);
        await user.keyboard('{Enter}');
        if (writes) expect(await screen.findByLabelText('Payee')).toBeInTheDocument();
        else expect(screen.queryByLabelText('Payee')).toBeNull();
    });

    it(writes ? 'lists the write items in the row menu' : 'lists only Show other side and Show raw data in the row menu', async () => {
        serve(role);
        renderRegister();
        const row = await settled();

        fireEvent.contextMenu(row);
        const menu = await screen.findByRole('menu');
        const items = within(menu).getAllByRole('menuitem').map((i) => i.textContent?.trim());
        expect(items).toEqual(expect.arrayContaining(['Show other side', 'Show raw data']));
        for (const write of ['Accept', 'Edit', 'Duplicate', 'Create reminder', 'Delete']) {
            if (writes) expect(items.some((i) => i?.startsWith(write))).toBe(true);
            else expect(items.some((i) => i?.startsWith(write))).toBe(false);
        }
    });

    it(writes ? 'offers the bulk actions on a selection' : 'shows a selection\'s count and sum, with no bulk action', async () => {
        serve(role);
        renderRegister();
        const row = await settled();

        fireEvent.click(within(row).getByRole('checkbox'));
        // The summed figure is the server's, read through the client's own route.
        const bar = await screen.findByRole('region', { name: 'Bulk actions' });
        expect(await within(bar).findByText(SUM)).toBeInTheDocument();
        expect(stub.callsTo('POST', /\/transactions\/selection-summary$/)).not.toHaveLength(0);
        for (const name of ['✓ Cleared', '· Reconciling', 'Delete', 'Move to account…']) {
            if (writes) expect(screen.getByRole('button', { name })).toBeInTheDocument();
            else expect(screen.queryByRole('button', { name })).toBeNull();
        }
        expect(stub.unrouted).toEqual([]);
    });

    it('names a failed selection summary in place of the Σ', async () => {
        serve(role);
        stub.on('POST', /\/transactions\/selection-summary$/, problem(500, 'The selection could not be summed.'));
        renderRegister();
        const row = await settled();

        fireEvent.click(within(row).getByRole('checkbox'));
        const bar = await screen.findByRole('region', { name: 'Bulk actions' });
        expect(await within(bar).findByRole('alert')).toHaveTextContent('The selection could not be summed.');
        expect(within(bar).getByText('1 selected')).toBeInTheDocument();
        expect(within(bar).queryByText(/Σ/)).toBeNull();
    });

    it(writes ? 'cycles the reconciliation status from the badge' : 'draws the status badge without the cycle control', async () => {
        serve(role);
        renderRegister();
        const row = await settled();

        const cycle = within(row).queryByTitle('Cycle reconciliation status');
        if (writes) expect(cycle).not.toBeNull();
        else expect(cycle).toBeNull();
    });
});
