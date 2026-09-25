import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ActivityPanel } from './ActivityPanel';
import * as apiModule from '@/lib/api';
import type { LedgerOperationSummary } from '@/lib/types';

// The undo-import entry point.
//
// `undo-import` existed for releases with exactly one caller: the import
// dialog. The undo therefore lived only as long as that dialog stayed open —
// close it, or go and look at what actually landed, and the endpoint was
// unreachable from the app. An import you have not looked at is the one you
// are least likely to want to undo, so the affordance belonged where you go to
// look.
//
// These tests exist because "the button renders" is not the property that
// matters. What matters is that clicking it reaches the endpoint, that the
// confirm states what will be lost, and that it is absent where an undo would
// be a lie.

const LEDGER_ID = '00000000-0000-0000-0000-0000000000b1';

function op(overrides: Partial<LedgerOperationSummary>): LedgerOperationSummary {
    return {
        id: '00000000-0000-0000-0000-00000000f001',
        family: 'ingest',
        providerKey: 'file',
        triggeredVia: 'file-upload',
        status: 'completed',
        startedAt: '2026-09-20T12:00:00Z',
        completedAt: '2026-09-20T12:00:03Z',
        triggeredByUserId: null,
        details: { txns_inserted: 12 },
        errorCount: 0,
        ...overrides,
    };
}

function renderPanel() {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={client}>
            <ActivityPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('ActivityPanel — undo import', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('offers an undo on a file import, and reaching it calls the endpoint', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);
        const undoSpy = vi
            .spyOn(apiModule, 'undoImport')
            .mockResolvedValue({ found: 12, edited: 0, deleted: 0, tooLarge: false });

        renderPanel();

        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /undo this import/i }));

        // The DRY RUN is the point: nothing is deleted to find out what would be.
        await waitFor(() => {
            expect(undoSpy).toHaveBeenCalledWith(
                LEDGER_ID, '00000000-0000-0000-0000-00000000f001', { dryRun: true },
            );
        });
    });

    it('says how many edited rows the undo would take with it', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);
        vi.spyOn(apiModule, 'undoImport')
            .mockResolvedValue({ found: 12, edited: 3, deleted: 0, tooLarge: false });

        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /undo this import/i }));

        // Anchored on the count that came back, then on the warning: a confirm
        // that does not say what is about to be destroyed is not a confirm.
        expect(await screen.findByText(/remove 12 transactions/i)).toBeInTheDocument();
        expect(screen.getByText(/3 have been edited since/i)).toBeInTheDocument();
    });

    it('only deletes after the confirm is clicked', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);
        const undoSpy = vi
            .spyOn(apiModule, 'undoImport')
            .mockResolvedValue({ found: 12, edited: 0, deleted: 12, tooLarge: false });

        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /undo this import/i }));
        await screen.findByText(/remove 12 transactions/i);

        // Still only the dry run at this point — the preview must not delete.
        expect(undoSpy).toHaveBeenCalledTimes(1);
        expect(undoSpy.mock.calls[0]![2]).toEqual({ dryRun: true });

        await user.click(screen.getByRole('button', { name: /yes, remove them/i }));

        await waitFor(() => expect(undoSpy).toHaveBeenCalledTimes(2));
        // The real call carries no dryRun.
        expect(undoSpy.mock.calls[1]![2]).toBeUndefined();
        expect(await screen.findByText(/undone — 12 transactions removed/i)).toBeInTheDocument();
    });

    it('does not offer an undo on a live-feed sync', async () => {
        // Undoing a SimpleFIN sync would be a lie: its rows dedup on the
        // provider's own id, so the next sync brings them straight back.
        vi.spyOn(apiModule, 'fetchLedgerOperations')
            .mockResolvedValue([op({ providerKey: 'simplefin', triggeredVia: 'manual' })]);
        vi.spyOn(apiModule, 'undoImport').mockResolvedValue(
            { found: 0, edited: 0, deleted: 0, tooLarge: false },
        );

        renderPanel();

        // Anchored on the row being present before asserting the absence — a
        // bare queryBy would pass on the loading frame, before anything renders.
        expect(await screen.findByText(/SimpleFIN|simplefin/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /undo this import/i })).toBeNull();
    });

    it('explains a refusal rather than offering an action that cannot work', async () => {
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);
        vi.spyOn(apiModule, 'undoImport')
            .mockResolvedValue({ found: 25000, edited: 0, deleted: 0, tooLarge: true });

        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /undo this import/i }));

        expect(await screen.findByText(/too large to undo/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /yes, remove them/i })).toBeNull();
    });
});
