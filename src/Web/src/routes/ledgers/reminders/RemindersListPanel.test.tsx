import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RemindersListPanel } from './RemindersListPanel';
import * as apiModule from '@/lib/api';
import { formatSignedAmount } from '@/lib/money';
import type { ReminderSummary } from '@/lib/types';

// Mock the API barrel; the panel's formatting (money/dates/recurrence) comes
// from other modules and runs for real, so the humanized recurrence + signed
// amount are exercised end-to-end.
vi.mock('@/lib/api', () => ({
    fetchReminders: vi.fn(),
    setReminderActive: vi.fn(),
    skipReminder: vi.fn(),
    ApiError: class ApiError extends Error {
        status: number; detail: string; code?: string;
        constructor(status: number, detail: string, code?: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail; this.code = code;
        }
    },
}));

const fetchReminders = vi.mocked(apiModule.fetchReminders);
const setReminderActive = vi.mocked(apiModule.setReminderActive);

const LEDGER = 'ledger-1';

const RENT: ReminderSummary = {
    id: 'r1', payee: 'Rent', memo: null, amount: -1500,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-07-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', currencyCode: 'USD',
};

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    render(
        <QueryClientProvider client={queryClient}>
            <RemindersListPanel ledgerId={LEDGER} canEdit />
        </QueryClientProvider>,
    );
    return { invalidateSpy };
}

beforeEach(() => {
    vi.clearAllMocks();
    fetchReminders.mockResolvedValue([RENT]);
    setReminderActive.mockResolvedValue(undefined);
});

describe('RemindersListPanel', () => {
    it('renders a series row with humanized recurrence and signed amount', async () => {
        renderPanel();
        expect(await screen.findByText('Rent')).toBeInTheDocument();
        expect(screen.getByText((t) => t.includes('Monthly on the 1st'))).toBeInTheDocument();
        expect(screen.getByText((t) => t.includes('1,500'))).toBeInTheDocument();
    });

    it('shows an unposted amount muted, never red or green (ADR-0023 §H)', async () => {
        fetchReminders.mockResolvedValue([
            RENT,
            { ...RENT, id: 'r2', payee: 'Payee B', amount: 2480 },
        ]);
        renderPanel();
        await screen.findByText('Payee B');
        for (const text of ['-$1,500.00', '$2,480.00']) {
            const cls = screen.getByText(text).className;
            expect(cls).toContain('text-text-muted');
            expect(cls).not.toContain('state-danger');
            expect(cls).not.toContain('state-success');
        }
    });

    it('prints each series amount in its source account\'s currency, and none for a series with no source', async () => {
        fetchReminders.mockResolvedValue([
            { ...RENT, currencyCode: 'EUR' },
            { ...RENT, id: 'r2', payee: 'Payee B', amount: 0, currencyCode: null },
        ]);
        renderPanel();
        const rent = (await screen.findByText('Rent')).closest('li')!;
        expect(within(rent).getByText(formatSignedAmount(-1500, 'EUR'))).toBeInTheDocument();
        // No source, so no currency: a dash, not a figure in dollars.
        const other = screen.getByText('Payee B').closest('li')!;
        expect(within(other).getByText('—')).toBeInTheDocument();
        expect(screen.getByRole('list').textContent).not.toContain('$');
    });

    it('marks an estimating series\' amount "≈", with an estimate or before it has one, and a fixed amount not', async () => {
        fetchReminders.mockResolvedValue([
            { ...RENT, id: 'made', payee: 'Payee Made', amount: -50,
                estimate: { requestedSampleCount: 3, availableSampleCount: 2, amount: -50, unavailableReason: null } },
            { ...RENT, id: 'not-yet', payee: 'Payee New', amount: -35,
                estimate: { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-history' } },
            { ...RENT, id: 'fixed', payee: 'Payee Fixed', amount: -20 },
        ]);
        renderPanel();
        const row = async (payee: string) => (await screen.findByText(payee)).closest('li')!;

        const made = await row('Payee Made');
        expect(within(made).getByText('≈')).toHaveAttribute('title', 'Estimated from the last 2 occurrences');
        expect(made).toHaveTextContent(`≈${formatSignedAmount(-50, 'USD')}`);

        const notYet = await row('Payee New');
        expect(within(notYet).getByText('≈'))
            .toHaveAttribute('title', 'Estimated: the amount entered is used until an estimate can be made');
        expect(notYet).toHaveTextContent(`≈${formatSignedAmount(-35, 'USD')}`);

        const fixed = await row('Payee Fixed');
        expect(within(fixed).getByText(formatSignedAmount(-20, 'USD'))).toBeInTheDocument();
        expect(fixed.textContent).not.toContain('≈');
    });

    it('Disable calls setReminderActive(false) and invalidates BOTH the list and upcoming queries', async () => {
        const { invalidateSpy } = renderPanel();
        await screen.findByText('Rent');

        await userEvent.click(screen.getByRole('button', { name: 'Disable' }));

        await waitFor(() => {
            expect(setReminderActive).toHaveBeenCalledWith(LEDGER, 'r1', { active: false });
        });
        // The headline correctness property: a manage action refreshes the
        // list AND the calendar/agenda window (prefix key).
        await waitFor(() => {
            expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['reminders', LEDGER] });
            expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['reminders', 'upcoming', LEDGER] });
        });
    });

    it('shows the empty state when there are no reminders', async () => {
        fetchReminders.mockResolvedValue([]);
        renderPanel();
        expect(await screen.findByText('No reminders yet.')).toBeInTheDocument();
    });
});
