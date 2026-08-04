import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ReminderEditorDialog } from './ReminderEditorDialog';
import * as apiModule from '@/lib/api';
import type { AccountSummary, ReminderDetail } from '@/lib/types';

// ADR-0051 slice B. This is a UNIT test of the dialog's ORCHESTRATION: it picks
// the kind from the source account, embeds the right editor, maps the editor's
// emitted transaction + the schedule into one create/edit call, and fires
// onSaved. The heavy children (TxnRowEdit / InvestmentTxnRowEdit /
// RecurrenceBuilder / AccountCategoryPicker) are stubbed so the test exercises
// the wiring, not the editors themselves.

// --- API surface (whole module replaced) --------------------------------
vi.mock('@/lib/api', () => ({
    ApiError: class ApiError extends Error {
        status: number; detail: string;
        constructor(status: number, detail: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail;
        }
    },
    fetchAccounts: vi.fn(),
    fetchPayees: vi.fn(),
    fetchReminderDetail: vi.fn(),
    createReminderBank: vi.fn(),
    createReminderInvestment: vi.fn(),
    updateReminderBank: vi.fn(),
    updateReminderInvestment: vi.fn(),
}));

// --- Embedded editor stubs ----------------------------------------------
// Bank editor: on click, emit a representative CreateTransactionRequest.
vi.mock('@/routes/ledgers/TxnRowEdit', () => ({
    TxnRowEdit: (props: {
        onSaveCreate: (body: {
            sourceAccountId: string;
            postings: { counterpartyAccountId: string; amount: number; legMemo: string | null }[];
            payee: string | null; memo: string | null; checkNumber: string | null; postedAt: string;
        }) => void;
    }) => (
        <button
            data-testid="bank-editor-save"
            onClick={() => props.onSaveCreate({
                sourceAccountId: 'acct-bank',
                postings: [{ counterpartyAccountId: 'cat-1', amount: -50, legMemo: null }],
                payee: 'Rent', memo: null, checkNumber: null, postedAt: '2026-07-01',
            })}
        >bank-save</button>
    ),
}));

// Investment editor: on click, emit a minimal CreateInvestmentTransactionRequest
// via the `fire`-mode onSubmit (the dialog wires onInvestmentSubmit to it).
vi.mock('@/routes/ledgers/investment-edit/InvestmentTxnRowEdit', () => ({
    InvestmentTxnRowEdit: (props: {
        mode: { onSubmit: (req: { brokerageAccountId: string; action: string; postedAt: string }) => void };
    }) => (
        <button
            data-testid="inv-editor-save"
            onClick={() => props.mode.onSubmit({
                brokerageAccountId: 'acct-inv', action: 'buy', postedAt: '2026-07-01',
            })}
        >inv-save</button>
    ),
}));

// RecurrenceBuilder: no-op view, but re-export the REAL defaultSchedule /
// ScheduleValue so the dialog's default schedule (and thus buildRrule) is real.
vi.mock('./RecurrenceBuilder', async () => {
    const actual = await vi.importActual<typeof import('./RecurrenceBuilder')>('./RecurrenceBuilder');
    return { ...actual, RecurrenceBuilder: () => <div data-testid="recurrence-builder" /> };
});

// AccountCategoryPicker: two buttons so a test can drive either source kind.
vi.mock('@/components/register/AccountCategoryPicker', () => ({
    AccountCategoryPicker: (props: { onChangeId: (id: string | null) => void }) => (
        <div>
            <button data-testid="pick-bank" onClick={() => props.onChangeId('acct-bank')}>pick-bank</button>
            <button data-testid="pick-inv" onClick={() => props.onChangeId('acct-inv')}>pick-inv</button>
        </div>
    ),
}));

const fetchAccounts = vi.mocked(apiModule.fetchAccounts);
const fetchPayees = vi.mocked(apiModule.fetchPayees);
const fetchReminderDetail = vi.mocked(apiModule.fetchReminderDetail);
const createReminderBank = vi.mocked(apiModule.createReminderBank);
const createReminderInvestment = vi.mocked(apiModule.createReminderInvestment);
const updateReminderBank = vi.mocked(apiModule.updateReminderBank);
const updateReminderInvestment = vi.mocked(apiModule.updateReminderInvestment);

const LEDGER = 'ledger-1';

const baseAccount = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false, institutionName: null,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const accounts: AccountSummary[] = [
    { ...baseAccount, id: 'acct-bank', name: 'Checking', accountType: 'bank' },
    { ...baseAccount, id: 'acct-inv', name: 'Brokerage', accountType: 'investment' },
    { ...baseAccount, id: 'cat-1', name: 'Rent', accountType: 'category', categoryKind: 'expense' },
];

const bankDetail: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Rent', memo: null, checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-07-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: 'acct-bank',
    legs: [
        {
            accountId: 'acct-bank', accountName: 'Checking', postingIndex: 0, amount: -50,
            legMemo: null, securityId: null, securityTicker: null, quantity: null,
            unitPrice: null, postingRole: null,
        },
        {
            accountId: 'cat-1', accountName: 'Rent', postingIndex: 0, amount: 50,
            legMemo: null, securityId: null, securityTicker: null, quantity: null,
            unitPrice: null, postingRole: null,
        },
    ],
};

