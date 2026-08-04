import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RestoreBackupCard } from './RestoreBackupCard';
import * as backupApi from '@/lib/api/backup';
import { ApiError } from '@/lib/api';

// RestoreBackupCard (ADR-0071 D3): the authenticated-admin whole-DB restore.
// Locked down: the typed-confirmation gate, the KEK-mismatch acknowledge flow,
// and the post-success restarting notice.

function renderCard() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={qc}>
            <RestoreBackupCard />
        </QueryClientProvider>,
    );
}

const backupFile = () => new File(['ciphertext'], 'db.cofferbak');

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
    await user.upload(screen.getByLabelText(/backup file/i), backupFile());
    await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
    await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
}

describe('RestoreBackupCard', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('enables Restore only with a file, passphrase, and the exact confirm phrase', async () => {
        const user = userEvent.setup();
        renderCard();

        const button = screen.getByRole('button', { name: /restore database/i });
        expect(button).toBeDisabled();

        await user.upload(screen.getByLabelText(/backup file/i), backupFile());
        await user.type(screen.getByLabelText(/^passphrase$/i), 'pw');
        await user.type(screen.getByLabelText(/to confirm/i), 'not the phrase');
        expect(button).toBeDisabled();

        await user.clear(screen.getByLabelText(/to confirm/i));
        await user.type(screen.getByLabelText(/to confirm/i), backupApi.RESTORE_CONFIRM_PHRASE);
        expect(button).toBeEnabled();
    });

    it('surfaces a KEK mismatch, requires acknowledgement, then proceeds', async () => {
        const spy = vi
            .spyOn(backupApi, 'restoreBackup')
            .mockRejectedValueOnce(
                new ApiError(422, 'This backup was sealed under a different Master KEK.', 'backup-kek-mismatch'),
            )
            .mockResolvedValueOnce(undefined);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/different master kek/i)).toBeInTheDocument();
        // Blocked until the mismatch is acknowledged.
        expect(screen.getByRole('button', { name: /restore database/i })).toBeDisabled();

        await user.click(screen.getByRole('checkbox', { name: /restore anyway/i }));
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(spy).toHaveBeenCalledTimes(2);
        expect(spy.mock.calls[1][3]).toBe(true);   // acknowledgeKekMismatch on the retry
    });

    it('shows the restarting notice on success', async () => {
        vi.spyOn(backupApi, 'restoreBackup').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderCard();

        await fillValidForm(user);
        await user.click(screen.getByRole('button', { name: /restore database/i }));

        expect(await screen.findByText(/restoring/i)).toBeInTheDocument();
        expect(screen.getByText(/signed out/i)).toBeInTheDocument();
    });
});
