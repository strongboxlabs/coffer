import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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
import { stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import type { AccountSummary, BankRow, LedgerSummary, RegisterEntry } from '@/lib/types';

/**
 * After an imported row is accepted or merged, the register shows what a
 * reload would: the settled row at its sorted position and every running
 * balance the server's. The sort key is the full posted timestamp, so a save
 * that keeps the calendar day but moves the time (the editor writes the
 * posted date at midnight; a merge survivor adopts the imported row's time)
 * still moves the row among that day's rows. Mocked at `fetch`.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECKING = '00000000-0000-0000-0000-000000000100';
const GROCERIES = '00000000-0000-0000-0000-000000000302';

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
    account({ id: GROCERIES, name: 'Groceries' }),
];

function row(over: Partial<BankRow> & { id: string; headerId: string }): BankRow {
    return {
        ...NO_NOTES,
        kind: 'bank',
        accountId: CHECKING,
        payee: 'Corner Shop',
        amount: -10,
        postedAt: '2026-10-02T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 0,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
        origin: 'manual',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: `cp-${over.id}`,
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: GROCERIES,
        counterpartyAccountName: 'Groceries',
        counterpartyAccountType: 'category',
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
const headerOf = (e: RegisterEntry) => (e.kind === 'txn' ? e.txn!.headerId : e.legs![0]!.headerId);
const rowOf = (e: RegisterEntry) => (e.kind === 'txn' ? e.txn! : e.legs![0]!);

const page = (entries: RegisterEntry[], cursorForNewer: string | null = null) =>
    ({ entries, cursorForOlder: null, cursorForNewer });

/**
 * A register server with a before and an after state. Before the write it
 * answers with `before`; after it, with `after` the way the server pages it: a
 * request anchored at a header gets that header and everything older, and a
 * newer-direction request gets the rest.
 */
function serve(stub: FetchStub, before: RegisterEntry[], after: RegisterEntry[], written: () => boolean) {
    let anchorAt = 0;
    const window = (c: StubbedCall) => {
        if (c.search.get('account_id') !== CHECKING) return page([]);
        if (!written()) return page(before);
        const anchor = c.search.get('starting_at');
        if (anchor !== null) {
            anchorAt = after.findIndex((e) => headerOf(e) === anchor);
            return page(after.slice(anchorAt), anchorAt > 0 ? 'newer' : null);
        }
        if (c.search.get('cursor') === 'newer') return page(after.slice(0, anchorAt));
        return page(after);
    };
    const balances = (c: StubbedCall) => {
        const source = written() ? after : before;
        const ids = (c.body as { headerIds: string[] }).headerIds;
        return source
            .filter((e) => ids.includes(headerOf(e)))
            .map((e) => ({ headerId: headerOf(e), balanceAfter: rowOf(e).balanceAfter ?? 0, netAmount: rowOf(e).amount }));
    };
    stub
        .on('GET', /^\/api\/ledgers$/, [LEDGER])
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/tags$/, [])
        .on('GET', /\/payees$/, [])
        .on('GET', /\/transactions$/, window)
        .on('GET', /\/transactions\/index-buckets$/, [])
        .on('GET', /\/transactions\/status-counts$/, { all: 0, cleared: 0, uncleared: 0, reconciling: 0, scheduled: 0, needsReview: 0, hidden: 0 })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('POST', /\/transactions\/balances$/, balances)
        .on('POST', /\/selection\/summary$/, { count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0 });
}

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

const money = (n: number) => `${n < 0 ? '-' : ''}$${Math.abs(n).toLocaleString('en-US', { minimumFractionDigits: 2 })}`;

/** The register rows on screen, top to bottom. */
function shownRows(): HTMLElement[] {
    return Array.from(document.querySelectorAll<HTMLElement>('[role="row"][data-headerid]'));
}

/**
 * The screen is what a reload shows: an unbroken run of the server's rows, in
 * the server's order, each with the server's balance, and always including
 * `settled`. A run rather than the whole list because a window re-seated at a
 * row starts there, with the newer rows above it a scroll away, exactly as the
 * server pages it. Fails on a row out of order, a gap, or a stale balance.
 */
function expectReloadView(after: RegisterEntry[], settled: string) {
    const rows = shownRows();
    const headers = rows.map((r) => r.dataset.headerid!);
    expect(headers).toContain(settled);
    const start = after.findIndex((e) => headerOf(e) === headers[0]);
    expect(start).toBeGreaterThanOrEqual(0);
    const run = after.slice(start, start + headers.length);
    expect(headers).toEqual(run.map(headerOf));
    run.forEach((e, i) => {
        expect(rows[i]!.textContent).toContain(money(rowOf(e).balanceAfter!));
    });
}

