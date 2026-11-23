import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';

import { problem } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';

import {
    account,
    bankRow,
    CARD,
    CHECKING,
    homeStub,
    investmentRow,
    layoutWith,
    LEDGER,
    renderHome,
    splitEntry,
    TODAY,
    txnEntry,
} from '../homeTestFixtures';

// Recent transactions through the real page, API client and router, with the
// server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const BROKERAGE = 'acct-brokerage';

const ACCOUNTS = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: CARD, name: 'Card A', accountType: 'credit_card' }),
    account({ id: BROKERAGE, name: 'Brokerage A', accountType: 'investment' }),
    account({ id: 'cat-a', name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
];

const shown = (settings: Record<string, string | number> = {}) =>
    layoutWith({ 'recent-transactions': { visible: true, settings } });

const panel = () => screen.findByRole('heading', { level: 2, name: /^Recent transactions/ })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

/** The row's link, found by its payee: the whole row is the link. */
const rowOf = async (payee: string) =>
    (await within(await panel()).findByText(payee)).closest('a') as HTMLAnchorElement;

describe('Recent transactions', () => {
    it('asks for the entries up to today, in its direction and its rows, never for the review flag', async () => {
        const stub = homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ rows: 5, direction: 'outflow' }),
            entries: [txnEntry(bankRow({ id: 'leg-1', headerId: 'h-1', payee: 'Payee A', amount: -12 }))],
        });
        renderHome();

        expect(await rowOf('Payee A')).toBeInTheDocument();
        const call = stub.callsTo('GET', /\/transactions\/entries$/)[0]!;
        expect(call.search.get('today')).toBe(TODAY);
        expect(call.search.get('direction')).toBe('outflow');
        expect(call.search.get('limit')).toBe('5');
        expect(call.search.has('needs_review')).toBe(false);
        expect(within(await panel()).getByRole('heading', { level: 2 })).toHaveTextContent('Recent transactions · outflows');
    });

    it.each([
        ['all', 'all accounts'],
        ['inflow', 'inflows'],
    ])('names its direction %s in the head', async (direction, words) => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ direction }),
            entries: [txnEntry(bankRow({ id: 'leg-1', headerId: 'h-1', payee: 'Payee A', amount: 12 }))],
        });
        renderHome();

        expect(await rowOf('Payee A')).toBeInTheDocument();
        expect(within(await panel()).getByRole('heading', { level: 2 })).toHaveTextContent(`Recent transactions · ${words}`);
    });

    it('holds the height of its rows setting while the entries load', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ rows: 20 }) });
        // The entries read never answers; every other read does.
        const answered = globalThis.fetch;
        vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
            String(input).includes('/transactions/entries') ? new Promise<Response>(() => {}) : answered(input, init));
        renderHome();

        const loading = await within(await panel()).findByLabelText('Loading recent transactions');
        expect(loading.children).toHaveLength(20);
    });

    it('opens each row\'s own register at the entry, as its own account', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                // One transfer, an entry on each account it touches.
                txnEntry(bankRow({ id: 'leg-out', headerId: 'h-xfer', accountId: CHECKING, payee: 'Payee T', amount: -40,
                    counterpartyAccountId: CARD, counterpartyAccountName: 'Card A', counterpartyAccountType: 'credit_card' })),
                txnEntry(bankRow({ id: 'leg-in', headerId: 'h-xfer', accountId: CARD, payee: 'Payee T', amount: 40,
                    counterpartyAccountId: CHECKING, counterpartyAccountName: 'Account A', counterpartyAccountType: 'bank' })),
            ],
        });
        renderHome();

        const links = await waitFor(() => {
            const found = within(screen.getByRole('list', { name: 'Recent transactions' })).getAllByRole('link');
            expect(found).toHaveLength(2);
            return found;
        });
        expect(links[0]).toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/${CHECKING}?focus=h-xfer`);
        expect(links[1]).toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/${CARD}?focus=h-xfer`);
        // Each names its own account: the row is that account's register row.
        expect(within(links[0]!).getByText('Account A', { selector: `span[title="Account A"]` })).toBeInTheDocument();
        expect(within(links[1]!).getByText('Card A', { selector: `span[title="Card A"]` })).toBeInTheDocument();
        // The whole row is the link, so nothing inside it is a control of its own.
        expect(within(links[0]!).queryByRole('button')).toBeNull();
    });

    it('shows each row\'s status as the register resolves it, pending included', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                txnEntry(bankRow({ id: 'leg-p', headerId: 'h-p', payee: 'Payee P', isPending: true })),
                txnEntry(bankRow({ id: 'leg-c', headerId: 'h-c', payee: 'Payee C', status: 'cleared' })),
                txnEntry(bankRow({ id: 'leg-u', headerId: 'h-u', payee: 'Payee U' })),
            ],
        });
        renderHome();

        expect(within(await rowOf('Payee P')).getByRole('img', { name: 'Pending' })).toBeInTheDocument();
        expect(within(await rowOf('Payee C')).getByRole('img', { name: 'Cleared' })).toBeInTheDocument();
        expect(within(await rowOf('Payee U')).getByRole('img', { name: 'Uncleared' })).toBeInTheDocument();
    });

    it('draws the category cell as each register does: the chip, a split\'s summary, an investment event\'s category', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                txnEntry(bankRow({ id: 'leg-b', headerId: 'h-b', payee: 'Payee B' })),
                splitEntry('g-s', [
                    bankRow({ id: 'leg-s1', headerId: 'h-s', payee: 'Payee S', amount: -30, txnGroupId: 'g-s', headerAccountNetAmount: -50 }),
                    bankRow({ id: 'leg-s2', headerId: 'h-s', payee: 'Payee S', amount: -20, txnGroupId: 'g-s', legIndex: 1, headerAccountNetAmount: -50 }),
                ]),
                txnEntry(investmentRow({ id: 'inv-d', headerId: 'h-d', accountId: BROKERAGE, payee: 'Payee D', amount: 25,
                    investmentAction: 'dividend_cash', derivedAction: 'dividend_cash', postingRole: 'income',
                    categoryAccountId: 'cat-div', categoryAccountName: 'Category Div', categoryAccountType: 'category' })),
            ],
        });
        renderHome();

        expect(within(await rowOf('Payee B')).getByText('Category A')).toBeInTheDocument();
        const split = await rowOf('Payee S');
        expect(within(split).getByText('2 splits')).toBeInTheDocument();
        // The split's net on its account, as its collapsed register row shows it.
        expect(within(split).getByText('-$50.00')).toBeInTheDocument();
        // Its summary is text: nothing expands on the home screen.
        expect(within(split).queryByRole('button')).toBeNull();
        expect(within(await rowOf('Payee D')).getByText('Category Div')).toBeInTheDocument();
    });

    it('shows a transfer between currencies with the other leg\'s amount after its chip, as the register does', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                txnEntry(bankRow({ id: 'leg-x', headerId: 'h-x', payee: 'Payee X', amount: -40,
                    counterpartyAccountId: CARD, counterpartyAccountName: 'Card A', counterpartyAccountType: 'credit_card',
                    counterpartyAmount: 36, counterpartyCurrencyCode: 'EUR' })),
                txnEntry(bankRow({ id: 'leg-v', headerId: 'h-v', payee: 'Payee V', amount: -40,
                    counterpartyAccountId: CARD, counterpartyAccountName: 'Card A', counterpartyAccountType: 'credit_card' })),
                txnEntry(investmentRow({ id: 'inv-x', headerId: 'h-ix', accountId: BROKERAGE, payee: 'Payee Y', amount: 40,
                    investmentAction: 'transfer', derivedAction: 'transfer', postingRole: 'transfer',
                    transferAccountId: CHECKING, transferAccountName: 'Account A', transferAccountType: 'bank',
                    transferAmount: -44, transferCurrencyCode: 'CAD' })),
            ],
        });
        renderHome();

        const other = within(await rowOf('Payee X')).getByText(formatCurrency(36, 'EUR'));
        expect(other).toHaveAttribute('title', `Received in Card A: ${formatCurrency(36, 'EUR')}`);
        expect(other).toHaveClass('text-text-muted');
        // The same transfer in one currency: the chip alone.
        const plain = await rowOf('Payee V');
        expect(within(plain).getByText('Card A', { selector: 'span[title="Card A"] span' })).toBeInTheDocument();
        expect(plain.querySelector('[data-other-leg]')).toBeNull();
        // An investment event's transfer chip, through the investment register's slot.
        expect(within(await rowOf('Payee Y')).getByText(formatCurrency(-44, 'CAD')))
            .toHaveAttribute('title', `Sent from Account A: ${formatCurrency(-44, 'CAD')}`);
    });

    it('marks a row awaiting review with the register\'s needs-review chrome, in words too', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                txnEntry(bankRow({ id: 'leg-r', headerId: 'h-r', payee: 'Payee R', needsReview: true, origin: 'feed', providerKey: 'simplefin' })),
                txnEntry(bankRow({ id: 'leg-n', headerId: 'h-n', payee: 'Payee N' })),
            ],
        });
        renderHome();

        const review = await rowOf('Payee R');
        expect(review).toHaveClass('bg-state-warning-soft/70');
        expect(review.style.boxShadow).toContain('inset 3px 0 0 0 var(--color-state-warning)');
        expect(within(review).getByText('Needs review')).toHaveClass('sr-only');

        // The reviewed row beside it carries none of it.
        const plain = await rowOf('Payee N');
        expect(plain).toHaveClass('hover:bg-surface-hover');
        expect(plain).not.toHaveClass('bg-state-warning-soft/70');
        expect(plain.style.boxShadow).toBe('');
        expect(within(plain).queryByText('Needs review')).toBeNull();
    });

    it('leaves the rounded-at-import note to To review, on a rounded row awaiting review', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [txnEntry(bankRow({
                id: 'leg-r', headerId: 'h-r', payee: 'Payee R', amount: 1235, needsReview: true,
                origin: 'online_import', providerKey: 'simplefin', originalAmount: '1234.5', roundedAtImport: true,
            }), 'JPY')],
        });
        renderHome();

        // The row's payee is the anchor: the row and its amount are drawn by then.
        const row = await rowOf('Payee R');
        expect(within(row).getByText(formatCurrency(1235, 'JPY'))).toBeInTheDocument();
        expect(within(row).queryByText(/Rounded from/)).toBeNull();
    });

    it('prints each amount in its own account\'s currency, red only when it is negative', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown(),
            entries: [
                txnEntry(bankRow({ id: 'leg-e', headerId: 'h-e', accountId: CARD, payee: 'Payee E', amount: -12 }), 'EUR'),
                txnEntry(bankRow({ id: 'leg-i', headerId: 'h-i', payee: 'Payee I', amount: 30 })),
            ],
        });
        renderHome();

        expect(within(await rowOf('Payee E')).getByText('-€12.00')).toHaveClass('text-state-danger');
        const income = within(await rowOf('Payee I')).getByText('$30.00');
        expect(income).toHaveClass('text-text');
        expect(income).not.toHaveClass('text-state-danger');
    });

    it('says when there is nothing yet, and where the registers are', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown(), entries: [] });
        renderHome();

        const body = await within(await panel()).findByText(/No transactions yet/);
        expect(within(body).getByRole('link', { name: 'Registers →' })).toHaveAttribute('href', `/ledgers/${LEDGER}/accounts`);
    });

    describe('an investment entry, drawn as the investment register draws it', () => {
        /** The investment cell: the action chip's parent holds the action, the
         *  security and the event's chips, in that order. */
        const cellOf = (row: HTMLElement, action: string) =>
            within(row).getByText(action, { selector: 'span' }).parentElement as HTMLElement;

        it('shows a zero-amount reinvestment\'s action, its security and what it settled', async () => {
            homeStub({
                accounts: ACCOUNTS,
                prefs: shown(),
                entries: [txnEntry(investmentRow({
                    id: 'inv-r', headerId: 'h-r', accountId: BROKERAGE, payee: 'Payee R', amount: 0,
                    investmentAction: 'dividend_reinvest', derivedAction: 'dividend_reinvest',
                    securityTicker: 'FNDA', securityName: 'Fund A', quantity: 2, unitPrice: 35, settledAmount: 70,
                }))],
            });
            renderHome();

            const row = await rowOf('Payee R');
            expect(cellOf(row, 'Reinvest')).toHaveTextContent(/^ReinvestFNDA · Fund A$/);
            expect(within(row).getByText('FNDA · Fund A')).toHaveAttribute('title', 'FNDA · Fund A');
            // The register's second line under a zero amount, after it.
            const amount = within(row).getByText('$0.00');
            const settled = within(row).getByText('settled $70.00');
            expect(amount.compareDocumentPosition(settled) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
            // Left out to keep the row on one line: the shares-at-price line.
            expect(within(row).queryByText(/sh @/)).toBeNull();
        });

        it('shows a buy\'s action and security, with no second line under an amount that says it', async () => {
            homeStub({
                accounts: ACCOUNTS,
                prefs: shown(),
                entries: [txnEntry(investmentRow({
                    id: 'inv-b', headerId: 'h-b', accountId: BROKERAGE, payee: 'Payee B', amount: -424.95,
                    securityTicker: 'FNDB', securityName: 'Fund B', quantity: 10, unitPrice: 42,
                    feeAmount: 4.95, feeCategoryId: 'cat-fee', feeCategoryName: 'Category Fee',
                }))],
            });
            renderHome();

            const row = await rowOf('Payee B');
            expect(cellOf(row, 'Buy')).toHaveTextContent(/^BuyFNDB · Fund B/);
            expect(within(row).getByText('-$424.95')).toHaveClass('text-state-danger');
            expect(within(row).queryByText(/^fee /)).toBeNull();
            expect(within(row).queryByText(/sh @/)).toBeNull();
        });

        it('shows a dividend\'s income chip after its action and security', async () => {
            homeStub({
                accounts: ACCOUNTS,
                prefs: shown(),
                entries: [txnEntry(investmentRow({
                    id: 'inv-d', headerId: 'h-d', accountId: BROKERAGE, payee: 'Payee D', amount: 31.5,
                    investmentAction: 'dividend_cash', derivedAction: 'dividend_cash', postingRole: 'income',
                    securityTicker: 'FNDA', securityName: 'Fund A', quantity: null, unitPrice: null,
                    categoryAccountId: 'cat-div', categoryAccountName: 'Category Div', categoryAccountType: 'category',
                }))],
            });
            renderHome();

            const row = await rowOf('Payee D');
            expect(cellOf(row, 'Dividend')).toHaveTextContent(/^DividendFNDA · Fund ACategory Div$/);
            expect(within(row).getByText('$31.50')).toHaveClass('text-text');
        });

        it('names a security without a ticker by its name alone', async () => {
            homeStub({
                accounts: ACCOUNTS,
                prefs: shown(),
                entries: [txnEntry(investmentRow({
                    id: 'inv-n', headerId: 'h-n', accountId: BROKERAGE, payee: 'Payee N',
                    securityTicker: null, securityName: 'Fund C',
                }))],
            });
            renderHome();

            const row = await rowOf('Payee N');
            expect(cellOf(row, 'Buy')).toHaveTextContent(/^BuyFund C$/);
        });
    });

    it('shows the error band, not its empty copy, when the read fails', async () => {
        const stub = homeStub({ accounts: ACCOUNTS, prefs: shown(), entries: [] });
        stub.on('GET', /\/transactions\/entries$/, problem(500, 'Something broke.'));
        renderHome();

        const alert = await within(await panel()).findByRole('alert');
        expect(alert).toHaveTextContent("Couldn't load recent transactions.");
        expect(within(await panel()).queryByText(/No transactions yet/)).toBeNull();
    });
});
