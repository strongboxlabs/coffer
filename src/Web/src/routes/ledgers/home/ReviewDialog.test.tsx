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

import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatSignedAmount } from '@/lib/money';
import type { AccountSummary, BankRow, InvestmentRow, RegisterEntry } from '@/lib/types';

import { ReviewDialog, type ReviewDialogItem } from './ReviewDialog';

/**
 * The home screen's Review… dialog, against the wire: every read and write
 * goes through `fetch`, answered by the stub, with the live editors inside.
 * Each test ends with no unrouted request.
 */

const LEDGER = '00000000-0000-0000-0000-0000000000aa';
const CHECKING = '00000000-0000-0000-0000-0000000000c1';
const SAVINGS = '00000000-0000-0000-0000-0000000000c2';
const BROKERAGE = '00000000-0000-0000-0000-0000000000b1';
const HOLDINGS = '00000000-0000-0000-0000-0000000000b2';
const CATEGORY = '00000000-0000-0000-0000-0000000000d1';
const HEADER = '00000000-0000-0000-0000-000000000e01';
const SURVIVOR = '00000000-0000-0000-0000-000000000e02';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD', isActive: true,
    isSystem: false, feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Account A', accountType: 'bank', needsReviewCount: 1 },
    { ...base, id: SAVINGS, name: 'Account B', accountType: 'bank' },
    { ...base, id: BROKERAGE, name: 'Brokerage A', accountType: 'investment', holdingsAccountId: HOLDINGS },
    { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const rowBase = {
    ...NO_NOTES,
    id: 'leg-1', payee: 'Payee One', amount: -12.75,
    postedAt: '2026-03-10T00:00:00.000Z', transactedAt: '2026-03-10T00:00:00.000Z',
    status: 'uncleared' as const, isHidden: false, balanceAfter: 100, origin: 'simplefin',
    currencyCode: 'USD', balanceCurrencyCode: 'USD',
    isPending: false, externalId: null, checkNumber: null, counterpartyId: 'cp-1', txnGroupId: null,
    legIndex: 0, counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A',
    counterpartyAccountType: 'category', headerId: HEADER, clearedAt: null, clearedByUserId: null,
    createdAt: '2026-03-10T00:00:00.000Z', onlineMatchFitid: null, onlineMatchFiId: null,
    needsReview: true, providerRawPayload: null, headerAccountNetAmount: null, providerKey: 'simplefin',
    isMergeWinner: false, importSource: null, derivedAction: null, accountPostingsOnHeader: 1,
    headerTotalPostings: 2,
};

const bankRow = (over: Partial<BankRow> = {}): BankRow => ({ ...rowBase, kind: 'bank', accountId: CHECKING, ...over });

const invRow = (over: Partial<InvestmentRow> = {}): InvestmentRow => ({
    ...rowBase, kind: 'investment', accountId: BROKERAGE, investmentAction: 'misc',
    securityId: null, securityTicker: null, securityName: null, quantity: null, unitPrice: null,
    postingRole: 'income', ingestActionHint: null, ingestSecurityId: null, ingestShares: null,
    ingestUnitPrice: null, ingestFee: null, ingestAmount: null, ingestSecurityTickerHint: null,
    categoryAccountId: null, categoryAccountName: null, categoryAccountType: null,
    transferAccountId: null, transferAccountName: null, transferAccountType: null,
    feeAmount: null, feeCategoryId: null, feeCategoryName: null,
    ...over,
});

const txnEntry = (txn: BankRow | InvestmentRow): RegisterEntry => ({ kind: 'txn', txn, groupId: null, legs: null });
const page = (...entries: RegisterEntry[]) => ({ entries, cursorForOlder: null, cursorForNewer: null });

const bankItem = (entry: RegisterEntry, currencyCode = 'USD'): ReviewDialogItem => ({ accountId: CHECKING, currencyCode, entry });

const CANDIDATE = {
    headerId: SURVIVOR, payee: 'Payee Two', postedAt: '2026-03-09T00:00:00.000Z',
    postings: [{ counterpartyAccountId: CATEGORY, counterpartyAccountName: 'Category A' }],
    origin: 'manual',
};

const REGISTER_PAGE = /\/transactions$/;
const BANK_PATCH = /\/transactions\/[^/]+$/;
const BANK_MERGE = /\/transactions\/[^/]+\/merge$/;
const BANK_CANDIDATES = /\/transactions\/[^/]+\/merge-candidates$/;
const INV_PATCH = /\/investment-transactions\/[^/]+$/;
const INV_MERGE = /\/investment-transactions\/[^/]+\/merge$/;

let stub: FetchStub;

/** The reads every dialog makes, and the bank editor's side reads. */
function serveBank(fresh: RegisterEntry[], candidates: unknown[] = []) {
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', REGISTER_PAGE, page(...fresh))
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/transactions\/[^/]+\/similar-payees$/, [])
        .on('GET', BANK_CANDIDATES, candidates)
        // The save's refresh reads the accounts and the entry again.
        .on('PATCH', BANK_PATCH, { entries: [] })
        .on('POST', BANK_MERGE, { entries: [] });
}

