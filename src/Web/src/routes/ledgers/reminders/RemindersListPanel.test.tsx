import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RemindersListPanel } from './RemindersListPanel';
import * as apiModule from '@/lib/api';
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
    isLoanReminder: false, origin: 'manual',
};

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    render(
        <QueryClientProvider client={queryClient}>
            <RemindersListPanel ledgerId={LEDGER} />
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
