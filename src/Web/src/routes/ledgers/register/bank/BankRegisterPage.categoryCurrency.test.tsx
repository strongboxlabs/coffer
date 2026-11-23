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

import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatCurrency, formatSignedAmount } from '@/lib/money';
import type { AccountSummary, BankRow, LedgerSummary, RegisterEntry } from '@/lib/types';

import { BankRegisterPage } from './BankRegisterPage';

/**
 * A category's register when the accounts that pay into it are in different currencies
 * (ADR-0114 D7, D9). A category holds no money of its own, so each row's Amount is in the
 * currency of the real account on the other side, and the running Balance combines them,
 * converted into the reporting currency; a Balance that needs a missing rate reads "no
 * rate". Mocked at `fetch`; every name and figure is invented.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_USD = '00000000-0000-0000-0000-000000000100';
const ACCOUNT_CAD = '00000000-0000-0000-0000-000000000101';
const CATEGORY_A = '00000000-0000-0000-0000-000000000302';

const LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Ledger A', role: 'owner', currencyCode: 'USD' };

const base = {
    ledgerId: LEDGER_ID, parentId: null, currencyCode: 'USD', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType' | 'categoryKind'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: ACCOUNT_USD, name: 'Account A', accountType: 'bank', categoryKind: null },
    { ...base, id: ACCOUNT_CAD, name: 'Account B', accountType: 'bank', categoryKind: null, currencyCode: 'CAD' },
    { ...base, id: CATEGORY_A, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const MISSING = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-10-08', reason: 'rate_unavailable' as const };

const row = (over: Partial<BankRow> & Pick<BankRow, 'id' | 'headerId' | 'payee'>): BankRow => ({
    ...NO_NOTES,
    kind: 'bank', accountId: CATEGORY_A, amount: 10, postedAt: '2026-10-02T12:00:00Z', transactedAt: null,
    status: 'uncleared', isHidden: false, currencyCode: 'USD', balanceAfter: 10, balanceCurrencyCode: 'USD',
    origin: 'manual', isPending: false, externalId: null, checkNumber: null, counterpartyId: `cp-${over.id}`,
    txnGroupId: null, legIndex: 0, counterpartyAccountId: ACCOUNT_USD, counterpartyAccountName: 'Account A',
    counterpartyAccountType: 'bank', clearedAt: null, clearedByUserId: null, createdAt: '2026-10-02T12:00:00Z',
    onlineMatchFitid: null, onlineMatchFiId: null, needsReview: false, providerRawPayload: null,
    headerAccountNetAmount: null, providerKey: null, isMergeWinner: false, importSource: null, derivedAction: null,
    accountPostingsOnHeader: 1, headerTotalPostings: 2,
    ...over,
});

/** Newest first: a CAD line whose day has no rate, a CAD line converted, a USD line. */
const ROWS: BankRow[] = [
    row({
        id: 'leg-c', headerId: 'hdr-c', payee: 'Payee C', amount: 30, postedAt: '2026-10-08T12:00:00Z',
        currencyCode: 'CAD', counterpartyAccountId: ACCOUNT_CAD, counterpartyAccountName: 'Account B',
        balanceAfter: null, balanceRateUnavailable: [MISSING],
    }),
    row({
        id: 'leg-b', headerId: 'hdr-b', payee: 'Payee B', amount: 20, postedAt: '2026-10-05T12:00:00Z',
        currencyCode: 'CAD', counterpartyAccountId: ACCOUNT_CAD, counterpartyAccountName: 'Account B',
        balanceAfter: 24.4,
    }),
    row({ id: 'leg-a', headerId: 'hdr-a', payee: 'Payee A', amount: 10, balanceAfter: 10 }),
];
const ENTRIES: RegisterEntry[] = ROWS.map((txn) => ({ kind: 'txn', txn, groupId: null, legs: null }));

let stub: FetchStub;

const SUMMARY = /\/transactions\/selection-summary$/;

beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries: ENTRIES, cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 3, cleared: 0, uncleared: 3, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('POST', SUMMARY, { count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderCategory() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const elsewhere = ['/ledgers/$ledgerId', '/ledgers/$ledgerId/settings'].map((path) =>
        createRoute({ getParentRoute: () => root, path, component: () => <main>elsewhere</main> }));
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, ...elsewhere]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${CATEGORY_A}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** A row of the register, by its payee. */
const rowOf = async (payee: string) => (await screen.findByText(payee)).closest('[role="row"]') as HTMLElement;

describe('a category register over accounts in different currencies', () => {
    it('prints each Amount in the currency of the account that paid it', async () => {
        renderCategory();

        expect(await rowOf('Payee B')).toHaveTextContent(formatSignedAmount(20, 'CAD'));
        expect(await rowOf('Payee A')).toHaveTextContent(formatSignedAmount(10, 'USD'));
        expect(await rowOf('Payee A')).not.toHaveTextContent('CA$');
    });

    it('prints each Balance in the reporting currency it combines into', async () => {
        renderCategory();

        const converted = await rowOf('Payee B');
        expect(converted).toHaveTextContent(formatCurrency(24.4, 'USD'));
        expect(converted).not.toHaveTextContent(formatCurrency(24.4, 'CAD'));
    });

    it('reads a Balance that needs a missing rate "no rate", naming the pair and the day', async () => {
        renderCategory();

        const missing = await rowOf('Payee C');
        expect(within(missing).getByText('no rate')).toHaveAttribute('title', 'No CAD to USD rate for Oct 8, 2026.');
        // Its Amount still prints: it needs no rate.
        expect(missing).toHaveTextContent(formatSignedAmount(30, 'CAD'));
        // A row whose Balance has its rate says nothing of one.
        expect(within(await rowOf('Payee B')).queryByText('no rate')).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('the editor on a category register over accounts in different currencies', () => {
    // A row opens in the currency of the account that paid it, at that currency's
    // places, not in the register's (ADR-0114 D14, D15).
    const ACCOUNT_JPY = '00000000-0000-0000-0000-000000000102';

    it('seeds a whole-unit row at whole units and steps in them', async () => {
        const paid = row({
            id: 'leg-y', headerId: 'hdr-y', payee: 'Payee Y', amount: 40,
            currencyCode: 'JPY', counterpartyAccountId: ACCOUNT_JPY, counterpartyAccountName: 'Account Y',
        });
        stub
            .on('GET', /\/accounts$/, [
                ...ACCOUNTS,
                { ...base, id: ACCOUNT_JPY, name: 'Account Y', accountType: 'bank', categoryKind: null, currencyCode: 'JPY' },
            ])
            .on('GET', /\/transactions$/, {
                entries: [{ kind: 'txn', txn: paid, groupId: null, legs: null }],
                cursorForOlder: null,
                cursorForNewer: null,
            });
        renderCategory();
        const user = userEvent.setup();
        await user.click(await rowOf('Payee Y'));
        await user.keyboard('{Enter}');

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' }) as HTMLInputElement;
        // The register is the category's, in the ledger's two-place code: seeded in
        // it, the figure would read with two zero places.
        expect(amount.value).toBe('40');
        expect(amount).toHaveAttribute('step', '1');
    });
});

describe('a split a category owns, paid from accounts in different currencies', () => {
    // Each line is in its paying account's currency, so the split has no single total
    // (ADR-0114 D7): no figure adds currencies. The collapsed row and the editor's
    // totals show the mixed marker, never the server's sum in the first line's
    // currency. A split whose lines share a currency keeps its total, in that one.
    const splitLeg = (over: Partial<BankRow> & Pick<BankRow, 'id' | 'legIndex'>): BankRow => row({
        headerId: 'hdr-s', payee: 'Payee S', txnGroupId: 'grp-s', notesKind: 'splitSide',
        headerAccountNetAmount: 30, accountPostingsOnHeader: 2, headerTotalPostings: 4,
        ...over,
    });
    const MIXED_LEGS: BankRow[] = [
        splitLeg({ id: 'leg-s1', legIndex: 0, amount: 10 }),
        splitLeg({
            id: 'leg-s2', legIndex: 1, amount: 20,
            currencyCode: 'CAD', counterpartyAccountId: ACCOUNT_CAD, counterpartyAccountName: 'Account B',
        }),
    ];
    const ONE_CURRENCY_LEGS: BankRow[] = MIXED_LEGS.map((leg) => ({
        ...leg, currencyCode: 'CAD', counterpartyAccountId: ACCOUNT_CAD, counterpartyAccountName: 'Account B',
    }));
    const withSplit = (legs: BankRow[]) => {
        stub.on('GET', /\/transactions$/, {
            entries: [{ kind: 'group', txn: null, groupId: 'grp-s', legs }],
            cursorForOlder: null,
            cursorForNewer: null,
        });
        renderCategory();
    };
    const openEditor = async () => {
        const user = userEvent.setup();
        await user.click(await rowOf('Payee S'));
        await user.keyboard('{Enter}');
        await screen.findByLabelText('Posting 2 amount');
    };
    /** The hover over a sum, which the editor's two totals carry when they are one. */
    const SUM_TITLE = 'Sum of postings (saved as the transaction total)';
    /** The editor's two totals: the header's Amount and the split footer's. */
    const editorTotals = () => screen.getAllByTitle(SUM_TITLE);
    /** The marker's own hover (ADR-0114 D7, D14), as approved. */
    const MIXED_TITLE = 'These lines are paid from accounts in more than one currency, so there is no single total.';
    const SUMS = [formatSignedAmount(30, 'USD'), formatCurrency(30, 'USD'), formatCurrency(30, 'CAD')];

    it('shows the mixed marker on the collapsed row, not a sum, saying why on hover', async () => {
        withSplit(MIXED_LEGS);

        const parent = await rowOf('Payee S');
        const marker = within(parent).getByText('mixed');
        expect(marker).toHaveClass('text-text-subtle');
        expect(marker).toHaveAttribute('title', MIXED_TITLE);
        for (const sum of SUMS) expect(parent).not.toHaveTextContent(sum);
    });

    it('shows the mixed marker for both of the editor\'s totals, not a sum, under its own hover', async () => {
        withSplit(MIXED_LEGS);
        await openEditor();

        // The header's Amount and the split footer's: each reads the marker, which
        // says why on hover, and neither claims a sum.
        const markers = screen.getAllByTitle(MIXED_TITLE);
        expect(markers).toHaveLength(2);
        for (const marker of markers) {
            expect(marker).toHaveTextContent(/^mixed$/);
            expect(marker).toHaveClass('text-text-subtle');
            expect(marker.parentElement).toHaveTextContent(/^mixed$/);
            expect(marker.parentElement).not.toHaveAttribute('title');
        }
        expect(screen.queryAllByTitle(SUM_TITLE)).toHaveLength(0);
    });

    it('keeps the total of a split whose lines share a currency, in that currency', async () => {
        withSplit(ONE_CURRENCY_LEGS);

        expect(await rowOf('Payee S')).toHaveTextContent(formatSignedAmount(30, 'CAD'));
        await openEditor();
        for (const total of editorTotals()) expect(total).toHaveTextContent(formatCurrency(30, 'CAD'));
        expect(screen.queryByText('mixed')).toBeNull();
    });
});

describe('the amount filter on a category register over accounts in different currencies', () => {
    // Its rows can be in several currencies, so its range shows no symbol, not the
    // category's own code (ADR-0114 D14).
    it('shows no symbol on either bound', async () => {
        renderCategory();
        const user = userEvent.setup();
        await rowOf('Payee A');
        await user.click(screen.getByRole('button', { name: /^Filter/ }));

        for (const name of ['Amount min', 'Amount max']) {
            const input = screen.getByRole('spinbutton', { name });
            expect([name, input.parentElement!.querySelector('[data-money-affix]')]).toEqual([name, null]);
        }
    });
});

describe('the bulk bar\'s Σ on a category register over accounts in different currencies', () => {
    // The server sums the selection in one currency (ADR-0114 D7): in the reporting
    // currency, each row converted at its date, when the rows are in several; or null
    // with the missing rates named (ADR-0114 D9). The bar prints what it answers, in its currency.
    const select = async (...payees: string[]) => {
        const user = userEvent.setup();
        for (const payee of payees) {
            const id = ROWS.find((r) => r.payee === payee)!.id;
            await user.click(within(await rowOf(payee)).getByRole('checkbox', { name: `Select transaction ${id}` }));
        }
    };

    it('prints a converted Σ in the currency the summary names', async () => {
        stub.on('POST', SUMMARY, { count: 2, totalCount: 2, readOnlyCount: 0, sumOnAccount: 1234.4, currencyCode: 'USD' });
        renderCategory();
        await select('Payee B', 'Payee A');

        expect(await screen.findByText(`Σ ${formatSignedAmount(1234.4, 'USD')}`)).toBeInTheDocument();
        expect(stub.callsTo('POST', SUMMARY).length).toBeGreaterThan(0);
    });

    it('prints a Σ in one other currency in that currency, not the register\'s', async () => {
        stub.on('POST', SUMMARY, { count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 20, currencyCode: 'CAD' });
        renderCategory();
        await select('Payee B');

        expect(await screen.findByText(`Σ ${formatSignedAmount(20, 'CAD')}`)).toBeInTheDocument();
        expect(screen.queryByText(`Σ ${formatSignedAmount(20, 'USD')}`)).toBeNull();
    });

    it('reads a Σ that needs a missing rate "no rate", naming the pair and the day', async () => {
        stub.on('POST', SUMMARY, {
            count: 2, totalCount: 2, readOnlyCount: 0, sumOnAccount: null, currencyCode: 'USD', rateUnavailable: [MISSING],
        });
        renderCategory();
        await select('Payee C', 'Payee A');

        await waitFor(() => expect(stub.callsTo('POST', SUMMARY).length).toBeGreaterThan(0));
        const sigma = await screen.findByText((_, el) => el?.tagName === 'SPAN' && /^Σ no rate$/.test(el.textContent ?? ''));
        expect(within(sigma).getByText('no rate')).toHaveAttribute('title', 'No CAD to USD rate for Oct 8, 2026.');
        expect(screen.queryByText(`Σ ${formatSignedAmount(0, 'USD')}`)).toBeNull();
    });
});
