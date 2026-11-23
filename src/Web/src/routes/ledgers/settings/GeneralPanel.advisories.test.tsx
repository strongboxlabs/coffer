import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import type { LedgerConsistencyReport } from '@/lib/types';

import { GeneralPanel } from './GeneralPanel';

// Settings → General, the consistency check's parts ADR-0114 D15 adds, through the real
// API client with the server mocked at fetch: the Category amounts finding, repaired like
// every projection, and the two advisories, neutral, with no count and no button, each
// line linked to where its figure or currency is changed. Every ledger, account, payee
// and code here is invented.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const CHECK = /\/balances\/consistency$/;
const REPAIR = /\/balances\/consistency\/category_amounts\/repair$/;

let stub: FetchStub;

function serve(report: LedgerConsistencyReport) {
    stub
        .on('GET', /^\/api\/ledgers$/, [{ id: LEDGER_ID, name: 'Sample Ledger', role: 'owner', currencyCode: 'USD' }])
        .on('GET', /\/notifications\/events$/, [])
        .on('POST', CHECK, report);
}

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const settings = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <GeneralPanel ledgerId={LEDGER_ID} />,
    });
    const router = createRouter({
        routeTree: root.addChildren([settings]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

async function check() {
    await userEvent.click(await screen.findByRole('button', { name: /check consistency/i }));
}

/** An advisory's box, by its title. */
async function box(title: string) {
    return (await screen.findByText(title)).closest('div') as HTMLElement;
}

const HEALTHY_BALANCES = {
    projection: 'balances', healthy: true, checked: 40, mismatchedCount: 0, mismatches: [],
};

const CATEGORY_AMOUNTS = {
    projection: 'category_amounts', healthy: false, checked: 5, mismatchedCount: 2,
    mismatches: [
        {
            scope: 'Account A · Payee A', field: 'category line', stored: 12, expected: -10, diff: -22,
            accountId: 'acct-a', headerId: 'header-a',
        },
        {
            scope: 'Account A · Payee B', field: 'category line', stored: 0, expected: -8, diff: -8,
            accountId: 'acct-a', headerId: 'header-b',
        },
    ],
};

beforeEach(() => {
    stub = stubFetch();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('General: the Category amounts finding (ADR-0114 D15)', () => {
    it('is labelled, counted in category lines, says what its repair does, and repairs', async () => {
        const report: LedgerConsistencyReport = {
            healthy: false, projections: [HEALTHY_BALANCES, CATEGORY_AMOUNTS], unbackedPrices: [], oversold: [],
            unfitAmounts: [], refusedCurrencies: [],
        };
        serve(report);
        stub.on('POST', REPAIR, {
            projection: 'category_amounts',
            repaired: CATEGORY_AMOUNTS,
            notRepaired: [],
            after: { ...report, healthy: true, projections: [HEALTHY_BALANCES, { ...CATEGORY_AMOUNTS, healthy: true, mismatchedCount: 0, mismatches: [] }] },
        });
        renderPanel();
        await check();

        const title = await screen.findByText(/^Category amounts —/);
        expect(title.textContent).toBe('Category amounts — 2 of 5 category lines out of step');
        expect(screen.getByRole('link', { name: 'Account A · Payee A' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-a`);
        expect(screen.getByText(
            "This happens when a Moneydance import kept a category line's own figure where the account that paid it is in "
            + "another currency. Repair sets each line listed to the paying account's amount, negated, as every category "
            + "line now is. No account's balance changes; the category's totals then show what was paid.",
        )).toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Repair category amounts' }));

        expect(await screen.findByText(/^Category amounts — healthy/)).toBeInTheDocument();
        expect(stub.callsTo('POST', REPAIR)).toHaveLength(1);
        expect(stub.unrouted).toEqual([]);
    });

    it('counts one category line as one', async () => {
        serve({
            healthy: false,
            projections: [{ ...CATEGORY_AMOUNTS, checked: 1, mismatchedCount: 1, mismatches: [CATEGORY_AMOUNTS.mismatches[0]!] }],
            unbackedPrices: [], oversold: [], unfitAmounts: [], refusedCurrencies: [],
        });
        renderPanel();
        await check();

        expect((await screen.findByText(/^Category amounts —/)).textContent)
            .toBe('Category amounts — 1 of 1 category line out of step');
    });

    it('links a hidden line to its register\'s Hidden view, and names a reminder\'s line as text', async () => {
        // A hidden row is in its register's Hidden view alone (ADR-0072 D1). No register
        // shows a reminder's template, so the server sends its line with no ids.
        serve({
            healthy: false,
            projections: [{
                ...CATEGORY_AMOUNTS, checked: 3, mismatchedCount: 3,
                mismatches: [
                    { ...CATEGORY_AMOUNTS.mismatches[0]!, scope: 'Account A · Payee H', headerId: 'header-h', hidden: true },
                    { ...CATEGORY_AMOUNTS.mismatches[1]!, scope: 'Account A · Payee V', headerId: 'header-v', hidden: false },
                    {
                        scope: 'Payee R · reminder', field: 'category line', stored: 13, expected: 10, diff: -3,
                        accountId: null, headerId: null,
                    },
                ],
            }],
            unbackedPrices: [], oversold: [], unfitAmounts: [], refusedCurrencies: [],
        });
        renderPanel();
        await check();

        await screen.findByText(/^Category amounts —/);
        expect(screen.getByRole('link', { name: 'Account A · Payee H' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-h&status=hidden`);
        expect(screen.getByRole('link', { name: 'Account A · Payee V' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-v`);
        expect(screen.getByText(/^Payee R · reminder/).closest('a')).toBeNull();
        expect(screen.queryByRole('link', { name: /Payee R/ })).toBeNull();
    });
});

describe('General: Amounts with more decimals than their currency (ADR-0114 D15)', () => {
    it('lists each figure where it lives, linked to where it is edited, with no count and no button', async () => {
        serve({
            healthy: true, projections: [HEALTHY_BALANCES], unbackedPrices: [], oversold: [], refusedCurrencies: [],
            unfitAmounts: [
                {
                    kind: 'transaction', accountId: 'acct-a', account: 'Account A', currencyCode: 'JPY', places: 0,
                    headerId: 'header-a', date: '2026-03-04', payee: 'Payee A',
                },
                { kind: 'opening_balance', accountId: 'acct-b', account: 'Account B', currencyCode: 'JPY', places: 0 },
                { kind: 'loan_terms', accountId: 'loan-a', account: 'Loan A', currencyCode: 'KWD', places: 3 },
                {
                    kind: 'budget_target', accountId: 'cat-a', account: 'Category A', currencyCode: 'JPY', places: 0,
                    month: '2026-03-01',
                },
                {
                    kind: 'reminder', accountId: 'acct-a', account: 'Account A', currencyCode: 'JPY', places: 0,
                    reminderId: 'rem-a', payee: 'Payee R',
                },
                {
                    kind: 'transaction', accountId: 'cat-b', account: 'Category B', currencyCode: 'USD', places: 2,
                    headerId: 'header-c', date: '2026-02-01', payee: 'Payee C', reason: 'category-line-between-categories',
                },
                {
                    kind: 'transaction', accountId: 'acct-a', account: 'Account A', currencyCode: 'USD', places: 2,
                    headerId: 'header-d', date: '2026-02-02', payee: 'Payee D', reason: 'category-line-many-lines',
                },
            ],
        });
        renderPanel();
        await check();

        const advisory = await box('Amounts with more decimals than their currency');
        expect(advisory).toHaveClass('border-border', 'bg-surface-muted');
        expect(within(advisory).getByText(
            'These have more places than their currency uses. They are kept as they are. Editing one to fit its '
            + 'currency takes it off this list.',
        )).toBeInTheDocument();
        const links = within(advisory).getAllByRole('link');
        expect(links.slice(0, 5).map((a) => [a.textContent, a.getAttribute('href')])).toEqual([
            [`Account A · ${formatLedgerDate('2026-03-04')} · Payee A`, `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-a`],
            ['Account B · opening balance', `/ledgers/${LEDGER_ID}/accounts?edit=acct-b`],
            ['Loan A · loan terms', `/ledgers/${LEDGER_ID}/accounts?edit=loan-a`],
            ['Category A · March 2026 target', `/ledgers/${LEDGER_ID}/budget?month=2026-03`],
            ['Payee R · reminder', `/ledgers/${LEDGER_ID}/reminders?reminder=rem-a`],
        ]);
        // A category line the Category amounts repair cannot set is listed in this box, linked
        // where it is, with why after the link, muted (ADR-0114 D15). A line listed for its
        // places says nothing more.
        expect(links).toHaveLength(7);
        expect(links.slice(5).map((a) => [a.textContent, a.getAttribute('href')])).toEqual([
            [`Category B · ${formatLedgerDate('2026-02-01')} · Payee C`, `/ledgers/${LEDGER_ID}/accounts/cat-b?focus=header-c`],
            [`Account A · ${formatLedgerDate('2026-02-02')} · Payee D`, `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-d`],
        ]);
        const lines = within(advisory).getAllByRole('listitem');
        expect(lines.map((li) => li.textContent)).toEqual([
            ...links.slice(0, 5).map((a) => a.textContent),
            `Category B · ${formatLedgerDate('2026-02-01')} · Payee C · both lines are on categories, so Repair cannot set it`,
            `Account A · ${formatLedgerDate('2026-02-02')} · Payee D · more than one account or category line, so Repair cannot set it`,
        ]);
        expect(within(lines[5]!).getByText('· both lines are on categories, so Repair cannot set it'))
            .toHaveClass('text-text-muted');
        expect(within(lines[6]!).getByText('· more than one account or category line, so Repair cannot set it'))
            .toHaveClass('text-text-muted');
        // An advisory: no count in its title and nothing to press, and the verdict stays healthy.
        expect(within(advisory).queryByRole('button')).toBeNull();
        expect(within(advisory).getByText('Amounts with more decimals than their currency').textContent)
            .toBe('Amounts with more decimals than their currency');
        expect(screen.queryByRole('button', { name: /^Repair/ })).toBeNull();
        expect(screen.getByText(/^Running balances — healthy/)).toBeInTheDocument();
        expect(stub.unrouted).toEqual([]);
    });

    it('links a hidden transaction to its register\'s Hidden view, focused on it', async () => {
        // A hidden row is in its register's Hidden view alone (ADR-0072 D1): a link to the
        // default view would land on a register with no row to focus.
        serve({
            healthy: true, projections: [HEALTHY_BALANCES], unbackedPrices: [], oversold: [], refusedCurrencies: [],
            unfitAmounts: [
                {
                    kind: 'transaction', accountId: 'acct-a', account: 'Account A', currencyCode: 'JPY', places: 0,
                    headerId: 'header-h', date: '2026-03-04', payee: 'Payee H', hidden: true,
                },
                {
                    kind: 'transaction', accountId: 'acct-a', account: 'Account A', currencyCode: 'JPY', places: 0,
                    headerId: 'header-v', date: '2026-03-05', payee: 'Payee V', hidden: false,
                },
            ],
        });
        renderPanel();
        await check();

        const advisory = await box('Amounts with more decimals than their currency');
        expect(within(advisory).getAllByRole('link').map((a) => [a.textContent, a.getAttribute('href')])).toEqual([
            [`Account A · ${formatLedgerDate('2026-03-04')} · Payee H`,
                `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-h&status=hidden`],
            [`Account A · ${formatLedgerDate('2026-03-05')} · Payee V`,
                `/ledgers/${LEDGER_ID}/accounts/acct-a?focus=header-v`],
        ]);
    });
});

describe('General: Currencies Coffer no longer accepts (ADR-0114 D15)', () => {
    it('lists each account by its code, linked to its editor, and the reporting currency as text', async () => {
        serve({
            healthy: true, projections: [HEALTHY_BALANCES], unbackedPrices: [], oversold: [], unfitAmounts: [],
            refusedCurrencies: [
                { kind: 'account', accountId: 'acct-x', account: 'Account X', currencyCode: 'XQQ' },
                { kind: 'reporting', accountId: null, account: null, currencyCode: 'XQR' },
            ],
        });
        renderPanel();
        await check();

        const advisory = await box('Currencies Coffer no longer accepts');
        expect(advisory).toHaveClass('border-border', 'bg-surface-muted');
        expect(within(advisory).getByText(
            'These are kept in a code that is not an ISO 4217 currency Coffer accepts. They keep working at 2 decimals, '
            + "as before. Pick an accepted currency in the account's settings to change one; a new account cannot use "
            + 'these codes.',
        )).toBeInTheDocument();
        expect(within(advisory).getByRole('link', { name: 'Account X · XQQ' }))
            .toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts?edit=acct-x`);
        const reporting = within(advisory).getByText('Reporting currency · XQR');
        expect(reporting.closest('a')).toBeNull();
        expect(within(advisory).queryByRole('button')).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('General: a check with neither advisory', () => {
    it('draws neither box', async () => {
        serve({
            healthy: true, projections: [HEALTHY_BALANCES], unbackedPrices: [], oversold: [],
            unfitAmounts: [], refusedCurrencies: [],
        });
        renderPanel();
        await check();

        // The verdict is the anchor: the report is drawn by then.
        expect(await screen.findByText(/^Running balances — healthy/)).toBeInTheDocument();
        expect(screen.queryByText('Amounts with more decimals than their currency')).toBeNull();
        expect(screen.queryByText('Currencies Coffer no longer accepts')).toBeNull();
    });
});
