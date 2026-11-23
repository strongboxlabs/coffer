import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountSummary, BankRow, InvestmentRow, LedgerSummary } from '@/lib/types';
import { createAppRouter } from '@/router';

import { BankRegisterPage } from '../bank/BankRegisterPage';
import { InvestmentRegisterPage } from '../investment/InvestmentRegisterPage';

/**
 * A register opened by its `?status=` arrival instruction. `hidden` opens the Hidden view
 * (ADR-0072 D1), the one view that shows a hidden row, so a link to a hidden row (the
 * consistency check's advisory, ADR-0114 D15) lands on it: the register fetches the
 * hidden rows, anchored on the row. Mocked at `fetch`; every name and figure is invented.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BANK = '00000000-0000-0000-0000-000000000100';
const BROKERAGE = '00000000-0000-0000-0000-000000000200';
const HOLDINGS = '00000000-0000-0000-0000-000000000201';
const CATEGORY = '00000000-0000-0000-0000-000000000302';
const HIDDEN_HEADER = '00000000-0000-0000-0000-000000000e01';

const LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Ledger A', role: 'owner', currencyCode: 'USD' };

const base = {
    ledgerId: LEDGER_ID, parentId: null, categoryKind: null, currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: BANK, name: 'Account A', accountType: 'bank' },
    { ...base, id: BROKERAGE, name: 'Brokerage A', accountType: 'investment', holdingsAccountId: HOLDINGS },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const bankRow = (over: Partial<BankRow>): BankRow => ({
    ...NO_NOTES,
    kind: 'bank', id: 'leg-1', accountId: BANK, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, currencyCode: 'USD', balanceCurrencyCode: 'USD', origin: 'manual', isPending: false,
    externalId: null, checkNumber: null, counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0,
    counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A', counterpartyAccountType: 'category',
    headerId: '00000000-0000-0000-0000-000000000e02', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false,
    providerRawPayload: null, headerAccountNetAmount: null, providerKey: null, isMergeWinner: false,
    importSource: null, derivedAction: null, accountPostingsOnHeader: 1, headerTotalPostings: 1,
    ...over,
});

const investmentRow = (over: Partial<InvestmentRow>): InvestmentRow => ({
    ...NO_NOTES,
    kind: 'investment', id: 'leg-1', accountId: BROKERAGE, payee: 'Payee One', amount: -10,
    postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared', isHidden: false,
    balanceAfter: 90, origin: 'manual', isPending: false, investmentAction: 'deposit',
    currencyCode: 'USD', balanceCurrencyCode: 'USD',
    externalId: null, checkNumber: null, counterpartyId: 'cp-1', txnGroupId: null, legIndex: 0,
    counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A', counterpartyAccountType: 'category',
    headerId: '00000000-0000-0000-0000-000000000e02', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false,
    securityId: null, securityTicker: null, securityName: null, quantity: null, unitPrice: null,
    postingRole: null, ingestActionHint: null, ingestSecurityId: null, ingestShares: null,
    ingestUnitPrice: null, ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
    categoryAccountId: null, categoryAccountName: null, categoryAccountType: null, transferAccountId: null,
    transferAccountName: null, transferAccountType: null, feeAmount: null, feeCategoryId: null,
    feeCategoryName: null, providerRawPayload: null, headerAccountNetAmount: null, providerKey: null,
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 2,
    ...over,
});

/** The register's rows: the hidden one in the Hidden view, a visible one otherwise. */
function serve(hiddenRow: BankRow | InvestmentRow, visibleRow: BankRow | InvestmentRow) {
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, (call: { search: URLSearchParams }) => ({
            entries: [call.search.get('hidden') === 'true' ? hiddenRow : visibleRow]
                .map((txn) => ({ kind: 'txn', txn, groupId: null, legs: null })),
            cursorForOlder: null,
            cursorForNewer: null,
        }))
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, {
            all: 1, cleared: 0, uncleared: 1, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 1,
        })
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE, accountName: 'Brokerage A', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('POST', /\/transactions\/balances$/, [])
        .on('POST', /\/transactions\/selection-summary$/, {
            count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD',
        });
}

function renderAt(page: typeof BankRegisterPage | typeof InvestmentRegisterPage, url: string) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: page,
    });
    const elsewhere = ['/ledgers/$ledgerId', '/ledgers/$ledgerId/settings', '/ledgers/$ledgerId/categories']
        .map((path) => createRoute({ getParentRoute: () => root, path, component: () => <main>elsewhere</main> }));
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, ...elsewhere]),
        history: createMemoryHistory({ initialEntries: [url] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** The register's first page read: the Hidden view's, anchored on the hidden row. */
function expectHiddenFirstRead() {
    const first = stub.callsTo('GET', /\/transactions$/)[0]!;
    expect([first.search.get('hidden'), first.search.get('starting_at')]).toEqual(['true', HIDDEN_HEADER]);
}

let stub: FetchStub;

beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('the register route\'s status arrival', () => {
    const validate = (search: Record<string, unknown>) => {
        const router = createAppRouter(new QueryClient());
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const route = (router.routesByPath as any)['/ledgers/$ledgerId/accounts/$accountId'];
        return route.options.validateSearch(search) as { focus?: string; status?: string };
    };

    it('keeps the Hidden and Needs review views and drops any other value', () => {
        expect(validate({ focus: 'header-a', status: 'hidden' })).toEqual({ focus: 'header-a', status: 'hidden' });
        expect(validate({ status: 'needs_review' })).toEqual({ status: 'needs_review' });
        expect(validate({ status: 'cleared' })).toEqual({});
    });
});

describe('a bank register opened at a hidden row', () => {
    it('opens the Hidden view, anchored on the row, and shows it', async () => {
        serve(
            bankRow({ id: 'leg-h', headerId: HIDDEN_HEADER, payee: 'Payee Hidden', isHidden: true, externalId: 'feed-a' }),
            bankRow({ id: 'leg-v', payee: 'Payee Visible' }),
        );
        renderAt(BankRegisterPage, `/ledgers/${LEDGER_ID}/accounts/${BANK}?focus=${HIDDEN_HEADER}&status=hidden`);

        expect(await screen.findByText('Payee Hidden')).toBeInTheDocument();
        expectHiddenFirstRead();
        expect(screen.queryByText('Payee Visible')).toBeNull();
    });
});

describe('an investment register opened at a hidden row', () => {
    it('opens the Hidden view, anchored on the row, and shows it', async () => {
        serve(
            investmentRow({ id: 'leg-h', headerId: HIDDEN_HEADER, payee: 'Payee Hidden', isHidden: true, externalId: 'feed-a' }),
            investmentRow({ id: 'leg-v', payee: 'Payee Visible' }),
        );
        renderAt(InvestmentRegisterPage, `/ledgers/${LEDGER_ID}/accounts/${BROKERAGE}?focus=${HIDDEN_HEADER}&status=hidden`);

        expect(await screen.findByText('Payee Hidden')).toBeInTheDocument();
        expectHiddenFirstRead();
        expect(screen.queryByText('Payee Visible')).toBeNull();
    });
});
