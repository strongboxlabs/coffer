import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatSignedAmount } from '@/lib/money';
import type { AccountSummary, InvestmentRow, LedgerSummary } from '@/lib/types';

/**
 * A viewer reads an investment register and is offered no write, as in the
 * bank register: no new transaction, no editor by double-click or Enter, no
 * write item in the row menu (Show other side and Show raw data stay), no bulk
 * action, no import or sync, no status cycling. Each check runs for an editor
 * too. Every negative waits first for the ledger's role (the breadcrumb names
 * the ledger from the same read) and the row. Mocked at `fetch`.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BROKERAGE = '00000000-0000-0000-0000-000000000200';
const HOLDINGS = '00000000-0000-0000-0000-000000000201';
const CATEGORY = '00000000-0000-0000-0000-000000000302';
const HEADER = '00000000-0000-0000-0000-000000000e01';

const ledger = (role: string): LedgerSummary => ({ id: LEDGER_ID, name: 'Ledger A', role, currencyCode: 'USD' });

const base = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: null, currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: BROKERAGE, name: 'Brokerage A', accountType: 'investment', holdingsAccountId: HOLDINGS, feedConnectionId: 'feed-1' },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const row = (over: Partial<InvestmentRow> = {}): InvestmentRow => ({
    ...NO_NOTES,
    kind: 'investment', id: 'leg-1', accountId: BROKERAGE, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, origin: 'simplefin', isPending: false, investmentAction: 'misc',
    currencyCode: 'USD',
    balanceCurrencyCode: 'USD',
    externalId: null, checkNumber: null, counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0,
    counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A', counterpartyAccountType: 'category',
    headerId: HEADER, clearedAt: null, clearedByUserId: null, createdAt: '2026-10-02T12:00:00Z',
    onlineMatchFitid: null, onlineMatchFiId: null, needsReview: true, securityId: null,
    securityTicker: null, securityName: null, quantity: null, unitPrice: null, postingRole: 'income',
    ingestActionHint: null, ingestSecurityId: null, ingestShares: null, ingestUnitPrice: null,
    ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null, categoryAccountId: CATEGORY,
    categoryAccountName: 'Category A', categoryAccountType: 'category', transferAccountId: null,
    transferAccountName: null, transferAccountType: null, feeAmount: null, feeCategoryId: null,
    feeCategoryName: null, providerRawPayload: null, headerAccountNetAmount: null, providerKey: 'simplefin',
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 2,
    ...over,
});

let stub: FetchStub;

function serve(role: string) {
    const shown = row();
    stub
        .on('GET', /^\/api\/ledgers$/, [ledger(role)])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries: [{ kind: 'txn', txn: shown, groupId: null, legs: null }], cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 1, cleared: 0, uncleared: 1, reconciling: 0, scheduled: 0, needsReview: 1, hidden: 0 })
        .on('GET', /\/transactions\/[^/]+\/legs$/, [shown, row({ id: 'leg-2', accountId: CATEGORY, amount: 10, legIndex: 1 })])
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE, accountName: 'Brokerage A', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
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
        component: InvestmentRegisterPage,
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${BROKERAGE}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

async function settled(): Promise<HTMLElement> {
    await screen.findByRole('link', { name: 'Ledger A' });
    return (await screen.findByText('Payee One')).closest('[role="row"]') as HTMLElement;
}

/** The edit's leg read, which an editor waits for before it mounts. */
const LEGS = /\/transactions\/[^/]+\/legs$/;

