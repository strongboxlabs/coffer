import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, waitFor, within, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { AccountsManagementPage } from './AccountsManagementPage';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';
import {
    LEDGER_ID,
    renderInRouter,
    seedRole,
    testQueryClient,
} from '../structure/structureTestFixtures';

// The Accounts tab of Structure (ADR-0050, ADR-0111): accounts grouped by type, on the same toolbar,
// row and menu as the other two tabs.

vi.mock('./AccountEditorDialog', () => ({
    AccountEditorDialog: ({ account }: { account: AccountSummary | null }) => (
        <div role="dialog">{`editor:${account ? account.name : 'new'}`}</div>
    ),
}));

function acct(over: Partial<AccountSummary>): AccountSummary {
    return {
        id: 'a1', ledgerId: LEDGER_ID, parentId: null, name: 'Checking', accountType: 'bank',
        categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
        feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
        isTradeCommission: false, institutionName: null, ...over,
    };
}

const ALL: AccountSummary[] = [
    acct({ id: 'a1', name: 'Checking', accountType: 'bank', institutionName: 'First Bank', needsReviewCount: 3 }),
    acct({ id: 'sys', name: 'Reserved', accountType: 'bank', isSystem: true }),
    acct({ id: 'old', name: 'Old Savings', accountType: 'bank', isActive: false }),
    acct({ id: 'card', name: 'Travel Card', accountType: 'credit_card', institutionName: 'Card Co' }),
    acct({ id: 'cat', name: 'Groceries', accountType: 'category', categoryKind: 'expense' }),
];

/** The tab at the path it has in the app, so it reads the ledger from the URL as it does there. */
const WHERE = { path: '/ledgers/$ledgerId/accounts', location: `/ledgers/${LEDGER_ID}/accounts` };

function renderPage({ role = 'owner', accounts = ALL }: { role?: string; accounts?: AccountSummary[] } = {}) {
    const fetchAccounts = vi.spyOn(apiModule, 'fetchAccounts').mockImplementation(
        (_ledgerId: string, opts?: { includeInactive?: boolean }) =>
            Promise.resolve(opts?.includeInactive ? accounts : accounts.filter((a) => a.isActive)));
    const queryClient = testQueryClient();
    seedRole(queryClient, role);
    renderInRouter(<AccountsManagementPage />, queryClient, WHERE);
    return { fetchAccounts, queryClient };
}

beforeEach(() => {
    vi.restoreAllMocks();
});

