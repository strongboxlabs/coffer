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

import { InvestmentRegisterPage } from './InvestmentRegisterPage';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type {
    AccountSummary,
    InvestmentRow,
    LedgerSummary,
    PatchInvestmentTransactionRequest,
    RegisterEntry,
} from '@/lib/types';

/**
 * The investment register against the memo-and-tags read contract, mocked at
 * `fetch` because the server half is not built yet.
 *
 * One memo and one tag set per event. A one-posting row the brokerage did not
 * annotate shows the other side's value inherited; the editor opens on the
 * brokerage's own (blank) value and an untouched save writes neither field.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BROKERAGE_ID = '00000000-0000-0000-0000-000000000200';
const HOLDINGS_ID = '00000000-0000-0000-0000-000000000201';
const CHECKING_ID = '00000000-0000-0000-0000-000000000100';
const COMMISSION_ID = '00000000-0000-0000-0000-000000000300';

const LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' };

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        accountType: 'bank',
        categoryKind: null,
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...over,
    };
}

const ACCOUNTS = [
    account({ id: BROKERAGE_ID, name: 'Brokerage', accountType: 'investment', holdingsAccountId: HOLDINGS_ID }),
    account({ id: HOLDINGS_ID, name: 'Brokerage Holdings', accountType: 'investment', isSystem: true }),
    account({ id: CHECKING_ID, name: 'Checking' }),
    account({ id: COMMISSION_ID, name: 'Commission', accountType: 'category', categoryKind: 'expense' }),
];

function row(over: Partial<InvestmentRow> & { id: string }): InvestmentRow {
    return {
        ...NO_NOTES,
        kind: 'investment',
        accountId: BROKERAGE_ID,
        payee: 'Contribution',
        amount: 500,
        postedAt: '2026-10-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 1500,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
        origin: 'manual',
        isPending: false,
        investmentAction: 'transfer',
        externalId: null,
        checkNumber: null,
        counterpartyId: 'cp',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: CHECKING_ID,
        counterpartyAccountName: 'Checking',
        counterpartyAccountType: 'bank',
        headerId: 'hdr-xfr',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-10-01T12:00:00Z',
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        securityId: null,
        securityTicker: null,
        securityName: null,
        quantity: null,
        unitPrice: null,
        postingRole: 'transfer',
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
        transferAccountId: CHECKING_ID,
        transferAccountName: 'Checking',
        transferAccountType: 'bank',
        feeAmount: null,
        feeCategoryId: null,
        feeCategoryName: null,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: 'transfer',
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
        ...over,
    };
}

/** A one-posting Xfr whose memo and tags were entered on the bank side. */
const brokerageInherits = () => row({
    id: 'leg-brokerage',
    memo: 'Monthly',
    tags: ['Retirement'],
    memoInherited: true,
    tagsInherited: true,
    inheritedMemo: 'Monthly',
    inheritedTags: ['Retirement'],
    inheritFromAccountId: CHECKING_ID,
});
const checkingLeg = () => row({
    id: 'leg-checking',
    accountId: CHECKING_ID,
    amount: -500,
    counterpartyAccountId: BROKERAGE_ID,
    counterpartyAccountName: 'Brokerage',
    memo: 'Monthly',
    tags: ['Retirement'],
    notesKind: 'eventCounterpartOne',
    isReadOnly: true,
});

const txn = (r: InvestmentRow): RegisterEntry => ({ kind: 'txn', txn: r, groupId: null, legs: null });

function serve(stub: FetchStub, entries: RegisterEntry[], legs: InvestmentRow[]) {
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/tags$/, [{ id: 't1', name: 'Retirement', color: null, usageCount: 2, transactionCount: 2 }])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, { entries, cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: entries.length, cleared: 0, uncleared: entries.length, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('GET', /\/transactions\/[^/]+\/legs$/, legs)
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE_ID, accountName: 'Brokerage', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', /\/transactions\/balances$/, [])
        .on('POST', /\/selection\/summary$/, { count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0 });
}