async function rowWith(text: string): Promise<HTMLElement> {
    return (await screen.findByText(text)).closest('[role="row"]') as HTMLElement;
}

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    vi.stubGlobal('scrollTo', () => {});
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('accepting an imported row', () => {
    it('re-seats it where the accepted time sorts it, with the server\'s balances', async () => {
        // The feed row arrived late in the day; accepting writes the posted date
        // at midnight, so it settles below the other row of that day.
        const pending = row({
            id: 'leg-pend', headerId: 'hdr-pend', payee: 'Card purchase', origin: 'simplefin',
            needsReview: true, postedAt: '2026-10-02T18:00:00Z', amount: -40, balanceAfter: 910,
        });
        const sameDay = row({ id: 'leg-day', headerId: 'hdr-day', payee: 'Bakery', postedAt: '2026-10-02T12:00:00Z', amount: -50, balanceAfter: 950 });
        const older = row({ id: 'leg-old', headerId: 'hdr-old', payee: 'Deposit', postedAt: '2026-10-01T12:00:00Z', amount: 1000, balanceAfter: 1000 });
        const accepted = { ...pending, needsReview: false, postedAt: '2026-10-02T00:00:00.000Z', balanceAfter: 960 };
        const after = [txn({ ...sameDay, balanceAfter: 910 }), txn(accepted), txn(older)];

        let patched = false;
        serve(stub, [txn(pending), txn(sameDay), txn(older)], after, () => patched);
        stub.on('PATCH', /\/transactions\/hdr-pend$/, () => {
            patched = true;
            return { entries: [txn(accepted)] };
        });
        renderPage();

        fireEvent.doubleClick(await rowWith('Card purchase'));
        await userEvent.click(await screen.findByRole('button', { name: /^accept$/i }));
        await waitFor(() => expect(stub.callsTo('PATCH', /\/transactions\/hdr-pend$/)).toHaveLength(1));

        await waitFor(() => expectReloadView(after, 'hdr-pend'));
        expect(screen.queryByRole('button', { name: /^accept$/i })).not.toBeInTheDocument();
    });

    it('keeps it in place when its key is unchanged, and refreshes every balance', async () => {
        // Already at midnight, so accepting does not move it; the amount was
        // corrected in the editor, which moves the balance of the row above.
        const pending = row({
            id: 'leg-pend', headerId: 'hdr-pend', payee: 'Card purchase', origin: 'simplefin',
            needsReview: true, postedAt: '2026-10-02T00:00:00Z', amount: -40, balanceAfter: 960,
        });
        const later = row({ id: 'leg-day', headerId: 'hdr-day', payee: 'Bakery', postedAt: '2026-10-02T12:00:00Z', amount: -50, balanceAfter: 910 });
        const older = row({ id: 'leg-old', headerId: 'hdr-old', payee: 'Deposit', postedAt: '2026-10-01T12:00:00Z', amount: 1000, balanceAfter: 1000 });
        const accepted = { ...pending, needsReview: false, postedAt: '2026-10-02T00:00:00.000Z', amount: -45, balanceAfter: 955 };
        const after = [txn({ ...later, balanceAfter: 905 }), txn(accepted), txn(older)];

        let patched = false;
        serve(stub, [txn(later), txn(pending), txn(older)], after, () => patched);
        stub.on('PATCH', /\/transactions\/hdr-pend$/, () => {
            patched = true;
            return { entries: [txn(accepted)] };
        });
        renderPage();

        fireEvent.doubleClick(await rowWith('Card purchase'));
        // Whatever sign the field shows the posting in, the correction keeps it.
        const amount = await screen.findByLabelText('Amount') as HTMLInputElement;
        fireEvent.change(amount, { target: { value: amount.value.startsWith('-') ? '-45' : '45' } });
        fireEvent.blur(amount);
        await userEvent.click(screen.getByRole('button', { name: /^accept$/i }));
        await waitFor(() => expect(stub.callsTo('PATCH', /\/transactions\/hdr-pend$/)).toHaveLength(1));

        await waitFor(() => expectReloadView(after, 'hdr-pend'));
        // Nothing moved, so the window was patched in place, not re-seeded.
        expect(stub.calls.filter((c) => c.search.get('starting_at') !== null)).toHaveLength(0);
    });
});

