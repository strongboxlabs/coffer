import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RestoreSnapshotDialog } from './RestoreSnapshotDialog';
import * as apiModule from '@/lib/api';
import type { SnapshotSummary } from '@/lib/types';

vi.mock('@/lib/api', () => ({
    restoreSnapshot: vi.fn(),
    ApiError: class ApiError extends Error {
        status: number; detail: string; code?: string;
        constructor(status: number, detail: string, code?: string) {
            super(detail); this.name = 'ApiError'; this.status = status; this.detail = detail; this.code = code;
        }
    },
}));

const restoreSnapshot = vi.mocked(apiModule.restoreSnapshot);

const SNAPSHOT: SnapshotSummary = {
    id: 'snap-1',
    createdAt: '2026-01-02T10:00:00Z',
    createdByUserId: 'user-1',
    kind: 'manual',
    description: null,
    schemaVersion: '200_example.sql',
    contentSizeStored: 1024,
    restorable: true,
};

function renderDialog() {
    const onClose = vi.fn();
    const onRestored = vi.fn();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <RestoreSnapshotDialog ledgerId="ledger-1" snapshot={SNAPSHOT} onClose={onClose} onRestored={onRestored} />
        </QueryClientProvider>,
    );
    return { onClose, onRestored };
}

beforeEach(() => {
    vi.clearAllMocks();
});

describe('RestoreSnapshotDialog', () => {
    it('keeps the refusal shown during a retry, and holds Try again and Close until the retry lands', async () => {
        let finishRetry: () => void = () => {};
        restoreSnapshot
            .mockRejectedValueOnce(new apiModule.ApiError(500, 'The server could not restore.'))
            .mockImplementationOnce(() => new Promise<void>((resolve) => { finishRetry = resolve; }) as never);
        const { onClose, onRestored } = renderDialog();

        fireEvent.click(screen.getByRole('checkbox', { name: /I understand/ }));
        fireEvent.click(screen.getByRole('button', { name: 'Restore' }));
        const tryAgain = await screen.findByRole('button', { name: 'Try again' });
        fireEvent.click(tryAgain);

        // The retry renders pending (TanStack tells the page on a later task).
        await waitFor(() => expect(screen.getByRole('button', { name: /Try again|Restoring/ })).toHaveAttribute('aria-busy', 'true'));
        const retrying = screen.getByRole('button', { name: /Try again|Restoring/ });
        expect(retrying).toHaveAttribute('aria-disabled', 'true');
        expect(screen.getByRole('heading', { name: 'Restore failed' })).toBeInTheDocument();
        expect(screen.getByRole('alert')).toHaveTextContent('The server could not restore.');

        // A second press sends nothing.
        fireEvent.click(retrying);
        expect(restoreSnapshot).toHaveBeenCalledTimes(2);

        // Close waits for the retry, as Esc does.
        const close = screen.getByRole('button', { name: 'Close' });
        expect(close).toHaveAttribute('aria-disabled', 'true');
        fireEvent.click(close);
        fireEvent.keyDown(close, { key: 'Escape' });
        expect(onClose).not.toHaveBeenCalled();

        await act(async () => { finishRetry(); });
        const done = await screen.findByRole('button', { name: 'Done' });
        expect(screen.getByRole('heading', { name: 'Restore complete' })).toBeInTheDocument();
        fireEvent.click(done);
        expect(onRestored).toHaveBeenCalledTimes(1);
        expect(onClose).not.toHaveBeenCalled();
    });
});