/** Let every request a gesture started return and render, so a negative is not checked early. */
async function flush() {
    for (let i = 0; i < 3; i++) {
        await act(async () => {
            await new Promise((resolve) => setTimeout(resolve, 0));
        });
    }
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
])('an investment register for an $role', ({ role, writes }) => {
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
        if (writes) {
            expect(await screen.findByLabelText('Action')).toBeInTheDocument();
        } else {
            await flush();
            expect(screen.queryByLabelText('Action')).toBeNull();
        }
    });

    it(writes ? 'opens the editor on double-click and on Enter' : 'opens no editor on double-click or Enter', async () => {
        serve(role);
        renderRegister();
        const shown = await settled();

        // An edit mounts only after its leg read returns, so a negative checked
        // at once would pass before the editor could appear. The viewer's
        // checks wait out that read and then assert it was never made.
        const user = userEvent.setup();
        fireEvent.doubleClick(shown);
        if (writes) {
            expect(await screen.findByLabelText('Action')).toBeInTheDocument();
            await user.click(screen.getByRole('button', { name: 'Cancel' }));
            await waitFor(() => expect(screen.queryByLabelText('Action')).toBeNull());
        } else {
            await flush();
            expect(stub.callsTo('GET', LEGS)).toEqual([]);
            expect(screen.queryByLabelText('Action')).toBeNull();
        }

        await user.click((await screen.findByText('Payee One')).closest('[role="row"]')!);
        await user.keyboard('{Enter}');
        if (writes) {
            expect(await screen.findByLabelText('Action')).toBeInTheDocument();
            expect(stub.callsTo('GET', LEGS)).not.toEqual([]);
        } else {
            await flush();
            expect(stub.callsTo('GET', LEGS)).toEqual([]);
            expect(screen.queryByLabelText('Action')).toBeNull();
        }
    });

    it(writes ? 'lists the write items in the row menu' : 'lists only Show other side and Show raw data in the row menu', async () => {
        serve(role);
        renderRegister();
        const shown = await settled();

        fireEvent.contextMenu(shown);
        const menu = await screen.findByRole('menu');
        const items = within(menu).getAllByRole('menuitem').map((i) => i.textContent?.trim());
        expect(items).toEqual(expect.arrayContaining(['Show other side', 'Show raw data']));
        for (const write of ['Accept', 'Edit', 'Duplicate', 'Delete']) {
            if (writes) expect(items.some((i) => i?.startsWith(write))).toBe(true);
            else expect(items.some((i) => i?.startsWith(write))).toBe(false);
        }
    });

    it(writes ? 'offers the bulk actions on a selection' : 'shows a selection\'s count and sum, with no bulk action', async () => {
        serve(role);
        renderRegister();
        const shown = await settled();

        fireEvent.click(within(shown).getByRole('checkbox'));
        // The summed figure is the server's, read through the client's own route.
        const bar = await screen.findByRole('region', { name: 'Bulk actions' });
        expect(await within(bar).findByText(SUM)).toBeInTheDocument();
        expect(stub.callsTo('POST', /\/transactions\/selection-summary$/)).not.toHaveLength(0);
        for (const name of ['✓ Cleared', '· Reconciling', 'Delete']) {
            if (writes) expect(screen.getByRole('button', { name })).toBeInTheDocument();
            else expect(screen.queryByRole('button', { name })).toBeNull();
        }
        expect(stub.unrouted).toEqual([]);
    });

    it('names a failed selection summary in place of the Σ', async () => {
        serve(role);
        stub.on('POST', /\/transactions\/selection-summary$/, problem(500, 'The selection could not be summed.'));
        renderRegister();
        const shown = await settled();

        fireEvent.click(within(shown).getByRole('checkbox'));
        const bar = await screen.findByRole('region', { name: 'Bulk actions' });
        expect(await within(bar).findByRole('alert')).toHaveTextContent('The selection could not be summed.');
        expect(within(bar).getByText('1 selected')).toBeInTheDocument();
        expect(within(bar).queryByText(/Σ/)).toBeNull();
    });

    it(writes ? 'cycles the reconciliation status from the badge' : 'draws the status badge without the cycle control', async () => {
        serve(role);
        renderRegister();
        const shown = await settled();

        const cycle = within(shown).queryByTitle('Cycle reconciliation status');
        if (writes) expect(cycle).not.toBeNull();
        else expect(cycle).toBeNull();
    });
});
