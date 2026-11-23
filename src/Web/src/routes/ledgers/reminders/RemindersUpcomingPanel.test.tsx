import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RemindersUpcomingPanel } from './RemindersUpcomingPanel';
import * as apiModule from '@/lib/api';
import { formatSignedAmount } from '@/lib/money';
import type { UpcomingOccurrence } from '@/lib/types';

vi.mock('@/lib/api', () => ({
    fetchUpcomingReminders: vi.fn(),
    unskipReminder: vi.fn(),
    ApiError: class ApiError extends Error {
        status: number; detail: string; code?: string;
        constructor(status: number, detail: string, code?: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail; this.code = code;
        }
    },
}));

// The occurrence dialog is heavy (it hosts the live editors); it's tested on its
// own. Here we stub it to verify the panel opens it with the right occurrence
// and surfaces its onActed notice.
// The stub is drawn in the real dialog shell, which gives focus back to the
// chip that opened it as the real dialog does.
vi.mock('./ReminderOccurrenceModal', async () => {
    const { Modal } = await import('@/components/ui/Modal');
    return {
        ReminderOccurrenceModal: ({ occ, onClose, onActed }: {
            occ: UpcomingOccurrence; onClose: () => void; onActed: (n: string | null) => void;
        }) => (
            <Modal open onClose={onClose} ariaLabel="occurrence dialog">
                <span>{`stub:${occ.payee}:${occ.date}`}</span>
                <button type="button" onClick={() => { onActed('Posted. Also marked 2 earlier occurrences as skipped.'); onClose(); }}>
                    act
                </button>
            </Modal>
        ),
    };
});

const fetchUpcomingReminders = vi.mocked(apiModule.fetchUpcomingReminders);
const unskipReminder = vi.mocked(apiModule.unskipReminder);
const LEDGER = 'ledger-1';

const now = new Date();
const TODAY = `${now.getFullYear().toString().padStart(4, '0')}-${
    (now.getMonth() + 1).toString().padStart(2, '0')}-${
    now.getDate().toString().padStart(2, '0')}`;

function occ(over: Partial<UpcomingOccurrence>): UpcomingOccurrence {
    return {
        date: TODAY, kind: 'reminder', reminderId: 'r1', headerId: null,
        payee: 'Electric Bill', memo: null, amount: -120, seriesNextDue: null, currencyCode: 'USD', ...over,
    };
}

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <RemindersUpcomingPanel ledgerId={LEDGER} canEdit />
        </QueryClientProvider>,
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    fetchUpcomingReminders.mockResolvedValue([occ({})]);
});

afterEach(() => {
    vi.restoreAllMocks();
});

