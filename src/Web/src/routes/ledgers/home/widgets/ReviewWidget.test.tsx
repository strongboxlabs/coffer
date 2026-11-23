import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { problem, type StubbedCall } from '@/lib/annotations/fetchStub';
import { categoryChipVariant } from '@/lib/categoryChip';
import { formatCurrency } from '@/lib/money';
import type { AccountSummary, BankRow, LedgerEntry } from '@/lib/types';

import {
    account,
    bankRow,
    CARD,
    CHECKING,
    customiseWhenRead,
    homeStub,
    investmentRow,
    layoutWith,
    LEDGER,
    renderHome,
    splitEntry,
    txnEntry,
} from '../homeTestFixtures';

// To review through the real page, API client and router, with the server mocked
// at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const BROKERAGE = 'acct-brokerage';
const ENTRIES = /\/transactions\/entries$/;

/** Ids whose Uncategorized chip hashes to a colour, so only the system flag can make
 *  it neutral. */
const SYSTEM_UNCATEGORIZED = Array.from({ length: 50 }, (_, i) => `00000000-0000-0000-0000-0000000004${String(i).padStart(2, '0')}`)
    .find((id) => categoryChipVariant('Uncategorized', 'category', id, false) !== 'default')!;

const accounts = (card: number, checking: number, brokerage = 0): AccountSummary[] => [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank', needsReviewCount: checking }),
    account({ id: CARD, name: 'Card A', accountType: 'credit_card', needsReviewCount: card }),
    account({ id: BROKERAGE, name: 'Brokerage A', accountType: 'investment', needsReviewCount: brokerage }),
    account({ id: 'cat-a', name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
    account({ id: SYSTEM_UNCATEGORIZED, name: 'Uncategorized', accountType: 'category', categoryKind: 'expense', isSystem: true }),
];

/** A feed row awaiting review on `accountId`, dated `day` of the fixture's month. */
const flagged = (n: number, accountId: string, day: number, amount: number, over: Partial<BankRow> = {}) =>
    bankRow({
        id: `leg-${n}`, headerId: `h-${n}`, accountId, payee: `Payee ${n}`, amount,
        postedAt: `2026-03-${String(day).padStart(2, '0')}T00:00:00Z`, needsReview: true,
        origin: 'online_import', providerKey: 'simplefin', ...over,
    });

/** Seven rows awaiting review on two accounts, newest first: four on the card,
 *  three on the checking account. */
const SEVEN: LedgerEntry[] = [
    txnEntry(flagged(1, CARD, 11, -38.4)),
    txnEntry(flagged(2, CHECKING, 11, -12.75)),
    txnEntry(flagged(3, CARD, 10, -94.18)),
    txnEntry(flagged(4, CHECKING, 10, 25)),
    txnEntry(flagged(5, CARD, 9, -51.02)),
    txnEntry(flagged(6, CARD, 8, -10)),
    txnEntry(flagged(7, CHECKING, 7, -5)),
];

const panel = () => screen.findByRole('heading', { level: 2, name: /^To review/ })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);
const rowList = async () => within(await panel()).findByRole('list', { name: 'Rows to review' });
/** The payees of the rows listed, in order. */
const payees = (list: HTMLElement) =>
    within(list).getAllByRole('listitem').map((li) => within(li).getByText(/^Payee \d+$/).textContent);
const rowOf = async (payee: string) =>
    (await within(await panel()).findByText(payee, { selector: 'span' })).closest('li') as HTMLElement;
const footer = async () => (await within(await panel()).findByText(/^\+\d+ more:/)) as HTMLElement;

/**
 * Holds the review rows until the ledger's role has been read and the page has drawn
 * it: before then the page knows no role, so a row drawn early is drawn for none.
 */
function rowsAfterTheRole() {
    const answered = globalThis.fetch;
    let roleRead!: () => void;
    const role = new Promise<void>((resolve) => { roleRead = resolve; });
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = new URL(String(input), 'http://localhost').pathname;
        if (path.endsWith('/transactions/entries')) await role;
        const response = await answered(input, init);
        if (path === '/api/ledgers') setTimeout(roleRead, 50);
        return response;
    });
}