function renderPage() {
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
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${BROKERAGE_ID}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('a one-posting row the brokerage did not annotate', () => {
    it('shows the other side\'s memo and tags inherited', async () => {
        serve(stub, [txn(brokerageInherits())], [brokerageInherits(), checkingLeg()]);
        renderPage();

        const r = (await screen.findByText('Monthly')).closest('[role="row"]') as HTMLElement;
        expect(within(r).getAllByRole('img', { name: 'From Checking' })).toHaveLength(2);
        expect(within(r).queryByText('↗ Investment')).not.toBeInTheDocument();
    });

    it('opens on the brokerage\'s own blank value, and an untouched save writes no memo or tags', async () => {
        serve(stub, [txn(brokerageInherits())], [brokerageInherits(), checkingLeg()]);
        stub.on('PATCH', /\/investment-transactions\/hdr-xfr$/, { entries: [txn(brokerageInherits())] });
        renderPage();

        fireEvent.doubleClick((await screen.findByText('Monthly')).closest('[role="row"]')!);

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', 'Monthly');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);

        fireEvent.keyDown(memo, { key: 'Enter' });
        await waitFor(() => expect(stub.callsTo('PATCH', /\/investment-transactions\//)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/investment-transactions\//)[0]!.body as PatchInvestmentTransactionRequest;
        expect(body.action).toBe('transfer');
        expect('memo' in body).toBe(false);
        expect('tags' in body).toBe(false);
    });
});

describe('a feed row opened through its classifier hint', () => {
    // A bank-shape feed row the brokerage did not annotate: the hint path
    // seeds the editor from the same own values as an investment-shape row.
    const hinted = () => row({
        id: 'leg-hinted',
        headerId: 'hdr-hinted',
        origin: 'simplefin',
        investmentAction: null,
        derivedAction: null,
        postingRole: null,
        ingestActionHint: 'transfer',
        memo: 'Monthly',
        tags: ['Retirement'],
        memoInherited: true,
        tagsInherited: true,
        inheritedMemo: 'Monthly',
        inheritedTags: ['Retirement'],
        inheritFromAccountId: CHECKING_ID,
    });

    it('opens on the brokerage\'s own blank value, and shows the inherited one once the transfer names its side', async () => {
        // The bank-shape legs carry no role. The save rebuilds them as the
        // transfer the draft describes and keeps the other leg, with its memo
        // and tags, only on the account it is on: until the draft names that
        // account, the save could not keep it, so nothing shows as inherited.
        const user = userEvent.setup();
        serve(stub, [txn(hinted())], [hinted(), { ...checkingLeg(), headerId: 'hdr-hinted', postingRole: null }]);
        renderPage();

        fireEvent.doubleClick((await screen.findByText('Monthly')).closest('[role="row"]')!);

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', '(optional)');
        expect(screen.queryByText(/^from /)).not.toBeInTheDocument();

        const picker = screen.getByRole('combobox', { name: 'Transfer destination' });
        await user.type(picker, 'Che');
        await user.click(await screen.findByRole('option', { name: /Checking/ }));

        expect(memo).toHaveAttribute('placeholder', 'Monthly');
        expect(screen.getAllByText('from Checking')).toHaveLength(2);
    });
});

describe('a multi-posting transfer on its own brokerage', () => {
    // The event's memo and tags are on the transaction: the brokerage's own.
    const xfr = () => row({
        id: 'leg-brokerage',
        headerId: 'hdr-xfr',
        accountPostingsOnHeader: 2,
        headerTotalPostings: 2,
        memo: 'Annual top-up',
        tags: ['Retirement'],
        headerMemo: 'Annual top-up',
        headerTags: ['Retirement'],
        notesKind: 'eventSide',
    });

    it('opens on the event\'s own value, and an untouched save writes no memo or tags', async () => {
        serve(stub, [txn(xfr())], [xfr(), checkingLeg()]);
        stub.on('PATCH', /\/investment-transactions\/hdr-xfr$/, { entries: [txn(xfr())] });
        renderPage();

        fireEvent.doubleClick((await screen.findByText('Annual top-up')).closest('[role="row"]')!);

        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('Annual top-up');
        expect(screen.queryByText(/^from /)).not.toBeInTheDocument();

        fireEvent.keyDown(memo, { key: 'Enter' });
        await waitFor(() => expect(stub.callsTo('PATCH', /\/investment-transactions\//)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/investment-transactions\//)[0]!.body as PatchInvestmentTransactionRequest;
        expect('memo' in body).toBe(false);
        expect('tags' in body).toBe(false);
    });

    it('takes the row from the save\'s answer, leaving the rows around it', async () => {
        const other = row({ id: 'leg-other', headerId: 'hdr-other', payee: 'Dividend', memo: 'Q3 payout', postedAt: '2026-09-30T12:00:00Z' });
        serve(stub, [txn(xfr()), txn(other)], [xfr(), checkingLeg()]);
        // The server's answer: every entry the header has here.
        stub.on('PATCH', /\/investment-transactions\/hdr-xfr$/, {
            entries: [txn({ ...xfr(), memo: 'Biannual top-up', headerMemo: 'Biannual top-up' })],
        });
        renderPage();

        fireEvent.doubleClick((await screen.findByText('Annual top-up')).closest('[role="row"]')!);
        const memo = await screen.findByLabelText('Memo');
        fireEvent.change(memo, { target: { value: 'Biannual top-up' } });
        fireEvent.keyDown(memo, { key: 'Enter' });

        await waitFor(() => expect(stub.callsTo('PATCH', /\/investment-transactions\//)).toHaveLength(1));
        const saved = (await screen.findByText('Biannual top-up')).closest('[role="row"]') as HTMLElement;
        expect(saved).not.toHaveAttribute('data-editing', 'true');
        expect(screen.queryByText('Annual top-up')).not.toBeInTheDocument();
        expect((await screen.findByText('Q3 payout')).closest('[role="row"]')).toHaveTextContent('Dividend');
    });
});

describe('a merge into a transaction with several rows here', () => {
    it('swaps in every row the survivor has here, and drops the merged row', async () => {
        // The survivor is a split Checking owns with both lines going to the
        // brokerage: from here each line is its own read-only row, so the
        // merge answers with two entries for one header.
        const opposite = (id: string, legIndex: number, amount: number, memo: string) => txn(row({
            id,
            headerId: 'hdr-sur',
            legIndex,
            amount,
            investmentAction: null,
            derivedAction: null,
            accountPostingsOnHeader: 2,
            headerTotalPostings: 2,
            notesKind: 'splitOpposite',
            isReadOnly: true,
            memo,
            memoInherited: true,
            inheritedMemo: memo,
            inheritFromAccountId: CHECKING_ID,
        }));
        const imported = row({ id: 'leg-imp', headerId: 'hdr-imp', origin: 'simplefin', amount: 500, memo: 'Imported line' });
        serve(stub, [
            txn(imported),
            opposite('leg-sur-0', 0, 300, 'First half'),
            opposite('leg-sur-1', 1, 200, 'Second half'),
        ], [imported, { ...checkingLeg(), headerId: 'hdr-imp' }]);
        stub.on('GET', /\/investment-transactions\/hdr-imp\/merge-candidates$/, [{
            headerId: 'hdr-sur', postedAt: '2026-10-01T12:00:00Z', dayDelta: 0, action: 'transfer',
            derivedAction: 'transfer', securityTicker: null, shares: null, unitPrice: null, amount: 500,
            payee: 'Contribution', counterpartyAccountId: CHECKING_ID, counterpartyAccountName: 'Checking',
            origin: 'manual',
        }]);
        // The answer's values are what the rows show afterwards; these differ
        // from the window's so the swap is visible.
        stub.on('POST', /\/investment-transactions\/hdr-imp\/merge$/, {
            entries: [
                opposite('leg-sur-0', 0, 300, 'Part one'),
                opposite('leg-sur-1', 1, 200, 'Part two'),
            ],
        });
        renderPage();

        fireEvent.doubleClick((await screen.findByText('Imported line')).closest('[role="row"]')!);
        await userEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-10-01,/));
        await userEvent.click(screen.getByRole('button', { name: /fold into selected/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/investment-transactions\/hdr-imp\/merge$/)).toHaveLength(1));
        const merge = stub.callsTo('POST', /\/investment-transactions\/hdr-imp\/merge$/)[0]!;
        expect(merge.body).toEqual({ fromHeaderId: 'hdr-sur' });
        expect(merge.search.get('account_id')).toBe(BROKERAGE_ID);

        // Both rows arrive in the same swap, so once one shows the other must.
        const one = (await screen.findByText('Part one')).closest('[role="row"]');
        const two = screen.getByText('Part two').closest('[role="row"]');
        expect(two).not.toBeNull();
        expect(one).not.toBe(two);
        expect(screen.queryByText('First half')).not.toBeInTheDocument();
        expect(screen.queryByText('Second half')).not.toBeInTheDocument();
        expect(screen.queryByText('Imported line')).not.toBeInTheDocument();
    });
});

describe('a multi-posting event on its own brokerage', () => {
    // A buy with a fee: the event's memo and tags are the brokerage's own.
    const buy = () => row({
        id: 'leg-buy',
        headerId: 'hdr-buy',
        payee: 'Rebalance',
        investmentAction: 'buy',
        derivedAction: 'buy',
        amount: -505,
        postingRole: 'security',
        transferAccountId: null,
        transferAccountName: null,
        feeAmount: 5,
        feeCategoryId: COMMISSION_ID,
        feeCategoryName: 'Commission',
        accountPostingsOnHeader: 2,
        headerTotalPostings: 2,
        memo: 'Quarterly rebalance',
        tags: ['Retirement'],
        headerMemo: 'Quarterly rebalance',
        headerTags: ['Retirement'],
        notesKind: 'eventSide',
    });

    it('shows one memo and one tag set, as its own', async () => {
        serve(stub, [txn(buy())], [buy()]);
        renderPage();

        const r = (await screen.findByText('Quarterly rebalance')).closest('[role="row"]') as HTMLElement;
        expect(within(r).getByTitle('Retirement')).not.toHaveAttribute('data-inherited');
        expect(within(r).queryByRole('img', { name: /^From / })).not.toBeInTheDocument();
    });

    it('Duplicate copies the event\'s memo and tags as the copy\'s own', async () => {
        serve(stub, [txn(buy())], [buy()]);
        stub.on('POST', /\/investment-transactions$/, { headerId: 'hdr-new' });
        renderPage();

        fireEvent.contextMenu((await screen.findByText('Quarterly rebalance')).closest('[role="row"]')!);
        await userEvent.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: /^Duplicate/ }));

        const editor = await screen.findByRole('row', { name: /new investment transaction/i });
        expect(within(editor).getByLabelText('Memo')).toHaveValue('Quarterly rebalance');
        expect(within(editor).getByTitle('Retirement')).not.toHaveAttribute('data-inherited');
        // Nothing inherited on a new transaction, so no "from" caption.
        expect(within(editor).queryByText(/^from /)).not.toBeInTheDocument();
    });
});