describe('RemindersUpcomingPanel', () => {
    it('renders an un-fired occurrence as a clickable chip with its signed amount', async () => {
        renderPanel();
        const chip = await screen.findByRole('button', { name: /Electric Bill/ });
        expect(chip).toHaveTextContent('-$120.00');
    });

    it('prints a chip\'s amount in its source account\'s currency, and none for a series with no source', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ currencyCode: 'EUR' }),
            occ({ reminderId: 'r2', payee: 'Custom Series', amount: 0, currencyCode: null }),
        ]);
        renderPanel();
        const euro = await screen.findByRole('button', { name: /Electric Bill/ });
        expect(euro).toHaveTextContent(formatSignedAmount(-120, 'EUR'));
        expect(euro).toHaveAttribute('title', `Electric Bill · ${formatSignedAmount(-120, 'EUR')}`);
        // No source, so no currency: the chip names the series and carries no figure.
        const custom = screen.getByRole('button', { name: /Custom Series/ });
        expect(custom).toHaveTextContent(/^● Custom Series$/);
        expect(custom).toHaveAttribute('title', 'Custom Series');
        expect(euro.textContent).not.toContain('$');
    });

    it('marks an estimating series\' amount "≈", with an estimate or before it has one, and a fixed amount not', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ reminderId: 'made', payee: 'Payee Made', amount: -50,
                estimate: { requestedSampleCount: 3, availableSampleCount: 2, amount: -50, unavailableReason: null } }),
            occ({ reminderId: 'not-yet', payee: 'Payee New', amount: -35,
                estimate: { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-history' } }),
            occ({ reminderId: 'fixed', payee: 'Payee Fixed', amount: -20 }),
        ]);
        renderPanel();

        const made = await screen.findByRole('button', { name: /Payee Made/ });
        const madeAmount = `≈${formatSignedAmount(-50, 'USD')}`;
        expect(made).toHaveTextContent(madeAmount);
        expect(made).toHaveAttribute('title', `Payee Made · ${madeAmount} · Estimated from the last 2 occurrences`);

        const notYet = screen.getByRole('button', { name: /Payee New/ });
        const notYetAmount = `≈${formatSignedAmount(-35, 'USD')}`;
        expect(notYet).toHaveTextContent(notYetAmount);
        expect(notYet).toHaveAttribute('title',
            `Payee New · ${notYetAmount} · Estimated: the amount entered is used until an estimate can be made`);

        const fixed = screen.getByRole('button', { name: /Payee Fixed/ });
        expect(fixed).toHaveTextContent(formatSignedAmount(-20, 'USD'));
        expect(fixed).toHaveAttribute('title', `Payee Fixed · ${formatSignedAmount(-20, 'USD')}`);
        expect(fixed.textContent).not.toContain('≈');
    });

    it('clicking a chip opens the occurrence dialog for that occurrence', async () => {
        renderPanel();
        await userEvent.click(await screen.findByRole('button', { name: /Electric Bill/ }));
        const dialog = await screen.findByRole('dialog', { name: 'occurrence dialog' });
        expect(within(dialog).getByText(`stub:Electric Bill:${TODAY}`)).toBeInTheDocument();
    });

    it('surfaces the dialog\'s post-action notice and closes it', async () => {
        renderPanel();
        await userEvent.click(await screen.findByRole('button', { name: /Electric Bill/ }));
        await userEvent.click(await screen.findByRole('button', { name: 'act' }));
        expect(await screen.findByText(/Also marked 2 earlier occurrences as skipped/)).toBeInTheDocument();
        await waitFor(() =>
            expect(screen.queryByRole('dialog', { name: 'occurrence dialog' })).not.toBeInTheDocument());
    });

    it('a scheduled (posted) occurrence is read-only — not a button', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ kind: 'scheduled', headerId: 'h9', payee: 'Posted Rent', amount: -1500 }),
        ]);
        renderPanel();
        expect(await screen.findByText(/Posted Rent/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /Posted Rent/ })).not.toBeInTheDocument();
    });

    /*
     * The fire paths reject a suppressed slot with "un-skip it before firing", so the
     * un-skip route is what keeps a mis-clicked Skip from being permanent, and the chip
     * is its affordance. The struck-through styling stays: it is still a suppressed
     * occurrence, not a pending one.
     */
    it('a skipped occurrence can be restored from its chip', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ kind: 'skipped', payee: 'Skipped Rent', amount: -1500 }),
        ]);
        unskipReminder.mockResolvedValue({ occurrenceDate: TODAY, nextDueDate: TODAY });
        renderPanel();

        const chip = await screen.findByRole('button', { name: /Skipped Rent/ });
        expect(chip.textContent).toContain('⊘');
        expect(chip.querySelector('.line-through')).not.toBeNull();

        await userEvent.click(chip);

        await waitFor(() => expect(unskipReminder).toHaveBeenCalledTimes(1));
        // The slot, and only the slot, is what gets restored.
        expect(unskipReminder.mock.calls[0][2]).toBe(TODAY);
    });

    it('a posted occurrence stays read-only — undoing it is a register action', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ kind: 'scheduled', headerId: 'h9', payee: 'Posted Rent', amount: -1500 }),
        ]);
        renderPanel();
        await screen.findByText(/Posted Rent/);
        expect(screen.queryByRole('button', { name: /Posted Rent/ })).not.toBeInTheDocument();
        expect(unskipReminder).not.toHaveBeenCalled();
    });

    it('caps chips at 3 and reveals the rest via "+N more"', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ reminderId: 'a', payee: 'Bill A' }),
            occ({ reminderId: 'b', payee: 'Bill B' }),
            occ({ reminderId: 'c', payee: 'Bill C' }),
            occ({ reminderId: 'd', payee: 'Bill D' }),
        ]);
        renderPanel();
        await screen.findByRole('button', { name: /Bill A/ });
        expect(screen.queryByRole('button', { name: /Bill D/ })).not.toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: /\+1 more/ }));
        expect(await screen.findByRole('button', { name: /Bill D/ })).toBeInTheDocument();
    });
});