function renderDialog(props: { reminderId?: string | null } = {}) {
    const onClose = vi.fn();
    const onSaved = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <ReminderEditorDialog
                ledgerId={LEDGER}
                reminderId={props.reminderId ?? null}
                onClose={onClose}
                onSaved={onSaved}
            />
        </QueryClientProvider>,
    );
    return { onClose, onSaved };
}

beforeEach(() => {
    vi.clearAllMocks();
    fetchAccounts.mockResolvedValue(accounts);
    fetchPayees.mockResolvedValue([]);
    fetchReminderDetail.mockResolvedValue(bankDetail);
    createReminderBank.mockResolvedValue(undefined as never);
    createReminderInvestment.mockResolvedValue(undefined as never);
    updateReminderBank.mockResolvedValue(undefined as never);
    updateReminderInvestment.mockResolvedValue(undefined as never);
});
afterEach(() => { vi.restoreAllMocks(); });

describe('ReminderEditorDialog', () => {
    it('create: shows the picker first; the editor + title account appear only after a source is picked', async () => {
        renderDialog();

        // New-reminder shell + picker; no detail fetch on create.
        expect(await screen.findByText('New reminder')).toBeInTheDocument();
        expect(await screen.findByTestId('pick-bank')).toBeInTheDocument();
        expect(fetchReminderDetail).not.toHaveBeenCalled();

        // Until a source is picked, neither the schedule nor the editor shows.
        expect(screen.queryByTestId('recurrence-builder')).not.toBeInTheDocument();
        expect(screen.queryByTestId('bank-editor-save')).not.toBeInTheDocument();

        await userEvent.click(screen.getByTestId('pick-bank'));

        // Source picked → bank editor + schedule mount; the title names the account.
        expect(await screen.findByTestId('bank-editor-save')).toBeInTheDocument();
        expect(screen.getByTestId('recurrence-builder')).toBeInTheDocument();
        expect(screen.getByText('Checking')).toBeInTheDocument();
    });

    it('create bank save: maps editor body + schedule into createReminderBank and fires onSaved', async () => {
        const { onSaved } = renderDialog();

        await userEvent.click(await screen.findByTestId('pick-bank'));
        await userEvent.click(await screen.findByTestId('bank-editor-save'));

        await waitFor(() => expect(createReminderBank).toHaveBeenCalledTimes(1));
        const [ledgerArg, body] = createReminderBank.mock.calls[0];
        expect(ledgerArg).toBe(LEDGER);
        expect(typeof body.rrule).toBe('string');
        expect(body.rrule.length).toBeGreaterThan(0);
        expect(body.sourceAccountId).toBe('acct-bank');
        expect(body.postings).toHaveLength(1);

        await waitFor(() => expect(onSaved).toHaveBeenCalled());
        expect(updateReminderBank).not.toHaveBeenCalled();
    });

    it('edit: fetches the detail, shows the edit title + account, and Save patches (not creates)', async () => {
        renderDialog({ reminderId: 'r1' });

        await waitFor(() => expect(fetchReminderDetail).toHaveBeenCalledWith(LEDGER, 'r1'));
        expect(await screen.findByText('Edit reminder')).toBeInTheDocument();
        expect(await screen.findByText('Checking')).toBeInTheDocument();

        // No source picker on edit (the series fixes the source).
        expect(screen.queryByTestId('pick-bank')).not.toBeInTheDocument();

        await userEvent.click(await screen.findByTestId('bank-editor-save'));

        await waitFor(() => expect(updateReminderBank).toHaveBeenCalledTimes(1));
        const [ledgerArg, idArg] = updateReminderBank.mock.calls[0];
        expect(ledgerArg).toBe(LEDGER);
        expect(idArg).toBe('r1');
        expect(createReminderBank).not.toHaveBeenCalled();
    });

    it('investment: picking an investment source mounts the investment editor and saves via createReminderInvestment', async () => {
        const { onSaved } = renderDialog();

        await userEvent.click(await screen.findByTestId('pick-inv'));

        // The investment editor replaces the bank one (kind derived from acct type).
        expect(await screen.findByTestId('inv-editor-save')).toBeInTheDocument();
        expect(screen.queryByTestId('bank-editor-save')).not.toBeInTheDocument();
        expect(screen.getByText('Brokerage')).toBeInTheDocument();

        await userEvent.click(screen.getByTestId('inv-editor-save'));

        await waitFor(() => expect(createReminderInvestment).toHaveBeenCalledTimes(1));
        const [ledgerArg, body] = createReminderInvestment.mock.calls[0];
        expect(ledgerArg).toBe(LEDGER);
        expect(typeof body.rrule).toBe('string');
        expect(body.transaction.brokerageAccountId).toBe('acct-inv');
        await waitFor(() => expect(onSaved).toHaveBeenCalled());
        expect(createReminderBank).not.toHaveBeenCalled();
    });
});
