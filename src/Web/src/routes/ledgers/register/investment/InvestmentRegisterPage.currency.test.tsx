import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { ACCOUNT_NOT_LISTED } from '../shell/accountReadError';
import { InvestmentRegisterPage } from './InvestmentRegisterPage';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatCurrency, formatPrice } from '@/lib/money';
import type { AccountSummary, InvestmentRow, LedgerSummary } from '@/lib/types';

/**
 * An investment register in an account whose currency is not USD: the trade
 * price under the security, the fee and the settled amount under the Amount
 * are in the account's currency, as the Amount itself is. Mocked at `fetch`.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BROKERAGE = '00000000-0000-0000-0000-000000000200';
const HOLDINGS = '00000000-0000-0000-0000-000000000201';
const CATEGORY = '00000000-0000-0000-0000-000000000302';
const SECURITY = '00000000-0000-0000-0000-000000000400';
const CURRENCY = 'EUR';

const ledger: LedgerSummary = { id: LEDGER_ID, name: 'Ledger A', role: 'owner', currencyCode: 'USD' };

const base = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: null, currencyCode: CURRENCY, isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: BROKERAGE, name: 'Brokerage A', accountType: 'investment', holdingsAccountId: HOLDINGS },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const row = (over: Partial<InvestmentRow>): InvestmentRow => ({
    ...NO_NOTES,
    kind: 'investment', id: 'leg-1', accountId: BROKERAGE, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, origin: 'manual', isPending: false, investmentAction: 'buy',
    // A real account's rows are in its own currency, Amount and Balance alike (ADR-0114 D7).
    currencyCode: CURRENCY,
    balanceCurrencyCode: CURRENCY,
    externalId: null, checkNumber: null, counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0,
    counterpartyAccountId: HOLDINGS, counterpartyAccountName: 'Holdings', counterpartyAccountType: 'holding',
    headerId: '00000000-0000-0000-0000-000000000e01', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false,
    securityId: SECURITY, securityTicker: 'SECA', securityName: 'Security A', quantity: null, unitPrice: null,
    postingRole: 'security', ingestActionHint: null, ingestSecurityId: null, ingestShares: null,
    ingestUnitPrice: null, ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
    categoryAccountId: null, categoryAccountName: null, categoryAccountType: null, transferAccountId: null,
    transferAccountName: null, transferAccountType: null, feeAmount: null, feeCategoryId: null,
    feeCategoryName: null, providerRawPayload: null, headerAccountNetAmount: null, providerKey: null,
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 2,
    ...over,
});

/** A buy with a fee: the trade price under the security, the fee under the Amount. */
const BUY = row({
    id: 'leg-buy', headerId: '00000000-0000-0000-0000-000000000e01', payee: 'Payee One',
    amount: -126.5, quantity: 10, unitPrice: 12.5, feeAmount: 1.5, feeCategoryId: CATEGORY,
    feeCategoryName: 'Category A',
});
/** A reinvestment: a zero Amount, with the settled figure beneath it. */
const REINVEST = row({
    id: 'leg-reinvest', headerId: '00000000-0000-0000-0000-000000000e02', payee: 'Payee Two',
    investmentAction: 'dividend_reinvest', amount: 0, settledAmount: 30, balanceAfter: 90,
});

let stub: FetchStub;

function serve() {
    stub
        .on('GET', /^\/api\/ledgers$/, [ledger])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, {
            entries: [BUY, REINVEST].map((txn) => ({ kind: 'txn', txn, groupId: null, legs: null })),
            cursorForOlder: null, cursorForNewer: null,
        })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 2, cleared: 0, uncleared: 2, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE, accountName: 'Brokerage A', currencyCode: CURRENCY,
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('POST', /\/transactions\/balances$/, []);
}

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

const rowOf = async (payee: string) =>
    (await screen.findByText(payee)).closest('[role="row"]') as HTMLElement;

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('an investment register in a non-USD account', () => {
    it('draws the trade price and the fee in the account\'s currency', async () => {
        serve();
        renderRegister();
        const buy = await rowOf('Payee One');

        expect(within(buy).getByText(`10 sh @ ${formatPrice(12.5, CURRENCY)}`)).toBeInTheDocument();
        expect(within(buy).getByText(`fee ${formatCurrency(1.5, CURRENCY)}`)).toBeInTheDocument();
        expect(within(buy).queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('draws a zero-amount row\'s settled figure in the account\'s currency', async () => {
        serve();
        renderRegister();
        const reinvest = await rowOf('Payee Two');

        expect(within(reinvest).getByText(`settled ${formatCurrency(30, CURRENCY)}`)).toBeInTheDocument();
        expect(within(reinvest).queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('an investment register before its account is known', () => {
    /** Holds the account read until the returned function lets it answer. */
    function holdAccounts(): () => void {
        let open!: () => void;
        const gate = new Promise<void>((resolve) => { open = resolve; });
        const answer = globalThis.fetch;
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            if (new URL(raw, 'http://localhost').pathname.endsWith('/accounts')) await gate;
            return answer(input, init);
        });
        return open;
    }

    it('starts no new transaction, from the button or the N key, until the account is known', async () => {
        serve();
        const openAccounts = holdAccounts();
        renderRegister();
        await waitFor(() => expect(stub.callsTo('GET', /\/transactions$/)).toHaveLength(1));
        // Shown once the role is read, so the key is bound for an editor by then.
        await waitFor(() => expect(screen.getByRole('button', { name: /new transaction/i })).toBeDisabled());
        expect(screen.getByText('Loading…')).toBeInTheDocument();

        await userEvent.keyboard('n');
        openAccounts();

        // The list drawn is the moment an editor started earlier would open with it.
        expect(await rowOf('Payee One')).toBeInTheDocument();
        expect(screen.queryByLabelText('Action')).toBeNull();
        expect(screen.getByRole('button', { name: /new transaction/i })).toBeEnabled();
    });

    it('says the account could not be loaded when the account list answers without it', async () => {
        serve();
        stub.on('GET', /\/accounts$/, ACCOUNTS.filter((a) => a.id !== BROKERAGE));
        renderRegister();

        expect(await screen.findByRole('alert')).toHaveTextContent(ACCOUNT_NOT_LISTED);
        expect(screen.queryByText('Loading…')).toBeNull();
        expect(screen.queryByText('Payee One')).toBeNull();
    });
});
