import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { stubFetch } from '@/lib/annotations/fetchStub';
import type { AccountSummary } from '@/lib/types';

import { AccountsManagementPage } from './AccountsManagementPage';
import { LEDGER_ID, renderInRouter, seedRole, testQueryClient } from '../structure/structureTestFixtures';

// The Accounts tab's `?edit=<id>` link (ADR-0114 D15): the consistency check's advisories
// open an account's editor from it, for an opening balance, a loan's terms or a currency.
// The server is mocked at fetch; the editor is stood in for, so the test reads which
// account it was opened on and that closing it leaves the link.

vi.mock('./AccountEditorDialog', () => ({
    AccountEditorDialog: ({ account, onClose }: { account: AccountSummary | null; onClose: () => void }) => (
        <div role="dialog">
            {`editor:${account ? account.name : 'new'}`}
            <button type="button" onClick={onClose}>Close editor</button>
        </div>
    ),
}));

function acct(over: Partial<AccountSummary>): AccountSummary {
    return {
        id: 'a1', ledgerId: LEDGER_ID, parentId: null, name: 'Account A', accountType: 'bank',
        categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
        feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
        isTradeCommission: false, institutionName: null, ...over,
    };
}

const ALL: AccountSummary[] = [
    acct({ id: 'a1', name: 'Account A' }),
    acct({ id: 'old', name: 'Account Old', isActive: false }),
    acct({ id: 'loan', name: 'Loan A', accountType: 'loan' }),
    acct({ id: 'sys', name: 'Account Sys', isSystem: true }),
    acct({ id: 'cat', name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
];

function serve() {
    return stubFetch().on('GET', /\/accounts$/, (call: { search: URLSearchParams }) =>
        (call.search.get('includeInactive') === 'true' ? ALL : ALL.filter((a) => a.isActive)));
}

function renderAt(edit: string, role = 'owner') {
    const queryClient = testQueryClient();
    seedRole(queryClient, role);
    renderInRouter(<AccountsManagementPage />, queryClient, {
        path: '/ledgers/$ledgerId/accounts',
        location: `/ledgers/${LEDGER_ID}/accounts?edit=${edit}`,
    });
}

afterEach(() => {
    vi.unstubAllGlobals();
});

describe('AccountsManagementPage: ?edit=<id>', () => {
    it.each([
        ['an active account', 'a1', 'Account A'],
        ['an inactive one, which the list hides', 'old', 'Account Old'],
        ['a loan', 'loan', 'Loan A'],
    ])('opens the editor on %s', async (_what, id, name) => {
        const stub = serve();
        renderAt(id);

        expect(await screen.findByRole('dialog')).toHaveTextContent(`editor:${name}`);
        expect(stub.unrouted).toEqual([]);
    });

    it('closes for good: closing the editor clears the link', async () => {
        serve();
        renderAt('a1');
        await userEvent.click(await screen.findByRole('button', { name: 'Close editor' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        // The list is still there, and nothing reopens it.
        expect(await screen.findByText('Account A')).toBeInTheDocument();
        expect(screen.queryByRole('dialog')).toBeNull();
    });

    it.each([
        ['an id that names no account', 'nope', 'owner'],
        ['a system account', 'sys', 'owner'],
        ['a category, which this tab does not edit', 'cat', 'owner'],
        ['a viewer', 'a1', 'viewer'],
    ])('opens nothing for %s', async (_what, id, role) => {
        serve();
        renderAt(id, role);

        // The anchor: the list with inactive accounts shown is drawn from the read the
        // link looks the account up in, so the lookup has answered by then.
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Show inactive' }));
        expect(await screen.findByText('Account Old')).toBeInTheDocument();
        expect(screen.queryByRole('dialog')).toBeNull();
    });
});
