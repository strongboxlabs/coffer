import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RemindersUpcomingPanel } from './RemindersUpcomingPanel';
import * as apiModule from '@/lib/api';
import type { UpcomingOccurrence } from '@/lib/types';

vi.mock('@/lib/api', () => ({
    fetchUpcomingReminders: vi.fn(),
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
vi.mock('./ReminderOccurrenceModal', () => ({
    ReminderOccurrenceModal: ({ occ, onClose, onActed }: {
        occ: UpcomingOccurrence; onClose: () => void; onActed: (n: string | null) => void;
    }) => (
        <div role="dialog" aria-label="occurrence dialog">
            <span>{`stub:${occ.payee}:${occ.date}`}</span>
            <button type="button" onClick={() => { onActed('Posted. Also marked 2 earlier occurrences as skipped.'); onClose(); }}>
                act
            </button>
        </div>
    ),
}));

const fetchUpcomingReminders = vi.mocked(apiModule.fetchUpcomingReminders);
const LEDGER = 'ledger-1';

const now = new Date();
const TODAY = `${now.getFullYear().toString().padStart(4, '0')}-${
    (now.getMonth() + 1).toString().padStart(2, '0')}-${
    now.getDate().toString().padStart(2, '0')}`;

function occ(over: Partial<UpcomingOccurrence>): UpcomingOccurrence {
    return {
        date: TODAY, kind: 'reminder', reminderId: 'r1', headerId: null,
        payee: 'Electric Bill', memo: null, amount: -120, seriesNextDue: null, ...over,
    };
}

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <RemindersUpcomingPanel ledgerId={LEDGER} />
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

    it('a skipped occurrence is a read-only struck-through chip — not a button', async () => {
        fetchUpcomingReminders.mockResolvedValue([
            occ({ kind: 'skipped', payee: 'Skipped Rent', amount: -1500 }),
        ]);
        renderPanel();
        const chip = await screen.findByText(/Skipped Rent/);
        expect(chip.className).toContain('line-through');
        expect(screen.queryByRole('button', { name: /Skipped Rent/ })).not.toBeInTheDocument();
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