describe('To review', () => {
    it('lists the newest rows awaiting review, newest first, and links what it leaves out per account', async () => {
        const stub = homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        renderHome();

        const list = await rowList();
        expect(payees(list)).toEqual(['Payee 1', 'Payee 2', 'Payee 3', 'Payee 4', 'Payee 5']);
        const call = stub.callsTo('GET', ENTRIES)[0]!;
        expect(call.search.get('needs_review')).toBe('true');
        expect(call.search.get('limit')).toBe('5');

        // Each row: its account, and its amount red when negative.
        const first = await rowOf('Payee 1');
        expect(within(first).getByText('Card A', { selector: 'span[title="Card A"]' })).toBeInTheDocument();
        expect(within(first).getByText('-$38.40')).toHaveClass('text-state-danger');
        const income = within(await rowOf('Payee 4')).getByText('$25.00');
        expect(income).toHaveClass('text-text');
        expect(income).not.toHaveClass('text-state-danger');

        // The head counts every row; the footer, what the five rows leave out.
        expect(within(await panel()).getByRole('heading', { level: 2 })).toHaveTextContent('To review · 7 in 2 accounts');
        const more = await footer();
        expect(more).toHaveTextContent('+2 more: Card A 1 → · Account A 1 →');
        const links = within(more).getAllByRole('link');
        expect(links.map((a) => a.getAttribute('href'))).toEqual([
            `/ledgers/${LEDGER}/accounts/${CARD}?status=needs_review`,
            `/ledgers/${LEDGER}/accounts/${CHECKING}?status=needs_review`,
        ]);
    });

    it('keeps the category column at 128px at every panel width, beside the row actions', async () => {
        homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        renderHome();

        const cell = (await rowOf('Payee 1')).querySelector('span[class*="order-5"]') as HTMLElement;
        expect(cell.className).toContain('@xs/panel:w-fixed-128px');
        expect(cell.className).not.toMatch(/w-fixed-224px/);
    });

    it('asks for no rows when there is nothing to review', async () => {
        const stub = homeStub({ accounts: accounts(0, 0), entries: SEVEN });
        renderHome();

        expect(await within(await panel()).findByText(/^Nothing to review/)).toBeInTheDocument();
        expect(stub.callsTo('GET', ENTRIES)).toEqual([]);
    });

    it('gives every row a kebab', async () => {
        homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        renderHome();

        const items = within(await rowList()).getAllByRole('listitem');
        expect(items).toHaveLength(5);
        for (const [i, li] of items.entries()) {
            expect(within(li).getByRole('button', { name: `Actions for Payee ${i + 1}` })).toHaveAttribute('aria-haspopup', 'menu');
        }
    });

    it('prints each row in its own account\'s currency', async () => {
        homeStub({
            accounts: accounts(1, 1),
            entries: [
                txnEntry(flagged(1, CARD, 11, -38.4), 'EUR'),
                txnEntry(flagged(2, CHECKING, 11, -12.75)),
            ],
        });
        renderHome();

        expect(within(await rowOf('Payee 1')).getByText('-€38.40')).toHaveClass('text-state-danger');
        expect(within(await rowOf('Payee 2')).getByText('-$12.75')).toBeInTheDocument();
        expect((await rowOf('Payee 1')).textContent).not.toContain('$');
    });

    it('shows a transfer between currencies with the other leg\'s amount after its chip', async () => {
        homeStub({
            accounts: accounts(0, 2),
            entries: [
                txnEntry(flagged(1, CHECKING, 11, -40, {
                    counterpartyAccountId: CARD, counterpartyAccountName: 'Card A', counterpartyAccountType: 'credit_card',
                    counterpartyAmount: 36, counterpartyCurrencyCode: 'EUR',
                })),
                txnEntry(flagged(2, CHECKING, 10, -40, {
                    counterpartyAccountId: CARD, counterpartyAccountName: 'Card A', counterpartyAccountType: 'credit_card',
                })),
            ],
        });
        renderHome();

        const other = within(await rowOf('Payee 1')).getByText(formatCurrency(36, 'EUR'));
        expect(other).toHaveAttribute('title', `Received in Card A: ${formatCurrency(36, 'EUR')}`);
        // The one in a single currency, anchored on its chip, has none.
        const plain = await rowOf('Payee 2');
        expect(within(plain).getByText('Card A')).toBeInTheDocument();
        expect(plain.querySelector('[data-other-leg]')).toBeNull();
    });

    it('draws a split as its register\'s collapsed row, counted once in the footer', async () => {
        homeStub({
            accounts: accounts(2, 0),
            entries: [
                splitEntry('g-s', [
                    flagged(1, CARD, 11, -30, { txnGroupId: 'g-s', headerAccountNetAmount: -50 }),
                    flagged(1, CARD, 11, -20, { id: 'leg-1b', txnGroupId: 'g-s', legIndex: 1, headerAccountNetAmount: -50 }),
                ]),
            ],
        });
        renderHome();

        const split = await rowOf('Payee 1');
        expect(within(split).getByText('2 splits')).toBeInTheDocument();
        expect(within(split).getByText('-$50.00')).toBeInTheDocument();
        expect(await footer()).toHaveTextContent('+1 more: Card A 1 →');
    });

    it('draws the system Uncategorized as the neutral chip and a file import\'s mark neutral', async () => {
        homeStub({
            accounts: accounts(1, 0),
            entries: [txnEntry(flagged(1, CARD, 11, -12.75, {
                origin: 'file_import', providerKey: 'ofx',
                counterpartyAccountId: SYSTEM_UNCATEGORIZED, counterpartyAccountName: 'Uncategorized',
            }))],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        expect(within(row).getByTitle('Uncategorized')).toHaveClass('bg-chip-neutral');
        const mark = within(row).getByLabelText('File import · OFX');
        expect(mark.querySelector('svg')).toHaveClass('text-text-subtle');
    });

    it('holds the height of its rows setting while the rows load, under the counts', async () => {
        homeStub({ accounts: accounts(4, 3), prefs: layoutWith({ review: { settings: { rows: 8 } } }) });
        const answered = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
            String(input).includes('/transactions/entries') ? new Promise<Response>(() => {}) : answered(input, init));
        renderHome();

        const loading = await within(await panel()).findByLabelText('Loading the rows to review');
        expect(loading.children).toHaveLength(8);
        expect(within(await panel()).getByRole('list', { name: 'Rows to review by account' })).toBeInTheDocument();
    });

    it('shows the error band in place of the rows, keeping the head and the counts', async () => {
        const stub = homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        stub.on('GET', ENTRIES, problem(500, 'Something broke.'));
        renderHome();

        const alert = await within(await panel()).findByRole('alert');
        expect(alert).toHaveTextContent("Couldn't load the rows to review.");
        expect(within(await panel()).getByRole('heading', { level: 2 })).toHaveTextContent('To review · 7 in 2 accounts');
        expect(within(await panel()).getByRole('list', { name: 'Rows to review by account' })).toBeInTheDocument();
        expect(within(await panel()).queryByText(/^Nothing to review/)).toBeNull();
        expect(within(await panel()).queryByRole('list', { name: 'Rows to review' })).toBeNull();
    });

    it('groups by account under section labels, groups in the order of their newest rows', async () => {
        homeStub({
            accounts: accounts(3, 2),
            prefs: layoutWith({ review: { settings: { groupByAccount: true } } }),
            entries: [
                // The newest row is the checking account's; the card has more rows.
                txnEntry(flagged(1, CHECKING, 11, -1)),
                txnEntry(flagged(2, CARD, 10, -2)),
                txnEntry(flagged(3, CARD, 9, -3)),
                txnEntry(flagged(4, CHECKING, 8, -4)),
                txnEntry(flagged(5, CARD, 7, -5)),
            ],
        });
        renderHome();

        const widget = await panel();
        const checking = await within(widget).findByRole('region', { name: 'Account A' });
        const card = within(widget).getByRole('region', { name: 'Card A' });
        expect(checking.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(within(checking).getByRole('heading', { level: 3 })).toHaveTextContent('Account A');
        expect(payees(checking)).toEqual(['Payee 1', 'Payee 4']);
        expect(payees(card)).toEqual(['Payee 2', 'Payee 3', 'Payee 5']);
        // The five rows are the whole list, not five a group.
        expect(within(widget).getAllByText(/^Payee \d+$/)).toHaveLength(5);
        expect(within(widget).queryByRole('list', { name: 'Rows to review' })).toBeNull();
    });

    it('previews a rows change before Save, and Cancel puts it back', async () => {
        const nine = [...SEVEN, txnEntry(flagged(8, CARD, 6, -1)), txnEntry(flagged(9, CARD, 5, -1))];
        const stub = homeStub({ accounts: accounts(6, 3), entries: nine });
        renderHome();
        const user = userEvent.setup({ delay: null });
        expect(within(await rowList()).getAllByRole('listitem')).toHaveLength(5);

        await user.click(await screen.findByRole('button', { name: 'To review actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));
        const settings = await screen.findByRole('group', { name: 'To review settings' });
        await user.click(within(within(settings).getByRole('group', { name: 'Rows' })).getByRole('button', { name: '8' }));

        await waitFor(async () => expect(within(await rowList()).getAllByRole('listitem')).toHaveLength(8));
        expect(stub.callsTo('GET', ENTRIES).map((c) => c.search.get('limit'))).toEqual(['5', '8']);

        await user.click(within(settings).getByRole('button', { name: 'Cancel' }));
        await waitFor(async () => expect(within(await rowList()).getAllByRole('listitem')).toHaveLength(5));
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toEqual([]);
    });

    it('opens nothing from Review… while the layout is edited', async () => {
        const stub = homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });
        const customise = await customiseWhenRead();
        await user.click(customise);
        await screen.findByRole('region', { name: 'Hidden widgets' });

        const review = within(await rowOf('Payee 1')).getByRole('button', { name: 'Review…' });
        await user.click(review);

        expect(review).toHaveAttribute('tabindex', '-1');
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(stub.callsTo('GET', /\/transactions$/)).toEqual([]);
    });

    it('opens no row menu on a right-click while the layout is edited', async () => {
        const stub = homeStub({ accounts: accounts(4, 3), entries: SEVEN });
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });

        // Outside edit mode the right-click opens the row's menu.
        fireEvent.contextMenu(await rowOf('Payee 1'));
        expect(within(await screen.findByRole('menu')).getAllByRole('menuitem').map((i) => i.textContent))
            .toEqual(['Review…', 'Open in register']);
        await user.keyboard('{Escape}');
        await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());

        const customise = await customiseWhenRead();
        await user.click(customise);
        await screen.findByRole('region', { name: 'Hidden widgets' });
        const row = await rowOf('Payee 1');
        await waitFor(() => expect(within(row).getByRole('button', { name: 'Review…' })).toHaveAttribute('tabindex', '-1'));

        fireEvent.contextMenu(row);

        expect(screen.queryByRole('menu')).toBeNull();
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(stub.callsTo('GET', /\/transactions$/)).toEqual([]);
    });
});

