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
import { formatLedgerDate } from '@/lib/dates';
import type {
    AccountSummary,
    BankRow,
    CreateTransactionRequest,
    LedgerSummary,
    PatchTransactionRequest,
    RegisterEntry,
} from '@/lib/types';

/**
 * The bank register against the memo-and-tags read contract.
 *
 * MOCKED AT `fetch`, not at the API client: the server half of these fields is
 * not built yet, so the stubbed responses below are the contract itself, and
 * the client's own URL building and parsing run for real.
 *
 * Every shape the register can show is a row here, seen from the register that
 * would show it, and each assertion is about what the user sees: the value, the
 * inherited marker, the read-only badge, the menu. Negative assertions are
 * anchored on data that exists only on the row in question.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECKING = '00000000-0000-0000-0000-000000000100';
const DINING = '00000000-0000-0000-0000-000000000301';
const GROCERIES = '00000000-0000-0000-0000-000000000302';
const SUPPLIES = '00000000-0000-0000-0000-000000000303';
const BROKERAGE = '00000000-0000-0000-0000-000000000200';

const LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' };

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        accountType: 'category',
        categoryKind: 'expense',
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

const ACCOUNTS: AccountSummary[] = [
    account({ id: CHECKING, name: 'Checking', accountType: 'bank', categoryKind: null }),
    account({ id: DINING, name: 'Dining' }),
    account({ id: GROCERIES, name: 'Groceries' }),
    account({ id: SUPPLIES, name: 'Supplies' }),
    account({ id: BROKERAGE, name: 'Brokerage', accountType: 'investment', categoryKind: null }),
];

let seq = 0;
function row(over: Partial<BankRow> & { accountId: string }): BankRow {
    seq += 1;
    return {
        ...NO_NOTES,
        kind: 'bank',
        id: `leg-${seq}`,
        payee: 'Corner Cafe',
        amount: -12,
        postedAt: '2026-10-02T12:00:00Z',
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
        counterpartyId: `cp-${seq}`,
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: DINING,
        counterpartyAccountName: 'Dining',
        counterpartyAccountType: 'category',
        headerId: `hdr-${seq}`,
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-10-02T12:00:00Z',
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
        ...over,
    };
}

const txn = (r: BankRow): RegisterEntry => ({ kind: 'txn', txn: r, groupId: null, legs: null });

function serve(stub: FetchStub, accountId: string, entries: RegisterEntry[]) {
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [
            { id: 't1', name: 'Work', color: '#14b8a6', usageCount: 3, transactionCount: 3 },
            { id: 't2', name: 'Household', color: null, usageCount: 1, transactionCount: 1 },
            { id: 't3', name: 'Organic', color: null, usageCount: 1, transactionCount: 1 },
            { id: 't4', name: 'Retirement', color: null, usageCount: 1, transactionCount: 1 },
        ])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, (c: { search: URLSearchParams }) =>
            c.search.get('account_id') === accountId
                ? { entries, cursorForOlder: null, cursorForNewer: null }
                : { entries: [], cursorForOlder: null, cursorForNewer: null })
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: entries.length, cleared: 0, uncleared: entries.length, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', /\/transactions\/balances$/, [])
        .on('POST', /\/selection\/summary$/, { count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0 });
}

function renderAt(accountId: string) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const categoriesRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/categories',
        component: () => <main>categories</main>,
    });
    const ledgerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId',
        component: () => <main>ledger</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute, categoriesRoute, ledgerRoute]),
        history: createMemoryHistory({ initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${accountId}`] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** The register row holding this exact text. */
async function rowWith(text: string): Promise<HTMLElement> {
    return (await screen.findByText(text)).closest('[role="row"]') as HTMLElement;
}