describe('merging an imported row', () => {
    const candidate = (headerId: string, postedAt: string) => [{
        headerId, payee: 'Manual entry', postedAt,
        postings: [{ counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries' }],
        origin: 'manual',
    }];

    it('re-seats the survivor where the imported row\'s time sorts it, with the server\'s balances', async () => {
        // The survivor was entered at midnight; it adopts the imported row's
        // time of day and moves above the other row of that day.
        const imported = row({
            id: 'leg-imp', headerId: 'hdr-imp', payee: 'Card purchase', origin: 'simplefin',
            needsReview: true, postedAt: '2026-10-02T18:00:00Z', amount: -40, balanceAfter: 870,
        });
        const sameDay = row({ id: 'leg-day', headerId: 'hdr-day', payee: 'Bakery', postedAt: '2026-10-02T12:00:00Z', amount: -50, balanceAfter: 910 });
        const survivor = row({ id: 'leg-sur', headerId: 'hdr-sur', payee: 'Manual entry', postedAt: '2026-10-02T00:00:00Z', amount: -40, balanceAfter: 960 });
        const older = row({ id: 'leg-old', headerId: 'hdr-old', payee: 'Deposit', postedAt: '2026-10-01T12:00:00Z', amount: 1000, balanceAfter: 1000 });
        const merged = { ...survivor, postedAt: '2026-10-02T18:00:00Z', isMergeWinner: true, balanceAfter: 910 };
        const after = [txn(merged), txn({ ...sameDay, balanceAfter: 950 }), txn(older)];

        let folded = false;
        serve(stub, [txn(imported), txn(sameDay), txn(survivor), txn(older)], after, () => folded);
        stub.on('GET', /\/transactions\/hdr-imp\/merge-candidates$/, candidate('hdr-sur', '2026-10-02T00:00:00Z'));
        stub.on('POST', /\/transactions\/hdr-imp\/merge$/, () => {
            folded = true;
            return { entries: [txn(merged)] };
        });
        renderPage();

        fireEvent.doubleClick(await rowWith('Card purchase'));
        await userEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-10-02,/));
        await userEvent.click(screen.getByRole('button', { name: /fold into selected/i }));
        await waitFor(() => expect(stub.callsTo('POST', /\/merge$/)).toHaveLength(1));

        await waitFor(() => expectReloadView(after, 'hdr-sur'));
        expect(screen.queryByText('Card purchase')).not.toBeInTheDocument();
    });

    it('keeps the survivor in place when its key is unchanged, and refreshes every balance', async () => {
        // Same instant on both, so the survivor does not move; dropping the
        // imported row still moves the balance of every row above it.
        const later = row({ id: 'leg-day', headerId: 'hdr-day', payee: 'Bakery', postedAt: '2026-10-02T18:00:00Z', amount: -50, balanceAfter: 870 });
        const imported = row({
            id: 'leg-imp', headerId: 'hdr-imp', payee: 'Card purchase', origin: 'simplefin',
            needsReview: true, postedAt: '2026-10-02T12:00:00Z', amount: -40, balanceAfter: 920,
        });
        const survivor = row({ id: 'leg-sur', headerId: 'hdr-sur', payee: 'Manual entry', postedAt: '2026-10-02T12:00:00Z', amount: -40, balanceAfter: 960 });
        const older = row({ id: 'leg-old', headerId: 'hdr-old', payee: 'Deposit', postedAt: '2026-10-01T12:00:00Z', amount: 1000, balanceAfter: 1000 });
        const merged = { ...survivor, isMergeWinner: true };
        const after = [txn({ ...later, balanceAfter: 910 }), txn(merged), txn(older)];

        let folded = false;
        serve(stub, [txn(later), txn(imported), txn(survivor), txn(older)], after, () => folded);
        stub.on('GET', /\/transactions\/hdr-imp\/merge-candidates$/, candidate('hdr-sur', '2026-10-02T12:00:00Z'));
        stub.on('POST', /\/transactions\/hdr-imp\/merge$/, () => {
            folded = true;
            return { entries: [txn(merged)] };
        });
        renderPage();

        fireEvent.doubleClick(await rowWith('Card purchase'));
        await userEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-10-02,/));
        await userEvent.click(screen.getByRole('button', { name: /fold into selected/i }));
        await waitFor(() => expect(stub.callsTo('POST', /\/merge$/)).toHaveLength(1));

        await waitFor(() => expectReloadView(after, 'hdr-sur'));
        expect(stub.calls.filter((c) => c.search.get('starting_at') !== null)).toHaveLength(0);
    });
});
