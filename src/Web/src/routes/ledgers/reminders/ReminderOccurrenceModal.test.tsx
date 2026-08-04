import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';
import * as apiModule from '@/lib/api';
import type { UpcomingOccurrence, ReminderDetail } from '@/lib/types';

vi.mock('@/lib/api', () => ({
    ApiError: class ApiError extends Error {
        status: number; detail: string; code?: string;
        constructor(status: number, detail: string, code?: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail; this.code = code;
        }
    },
    fetchAccounts: vi.fn(),
    fetchPayees: vi.fn(),
    fetchReminderDetail: vi.fn(),
    fireReminderBank: vi.fn(),
    fireReminderInvestment: vi.fn(),
    skipReminder: vi.fn(),
}));
vi.mock('@/lib/accountPath', () => ({ buildAccountPathMap: () => new Map() }));
vi.mock('@/routes/ledgers/register/bank/columns', () => ({ BANK_COLS: 'c' }));
vi.mock('@/routes/ledgers/register/investment/columns', () => ({ INVESTMENT_REGISTER_COLS: 'c', INVESTMENT_FORM_COLS: 'c' }));

// Stub the heavy live editors — expose the modal-wired callbacks (onSaveCreate /
// mode.onSubmit) + the footerLeading slot (where Skip lives) so we can drive them.
vi.mock('@/routes/ledgers/TxnRowEdit', () => ({
    TxnRowEdit: ({ onSaveCreate, footerLeading }: {
        onSaveCreate: (b: unknown) => void; footerLeading: React.ReactNode;
    }) => (
        <div data-testid="bank-editor">
            {footerLeading}
            <button type="button" onClick={() => onSaveCreate({
                sourceAccountId: 'src',
                postings: [{ counterpartyAccountId: 'cat', amount: -90.5 }],
                payee: 'Edited', memo: null, checkNumber: null,
                postedAt: '2026-08-02T00:00:00.000Z',
            })}>bank-post</button>
        </div>
    ),
}));
vi.mock('@/routes/ledgers/investment-edit/InvestmentTxnRowEdit', () => ({
    InvestmentTxnRowEdit: ({ mode, footerLeading }: {
        mode: { onSubmit: (r: unknown) => void }; footerLeading: React.ReactNode;
    }) => (
        <div data-testid="inv-editor">
            {footerLeading}
            <button type="button" onClick={() => mode.onSubmit({
                brokerageAccountId: 'brok', action: 'buy', securityId: 'sec', shares: 12, price: 100,
            })}>inv-post</button>
        </div>
    ),
}));

const fetchAccounts = vi.mocked(apiModule.fetchAccounts);
const fetchPayees = vi.mocked(apiModule.fetchPayees);
const fetchReminderDetail = vi.mocked(apiModule.fetchReminderDetail);
const fireReminderBank = vi.mocked(apiModule.fireReminderBank);
const fireReminderInvestment = vi.mocked(apiModule.fireReminderInvestment);
const skipReminder = vi.mocked(apiModule.skipReminder);

const LEDGER = 'ledger-1';

const occ: UpcomingOccurrence = {
    date: '2026-08-02', kind: 'reminder', reminderId: 'r1', headerId: null,
    payee: 'Verizon', memo: null, amount: -84.99, seriesNextDue: '2026-05-02',
};

const baseDetail: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Verizon', memo: null, checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=2', startDate: '2026-01-02', endDate: null,
    nextDueDate: '2026-05-02', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: 'src',
    legs: [
        { accountId: 'src', accountName: 'Checking', postingIndex: 0, amount: -84.99, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'cat', accountName: 'Internet', postingIndex: 0, amount: 84.99, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
    ],
};
const investmentDetail: ReminderDetail = {
    ...baseDetail, kind: 'investment', action: 'buy', sourceAccountId: 'brok',
    legs: [
        { accountId: 'hold', accountName: 'Holdings', postingIndex: 0, amount: 1200, legMemo: null, securityId: 'sec', securityTicker: 'TST', quantity: 12, unitPrice: 100, postingRole: 'security' },
        { accountId: 'brok', accountName: 'Brokerage', postingIndex: 0, amount: -1200, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: 'security' },
    ],
};

