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

import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import type { AccountDetail, AccountSummary } from '@/lib/types';

import { AccountEditorDialog } from './AccountEditorDialog';

/**
 * A currency change refused for a transfer whose two amounts would differ in one currency
 * (`relabel-pair-not-representable`, ADR-0114 D13). When the transfer is on a row merged
 * into another, which no register shows, the refusal names each transaction it was merged
 * into (`mergedInto`), and the editor links each under the refusal's text to its register,
 * at it (decided by the owner, 2026-11-18), in the Hidden view for a hidden one (settled
 * by Claude, 2026-11-18). Without
 * the member, the refusal's text stands alone, as before. Mocked at `fetch`; every
 * account, payee and figure is invented.
 */

const LEDGER = '00000000-0000-0000-0000-000000000010';
const ACCOUNT = '00000000-0000-0000-0000-000000000020';
const WINNER_ACCOUNT = '00000000-0000-0000-0000-000000000021';
const WINNER = '00000000-0000-0000-0000-000000000030';
const HIDDEN_WINNER = '00000000-0000-0000-0000-000000000031';
const ONE = new RegExp(`/accounts/${ACCOUNT}$`);
const CODE = 'relabel-pair-not-representable';
const DETAIL = 'A transfer between two currencies would end up in EUR with two amounts that differ, '
    + 'which one currency cannot hold. That transfer was merged into another transaction, so no register shows it.';
const LIVE_DETAIL = 'A transfer between two currencies would end up in EUR with two amounts that differ, '
    + 'which one currency cannot hold. Make its two amounts match first.';

const summary: AccountSummary = {
    id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Account A', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false, institutionName: null,
};
const detail: AccountDetail = {
    id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Account A', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    institutionName: null, accountNumber: null, routingNumber: null,
    accountUrl: null, notes: null, openingBalance: 0, openedOn: null, taxStatus: null,
    loanTerms: null, managedReminder: null,
};

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub.on('GET', ONE, detail);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderEditor() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const page = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => (
            <AccountEditorDialog ledgerId={LEDGER} account={summary} onClose={vi.fn()} onSaved={vi.fn()} />
        ),
    });
    const router = createRouter({
        routeTree: root.addChildren([page]),
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

/** Change the account's currency to EUR and save, which the stubbed PATCH refuses. */
async function saveInEur() {
    const user = userEvent.setup();
    renderEditor();
    // The details are read before Save is offered.
    await waitFor(() => expect(stub.callsTo('GET', ONE).length).toBe(1));
    await user.selectOptions(await screen.findByLabelText('Currency'), 'EUR');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    return screen.findByRole('alert');
}

describe('the account editor\'s refusal of a currency change over a merged-away transfer', () => {
    it('links each transaction the transfer was merged into, a hidden one in the Hidden view', async () => {
        stub.on('PATCH', ONE, problem(422, DETAIL, CODE, {
            count: 2,
            mergedInto: [
                { headerId: WINNER, accountId: WINNER_ACCOUNT, account: 'Account B', date: '2026-03-02', payee: 'Payee A', hidden: false },
                { headerId: HIDDEN_WINNER, accountId: ACCOUNT, account: 'Account A', date: '2026-03-04', payee: null, hidden: true },
            ],
        }));

        const alert = await saveInEur();

        expect(alert.textContent).toContain(DETAIL);
        expect(alert.textContent).toContain('Merged into:');
        const links = within(alert).getAllByRole('link');
        expect(links.map((l) => [l.textContent, l.getAttribute('href')])).toEqual([
            [`Account B · ${formatLedgerDate('2026-03-02')} · Payee A`,
                `/ledgers/${LEDGER}/accounts/${WINNER_ACCOUNT}?focus=${WINNER}`],
            [`Account A · ${formatLedgerDate('2026-03-04')}`,
                `/ledgers/${LEDGER}/accounts/${ACCOUNT}?focus=${HIDDEN_WINNER}&status=hidden`],
        ]);
    });

    it('shows the refusal\'s text alone when it names no merged transaction', async () => {
        stub.on('PATCH', ONE, problem(422, LIVE_DETAIL, CODE, { count: 1 }));

        const alert = await saveInEur();

        expect(alert.textContent).toBe(LIVE_DETAIL);
        expect(within(alert).queryAllByRole('link')).toEqual([]);
    });
});
