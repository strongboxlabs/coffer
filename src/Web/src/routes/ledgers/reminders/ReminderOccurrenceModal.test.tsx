import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';
import * as apiModule from '@/lib/api';
import { formatSignedAmount } from '@/lib/money';
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

// Stub the heavy live editors — expose the modal-wired callbacks (onSaveCreate /
// mode.onSubmit), the footerLeading slot (where Skip lives) and the layout the
// modal asks for, so we can drive and read them.
vi.mock('@/routes/ledgers/TxnRowEdit', () => ({
    TxnRowEdit: ({ onSaveCreate, footerLeading, layout, currency }: {
        onSaveCreate: (b: unknown) => void; footerLeading: React.ReactNode; layout?: string; currency: string;
    }) => (
        <div data-testid="bank-editor" data-layout={layout} data-currency={currency}>
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
    InvestmentTxnRowEdit: ({ mode, footerLeading, layout }: {
        mode: { onSubmit: (r: unknown) => void }; footerLeading: React.ReactNode; layout?: string;
    }) => (
        <div data-testid="inv-editor" data-layout={layout}>
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
    payee: 'Internet Provider', memo: null, amount: -84.99, seriesNextDue: '2026-05-02',
};

const baseDetail: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Internet Provider', memo: null, tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=2', startDate: '2026-01-02', endDate: null,
    nextDueDate: '2026-05-02', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: 'src',
    legs: [
        { legId: 'l-src-0', accountId: 'src', accountName: 'Checking', postingIndex: 0, amount: -84.99, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-cat-0', accountId: 'cat', accountName: 'Internet', postingIndex: 0, amount: 84.99, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
    ],
};
const investmentDetail: ReminderDetail = {
    ...baseDetail, kind: 'investment', action: 'buy', sourceAccountId: 'brok',
    legs: [
        { legId: 'l-hold-0', accountId: 'hold', accountName: 'Holdings', postingIndex: 0, amount: 1200, legMemo: null, legTags: [], securityId: 'sec', securityTicker: 'TST', quantity: 12, unitPrice: 100, postingRole: 'security' },
        { legId: 'l-brok-0', accountId: 'brok', accountName: 'Brokerage', postingIndex: 0, amount: -1200, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: 'security' },
    ],
};

const loanDetail: ReminderDetail = {
    ...baseDetail, isLoanReminder: true, payee: 'Mortgage', sourceAccountId: 'chk',
    legs: [
        { legId: 'l-chk-0', accountId: 'chk', accountName: 'Checking', postingIndex: 0, amount: -1000.50, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-loan-0', accountId: 'loan', accountName: 'Mortgage', postingIndex: 0, amount: 1000.50, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-chk-1', accountId: 'chk', accountName: 'Checking', postingIndex: 1, amount: -800.25, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-int-1', accountId: 'int', accountName: 'Interest', postingIndex: 1, amount: 800.25, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-chk-2', accountId: 'chk', accountName: 'Checking', postingIndex: 2, amount: -300.75, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
        { legId: 'l-esc-2', accountId: 'esc', accountName: 'Escrow', postingIndex: 2, amount: 300.75, legMemo: null, legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null },
    ],
};
const loanAccounts = [
    { id: 'chk', currencyCode: 'USD', name: 'Checking' },
    { id: 'loan', name: 'Mortgage' }, { id: 'int', name: 'Interest' }, { id: 'esc', name: 'Escrow' },
] as never;

function renderModal(shown: UpcomingOccurrence = occ) {
    const onClose = vi.fn();
    const onActed = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    render(
        <QueryClientProvider client={queryClient}>
            <ReminderOccurrenceModal ledgerId={LEDGER} occ={shown} onClose={onClose} onActed={onActed} />
        </QueryClientProvider>,
    );
    return { onClose, onActed, invalidateSpy };
}

beforeEach(() => {
    vi.clearAllMocks();
    // Minimal accounts (currency + trade-commission lookups); paths are mocked.
    // Every account carries its currency, as the API's always does: the dialog
    // prints no figure in a currency it has not been told.
    fetchAccounts.mockResolvedValue([
        { id: 'src', currencyCode: 'USD' }, { id: 'brok', currencyCode: 'USD', isTradeCommission: false },
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

    it('hosts the bank editor in its dialog layout', async () => {
        renderModal();
        expect(await screen.findByTestId('bank-editor')).toHaveAttribute('data-layout', 'dialog');
    });

    it('hosts the investment editor in its dialog layout', async () => {
        fetchReminderDetail.mockResolvedValue(investmentDetail);
        renderModal();
        expect(await screen.findByTestId('inv-editor')).toHaveAttribute('data-layout', 'dialog');
    });

    it('puts the date, amount and account under the payee, as the subtitle', async () => {
        renderModal();
        const dialog = await screen.findByRole('dialog');
        const heading = within(dialog).getByRole('heading', { level: 2 });
        await screen.findByTestId('bank-editor');
        // The subtitle is the heading's one muted span; the title is the text before it.
        const subtitle = heading.querySelector('span.text-text-muted');
        expect(subtitle).not.toBeNull();
        expect(subtitle).toHaveTextContent(/2026 · -\$84\.99/);
        expect(heading.firstChild).toHaveTextContent('Internet Provider');
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

    it('managed loan reminder: the split is read for THIS occurrence, priced as of its date', async () => {
        // ADR-0078, 2026-10-21: the split is priced on the loan's balance as of the
        // payment's own date, so the dialog asks for the occurrence it shows.
        fetchReminderDetail.mockResolvedValue(loanDetail);
        fetchAccounts.mockResolvedValue(loanAccounts);
        renderModal();
        expect(await screen.findByText(/Managed loan payment/)).toBeInTheDocument();
        expect(fetchReminderDetail).toHaveBeenCalledWith(LEDGER, 'r1', '2026-08-02');
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

    it('managed loan reminder: Post refreshes every occurrence detail of the ledger', async () => {
        // A fired payment moves the balance a later occurrence's split is priced on
        // (ADR-0078), so no occurrence's cached preview may survive the fire.
        fetchReminderDetail.mockResolvedValue(loanDetail);
        fetchAccounts.mockResolvedValue(loanAccounts);
        const { invalidateSpy, onClose } = renderModal();
        await userEvent.click(await screen.findByRole('button', { name: 'Post' }));
        await waitFor(() => expect(onClose).toHaveBeenCalled());
        expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['reminders', 'detail', LEDGER] });
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

/** A literal string as a regular expression's source. */
const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

describe('ReminderOccurrenceModal: a non-USD source account', () => {
    const subtitleOf = async () => {
        const dialog = await screen.findByRole('dialog');
        await screen.findByTestId('bank-editor');
        return within(dialog).getByRole('heading', { level: 2 }).querySelector('span.text-text-muted')!;
    };

    it('formats the subtitle in the occurrence\'s currency and hands it to the editor', async () => {
        renderModal({ ...occ, sourceAccountId: 'src', sourceAccountName: 'Checking', currencyCode: 'EUR' });

        const subtitle = await subtitleOf();
        expect(subtitle).toHaveTextContent(formatSignedAmount(-84.99, 'EUR'));
        expect(subtitle.textContent).not.toContain('$');
        expect(screen.getByTestId('bank-editor')).toHaveAttribute('data-currency', 'EUR');
    });

    it('falls back to the source account\'s currency when the occurrence carries none', async () => {
        fetchAccounts.mockResolvedValue([{ id: 'src', currencyCode: 'EUR' }, { id: 'brok', isTradeCommission: false }] as never);
        renderModal();

        await waitFor(async () => expect(await subtitleOf()).toHaveTextContent(formatSignedAmount(-84.99, 'EUR')));
        expect(screen.getByTestId('bank-editor')).toHaveAttribute('data-currency', 'EUR');
    });

    it('formats a managed loan\'s split and total in the occurrence\'s currency', async () => {
        fetchReminderDetail.mockResolvedValue(loanDetail);
        fetchAccounts.mockResolvedValue(loanAccounts);
        renderModal({ ...occ, sourceAccountId: 'chk', currencyCode: 'EUR' });

        expect(await screen.findByText(/Managed loan payment/)).toBeInTheDocument();
        // Each line and the total, whichever side's sign the split is drawn with.
        const drawn = within(screen.getByRole('list')).getAllByRole('listitem')
            .map((li) => li.lastElementChild!.textContent);
        for (const amount of [1000.50, 800.25, 300.75, 2101.5]) {
            expect(drawn).toContainEqual(expect.stringMatching(
                new RegExp(`^(${escape(formatSignedAmount(amount, 'EUR'))}|${escape(formatSignedAmount(-amount, 'EUR'))})$`)));
        }
        expect(drawn.join(' ')).not.toContain('$');
    });
});
