import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { AccountEditorDialog } from './AccountEditorDialog';
import * as apiModule from '@/lib/api';
import type { AccountSummary, AccountDetail } from '@/lib/types';

vi.mock('@/lib/api', () => ({
    ApiError: class ApiError extends Error {
        status: number; detail: string;
        constructor(status: number, detail: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail;
        }
    },
    createAccount: vi.fn(),
    updateAccount: vi.fn(),
    fetchAccount: vi.fn(),
    fetchAccounts: vi.fn(),
    loanPaymentPreview: vi.fn(),
    setupPaymentReminder: vi.fn(),
}));

const createAccount = vi.mocked(apiModule.createAccount);
const updateAccount = vi.mocked(apiModule.updateAccount);
const fetchAccount = vi.mocked(apiModule.fetchAccount);
const fetchAccounts = vi.mocked(apiModule.fetchAccounts);
const loanPaymentPreview = vi.mocked(apiModule.loanPaymentPreview);
const LEDGER = 'ledger-1';

const bank: AccountSummary = {
    id: 'a1', ledgerId: LEDGER, parentId: null, name: 'Checking', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false, institutionName: 'Bank X',
};

const bankDetail: AccountDetail = {
    id: 'a1', ledgerId: LEDGER, parentId: null, name: 'Checking', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    institutionName: 'Bank X', accountNumber: '1234', routingNumber: '5678',
    accountUrl: null, notes: null, openingBalance: 0, openedOn: null, taxStatus: null, loanTerms: null,
    managedReminder: null,
};

const loanSummary: AccountSummary = {
    id: 'loan1', ledgerId: LEDGER, parentId: null, name: 'Mortgage', accountType: 'loan',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false, institutionName: null,
};

const loanDetail = (managedReminder: AccountDetail['managedReminder']): AccountDetail => ({
    ...bankDetail,
    id: 'loan1', name: 'Mortgage', accountType: 'loan', institutionName: null,
    loanTerms: {
        originalPrincipal: 500000, annualInterestRate: 4, points: 0, paymentCount: 360,
        paymentsPerYear: 12, firstPaymentDate: '2013-06-17', escrowAmount: 500,
        interestAccountId: null, escrowAccountId: null, paymentIsComputed: true, fixedPayment: null,
    },
    managedReminder,
});

function renderDialog(account: AccountSummary | null) {
    const onClose = vi.fn();
    const onSaved = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <AccountEditorDialog ledgerId={LEDGER} account={account} onClose={onClose} onSaved={onSaved} />
        </QueryClientProvider>,
    );
    return { onClose, onSaved, qc };
}

beforeEach(() => {
    vi.clearAllMocks();
    createAccount.mockResolvedValue(bank);
    updateAccount.mockResolvedValue(undefined);
    fetchAccount.mockResolvedValue(bankDetail);
    fetchAccounts.mockResolvedValue([]);
    loanPaymentPreview.mockResolvedValue({ periodicPayment: 2387.1, escrowAmount: 500.00, totalPayment: 2887.1 });
});
afterEach(() => { vi.restoreAllMocks(); });