let stub: FetchStub;
beforeEach(() => {
    seq = 0;
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('a one-split seen from both registers', () => {
    // Checking entered it, so Checking owns the memo and tags; Dining shows
    // them inherited until it sets its own.
    const inheriting = () => row({
        accountId: DINING,
        payee: 'Corner Cafe',
        counterpartyAccountId: CHECKING,
        counterpartyAccountName: 'Checking',
        counterpartyAccountType: 'bank',
        amount: 12,
        memo: 'Team coffee',
        tags: ['Work'],
        memoInherited: true,
        tagsInherited: true,
        inheritedMemo: 'Team coffee',
        inheritedTags: ['Work'],
        inheritFromAccountId: CHECKING,
    });
    const own = () => row({
        accountId: DINING,
        payee: 'Bistro Nine',
        counterpartyAccountId: CHECKING,
        counterpartyAccountName: 'Checking',
        counterpartyAccountType: 'bank',
        amount: 40,
        memo: 'Client lunch',
        tags: ['Household'],
        inheritedMemo: 'Card purchase',
        inheritedTags: ['Work'],
        inheritFromAccountId: CHECKING,
    });

    it('draws the inherited marker on the inherited memo and tags, naming the other side', async () => {
        serve(stub, DINING, [txn(inheriting()), txn(own())]);
        renderAt(DINING);

        const r = await rowWith('Team coffee');
        const markers = within(r).getAllByRole('img', { name: 'From Checking' });
        // One before the memo, one before the tags.
        expect(markers).toHaveLength(2);
        expect(within(r).getByText('Team coffee').closest('[data-inherited="true"]'))
            .toHaveClass('text-text-subtle');
        expect(within(r).getByTitle('Work')).toHaveAttribute('data-inherited', 'true');
    });

    it('shows an own value as plain text, with no marker', async () => {
        serve(stub, DINING, [txn(inheriting()), txn(own())]);
        renderAt(DINING);

        const r = await rowWith('Client lunch');
        expect(within(r).getByTitle('Household')).not.toHaveAttribute('data-inherited');
        expect(within(r).queryByRole('img', { name: /^From / })).not.toBeInTheDocument();
        // The inherited value it would fall back to is not shown at all.
        expect(r).not.toHaveTextContent('Card purchase');
    });

    it('an untouched save from the inheriting side sends no notes change', async () => {
        const r0 = inheriting();
        serve(stub, DINING, [txn(r0)]);
        stub.on('PATCH', /\/transactions\/hdr-1$/, { entries: [txn(r0)] });
        renderAt(DINING);

        fireEvent.doubleClick(await rowWith('Team coffee'));
        // The editor opened on this side's own value: blank, with the other
        // side's as placeholder.
        const memo = await screen.findByLabelText('Memo');
        expect(memo).toHaveValue('');
        expect(memo).toHaveAttribute('placeholder', 'Team coffee');

        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/transactions\//)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/transactions\//)[0]!.body as PatchTransactionRequest;
        expect(body.notes).toEqual({ side: DINING });
        // Postings carry shape only.
        expect(body.postings?.items[0]).toEqual({
            legId: r0.id, counterpartyAccountId: CHECKING, amount: 12,
        });
    });
});

describe('a multi-split from the side that owns it', () => {
    // Checking split it: the transaction row shows the transaction's memo and
    // tags; each line shows its own, and a line with none shows "—".
    const lines = () => {
        const common = {
            accountId: CHECKING,
            payee: 'Grocery Mart',
            headerId: 'hdr-split',
            txnGroupId: 'hdr-split',
            accountPostingsOnHeader: 2,
            headerTotalPostings: 2,
            notesKind: 'splitSide' as const,
            headerMemo: 'Weekly shop',
            headerTags: ['Household'],
        };
        return [
            row({ ...common, legIndex: 0, amount: -60, counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries', memo: 'Produce', tags: ['Organic'] }),
            row({ ...common, legIndex: 1, amount: -30, counterpartyAccountId: SUPPLIES, counterpartyAccountName: 'Supplies', memo: null, tags: [] }),
        ];
    };
    const group = (): RegisterEntry => ({ kind: 'group', txn: null, groupId: 'hdr-split', legs: lines() });

    it('shows the transaction\'s memo and tags on the collapsed row, and no line\'s', async () => {
        serve(stub, CHECKING, [group()]);
        renderAt(CHECKING);

        const parent = (await screen.findByRole('button', { name: /2 splits/i })).closest('[role="row"]') as HTMLElement;
        expect(parent).toHaveTextContent('Weekly shop');
        expect(within(parent).getByTitle('Household')).toBeInTheDocument();
        expect(parent).not.toHaveTextContent('Produce');
        expect(within(parent).queryByRole('img', { name: /^From / })).not.toBeInTheDocument();
    });

    it('shows each expanded line\'s own memo and tags, and "—" for a line with no memo', async () => {
        serve(stub, CHECKING, [group()]);
        renderAt(CHECKING);

        await userEvent.click(await screen.findByRole('button', { name: /2 splits/i }));

        const produce = await rowWith('Produce');
        expect(produce).toHaveAttribute('data-split-leg', 'true');
        expect(within(produce).getByTitle('Organic')).toBeInTheDocument();

        const legs = Array.from(document.querySelectorAll<HTMLElement>('[data-split-leg="true"]'));
        const supplies = legs.find((l) => l.textContent?.includes('Supplies'))!;
        expect(supplies).toBeDefined();
        // The line does not repeat the transaction's memo.
        expect(supplies).toHaveTextContent('—');
        expect(supplies).not.toHaveTextContent('Weekly shop');
    });

    it('opens the split editor with the transaction values captioned as the whole transaction\'s', async () => {
        serve(stub, CHECKING, [group()]);
        renderAt(CHECKING);

        fireEvent.doubleClick((await screen.findByRole('button', { name: /2 splits/i })).closest('[role="row"]')!);

        expect(await screen.findByLabelText('Memo (whole transaction)')).toHaveValue('Weekly shop');
        expect(screen.getByText('Tags (whole transaction)')).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 1 memo')).toHaveValue('Produce');
        expect(screen.getByLabelText('Posting 2 memo')).toHaveValue('');
    });

    it('an untouched save of the split sends no notes change', async () => {
        serve(stub, CHECKING, [group()]);
        stub.on('PATCH', /\/transactions\/hdr-split$/, { entries: [group()] });
        renderAt(CHECKING);

        fireEvent.doubleClick((await screen.findByRole('button', { name: /2 splits/i })).closest('[role="row"]')!);
        await screen.findByLabelText('Memo (whole transaction)');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/transactions\//)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/transactions\//)[0]!.body as PatchTransactionRequest;
        expect(body.notes).toEqual({ side: CHECKING });
    });
});

describe('the rows a save answers with', () => {
    // A save answers with every entry the header has in this register, and
    // the page swaps exactly those in for that header's rows.

    // A split Groceries made itself, every line going to Checking: the
    // symmetric shape, seen from the category that owns it.
    const symmetric = (memos: readonly [string, string]): RegisterEntry => {
        const common = {
            accountId: GROCERIES,
            payee: 'Grocery Mart',
            headerId: 'hdr-sym',
            txnGroupId: 'hdr-sym',
            counterpartyAccountId: CHECKING,
            counterpartyAccountName: 'Checking',
            counterpartyAccountType: 'bank' as const,
            accountPostingsOnHeader: 2,
            headerTotalPostings: 2,
            notesKind: 'splitSide' as const,
            headerMemo: 'Weekly shop',
            headerTags: [],
        };
        return {
            kind: 'group', txn: null, groupId: 'hdr-sym', legs: [
                row({ ...common, id: 'leg-sym-0', legIndex: 0, amount: 60, memo: memos[0] }),
                row({ ...common, id: 'leg-sym-1', legIndex: 1, amount: 25, memo: memos[1] }),
            ],
        };
    };
    const neighbour = () => txn(row({ accountId: GROCERIES, payee: 'Corner Shop', memo: 'Milk', headerId: 'hdr-other', postedAt: '2026-10-01T12:00:00Z' }));

    it('a symmetric split saved from the category updates every line', async () => {
        serve(stub, GROCERIES, [symmetric(['Produce', 'Bakery']), neighbour()]);
        stub.on('PATCH', /\/transactions\/hdr-sym$/, { entries: [symmetric(['Fruit', 'Bread'])] });
        renderAt(GROCERIES);

        fireEvent.doubleClick((await screen.findByRole('button', { name: /2 splits/i })).closest('[role="row"]')!);
        fireEvent.change(await screen.findByLabelText('Posting 1 memo'), { target: { value: 'Fruit' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));
        await waitFor(() => expect(stub.callsTo('PATCH', /\/transactions\/hdr-sym$/)).toHaveLength(1));

        await userEvent.click(await screen.findByRole('button', { name: /2 splits/i }));
        expect(await rowWith('Fruit')).toHaveAttribute('data-split-leg', 'true');
        expect(await rowWith('Bread')).toHaveAttribute('data-split-leg', 'true');
        expect(screen.queryByText('Produce')).not.toBeInTheDocument();
        expect(screen.queryByText('Bakery')).not.toBeInTheDocument();
        // The row around it is untouched.
        expect(await rowWith('Milk')).toHaveTextContent('Corner Shop');
    });

    it('a merge swaps in every row the survivor has here, and drops the merged row', async () => {
        // The survivor is a split Groceries owns with both lines on Checking:
        // from Checking each line is its own read-only row, so the merge
        // answers with two entries for one header.
        const opposite = (id: string, legIndex: number, amount: number, memo: string) => txn(row({
            id,
            accountId: CHECKING,
            payee: 'Grocery Mart',
            headerId: 'hdr-sur',
            amount,
            legIndex,
            counterpartyAccountId: GROCERIES,
            counterpartyAccountName: 'Groceries',
            accountPostingsOnHeader: 2,
            headerTotalPostings: 2,
            notesKind: 'splitOpposite',
            isReadOnly: true,
            memo,
            memoInherited: true,
            inheritedMemo: memo,
            inheritFromAccountId: GROCERIES,
        }));
        const imported = row({ accountId: CHECKING, payee: 'Card purchase', memo: 'Imported line', amount: -85, headerId: 'hdr-imp', origin: 'simplefin' });
        serve(stub, CHECKING, [
            txn(imported),
            opposite('leg-sur-0', 0, -60, 'Produce'),
            opposite('leg-sur-1', 1, -25, 'Bakery'),
        ]);
        stub.on('GET', /\/merge-candidates$/, [{
            headerId: 'hdr-sur', payee: 'Grocery Mart', postedAt: '2026-10-02T12:00:00Z',
            postings: [{ counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries' }],
            origin: 'manual',
        }]);
        // The answer's values are what the rows show afterwards, whatever
        // they are; these differ from the window's so the swap is visible.
        stub.on('POST', /\/transactions\/hdr-imp\/merge$/, {
            entries: [
                opposite('leg-sur-0', 0, -60, 'Fruit'),
                opposite('leg-sur-1', 1, -25, 'Bread'),
            ],
        });
        renderAt(CHECKING);

        fireEvent.doubleClick(await rowWith('Imported line'));
        await userEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-10-02,/));
        await userEvent.click(screen.getByRole('button', { name: /fold into selected/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/transactions\/hdr-imp\/merge$/)).toHaveLength(1));
        const merge = stub.callsTo('POST', /\/transactions\/hdr-imp\/merge$/)[0]!;
        expect(merge.body).toEqual({ fromHeaderId: 'hdr-sur' });
        expect(merge.search.get('account_id')).toBe(CHECKING);

        // Both rows arrive in the same swap, so once one shows the other must.
        const fruit = await rowWith('Fruit');
        const bread = screen.getByText('Bread').closest('[role="row"]');
        expect(bread).not.toBeNull();
        expect(fruit).not.toBe(bread);
        expect(screen.queryByText('Produce')).not.toBeInTheDocument();
        expect(screen.queryByText('Bakery')).not.toBeInTheDocument();
        expect(screen.queryByText('Imported line')).not.toBeInTheDocument();
    });

    it('drops the row when the header has no entry left here', async () => {
        const r0 = row({ accountId: CHECKING, memo: 'Team coffee', tags: [] });
        serve(stub, CHECKING, [txn(r0), txn(row({ accountId: CHECKING, memo: 'Bus fare', headerId: 'hdr-other' }))]);
        // 204: the save moved it out of this register.
        stub.on('PATCH', /\/transactions\/hdr-1$/, () => undefined);
        renderAt(CHECKING);

        fireEvent.doubleClick(await rowWith('Team coffee'));
        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: 'Client lunch' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(screen.queryByText('Team coffee')).not.toBeInTheDocument());
        expect(screen.queryByText('Client lunch')).not.toBeInTheDocument();
        expect(await rowWith('Bus fare')).toBeInTheDocument();
    });
});

describe('a refusal from the server', () => {
    // The write primitive's refusals arrive as ProblemDetails; the editor shows
    // the server's own words and stays open, so nothing typed is lost.
    const codes = [
        'read-only-row',
        'side-not-on-transaction',
        'line-notes-invalid',
        'line-notes-on-one-posting',
        'transaction-tags-too-many',
    ];

    it.each(codes)('shows the server\'s detail for %s and keeps the editor open', async (code) => {
        const r0 = row({ accountId: CHECKING, memo: 'Team coffee', tags: [] });
        serve(stub, CHECKING, [txn(r0)]);
        stub.on('PATCH', /\/transactions\/hdr-1$/, problem(422, `Refused here (${code}).`, code));
        renderAt(CHECKING);

        fireEvent.doubleClick(await rowWith('Team coffee'));
        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: 'Client lunch' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        const message = await screen.findByText(`Refused here (${code}).`);
        // The inline editor shows the refusal in its own alert.
        expect(message.closest('[role="alert"]')).not.toBeNull();
        expect(screen.getByLabelText('Memo')).toHaveValue('Client lunch');
    });
});

describe('a split line seen from its other side', () => {
    // From Groceries, each line Groceries is on is its own read-only row,
    // showing that line's memo and tags inherited — never the transaction's.
    const opposite = (over: Partial<BankRow>) => row({
        accountId: GROCERIES,
        payee: 'Grocery Mart',
        headerId: 'hdr-split',
        txnGroupId: 'hdr-split',
        counterpartyAccountId: CHECKING,
        counterpartyAccountName: 'Checking',
        counterpartyAccountType: 'bank',
        accountPostingsOnHeader: 2,
        headerTotalPostings: 3,
        notesKind: 'splitOpposite',
        isReadOnly: true,
        memoInherited: true,
        tagsInherited: true,
        inheritFromAccountId: CHECKING,
        ...over,
    });

    it('shows one read-only row per line of a symmetric split, each with that line\'s value', async () => {
        // The symmetric shape: every line of the split goes to this category,
        // so the category is on as many postings as the transaction has. Each
        // line is its own entry (keyed by its leg on this side).
        const symmetric = { accountPostingsOnHeader: 2, headerTotalPostings: 2 };
        serve(stub, GROCERIES, [
            txn(opposite({ ...symmetric, legIndex: 0, amount: 60, memo: 'Produce', inheritedMemo: 'Produce', tags: ['Organic'], inheritedTags: ['Organic'] })),
            txn(opposite({ ...symmetric, legIndex: 1, amount: 25, memo: 'Bakery', inheritedMemo: 'Bakery', tags: [], inheritedTags: [] })),
        ]);
        renderAt(GROCERIES);

        const produce = await rowWith('Produce');
        const bakery = await rowWith('Bakery');
        expect(produce).not.toBe(bakery);
        for (const r of [produce, bakery]) {
            expect(within(r).getByText('↗ Split')).toBeInTheDocument();
        }
        expect(within(produce).getAllByRole('img', { name: 'From Checking' })).toHaveLength(2);
        // Nothing clusters the two lines back into a split parent.
        expect(screen.queryByRole('button', { name: /\d+ splits/i })).not.toBeInTheDocument();
    });

    it('puts the badge in the slot after the date, leaving the payee cell to the payee and memo', async () => {
        serve(stub, GROCERIES, [
            txn(opposite({ legIndex: 0, amount: 60, memo: 'Produce', inheritedMemo: 'Produce', postedAt: '2026-10-05T12:00:00Z' })),
        ]);
        renderAt(GROCERIES);

        const r = await rowWith('Produce');
        const { payee, afterDate } = badgeSlots(r, 'Produce', formatLedgerDate('2026-10-05T12:00:00Z'));
        expect(within(afterDate).getByText('↗ Split')).toHaveAttribute(
            'title', 'One line of a split transaction · edit it in the split’s own register',
        );
        expect(within(payee).getByText('Grocery Mart')).toBeInTheDocument();
        expect(within(payee).queryByText('↗ Split')).toBeNull();
    });

    it('never shows the transaction\'s memo, and offers neither Edit nor Duplicate', async () => {
        serve(stub, GROCERIES, [
            txn(opposite({ legIndex: 0, amount: 60, memo: 'Produce', inheritedMemo: 'Produce' })),
        ]);
        renderAt(GROCERIES);

        const r = await rowWith('Produce');
        expect(r).not.toHaveTextContent('Weekly shop');

        fireEvent.contextMenu(r);
        const menu = await screen.findByRole('menu');
        expect(within(menu).getByRole('menuitem', { name: /show other side/i })).toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Edit/ })).not.toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Duplicate/ })).not.toBeInTheDocument();
    });

    it('opens no editor on double-click or Enter', async () => {
        serve(stub, GROCERIES, [
            txn(opposite({ legIndex: 0, amount: 60, memo: 'Produce', inheritedMemo: 'Produce' })),
        ]);
        renderAt(GROCERIES);

        const r = await rowWith('Produce');
        fireEvent.doubleClick(r);
        await userEvent.click(r);
        await userEvent.keyboard('{Enter}');

        expect(screen.getByText('Produce')).toBeInTheDocument();
        expect(document.querySelector('[data-editing="true"]')).toBeNull();
    });
});