const loanDetail: ReminderDetail = {
    ...baseDetail, isLoanReminder: true, payee: 'Mortgage', sourceAccountId: 'chk',
    legs: [
        { accountId: 'chk', accountName: 'Checking', postingIndex: 0, amount: -1629.93, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'loan', accountName: 'Mortgage', postingIndex: 0, amount: 1629.93, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'chk', accountName: 'Checking', postingIndex: 1, amount: -1389.31, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'int', accountName: 'Interest', postingIndex: 1, amount: 1389.31, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'chk', accountName: 'Checking', postingIndex: 2, amount: -1221.20, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { accountId: 'esc', accountName: 'Escrow', postingIndex: 2, amount: 1221.20, legMemo: null, securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
    ],
};
const loanAccounts = [
    { id: 'chk', currencyCode: 'USD', name: 'Checking' },
    { id: 'loan', name: 'Mortgage' }, { id: 'int', name: 'Interest' }, { id: 'esc', name: 'Escrow' },
] as never;

function renderModal() {
    const onClose = vi.fn();
    const onActed = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <ReminderOccurrenceModal ledgerId={LEDGER} occ={occ} onClose={onClose} onActed={onActed} />
        </QueryClientProvider>,
    );
    return { onClose, onActed };
}

beforeEach(() => {
    vi.clearAllMocks();
    // Minimal accounts (currency + trade-commission lookups); paths are mocked.
    fetchAccounts.mockResolvedValue([
        { id: 'src', currencyCode: 'USD' }, { id: 'brok', isTradeCommission: false },
    ] as never);
    fetchPayees.mockResolvedValue([]);
    fetchReminderDetail.mockResolvedValue(baseDetail);
    fireReminderBank.mockResolvedValue({ headerId: 'h1', skippedEarlierCount: 3, skippedEarlierFrom: '2026-05-02' });
    fireReminderInvestment.mockResolvedValue({ headerId: 'h2', skippedEarlierCount: 0, skippedEarlierFrom: null });
    skipReminder.mockResolvedValue({ occurrenceDate: '2026-08-02', nextDueDate: null, skippedEarlierCount: 0, skippedEarlierFrom: null });
});

afterEach(() => { vi.restoreAllMocks(); });

describe('ReminderOccurrenceModal', () => {
    it('shows the catch-up warning when earlier occurrences are un-acted', async () => {
        renderModal();
        expect(await screen.findByText(/Posting or skipping also marks earlier un-acted/)).toBeInTheDocument();
    });

    it('bank: Post commits the edited transaction via /fire/bank + reports the catch-up notice', async () => {
        const { onClose, onActed } = renderModal();
        await userEvent.click(await screen.findByRole('button', { name: 'bank-post' }));
        await waitFor(() => {
            expect(fireReminderBank).toHaveBeenCalledWith(LEDGER, 'r1', expect.objectContaining({
                occurrenceDate: '2026-08-02',
                sourceAccountId: 'src',
                payee: 'Edited',
                postedDate: '2026-08-02',
            }));
        });
        await waitFor(() => {
            expect(onActed).toHaveBeenCalledWith(expect.stringMatching(/Also marked 3 earlier/));
            expect(onClose).toHaveBeenCalled();
        });
    });

    it('investment: Post commits via /fire/investment', async () => {
        fetchReminderDetail.mockResolvedValue(investmentDetail);
        renderModal();
        await userEvent.click(await screen.findByRole('button', { name: 'inv-post' }));
        await waitFor(() => {
            expect(fireReminderInvestment).toHaveBeenCalledWith(LEDGER, 'r1', expect.objectContaining({
                occurrenceDate: '2026-08-02',
            }));
        });
    });

    it('managed loan reminder: shows a read-only computed split, not the editable editor', async () => {
        fetchReminderDetail.mockResolvedValue(loanDetail);
        fetchAccounts.mockResolvedValue(loanAccounts);
        renderModal();
        expect(await screen.findByText(/Managed loan payment/)).toBeInTheDocument();
        // The editable bank editor is NOT rendered for a managed reminder.
        expect(screen.queryByTestId('bank-editor')).not.toBeInTheDocument();
        // Read-only split rows (counterparty names).
        expect(screen.getByText('Mortgage')).toBeInTheDocument();
        expect(screen.getByText('Interest')).toBeInTheDocument();
        expect(screen.getByText('Escrow')).toBeInTheDocument();
    });

    it('managed loan reminder: Post fires via /fire/bank', async () => {
        fetchReminderDetail.mockResolvedValue(loanDetail);
        fetchAccounts.mockResolvedValue(loanAccounts);
        renderModal();
        await userEvent.click(await screen.findByRole('button', { name: 'Post' }));
        await waitFor(() => {
            expect(fireReminderBank).toHaveBeenCalledWith(LEDGER, 'r1', expect.objectContaining({
                occurrenceDate: '2026-08-02',
                sourceAccountId: 'chk',
            }));
        });
    });

    it('Skip (in the editor footer slot) calls /skip', async () => {
        renderModal();
        const editor = await screen.findByTestId('bank-editor');
        await userEvent.click(within(editor).getByRole('button', { name: 'Skip this occurrence' }));
        await waitFor(() => {
            expect(skipReminder).toHaveBeenCalledWith(LEDGER, 'r1', { occurrenceDate: '2026-08-02' });
        });
    });
});