describe('AccountEditorDialog', () => {
    it('create: posts the typed general attributes (no detail fetch)', async () => {
        const { onSaved, onClose } = renderDialog(null);
        await userEvent.type(screen.getByLabelText('Name'), 'Savings');
        await userEvent.click(screen.getByRole('button', { name: 'Create' }));
        await waitFor(() => {
            expect(createAccount).toHaveBeenCalledWith(LEDGER, expect.objectContaining({
                name: 'Savings', accountType: 'bank', currencyCode: 'USD', categoryKind: null,
            }));
        });
        await waitFor(() => { expect(onSaved).toHaveBeenCalled(); expect(onClose).toHaveBeenCalled(); });
        expect(fetchAccount).not.toHaveBeenCalled();
    });

    it('edit: fetches detail to prefill, locks Type, and Save patches the changed name', async () => {
        renderDialog(bank);
        expect(screen.getByLabelText('Name')).toHaveValue('Checking');
        expect(screen.getByLabelText('Type')).toBeDisabled();              // immutable after create
        await waitFor(() => expect(fetchAccount).toHaveBeenCalledWith(LEDGER, 'a1'));
        // metadata prefilled from the detail fetch + Save unlocked
        await waitFor(() => expect(screen.getByLabelText('Account number')).toHaveValue('1234'));
        await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());

        const name = screen.getByLabelText('Name');
        await userEvent.clear(name);
        await userEvent.type(name, 'Renamed Checking');
        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => {
            expect(updateAccount).toHaveBeenCalledWith(LEDGER, 'a1', expect.objectContaining({
                name: 'Renamed Checking',
            }));
        });
        expect(createAccount).not.toHaveBeenCalled();
    });

    it('edit: Save invalidates the singular account-detail key so a reopen re-seeds fresh', async () => {
        const { qc } = renderDialog(bank);
        await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
        // Spy AFTER mount so only the save's invalidations are captured.
        const spy = vi.spyOn(qc, 'invalidateQueries');

        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(updateAccount).toHaveBeenCalled());

        // The dialog prefills from ['account', ledgerId, id] via a capture-once
        // useEffect; the save must invalidate THAT key (not just the plural list
        // through invalidateLedgerRegister), or a reopen within the cache window
        // re-seeds stale metadata / loan terms.
        const keys = spy.mock.calls.map((c) => c[0]?.queryKey);
        expect(keys).toContainEqual(['account', LEDGER, 'a1']);
    });

    it('blocks save with a blank name and surfaces the error', async () => {
        renderDialog(null);
        await userEvent.click(screen.getByRole('button', { name: 'Create' }));
        expect(await screen.findByText('Name is required.')).toBeInTheDocument();
        expect(createAccount).not.toHaveBeenCalled();
    });

    it('loan: requires terms, then posts the loan terms on create', async () => {
        renderDialog(null);
        await userEvent.type(screen.getByLabelText('Name'), 'Mortgage');
        await userEvent.selectOptions(screen.getByLabelText('Type'), 'loan');

        // Incomplete terms → blocked before any POST.
        await userEvent.click(screen.getByRole('button', { name: 'Create' }));
        expect(await screen.findByText(/Loan terms are incomplete/)).toBeInTheDocument();
        expect(createAccount).not.toHaveBeenCalled();

        // Fill the required term fields (Payments / year defaults to 12).
        await userEvent.type(screen.getByLabelText('Original principal'), '500000');
        await userEvent.type(screen.getByLabelText('Annual rate %'), '4.00');
        await userEvent.type(screen.getByLabelText('Term (payments)'), '360');
        await userEvent.click(screen.getByRole('button', { name: 'Create' }));

        await waitFor(() => {
            expect(createAccount).toHaveBeenCalledWith(LEDGER, expect.objectContaining({
                accountType: 'loan',
                loanTerms: expect.objectContaining({
                    originalPrincipal: 500000, annualInterestRate: 4,
                    paymentCount: 360, paymentsPerYear: 12, paymentIsComputed: true,
                }),
            }));
        });
    });

    it('edit loan: shows the managed reminder cadence + badge when one exists', async () => {
        fetchAccount.mockResolvedValue(loanDetail({
            reminderId: 'r1', rrule: 'FREQ=MONTHLY;BYMONTHDAY=13', nextDue: '2026-08-13',
        }));
        renderDialog(loanSummary);
        expect(await screen.findByText(/Monthly \(day 13\)/)).toBeInTheDocument();
        expect(screen.getByText('Managed')).toBeInTheDocument();
        expect(
            screen.queryByRole('button', { name: /Set up scheduled payment/ }),
        ).not.toBeInTheDocument();
    });

    it('edit loan: offers to set up a scheduled payment when none exists', async () => {
        fetchAccount.mockResolvedValue(loanDetail(null));
        renderDialog(loanSummary);
        expect(
            await screen.findByRole('button', { name: /Set up scheduled payment/ }),
        ).toBeInTheDocument();
    });

    it('loan: shows the server-computed payment preview', async () => {
        renderDialog(null);
        await userEvent.selectOptions(screen.getByLabelText('Type'), 'loan');
        await userEvent.type(screen.getByLabelText('Original principal'), '500000');
        await userEvent.type(screen.getByLabelText('Annual rate %'), '4.00');
        await userEvent.type(screen.getByLabelText('Term (payments)'), '360');

        await waitFor(() => expect(loanPaymentPreview).toHaveBeenCalled());
        expect(await screen.findByText(/Estimated payment/)).toBeInTheDocument();
    });
});