describe.each([
    { role: 'viewer' as const, writes: false },
    { role: 'editor' as const, writes: true },
])('To review for an $role, once the role is read', ({ role, writes }) => {
    it(writes ? 'offers Review… on each row and in its menu' : 'offers Open → into the register instead of Review…', async () => {
        homeStub({ role, accounts: accounts(4, 3), entries: SEVEN });
        rowsAfterTheRole();
        renderHome();

        const row = await rowOf('Payee 1');
        if (writes) {
            expect(within(row).getByRole('button', { name: 'Review…' })).toBeInTheDocument();
            expect(within(row).queryByRole('link', { name: 'Open →' })).toBeNull();
        } else {
            expect(within(row).getByRole('link', { name: 'Open →' }))
                .toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/${CARD}?focus=h-1`);
            expect(within(row).queryByRole('button', { name: 'Review…' })).toBeNull();
        }

        await userEvent.click(within(row).getByRole('button', { name: 'Actions for Payee 1' }));
        const menu = await screen.findByRole('menu');
        expect(within(menu).getAllByRole('menuitem').map((i) => i.textContent?.trim()))
            .toEqual(writes ? ['Review…', 'Open in register'] : ['Open in register']);
    });
});

describe('Review… from the list', () => {
    const CANDIDATE = {
        headerId: 'h-manual', payee: 'Payee M', postedAt: '2026-03-10T00:00:00.000Z',
        postings: [{ counterpartyAccountId: 'cat-a', counterpartyAccountName: 'Category A' }],
        origin: 'manual',
    };

    /** The ledger as the server holds it: a review takes its row out of the list and
     *  its account's count down. Every row reviewed here is the card's. */
    function serveReview(initial: LedgerEntry[] = SEVEN, card = 4, checking = 3) {
        let entries = initial;
        let counts = { card, checking };
        const reviewed = (headerId: string) => {
            entries = entries.filter((e) => e.entry.kind !== 'txn' || e.entry.txn.headerId !== headerId);
            counts = { ...counts, card: counts.card - 1 };
        };
        const stub = homeStub({ accounts: accounts(card, checking), entries: initial });
        stub
            .on('GET', /\/accounts$/, () => accounts(counts.card, counts.checking))
            .on('GET', ENTRIES, (call: StubbedCall) => ({
                entries: entries.slice(0, Number(call.search.get('limit'))),
                hasMore: entries.length > Number(call.search.get('limit')),
            }))
            // The dialog's own reads: the row as its register shows it, and the
            // editor's strips.
            .on('GET', /\/transactions$/, (call: StubbedCall) => ({
                entries: entries.filter((e) => e.entry.kind === 'txn' && e.entry.txn.headerId === call.search.get('starting_at'))
                    .map((e) => e.entry),
                cursorForOlder: null, cursorForNewer: null,
            }))
            .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
            .on('GET', /\/transactions\/[^/]+\/similar-payees$/, [])
            .on('GET', /\/transactions\/[^/]+\/merge-candidates$/, [CANDIDATE])
            .on('PATCH', /\/transactions\/[^/]+$/, (call: StubbedCall) => { reviewed(call.path.split('/').pop()!); return { entries: [] }; })
            .on('POST', /\/transactions\/[^/]+\/merge$/, (call: StubbedCall) => { reviewed(call.path.split('/').at(-2)!); return { entries: [] }; });
        return stub;
    }

    it.each([
        ['Accept', 'PATCH'],
        ['a fold into a match', 'POST'],
    ])('takes the row out of the list and the count down after %s', async (_how, method) => {
        const stub = serveReview();
        renderHome();
        const user = userEvent.setup({ delay: null });

        await user.click(within(await rowOf('Payee 1')).getByRole('button', { name: 'Review…' }));
        const dialog = await screen.findByRole('dialog', { name: 'Review: Payee 1' });
        if (method === 'PATCH') {
            await user.click(await within(dialog).findByRole('button', { name: 'Accept' }));
        } else {
            fireEvent.click(await within(dialog).findByTitle(/^Folds this feed row into your manual row of 2026-03-10,/));
            fireEvent.click(within(dialog).getByRole('button', { name: 'Fold into selected →' }));
        }

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        await waitFor(async () => expect(within(await panel()).getByRole('heading', { level: 2 }))
            .toHaveTextContent('To review · 6 in 2 accounts'));
        await waitFor(async () => expect(payees(await rowList()))
            .toEqual(['Payee 2', 'Payee 3', 'Payee 4', 'Payee 5', 'Payee 6']));
        expect(stub.callsTo(method, method === 'PATCH' ? /\/transactions\/h-1$/ : /\/transactions\/h-1\/merge$/)).toHaveLength(1);
        expect(stub.unrouted).toEqual([]);
        // The reviewed row took focus with it: the next row's Review… holds it now.
        await waitFor(async () => expect(within(await rowOf('Payee 2')).getByRole('button', { name: 'Review…' })).toHaveFocus());
    });

    /** Opens Review… on the row, from its own button or from its menu. */
    async function openReview(user: ReturnType<typeof userEvent.setup>, payee: string, via: 'button' | 'menu') {
        const row = await rowOf(payee);
        if (via === 'menu') {
            await user.click(within(row).getByRole('button', { name: `Actions for ${payee}` }));
            await user.click(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Review…' }));
        } else {
            await user.click(within(row).getByRole('button', { name: 'Review…' }));
        }
        return screen.findByRole('dialog', { name: `Review: ${payee}` });
    }

    it.each([
        ['its own button', 'button', 'Review…'],
        ['its menu', 'menu', 'Actions for Payee 1'],
    ] as const)('gives focus back to the row when the dialog opened from %s is cancelled', async (_how, via, control) => {
        serveReview();
        renderHome();
        const user = userEvent.setup({ delay: null });

        const dialog = await openReview(user, 'Payee 1', via);
        await user.click(await within(dialog).findByRole('button', { name: 'Cancel' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        await waitFor(async () => expect(within(await rowOf('Payee 1')).getByRole('button', { name: control })).toHaveFocus());
    });

    it('gives focus to the next row\'s kebab after an Accept opened from the menu', async () => {
        serveReview();
        renderHome();
        const user = userEvent.setup({ delay: null });

        const dialog = await openReview(user, 'Payee 1', 'menu');
        await user.click(await within(dialog).findByRole('button', { name: 'Accept' }));

        await waitFor(async () => expect(payees(await rowList())).toEqual(['Payee 2', 'Payee 3', 'Payee 4', 'Payee 5', 'Payee 6']));
        await waitFor(async () => expect(within(await rowOf('Payee 2')).getByRole('button', { name: 'Actions for Payee 2' })).toHaveFocus());
    });

    it('gives focus to the panel when the last row is reviewed', async () => {
        serveReview([SEVEN[0]!], 1, 0);
        renderHome();
        const user = userEvent.setup({ delay: null });

        const dialog = await openReview(user, 'Payee 1', 'button');
        await user.click(await within(dialog).findByRole('button', { name: 'Accept' }));

        expect(await within(await panel()).findByText(/^Nothing to review/)).toBeInTheDocument();
        await waitFor(async () => expect(await panel()).toHaveFocus());
    });
});

describe('an investment entry in To review', () => {
    /** The investment cell: the action chip's parent holds the action, the security
     *  and the event's chips, in that order. */
    const cellOf = (row: HTMLElement, action: string) =>
        within(row).getByText(action, { selector: 'span' }).parentElement as HTMLElement;
    const review = (over: Parameters<typeof investmentRow>[0]) => txnEntry(investmentRow({
        accountId: BROKERAGE, needsReview: true, origin: 'online_import', providerKey: 'simplefin', ...over,
    }));

    it('shows a zero-amount reinvestment\'s action, its security and what it settled', async () => {
        homeStub({
            accounts: accounts(0, 0, 1),
            entries: [review({
                id: 'inv-r', headerId: 'h-r', payee: 'Payee 1', amount: 0,
                investmentAction: 'dividend_reinvest', derivedAction: 'dividend_reinvest',
                securityTicker: 'FNDA', securityName: 'Fund A', quantity: 2, unitPrice: 35, settledAmount: 70,
            })],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        expect(cellOf(row, 'Reinvest')).toHaveTextContent(/^ReinvestFNDA · Fund A$/);
        const amount = within(row).getByText('$0.00');
        const settled = within(row).getByText('settled $70.00');
        expect(amount.compareDocumentPosition(settled) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(within(row).queryByText(/sh @/)).toBeNull();
    });

    it('shows a buy\'s action and security', async () => {
        homeStub({
            accounts: accounts(0, 0, 1),
            entries: [review({
                id: 'inv-b', headerId: 'h-b', payee: 'Payee 1', amount: -420,
                securityTicker: 'FNDB', securityName: 'Fund B', quantity: 10, unitPrice: 42,
            })],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        expect(cellOf(row, 'Buy')).toHaveTextContent(/^BuyFNDB · Fund B$/);
        expect(within(row).getByText('-$420.00')).toHaveClass('text-state-danger');
        expect(within(row).queryByText(/^settled /)).toBeNull();
    });

    it('shows a dividend\'s income chip after its action and security', async () => {
        homeStub({
            accounts: accounts(0, 0, 1),
            entries: [review({
                id: 'inv-d', headerId: 'h-d', payee: 'Payee 1', amount: 31.5,
                investmentAction: 'dividend_cash', derivedAction: 'dividend_cash', postingRole: 'income',
                securityTicker: 'FNDA', securityName: 'Fund A', quantity: null, unitPrice: null,
                categoryAccountId: 'cat-div', categoryAccountName: 'Category Div', categoryAccountType: 'category',
            })],
        });
        renderHome();

        expect(cellOf(await rowOf('Payee 1'), 'Dividend')).toHaveTextContent(/^DividendFNDA · Fund ACategory Div$/);
    });

    it('shows a feed row not yet confirmed with the register\'s hint chip, then its Uncategorized', async () => {
        homeStub({
            accounts: accounts(0, 0, 1),
            entries: [review({
                id: 'inv-f', headerId: 'h-f', payee: 'Payee 1', amount: -420,
                investmentAction: null, derivedAction: null, ingestActionHint: 'buy',
                securityId: null, securityTicker: null, securityName: null, quantity: null, unitPrice: null,
                postingRole: null, categoryAccountId: SYSTEM_UNCATEGORIZED, categoryAccountName: 'Uncategorized',
                categoryAccountType: 'category',
            })],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        const hint = within(row).getByText('↗ Buy', { selector: 'span' });
        // Nothing here opens on a double-click, so the title names only what was detected.
        expect(hint).toHaveAttribute('title', 'Detected Buy from sync');
        expect(hint.parentElement).toHaveTextContent(/^↗ BuyUncategorized$/);
        expect(within(row).getByTitle('Uncategorized')).toHaveClass('bg-chip-neutral');
    });

    it('shows a row with no action and no hint with the register\'s neutral chip, then its Uncategorized', async () => {
        homeStub({
            accounts: accounts(0, 0, 1),
            entries: [review({
                id: 'inv-u', headerId: 'h-u', payee: 'Payee 1', amount: -420,
                investmentAction: null, derivedAction: null, ingestActionHint: null,
                securityId: null, securityTicker: null, securityName: null, quantity: null, unitPrice: null,
                postingRole: null, categoryAccountId: SYSTEM_UNCATEGORIZED, categoryAccountName: 'Uncategorized',
                categoryAccountType: 'category',
            })],
        });
        renderHome();

        // The row's payee is the anchor: the cell is drawn by then.
        const row = await rowOf('Payee 1');
        const unset = within(row).getByTitle('No action');
        expect(unset).toHaveTextContent(/^—$/);
        expect(unset.parentElement).toHaveTextContent(/^—Uncategorized$/);
    });
});

describe('a row rounded at import in To review', () => {
    // ADR-0114 D15, the Review marker (board RV-1a): an account whose currency has no
    // places, and feed rows whose bank figure had more.
    const YEN = 'acct-yen';
    const withYen = (count: number): AccountSummary[] => [
        ...accounts(0, 0),
        account({ id: YEN, name: 'Bank Y', accountType: 'bank', currencyCode: 'JPY', needsReviewCount: count }),
    ];
    const onYen = (n: number, amount: number, over: Partial<BankRow> = {}) =>
        txnEntry(flagged(n, YEN, 11 - n, amount, over), 'JPY');

    it('shows the bank\'s figure as sent on a muted line of its own, after the amount', async () => {
        homeStub({
            accounts: withYen(2),
            entries: [
                onYen(1, 1235, { originalAmount: '1234.5', roundedAtImport: true }),
                onYen(2, -4820),
            ],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        const note = within(row).getByText('Rounded from 1,234.5 as sent by the bank');
        expect(note).toHaveClass('text-right', 'text-[0.6875rem]', 'text-text-muted');
        // Its own line, last in the row, ending under the amount: an invisible copy of
        // the row's actions holds it there.
        const line = note.parentElement!;
        expect(line).toHaveClass('order-8', 'basis-full', 'justify-end');
        const spacer = line.lastElementChild as HTMLElement;
        expect(spacer).toHaveAttribute('aria-hidden', 'true');
        expect(spacer).toHaveClass('invisible', 'h-0');
        expect(spacer).toHaveTextContent('Review…');
        // After the amount in reading order, with no currency symbol of its own.
        const amount = within(row).getByText(formatCurrency(1235, 'JPY'));
        expect(amount.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(note.textContent).not.toMatch(/[¥$]|JPY/);
        // The other row is unchanged.
        expect(within(await rowOf('Payee 2')).queryByText(/^Rounded from/)).toBeNull();
    });

    it('keeps a trailing zero and the sign of the figure as sent', async () => {
        homeStub({
            accounts: withYen(1),
            entries: [onYen(1, -1235, { originalAmount: '-1234.50', roundedAtImport: true })],
        });
        renderHome();

        expect(within(await rowOf('Payee 1')).getByText('Rounded from -1,234.50 as sent by the bank')).toBeInTheDocument();
    });

    it('shows nothing for a rounded row no longer awaiting review, nor for one not rounded', async () => {
        const stub = homeStub({ accounts: withYen(2) });
        // Answered whatever the review flag: the widget, not the read, must leave the
        // accepted one unmarked.
        stub.on('GET', ENTRIES, {
            entries: [
                onYen(1, 1235, { originalAmount: '1234.5', roundedAtImport: true, needsReview: false }),
                onYen(2, 1235, { originalAmount: '1235', roundedAtImport: false }),
            ],
            hasMore: false,
        });
        renderHome();

        // Each row's payee is the anchor: the row is drawn by then.
        for (const payee of ['Payee 1', 'Payee 2']) {
            const row = await rowOf(payee);
            expect(within(row).getByText(formatCurrency(1235, 'JPY'))).toBeInTheDocument();
            expect(within(row).queryByText(/Rounded from/)).toBeNull();
        }
    });
});

describe('a row held for a cleared figure in To review', () => {
    // ADR-0114 D15: a promote-on-clear held the row because it does not hold the bank's
    // cleared figure.
    it('says a matched transaction cleared at another figure, in place of the row\'s own rounding', async () => {
        homeStub({
            accounts: accounts(0, 1),
            entries: [
                // A feed row rounded at its own import, approved, then held because a
                // pending row merged into it cleared at another figure.
                txnEntry(flagged(1, CHECKING, 11, -42.51, {
                    originalAmount: '-42.505', roundedAtImport: true,
                    reviewClearedAmount: '-45.00', reviewClearedByMatch: true, isMergeWinner: true,
                })),
            ],
        });
        renderHome();

        const row = await rowOf('Payee 1');
        const note = within(row).getByText('The bank cleared a matched transaction at -45.00');
        expect(note).toHaveClass('text-right', 'text-[0.6875rem]', 'text-text-muted');
        expect(note.parentElement!).toHaveClass('order-8', 'basis-full', 'justify-end');
        expect(note.textContent).not.toMatch(/\$|USD/);
        expect(within(row).queryByText(/^Rounded from/)).toBeNull();
    });

    it('says a split cleared at another figure, with what its lines add up to', async () => {
        const line = (n: number, amount: number) => flagged(n, CHECKING, 11, amount, {
            id: `leg-s${n}`, headerId: 'h-s', payee: 'Payee 1', legIndex: n, txnGroupId: 'h-s',
            headerAccountNetAmount: -100, accountPostingsOnHeader: 2,
            originalAmount: '-105.00', reviewClearedAmount: '-105.00', reviewClearedByMatch: false,
        });
        homeStub({ accounts: accounts(0, 1), entries: [splitEntry('h-s', [line(0, -60), line(1, -40)])] });
        renderHome();

        expect(within(await rowOf('Payee 1'))
            .getByText('The bank cleared this at -105.00; your lines add up to -100.00')).toBeInTheDocument();
    });

    it('gives the lines\' total only on the account the bank\'s figure was compared with', async () => {
        // The split's transfer line also lists under the account it moves money to: the
        // header's review flag is the whole transaction's. The bank's figure was compared
        // with the synced account's lines, so only that account's row has a total to give.
        const SAVINGS = 'acct-savings';
        const held = {
            headerId: 'h-s', payee: 'Payee 1', txnGroupId: 'h-s', headerTotalPostings: 2,
            originalAmount: '-105.00', reviewClearedAmount: '-105.00', reviewClearedByMatch: false,
        };
        const food = flagged(0, CHECKING, 11, -60, { ...held, id: 'leg-s0', legIndex: 0, headerAccountNetAmount: -100, accountPostingsOnHeader: 2 });
        const transfer = flagged(1, CHECKING, 11, -40, {
            ...held, id: 'leg-s1', legIndex: 1, headerAccountNetAmount: -100, accountPostingsOnHeader: 2,
            counterpartyAccountId: SAVINGS, counterpartyAccountName: 'Savings A', counterpartyAccountType: 'bank',
        });
        // The far side: a line of a transaction entered from the synced account.
        const savingsLine = flagged(2, SAVINGS, 11, 40, {
            ...held, id: 'leg-s2', legIndex: 1, isReadOnly: true, accountPostingsOnHeader: 1,
            counterpartyAccountId: CHECKING, counterpartyAccountName: 'Account A', counterpartyAccountType: 'bank',
        });
        homeStub({
            accounts: [
                ...accounts(0, 1),
                account({ id: SAVINGS, name: 'Savings A', accountType: 'bank', needsReviewCount: 1 }),
            ],
            entries: [splitEntry('h-s', [food, transfer]), txnEntry(savingsLine)],
        });
        renderHome();

        const onAccount = async (name: string) => within(await rowList()).getAllByRole('listitem')
            .find((li) => within(li).queryByText(name, { selector: `span[title="${name}"]` }) !== null)!;
        expect(within(await onAccount('Account A'))
            .getByText('The bank cleared this at -105.00; your lines add up to -100.00')).toBeInTheDocument();
        const far = await onAccount('Savings A');
        expect(within(far).getByText(formatCurrency(40, 'USD'))).toBeInTheDocument();
        expect(within(far).getByText('The bank cleared this at -105.00')).toBeInTheDocument();
        expect(within(far).queryByText(/add up to/)).toBeNull();
    });

    it('says nothing once the row no longer awaits review', async () => {
        const stub = homeStub({ accounts: accounts(0, 1) });
        stub.on('GET', ENTRIES, {
            entries: [txnEntry(flagged(1, CHECKING, 11, -42.5, {
                needsReview: false, reviewClearedAmount: '-45.00', reviewClearedByMatch: true,
            }))],
            hasMore: false,
        });
        renderHome();

        const row = await rowOf('Payee 1');
        expect(within(row).getByText(formatCurrency(-42.5, 'USD'))).toBeInTheDocument();
        expect(within(row).queryByText(/^The bank cleared/)).toBeNull();
    });
});