describe('RemindersUpcomingPanel: focus when a chip\'s own action changes it', () => {
    // ADR-0023 §L: a control that replaces or removes itself hands focus to the
    // control that takes its place, never to the page. Skip and restore keep
    // the chip (keyed by its occurrence); a post makes it read-only, and focus
    // goes to the next chip button drawn.
    function renderWithClient() {
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={queryClient}>
                <RemindersUpcomingPanel ledgerId={LEDGER} canEdit />
            </QueryClientProvider>,
        );
        return queryClient;
    }

    it('keeps focus on a skipped chip once its restore lands', async () => {
        let restored = false;
        fetchUpcomingReminders.mockImplementation(async () => [occ({ kind: restored ? 'reminder' : 'skipped', payee: 'Bill S' })]);
        unskipReminder.mockImplementation(async () => { restored = true; return undefined as never; });
        renderWithClient();
        const user = userEvent.setup();

        const chip = await screen.findByRole('button', { name: /Bill S/ });
        expect(chip).toHaveAttribute('title', expect.stringContaining('skipped'));
        chip.focus();
        await user.keyboard('{Enter}');

        await waitFor(() => expect(chip).not.toHaveAttribute('title', expect.stringContaining('skipped')));
        expect(screen.getByRole('button', { name: /Bill S/ })).toBe(chip);
        expect(chip).toHaveFocus();
    });

    it('keeps focus on a chip skipped from its dialog', async () => {
        let skipped = false;
        fetchUpcomingReminders.mockImplementation(async () => [occ({ kind: skipped ? 'skipped' : 'reminder', payee: 'Bill S' })]);
        const queryClient = renderWithClient();
        const user = userEvent.setup();

        const chip = await screen.findByRole('button', { name: /Bill S/ });
        await user.click(chip);
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'act' }));
        expect(chip).toHaveFocus();
        skipped = true;
        await act(() => queryClient.invalidateQueries({ queryKey: ['reminders'] }));

        await waitFor(() => expect(chip).toHaveAttribute('title', expect.stringContaining('skipped')));
        expect(chip).toHaveFocus();
    });

    it('gives focus to the next chip button when a post makes a chip read-only', async () => {
        let posted = false;
        fetchUpcomingReminders.mockImplementation(async () => [
            occ({ reminderId: 'r1', kind: posted ? 'scheduled' : 'reminder', payee: 'Bill A' }),
            occ({ reminderId: 'r2', payee: 'Bill B' }),
        ]);
        const queryClient = renderWithClient();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /Bill A/ }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'act' }));
        posted = true;
        await act(() => queryClient.invalidateQueries({ queryKey: ['reminders'] }));

        await waitFor(() => expect(screen.queryByRole('button', { name: /Bill A/ })).toBeNull());
        expect(screen.getByRole('button', { name: /Bill B/ })).toHaveFocus();
    });

    // The calendar has no add control, so when the post leaves no chip button
    // focus goes to the calendar itself: named, and no Tab stop of its own.
    it('gives focus to the calendar when a post leaves no chip button', async () => {
        let posted = false;
        fetchUpcomingReminders.mockImplementation(async () => [
            occ({ reminderId: 'r1', kind: posted ? 'scheduled' : 'reminder', payee: 'Bill A' }),
        ]);
        const queryClient = renderWithClient();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /Bill A/ }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'act' }));
        posted = true;
        await act(() => queryClient.invalidateQueries({ queryKey: ['reminders'] }));

        await waitFor(() => expect(screen.queryByRole('button', { name: /Bill A/ })).toBeNull());
        expect(screen.getByTitle(/Bill A/)).toHaveTextContent('✓ Bill A');
        const calendar = screen.getByRole('group', { name: /^Reminders in / });
        expect(calendar).toHaveAttribute('tabindex', '-1');
        expect(calendar).toHaveFocus();
    });
});
