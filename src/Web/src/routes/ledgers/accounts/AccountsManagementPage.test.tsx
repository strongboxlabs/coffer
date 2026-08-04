import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AccountsManagementPage } from './AccountsManagementPage';
import * as apiModule from '@/lib/api';
import type { AccountSummary } from '@/lib/types';

vi.mock('@tanstack/react-router', () => ({
    Link: ({ children }: { children: React.ReactNode }) => <a>{children}</a>,
    useParams: () => ({ ledgerId: 'ledger-1' }),
}));
vi.mock('@/components/ui/SidebarLayout', () => ({
    MainArea: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    MainPane: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    TopBar: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('@/lib/api', () => ({
    ApiError: class ApiError extends Error {},
    fetchAccounts: vi.fn(),
    fetchVisibleLedgers: vi.fn(),
}));
vi.mock('./AccountEditorDialog', () => ({
    AccountEditorDialog: ({ account }: { account: AccountSummary | null }) => (
        <div role="dialog">{`editor:${account ? account.name : 'new'}`}</div>
    ),
}));

const fetchAccounts = vi.mocked(apiModule.fetchAccounts);
const fetchVisibleLedgers = vi.mocked(apiModule.fetchVisibleLedgers);

function acct(over: Partial<AccountSummary>): AccountSummary {
    return {
        id: 'a1', ledgerId: 'ledger-1', parentId: null, name: 'Checking', accountType: 'bank',
        categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
        feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
        isTradeCommission: false, institutionName: null, ...over,
    };
}

const ALL: AccountSummary[] = [
    acct({ id: 'a1', name: 'Checking', accountType: 'bank' }),
    acct({ id: 'sys', name: 'Reserved', accountType: 'bank', isSystem: true }),
    acct({ id: 'old', name: 'Old Savings', accountType: 'bank', isActive: false }),
    acct({ id: 'cat', name: 'Groceries', accountType: 'category', categoryKind: 'expense' }),
];

function renderPage() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <AccountsManagementPage />
        </QueryClientProvider>,
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    fetchVisibleLedgers.mockResolvedValue([{ id: 'ledger-1', name: 'My Ledger', role: 'owner' }] as never);
    // Honor includeInactive so the "Show inactive" toggle is exercised.
    fetchAccounts.mockImplementation((_ledgerId: string, opts?: { includeInactive?: boolean }) =>
        Promise.resolve(opts?.includeInactive ? ALL : ALL.filter((a) => a.isActive)));
});
afterEach(() => { vi.restoreAllMocks(); });

describe('AccountsManagementPage', () => {
    it('groups by type, excludes categories, and gives system accounts no Edit', async () => {
        renderPage();
        expect(await screen.findByText('Checking')).toBeInTheDocument();
        expect(screen.getByText(/^Banking/)).toBeInTheDocument();     // group header (with count)
        expect(screen.getByText('Reserved')).toBeInTheDocument();     // system account shown...
        expect(screen.getByText('System')).toBeInTheDocument();       // ...but read-only
        expect(screen.queryByText('Groceries')).not.toBeInTheDocument(); // categories excluded
        // Only the non-system, active account is editable; inactive is hidden by default.
        expect(screen.getAllByRole('button', { name: 'Edit' })).toHaveLength(1);
        expect(screen.queryByText('Old Savings')).not.toBeInTheDocument();
    });

    it('"Show inactive" refetches with includeInactive and reveals inactive accounts', async () => {
        renderPage();
        await screen.findByText('Checking');
        await userEvent.click(screen.getByLabelText('Show inactive'));
        await waitFor(() => {
            expect(fetchAccounts).toHaveBeenCalledWith('ledger-1', { includeInactive: true });
        });
        expect(await screen.findByText('Old Savings')).toBeInTheDocument();
    });

    it('opens the editor in create + edit modes', async () => {
        renderPage();
        await screen.findByText('Checking');
        await userEvent.click(screen.getByRole('button', { name: /New account/ }));
        expect(screen.getByRole('dialog')).toHaveTextContent('editor:new');
        await userEvent.click(screen.getByRole('button', { name: 'Edit' }));
        expect(screen.getByRole('dialog')).toHaveTextContent('editor:Checking');
    });
});