/** The investment editor's reads, for a misc event: the brokerage leg and its category's. */
function serveInvestment(fresh: InvestmentRow, candidates: unknown[] = []) {
    const legs = [
        fresh,
        invRow({ id: 'leg-2', accountId: CATEGORY, amount: 12.75, legIndex: 1 }),
    ];
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', REGISTER_PAGE, page(txnEntry(fresh)))
        .on('GET', /\/transactions\/[^/]+\/legs$/, legs)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE, accountName: 'Brokerage A', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/transactions\/[^/]+\/similar-payees$/, [])
        .on('GET', /\/investment-transactions\/[^/]+\/merge-candidates$/, candidates)
        .on('PATCH', INV_PATCH, { entries: [] })
        .on('POST', INV_MERGE, { entries: [] });
}

/** `prime` fills the cache before the dialog opens, as an earlier open would have. */
function renderDialog(item: ReviewDialogItem, prime?: (queryClient: QueryClient) => void) {
    const onClose = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    prime?.(queryClient);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    const root = createRootRoute();
    const home = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <ReviewDialog ledgerId={LEDGER} item={item} onClose={onClose} />,
    });
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <main>register</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([home, registerRoute]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    const keys = () => invalidateSpy.mock.calls.map((c) => c[0]?.queryKey);
    return { onClose, keys };
}

const writes = () => stub.calls.filter((c) => c.method !== 'GET');