describe('AccountsManagementPage', () => {
    it('groups by type, excludes categories, and gives system accounts no actions', async () => {
        renderPage();
        expect(await screen.findByText('Checking')).toBeInTheDocument();
        expect(screen.getByText(/^Banking/)).toBeInTheDocument();     // group header (with count)
        // The same heading part as the category kinds: label, then the count in parentheses.
        expect(screen.getByRole('heading', { level: 3, name: /^Banking\s*\(2\)$/ })).toBeInTheDocument();
        expect(screen.getByText('Reserved')).toBeInTheDocument();     // system account shown...
        expect(screen.getByText('System')).toBeInTheDocument();       // ...but read-only
        expect(screen.queryByText('Groceries')).not.toBeInTheDocument(); // categories excluded
        // Only the non-system, active accounts have a menu; inactive is hidden by default.
        expect(await screen.findByRole('button', { name: 'Actions for Checking' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Actions for Travel Card' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for Reserved' })).not.toBeInTheDocument();
        expect(screen.queryByText('Old Savings')).not.toBeInTheDocument();
    });

    it('shows the institution and currency, and what is waiting to be reviewed', async () => {
        renderPage();
        const checking = (await screen.findByText('Checking')).closest('li')!;
        expect(within(checking).getByText('First Bank · USD')).toBeInTheDocument();
        expect(within(checking).getByText('3 to review')).toBeInTheDocument();
        // Nothing waiting says nothing, rather than "0 to review".
        const card = screen.getByText('Travel Card').closest('li')!;
        expect(within(card).queryByText(/to review/)).not.toBeInTheDocument();
    });

    it('names each account as a link to its register', async () => {
        renderPage();
        const link = await screen.findByRole('link', { name: 'Checking' });
        expect(link).toHaveAttribute('href', `/ledgers/${LEDGER_ID}/accounts/a1`);
        await userEvent.click(link);
        expect(await screen.findByText('register page')).toBeInTheDocument();
    });

    it('"Show inactive" refetches with includeInactive and reveals inactive accounts', async () => {
        const { fetchAccounts } = renderPage();
        await screen.findByText('Checking');
        await userEvent.click(screen.getByLabelText('Show inactive'));
        await waitFor(() => {
            expect(fetchAccounts).toHaveBeenCalledWith(LEDGER_ID, { includeInactive: true });
        });
        const old = (await screen.findByText('Old Savings')).closest('li')!;
        expect(within(old).getByText('Inactive')).toBeInTheDocument();
    });

    it('keeps the rows and the toolbar in place while "Show inactive" fetches the longer list', async () => {
        // The key changes when the box is ticked. Without the rows kept, the list blinks to a
        // skeleton and the checkbox the reader is standing on is disabled under them.
        let release: (rows: AccountSummary[]) => void = () => {};
        const fetchAccounts = vi.spyOn(apiModule, 'fetchAccounts').mockImplementation(
            (_ledgerId: string, opts?: { includeInactive?: boolean }) => opts?.includeInactive
                ? new Promise((resolve) => { release = resolve; })
                : Promise.resolve(ALL.filter((a) => a.isActive)));
        const queryClient = testQueryClient();
        seedRole(queryClient, 'owner');
        renderInRouter(<AccountsManagementPage />, queryClient, WHERE);
        await screen.findByText('Checking');

        const box = screen.getByLabelText('Show inactive');
        await userEvent.click(box);
        await waitFor(() => expect(fetchAccounts).toHaveBeenCalledWith(LEDGER_ID, { includeInactive: true }));

        // Mid-fetch: the same rows, the same enabled controls, no skeleton.
        expect(screen.getByText('Checking')).toBeInTheDocument();
        expect(screen.queryByLabelText('Loading accounts')).not.toBeInTheDocument();
        expect(box).toBeEnabled();
        expect(screen.getByRole('searchbox', { name: 'Search accounts' })).toBeEnabled();

        release(ALL);
        expect(await screen.findByText('Old Savings')).toBeInTheDocument();
    });

    it('opens the editor in create mode from the toolbar', async () => {
        renderPage();
        await screen.findByText('Checking');
        await userEvent.click(await screen.findByRole('button', { name: 'New account' }));
        expect(screen.getByRole('dialog')).toHaveTextContent('editor:new');
    });

    it('opens the editor in edit mode from a row menu', async () => {
        renderPage();
        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Checking' }));
        const items = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
        expect(items).toEqual(['Open register', 'Edit…', 'Deactivate']);
        await userEvent.click(screen.getByRole('menuitem', { name: 'Edit…' }));
        expect(screen.getByRole('dialog')).toHaveTextContent('editor:Checking');
    });

    it('hands focus back to the row’s button when the menu closes, so a keyboard reader keeps their place', async () => {
        renderPage();
        const kebab = await screen.findByRole('button', { name: 'Actions for Checking' });

        await userEvent.click(kebab);
        const menu = await screen.findByRole('menu', { name: 'Actions for Checking' });
        expect(menu).toHaveFocus();
        await userEvent.keyboard('{Escape}');
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
        expect(kebab).toHaveFocus();

        // And after a choice, the thing the choice opens inherits that place to return to.
        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Edit…' }));
        expect(kebab).toHaveFocus();
    });

    it('opens the same menu from a right-click', async () => {
        renderPage();
        fireEvent.contextMenu((await screen.findByText('Checking')).closest('li')!);
        expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent))
            .toEqual(['Open register', 'Edit…', 'Deactivate']);
    });

    it('asks first, then deactivates', async () => {
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        renderPage();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Checking' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Deactivate' }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/leaves the pickers and the sidebar/)).toBeInTheDocument();
        expect(setActive).not.toHaveBeenCalled();
        await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'a1', false));
    });

    it('reactivates an inactive account from its menu without asking', async () => {
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        renderPage();
        await userEvent.click(await screen.findByLabelText('Show inactive'));
        await screen.findByText('Old Savings');

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Old Savings' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Reactivate' }));

        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'old', true));
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('says so when an account cannot be brought back, and clears it when the next try works', async () => {
        // Reactivating asks nothing, so there is no dialog to say it failed in.
        const setActive = vi.spyOn(apiModule, 'setAccountActive')
            .mockRejectedValueOnce(new apiModule.ApiError(422, 'That account is not in this ledger.', 'account-not-in-ledger'))
            .mockResolvedValue(undefined);
        renderPage();
        await userEvent.click(await screen.findByLabelText('Show inactive'));
        await screen.findByText('Old Savings');

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Old Savings' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Reactivate' }));
        expect(await screen.findByRole('alert')).toHaveTextContent('That account is not in this ledger.');

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Old Savings' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Reactivate' }));
        await waitFor(() => expect(setActive).toHaveBeenCalledTimes(2));
        await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    });

    it('narrows the list by name or by institution, and says so when nothing matches', async () => {
        renderPage();
        await screen.findByText('Checking');
        const search = screen.getByRole('searchbox', { name: 'Search accounts' });

        await userEvent.type(search, 'card co');
        expect(screen.getByText('Travel Card')).toBeInTheDocument();
        expect(screen.queryByText('Checking')).not.toBeInTheDocument();
        // A group with no match is gone with its heading.
        expect(screen.queryByText(/^Banking/)).not.toBeInTheDocument();

        await userEvent.clear(search);
        await userEvent.type(search, 'zzz');
        expect(await screen.findByText('Nothing matches “zzz”')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: 'Clear search' }));
        expect(await screen.findByText('Checking')).toBeInTheDocument();
    });

    it('invites the first account when the ledger has none', async () => {
        renderPage({ accounts: [acct({ id: 'cat', name: 'Groceries', accountType: 'category', categoryKind: 'expense' })] });
        // The ledger has categories and no real account: that is still an empty accounts list.
        expect(await screen.findByText('No accounts yet')).toBeInTheDocument();
        expect(screen.getAllByRole('button', { name: /New account/ })).toHaveLength(2);
    });

    it('says the list could not be read, and tries again', async () => {
        const fetchAccounts = vi.spyOn(apiModule, 'fetchAccounts')
            .mockRejectedValueOnce(new Error('offline'))
            .mockResolvedValue(ALL);
        const queryClient = testQueryClient();
        seedRole(queryClient, 'owner');
        renderInRouter(<AccountsManagementPage />, queryClient, WHERE);

        // Said as the other two tabs say it: the server's words, or the failure's own.
        expect(await screen.findByRole('alert')).toHaveTextContent('offline');
        await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
        expect(await screen.findByText('Checking')).toBeInTheDocument();
        expect(fetchAccounts).toHaveBeenCalledTimes(2);
    });

    it('draws a busy placeholder while the list is on its way', async () => {
        vi.spyOn(apiModule, 'fetchAccounts').mockReturnValue(new Promise(() => {}));
        const queryClient = testQueryClient();
        seedRole(queryClient, 'owner');
        renderInRouter(<AccountsManagementPage />, queryClient, WHERE);

        expect(await screen.findByLabelText('Loading accounts')).toHaveAttribute('aria-busy', 'true');
    });

    it('offers a viewer the list and nothing to change it with', async () => {
        const { queryClient } = renderPage({ role: 'viewer' });
        expect(await screen.findByText('Checking')).toBeInTheDocument();
        // The role arrives with the ledger list. Wait for that read to have settled before
        // looking for the absence of a control, or the absence proves nothing.
        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('success'));
        expect(screen.queryByRole('button', { name: /New account/ })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for Checking' })).not.toBeInTheDocument();
        fireEvent.contextMenu(screen.getByText('Checking').closest('li')!);
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
        // A viewer still sees system rows labelled as such; the label is not an action.
        expect(screen.getByText('System')).toBeInTheDocument();
    });
});
