import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
import { formatSignedAmount } from '@/lib/money';
import type { AccountSummary, BankRow, LedgerSummary, RegisterEntry } from '@/lib/types';

import { ACCOUNT_NOT_LISTED } from '../shell/accountReadError';
import { BankRegisterPage } from './BankRegisterPage';

/**
 * The register's figures are in its account's currency, which only the account
 * list says. Until that list answers, the list keeps its loading state rather than
 * print its rows in a currency they are not in. Mocked at `fetch`, with the account
 * read held until the test lets it answer.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_A = '00000000-0000-0000-0000-000000000100';
const CATEGORY_A = '00000000-0000-0000-0000-000000000302';

const LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Ledger A', role: 'owner', currencyCode: 'USD' };

const base = {
    ledgerId: LEDGER_ID, parentId: null, currencyCode: 'EUR', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType' | 'categoryKind'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: ACCOUNT_A, name: 'Account A', accountType: 'bank', categoryKind: null },
    { ...base, id: CATEGORY_A, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const ROW: BankRow = {
    ...NO_NOTES,
    kind: 'bank', id: 'leg-a', headerId: 'hdr-a', accountId: ACCOUNT_A, payee: 'Payee A',
    amount: -12.5, postedAt: '2026-10-02T12:00:00Z', transactedAt: null, status: 'uncleared',
    isHidden: false, currencyCode: 'EUR', balanceAfter: 487.5, balanceCurrencyCode: 'EUR', origin: 'manual', isPending: false, externalId: null,
    checkNumber: null, counterpartyId: 'cp-a', txnGroupId: null, legIndex: 0,
    counterpartyAccountId: CATEGORY_A, counterpartyAccountName: 'Category A',
    counterpartyAccountType: 'category', clearedAt: null, clearedByUserId: null,
    createdAt: '2026-10-02T12:00:00Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: false, providerRawPayload: null, headerAccountNetAmount: null, providerKey: null,
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 1,
};
const ENTRY: RegisterEntry = { kind: 'txn', txn: ROW, groupId: null, legs: null };

let stub: FetchStub;
let openAccounts: () => void;

beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries: [ENTRY], cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 1, cleared: 0, uncleared: 1, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('POST', /\/selection\/summary$/, { count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0 });
    // The account read waits until the test opens it; every other read answers.
    let open!: () => void;
    const gate = new Promise<void>((resolve) => { open = resolve; });
    openAccounts = open;
    const answer = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        if (new URL(raw, 'http://localhost').pathname.endsWith('/accounts')) await gate;
        return answer(input, init);
    });
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderPage() {
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
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_A}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('the bank register before its account is known', () => {
    it('keeps its loading state with the rows in hand, then prints them in the account\'s currency', async () => {
        renderPage();

        // The rows have been read and the account has not: the list still says
        // it is loading once the read is in and the page has settled on it.
        await waitFor(() => expect(stub.callsTo('GET', /\/transactions$/)).toHaveLength(1));
        await act(async () => { await new Promise((resolve) => setTimeout(resolve, 50)); });
        expect(stub.callsTo('GET', /\/accounts$/)).toHaveLength(0);
        expect(screen.getByText('Loading…')).toBeInTheDocument();
        expect(screen.queryByText('Payee A')).toBeNull();
        expect(screen.queryByText(/\$/)).toBeNull();
        // Nothing can be started that would print a figure either.
        expect(screen.getByRole('button', { name: /new transaction/i })).toBeDisabled();

        openAccounts();

        expect(await screen.findByText('Payee A')).toBeInTheDocument();
        expect(screen.getByText(formatSignedAmount(-12.5, 'EUR'))).toBeInTheDocument();
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('starts no new transaction from the N key until the account is known', async () => {
        renderPage();
        await waitFor(() => expect(stub.callsTo('GET', /\/transactions$/)).toHaveLength(1));
        // Shown once the role is read, so the key is bound for an owner by then.
        await waitFor(() => expect(screen.getByRole('button', { name: /new transaction/i })).toBeDisabled());

        await userEvent.keyboard('n');
        openAccounts();

        // The list drawn is the moment an editor started earlier would open with it.
        expect(await screen.findByText('Payee A')).toBeInTheDocument();
        expect(screen.queryByLabelText('Payee')).toBeNull();
        expect(screen.getByRole('button', { name: /new transaction/i })).toBeEnabled();
    });

    it('says the account could not be loaded when the account list answers without it', async () => {
        stub.on('GET', /\/accounts$/, ACCOUNTS.filter((a) => a.id !== ACCOUNT_A));
        openAccounts();
        renderPage();

        expect(await screen.findByRole('alert')).toHaveTextContent(ACCOUNT_NOT_LISTED);
        expect(screen.queryByText('Loading…')).toBeNull();
        expect(screen.queryByText('Payee A')).toBeNull();
    });
});