beforeEach(() => {
    stub = stubFetch();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('ReviewDialog: a bank row awaiting review', () => {
    it('re-reads the row from its register, then Accept sends the PATCH with approve and closes', async () => {
        serveBank([txnEntry(bankRow())]);
        const { onClose, keys } = renderDialog(bankItem(txnEntry(bankRow())));

        await userEvent.click(await screen.findByRole('button', { name: 'Accept' }));

        await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
        const read = stub.callsTo('GET', REGISTER_PAGE)[0]!;
        expect(read.search.get('account_id')).toBe(CHECKING);
        expect(read.search.get('starting_at')).toBe(HEADER);
        expect(read.search.get('limit')).toBe('1');
        const patches = stub.callsTo('PATCH', BANK_PATCH);
        expect(patches).toHaveLength(1);
        expect(patches[0]!.path).toBe(`/api/ledgers/${LEDGER}/transactions/${HEADER}`);
        expect(patches[0]!.body).toMatchObject({ approve: true });
        expect(stub.callsTo('POST', BANK_MERGE)).toEqual([]);
        // The registers, the home screen and the editors' own caches.
        for (const key of [
            ['register', LEDGER], ['home', LEDGER], ['accounts', LEDGER],
            ['merge-candidates', LEDGER], ['similar-payees', LEDGER],
            ['investment-merge-candidates', LEDGER], ['payees', LEDGER],
        ]) {
            expect(keys()).toContainEqual(key);
        }
        expect(stub.unrouted).toEqual([]);
    });

    it('a picked candidate folds the row into it, with no PATCH', async () => {
        serveBank([txnEntry(bankRow())], [CANDIDATE]);
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        fireEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-03-09,/));
        fireEvent.click(screen.getByRole('button', { name: 'Fold into selected →' }));

        await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
        const merges = stub.callsTo('POST', BANK_MERGE);
        expect(merges).toHaveLength(1);
        expect(merges[0]!.path).toBe(`/api/ledgers/${LEDGER}/transactions/${HEADER}/merge`);
        expect(merges[0]!.body).toEqual({ fromHeaderId: SURVIVOR });
        expect(stub.callsTo('PATCH', BANK_PATCH)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('Cancel, the close button and the backdrop each close it and send nothing', async () => {
        serveBank([txnEntry(bankRow())]);
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));
        await userEvent.click(screen.getByRole('button', { name: 'Close' }));
        // The backdrop is the dialog's parent; a click on the panel does not reach it.
        fireEvent.click(screen.getByRole('dialog').parentElement!);

        expect(onClose).toHaveBeenCalledTimes(3);
        expect(writes()).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('a refused save keeps the dialog open with the reason', async () => {
        serveBank([txnEntry(bankRow())]);
        stub.on('PATCH', BANK_PATCH, problem(422, 'Not allowed on this row.', 'transaction-not-in-ledger'));
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        await userEvent.click(await screen.findByRole('button', { name: 'Accept' }));

        const dialog = screen.getByRole('dialog');
        expect(await within(dialog).findByRole('alert')).toHaveTextContent('Not allowed on this row.');
        expect(onClose).not.toHaveBeenCalled();
        expect(within(dialog).getByLabelText('Payee')).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('Escape in the category picker closes the picker and keeps the dialog', async () => {
        serveBank([txnEntry(bankRow())]);
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        const picker = await screen.findByRole('combobox', { name: 'Category' });
        await userEvent.click(picker);
        expect(picker).toHaveAttribute('aria-expanded', 'true');
        await userEvent.keyboard('{Escape}');

        expect(picker).toHaveAttribute('aria-expanded', 'false');
        expect(screen.getByRole('dialog')).toBeInTheDocument();
        expect(onClose).not.toHaveBeenCalled();
        expect(stub.unrouted).toEqual([]);
    });

    it('opens the category picker on Uncategorized with its text selected and every category listed', async () => {
        // The bank feed stamps a new row with the system Uncategorized
        // category, which the picker shows but never offers (ADR-0043). Its
        // text would narrow the list to nothing; the list stays whole until
        // the user types.
        const UNCATEGORIZED = '00000000-0000-0000-0000-0000000000d2';
        const OTHER = '00000000-0000-0000-0000-0000000000d3';
        const row = bankRow({ counterpartyAccountId: UNCATEGORIZED, counterpartyAccountName: 'Uncategorized' });
        serveBank([txnEntry(row)]);
        stub.on('GET', /\/accounts$/, [
            ...ACCOUNTS,
            { ...base, id: OTHER, name: 'Category B', accountType: 'category', categoryKind: 'expense' },
            {
                ...base, id: UNCATEGORIZED, name: 'Uncategorized', accountType: 'category',
                categoryKind: 'expense', isSystem: true,
            },
        ]);
        renderDialog(bankItem(txnEntry(row)));
        const user = userEvent.setup();

        const picker = await screen.findByRole('combobox', { name: 'Category' }) as HTMLInputElement;
        await user.click(picker);

        expect(picker).toHaveValue('Uncategorized');
        expect(picker.selectionStart).toBe(0);
        expect(picker.selectionEnd).toBe('Uncategorized'.length);
        // Every counterparty the bank editor offers: the other money accounts
        // and the categories.
        expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual([
            'Account BBank', 'Brokerage AInvestment', 'Category A', 'Category B',
        ]);

        await user.keyboard('Category B');

        expect(picker).toHaveValue('Category B');
        expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Category B']);
        expect(stub.unrouted).toEqual([]);
    });

    it('Escape in the open payee list closes the list and keeps the dialog; the next Escape closes it', async () => {
        serveBank([txnEntry(bankRow())]);
        stub.on('GET', /\/payees$/, [
            { name: 'Payee One', count: 2, lastUsedAt: '2026-03-01T00:00:00Z' },
            { name: 'Payee Two', count: 1, lastUsedAt: '2026-03-01T00:00:00Z' },
        ]);
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        const payee = await within(dialog).findByRole('combobox', { name: 'Payee' });
        await userEvent.clear(payee);
        await userEvent.type(payee, 'Pay');
        expect(within(dialog).getByRole('option', { name: 'Payee Two' })).toBeInTheDocument();

        await userEvent.keyboard('{Escape}');

        expect(within(dialog).queryByRole('listbox')).toBeNull();
        expect(screen.getByRole('dialog')).toBe(dialog);
        expect(onClose).not.toHaveBeenCalled();
        expect(payee).toHaveFocus();
        expect(payee).toHaveValue('Pay');

        await userEvent.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
        expect(writes()).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('titles the row with its payee, date, amount in its account\'s currency, and account', async () => {
        const row = bankRow();
        serveBank([txnEntry(row)]);
        renderDialog(bankItem(txnEntry(row), 'EUR'));

        const heading = within(await screen.findByRole('dialog')).getByRole('heading', { level: 2 });
        await screen.findByRole('button', { name: 'Accept' });
        expect(heading).toHaveTextContent(/^Review: Payee One/);
        const subtitle = heading.querySelector('span')!;
        expect(subtitle).toHaveTextContent(` · ${formatSignedAmount(-12.75, 'EUR')} · Account A`);
        expect(subtitle.textContent).not.toContain('$');
        expect(stub.unrouted).toEqual([]);
    });
});

describe('ReviewDialog: reopened over reads an earlier open cached', () => {
    const entryKey = (accountId: string) => ['home', LEDGER, 'review-entry', accountId, HEADER];
    /** What an earlier open leaves behind besides the row: the accounts and payees. */
    const primeEarlierOpen = (qc: QueryClient) => {
        qc.setQueryData(['accounts', LEDGER, { includeInactive: true }], ACCOUNTS);
        qc.setQueryData(['payees', LEDGER], []);
    };

    it('the bank editor seeds from the read made on this open', async () => {
        serveBank([txnEntry(bankRow({ payee: 'Payee Fresh' }))]);
        renderDialog(bankItem(txnEntry(bankRow())), (qc) => {
            primeEarlierOpen(qc);
            qc.setQueryData(entryKey(CHECKING), page(txnEntry(bankRow({ payee: 'Payee Stale' }))));
        });

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByLabelText('Payee')).toHaveValue('Payee Fresh');
        expect(stub.callsTo('GET', REGISTER_PAGE)).toHaveLength(1);
        expect(stub.unrouted).toEqual([]);
    });

    it('the investment editor seeds from the legs read on this open', async () => {
        const row = invRow({ payee: 'Payee Fresh' });
        serveInvestment(row);
        const stale = invRow({ payee: 'Payee Stale' });
        renderDialog({ accountId: BROKERAGE, currencyCode: 'USD', entry: txnEntry(row) }, (qc) => {
            primeEarlierOpen(qc);
            qc.setQueryData(entryKey(BROKERAGE), page(txnEntry(stale)));
            qc.setQueryData(['header-legs', LEDGER, HEADER], [
                stale,
                invRow({ id: 'leg-2', accountId: CATEGORY, amount: 12.75, legIndex: 1, payee: 'Payee Stale' }),
            ]);
        });

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByLabelText('Payee')).toHaveValue('Payee Fresh');
        expect(stub.callsTo('GET', /\/transactions\/[^/]+\/legs$/)).toHaveLength(1);
        expect(stub.unrouted).toEqual([]);
    });
});

describe('ReviewDialog: a row accepted since the list was drawn', () => {
    it('opens as a plain edit: the note, Save, and neither strip', async () => {
        serveBank([txnEntry(bankRow({ needsReview: false }))], [CANDIDATE]);
        // The list still had it awaiting review.
        renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText('Already accepted. Changes save as a normal edit.')).toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Save' })).toBeInTheDocument();
        expect(within(dialog).getByLabelText('Payee')).toBeInTheDocument();
        expect(within(dialog).queryByRole('button', { name: 'Accept' })).toBeNull();
        expect(within(dialog).queryByText('Merge candidates:')).toBeNull();
        expect(within(dialog).queryByText('Similar:')).toBeNull();
        expect(stub.callsTo('GET', BANK_CANDIDATES)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });
});

describe('ReviewDialog: no editor', () => {
    it('a read-only row says where it was entered and links there', async () => {
        const row = bankRow({ accountId: SAVINGS, isReadOnly: true, counterpartyAccountId: CHECKING, counterpartyAccountName: 'Account A' });
        serveBank([txnEntry(row)]);
        const { onClose } = renderDialog({ accountId: SAVINGS, currencyCode: 'USD', entry: txnEntry(row) });

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText(
            'This row is read-only here: it belongs to a transaction entered from Account A.',
        )).toBeInTheDocument();
        const open = within(dialog).getByRole('link', { name: 'Open in Account A →' });
        expect(open).toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/${CHECKING}?focus=${HEADER}`);
        expect(within(dialog).queryByLabelText('Payee')).toBeNull();

        // The footer's Close (the header's ✕ is named Close too): in a narrow
        // dialog it is the 44px button the editors' Cancel is.
        const close = within(dialog).getByText('Close', { selector: 'button' });
        expect(close).toHaveClass('@max-3xl/dialog:h-control-44px');
        await userEvent.click(close);
        expect(onClose).toHaveBeenCalled();
        expect(writes()).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    /** The footer's Close (the header's ✕ is named Close too). */
    const footerClose = (dialog: HTMLElement) => within(dialog).getByText('Close', { selector: 'button' });

    it('a row its register no longer shows says so, with a Close and no retry', async () => {
        // The register's read starts at a different row: this one is gone.
        serveBank([txnEntry(bankRow({ headerId: SURVIVOR, payee: 'Payee Two' }))]);
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText('This row is no longer in its register.')).toBeInTheDocument();
        expect(within(dialog).queryByRole('alert')).toBeNull();
        expect(within(dialog).queryByRole('button', { name: 'Retry' })).toBeNull();
        expect(within(dialog).queryByLabelText('Payee')).toBeNull();
        await userEvent.click(footerClose(dialog));
        expect(onClose).toHaveBeenCalledTimes(1);
        expect(writes()).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('an empty read says the row is no longer in its register', async () => {
        serveBank([]);
        renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText('This row is no longer in its register.')).toBeInTheDocument();
        expect(within(dialog).queryByLabelText('Payee')).toBeNull();
        expect(footerClose(dialog)).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('an investment row whose legs are gone says so', async () => {
        const row = invRow();
        serveInvestment(row);
        stub.on('GET', /\/transactions\/[^/]+\/legs$/, []);
        renderDialog({ accountId: BROKERAGE, currencyCode: 'USD', entry: txnEntry(row) });

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText('This row is no longer in its register.')).toBeInTheDocument();
        expect(within(dialog).queryByLabelText('Action')).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('a failed read offers Retry and Close, and Retry opens the editor once the read answers', async () => {
        serveBank([txnEntry(bankRow())]);
        let failing = true;
        stub.on('GET', REGISTER_PAGE, () => (failing
            ? problem(500, 'The register could not be read.')
            : page(txnEntry(bankRow()))));
        renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByRole('alert')).toHaveTextContent('Could not load this transaction.');
        expect(footerClose(dialog)).toBeInTheDocument();
        expect(within(dialog).queryByText('This row is no longer in its register.')).toBeNull();
        for (const button of [footerClose(dialog), within(dialog).getByRole('button', { name: 'Retry' })]) {
            expect(button).toHaveClass('@max-3xl/dialog:h-control-44px');
        }

        failing = false;
        await userEvent.click(within(dialog).getByRole('button', { name: 'Retry' }));
        expect(await within(dialog).findByLabelText('Payee')).toBeInTheDocument();
        expect(within(dialog).queryByRole('button', { name: 'Retry' })).toBeNull();
        expect(stub.callsTo('GET', REGISTER_PAGE)).toHaveLength(2);
        expect(stub.unrouted).toEqual([]);
    });

    it('a failed read closes from the footer, sending nothing', async () => {
        serveBank([txnEntry(bankRow())]);
        stub.on('GET', REGISTER_PAGE, problem(500, 'The register could not be read.'));
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        await within(dialog).findByRole('alert');
        await userEvent.click(footerClose(dialog));
        expect(onClose).toHaveBeenCalledTimes(1);
        expect(writes()).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('closes from the footer while the row is still loading', async () => {
        serveBank([txnEntry(bankRow())]);
        // The row read never answers.
        const answered = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
            new URL(String(input), 'http://localhost').pathname.endsWith('/transactions')
                ? new Promise<Response>(() => {})
                : answered(input, init));
        const { onClose } = renderDialog(bankItem(txnEntry(bankRow())));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText('Loading…')).toBeInTheDocument();
        await userEvent.click(footerClose(dialog));
        expect(onClose).toHaveBeenCalledTimes(1);
    });
});

describe('ReviewDialog: an investment row awaiting review', () => {
    const item = (row: InvestmentRow): ReviewDialogItem => ({ accountId: BROKERAGE, currencyCode: 'USD', entry: txnEntry(row) });

    it('Accept sends the investment PATCH with approve and closes', async () => {
        const row = invRow();
        serveInvestment(row);
        const { onClose, keys } = renderDialog(item(row));

        await userEvent.click(await screen.findByRole('button', { name: 'Accept' }));

        await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
        const patches = stub.callsTo('PATCH', INV_PATCH);
        expect(patches).toHaveLength(1);
        expect(patches[0]!.path).toBe(`/api/ledgers/${LEDGER}/investment-transactions/${HEADER}`);
        expect(patches[0]!.body).toMatchObject({ approve: true });
        expect(stub.callsTo('POST', INV_MERGE)).toEqual([]);
        expect(keys()).toContainEqual(['register', LEDGER]);
        expect(keys()).toContainEqual(['investment-merge-candidates', LEDGER]);
        expect(stub.unrouted).toEqual([]);
    });

    it('opens a row accepted since the list was drawn as a plain edit, without the strips', async () => {
        const row = invRow({ needsReview: false });
        serveInvestment(row);
        renderDialog(item(invRow()));

        const dialog = await screen.findByRole('dialog');
        expect(await within(dialog).findByText('Already accepted. Changes save as a normal edit.')).toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Save' })).toBeInTheDocument();
        expect(within(dialog).queryByRole('button', { name: 'Accept' })).toBeNull();
        expect(stub.callsTo('GET', /\/investment-transactions\/[^/]+\/merge-candidates$/)).toEqual([]);
        expect(stub.callsTo('GET', /\/similar-payees$/)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('a picked candidate folds the row into it, with no PATCH', async () => {
        const row = invRow();
        serveInvestment(row, [{
            headerId: SURVIVOR, postedAt: '2026-03-09T00:00:00Z', dayDelta: -1, action: 'misc',
            derivedAction: 'misc', payee: 'Payee Two', securityTicker: null, shares: null,
            unitPrice: null, amount: -12.75, counterpartyAccountId: null, counterpartyAccountName: null,
            origin: 'manual',
        }]);
        const { onClose } = renderDialog(item(row));

        fireEvent.click(await screen.findByTitle(/^Folds this feed row into your manual row of 2026-03-09,/));
        fireEvent.click(screen.getByRole('button', { name: 'Fold into selected →' }));

        await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
        const merges = stub.callsTo('POST', INV_MERGE);
        expect(merges).toHaveLength(1);
        expect(merges[0]!.path).toBe(`/api/ledgers/${LEDGER}/investment-transactions/${HEADER}/merge`);
        expect(stub.callsTo('PATCH', INV_PATCH)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });
});

describe('ReviewDialog: a row rounded at import (ADR-0114 D15, board RV-2)', () => {
    const NOTE = 'Rounded from 1,234.5 as sent by the bank';
    // The bank row's account in a currency with no places.
    const YEN_ACCOUNTS: AccountSummary[] = ACCOUNTS.map((a) => (a.id === CHECKING ? { ...a, currencyCode: 'JPY' } : a));
    const yenRow = (over: Partial<BankRow> = {}) => bankRow({
        amount: 1235, currencyCode: 'JPY', balanceCurrencyCode: 'JPY', ...over,
    });

    it('gives the bank editor\'s Amount the bank\'s figure as its description, under Category and Amount', async () => {
        const row = yenRow({ originalAmount: '1234.5', roundedAtImport: true });
        serveBank([txnEntry(row)]);
        stub.on('GET', /\/accounts$/, YEN_ACCOUNTS);
        renderDialog(bankItem(txnEntry(row), 'JPY'));

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(amount).toHaveAccessibleDescription(NOTE);
        // Whole yen only, with the yen sign inside the field.
        expect(amount).toHaveAttribute('step', '1');
        expect(amount.parentElement!.querySelector('[data-money-affix]')).toHaveTextContent('¥');
        const note = screen.getByText(NOTE);
        expect(note).toHaveClass('row-start-2', 'col-start-4', 'col-span-2', 'text-right', 'text-[0.6875rem]', 'text-text-muted');
        expect(stub.unrouted).toEqual([]);
    });

    it('puts the note on its own line directly under Amount on a transfer to another currency, not in the rate line\'s cell', async () => {
        const CAD_SAVINGS = '00000000-0000-0000-0000-0000000000c3';
        const row = bankRow({
            amount: -500, counterpartyAccountId: CAD_SAVINGS, counterpartyAccountName: 'Account C',
            counterpartyAccountType: 'bank', counterpartyAmount: 685, counterpartyCurrencyCode: 'CAD',
            originalAmount: '-500.004', roundedAtImport: true,
        });
        serveBank([txnEntry(row)]);
        stub.on('GET', /\/accounts$/, [
            ...ACCOUNTS,
            { ...base, id: CAD_SAVINGS, name: 'Account C', accountType: 'bank', currencyCode: 'CAD' },
        ]);
        renderDialog(bankItem(txnEntry(row)));

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        const rate = await screen.findByText('1 USD = 1.3700 CAD, from the two amounts');
        const note = screen.getByText(/^Rounded from .500\.004 as sent by the bank$/);
        expect(amount.getAttribute('aria-describedby')).toBe(note.id);
        expect(amount).toHaveAccessibleDescription(note.textContent!);
        // Next after Amount's cell, on the row under it; the rate line a cell of its own,
        // on the next row, over the same tracks, so the two end at one right edge.
        expect(note.previousElementSibling).toBe(amount.closest('label'));
        expect(note).toHaveClass('row-start-2', 'col-start-4', 'col-span-3', '@max-3xl/dialog:row-start-6');
        expect(rate.contains(note)).toBe(false);
        expect(rate.parentElement).toBe(note.parentElement);
        expect(rate).toHaveClass('row-start-3', 'col-start-4', 'col-span-3');
        expect(stub.unrouted).toEqual([]);
    });

    it.each([
        ['not rounded', { originalAmount: '1235', roundedAtImport: false }],
        ['rounded and accepted since the list was drawn', { originalAmount: '1234.5', roundedAtImport: true, needsReview: false }],
    ] as const)('describes Amount with nothing on a row %s', async (_how, over) => {
        const row = yenRow(over);
        serveBank([txnEntry(row)]);
        stub.on('GET', /\/accounts$/, YEN_ACCOUNTS);
        renderDialog(bankItem(txnEntry(yenRow({ originalAmount: '1234.5', roundedAtImport: true })), 'JPY'));

        // The Amount field is the anchor: the editor is drawn by then.
        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(amount).toHaveValue(1235);
        expect(amount).not.toHaveAccessibleDescription();
        expect(screen.queryByText(/Rounded from/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('gives a brokerage row\'s Amount the bank\'s figure as its description', async () => {
        const row = invRow({ amount: 12.75, originalAmount: '12.745', roundedAtImport: true });
        serveInvestment(row);
        renderDialog({ accountId: BROKERAGE, currencyCode: 'USD', entry: txnEntry(row) });

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(amount).toHaveAccessibleDescription('Rounded from 12.745 as sent by the bank');
        expect(screen.getByText('Rounded from 12.745 as sent by the bank')).toHaveClass('text-right', 'text-text-muted');
        expect(stub.unrouted).toEqual([]);
    });

    it('describes a brokerage row\'s Amount with nothing when it was not rounded', async () => {
        const row = invRow({ amount: 12.75, originalAmount: '12.75', roundedAtImport: false });
        serveInvestment(row);
        renderDialog({ accountId: BROKERAGE, currencyCode: 'USD', entry: txnEntry(row) });

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(amount).toHaveValue(12.75);
        expect(amount).not.toHaveAccessibleDescription();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('ReviewDialog: a row held for a matched transaction\'s cleared figure (ADR-0114 D15)', () => {
    it('describes Amount with what the bank cleared, in place of the row\'s own rounding', async () => {
        // Rounded at its own import, approved, then held because a pending row merged
        // into it cleared at another figure.
        const row = bankRow({
            amount: -42.51, needsReview: true, origin: 'online_import', providerKey: 'simplefin', isMergeWinner: true,
            originalAmount: '-42.505', roundedAtImport: true, reviewClearedAmount: '-45.00', reviewClearedByMatch: true,
        });
        serveBank([txnEntry(row)]);
        renderDialog(bankItem(txnEntry(row), 'USD'));

        const amount = await screen.findByRole('spinbutton', { name: 'Amount' });
        expect(amount).toHaveAccessibleDescription('The bank cleared a matched transaction at -45.00');
        expect(screen.queryByText(/Rounded from/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });
});