describe('an investment event seen from the bank', () => {
    it('shows a one-posting Xfr read-only, inheriting the event\'s memo and tags', async () => {
        serve(stub, CHECKING, [txn(row({
            accountId: CHECKING,
            payee: 'Contribution',
            amount: -500,
            counterpartyAccountId: BROKERAGE,
            counterpartyAccountName: 'Brokerage',
            counterpartyAccountType: 'investment',
            derivedAction: 'transfer',
            memo: 'Monthly',
            tags: ['Retirement'],
            memoInherited: true,
            tagsInherited: true,
            inheritedMemo: 'Monthly',
            inheritedTags: ['Retirement'],
            inheritFromAccountId: BROKERAGE,
            notesKind: 'eventCounterpartOne',
            isReadOnly: true,
        }))]);
        renderAt(CHECKING);

        const r = await rowWith('Monthly');
        expect(within(r).getByText('↗ Investment')).toBeInTheDocument();
        expect(within(r).getAllByRole('img', { name: 'From Brokerage' })).toHaveLength(2);

        fireEvent.contextMenu(r);
        const menu = await screen.findByRole('menu');
        expect(within(menu).getByRole('menuitem', { name: /show other side/i })).toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Edit/ })).not.toBeInTheDocument();
        expect(within(menu).queryByRole('menuitem', { name: /^Duplicate/ })).not.toBeInTheDocument();
    });

    it('puts the badge in the slot after the date, above the check number, leaving the payee cell to the payee and memo', async () => {
        serve(stub, CHECKING, [txn(row({
            accountId: CHECKING,
            payee: 'Contribution',
            amount: -500,
            postedAt: '2026-10-01T12:00:00Z',
            checkNumber: '1042',
            counterpartyAccountId: BROKERAGE,
            counterpartyAccountName: 'Brokerage',
            counterpartyAccountType: 'investment',
            derivedAction: 'transfer',
            memo: 'Monthly',
            memoInherited: true,
            inheritedMemo: 'Monthly',
            inheritFromAccountId: BROKERAGE,
            notesKind: 'eventCounterpartOne',
            isReadOnly: true,
        }))]);
        renderAt(CHECKING);

        const r = await rowWith('Monthly');
        const { payee, afterDate } = badgeSlots(r, 'Monthly', formatLedgerDate('2026-10-01T12:00:00Z'));
        const badge = within(afterDate).getByText('↗ Investment');
        expect(badge).toHaveAttribute(
            'title', 'Row owned by an investment transaction · view + edit in the brokerage register',
        );
        // Badge first, the check number under it; neither is lost to the other.
        const check = within(afterDate).getByText('1042');
        expect(badge.compareDocumentPosition(check) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(within(payee).getByText('Contribution')).toBeInTheDocument();
        expect(within(payee).queryByText('↗ Investment')).toBeNull();
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

describe('Duplicate', () => {
    it('copies the value the row shows as the new transaction\'s own', async () => {
        // Checking's row inherits Dining's memo and tags; the copy owns them.
        const r0 = row({
            accountId: CHECKING,
            memo: 'Team coffee',
            tags: ['Work'],
            memoInherited: true,
            tagsInherited: true,
            inheritedMemo: 'Team coffee',
            inheritedTags: ['Work'],
            inheritFromAccountId: DINING,
        });
        serve(stub, CHECKING, [txn(r0)]);
        stub.on('POST', /\/transactions$/, { headerId: 'hdr-new' });
        renderAt(CHECKING);

        fireEvent.contextMenu(await rowWith('Team coffee'));
        await userEvent.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: /^Duplicate/ }));

        const editor = document.querySelector<HTMLElement>('[data-creating="true"]')!;
        expect(within(editor).getByLabelText('Memo')).toHaveValue('Team coffee');
        expect(within(editor).getByTitle('Work')).not.toHaveAttribute('data-inherited');

        await userEvent.click(within(editor).getByRole('button', { name: /^save$/i }));
        await waitFor(() => expect(stub.callsTo('POST', /\/transactions$/)).toHaveLength(1));
        const body = stub.callsTo('POST', /\/transactions$/)[0]!.body as CreateTransactionRequest;
        expect(body.notes).toEqual({ side: CHECKING, memo: 'Team coffee', tags: ['Work'] });
    });
});
