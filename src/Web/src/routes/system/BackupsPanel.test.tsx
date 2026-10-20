import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { BackupsPanel } from './BackupsPanel';
import * as apiModule from '@/lib/api';
// The panel reaches the API through the barrel; the RestoreBackupCard it
// renders imports from the backup module directly. Both now read ONE query
// entry (BACKUPS_QUERY_KEY), so whichever mounts first supplies the fetcher
// for it — and a spy on only one of the two bindings leaves the other live,
// which in jsdom means a real request that never settles.
import * as backupModule from '@/lib/api/backup';
import type { BackupSchedule, BackupSummary } from '@/lib/types';

// Smoke tests for the admin Backups panel (ADR-0060). Behaviour locked down:
//   * passphrase-not-set → "Set passphrase" CTA, Create disabled, gated empty state
//   * passphrase-set + artifacts → "Change", Create enabled, rows with Download
//   * the Restore affordance (ADR-0071 D3 put it here; ADR-0094 made the UI the only
//     restore path)

const SCHEDULE_OFF: BackupSchedule = {
    enabled: false, hourLocal: 3, minuteLocal: 0, timezone: null,
    lastRunAt: null, nextRunAt: null, passphraseConfigured: false,
    consecutiveFailures: 0, lastError: null, lastFailureAt: null, disabledReason: null,
};

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <BackupsPanel />
        </QueryClientProvider>,
    );
}

describe('BackupsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('gates create + schedule until a passphrase is set', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue(SCHEDULE_OFF);

        renderPanel();

        // The set-passphrase CTA (not "Change") is shown.
        expect(await screen.findByRole('button', { name: /set passphrase/i })).toBeInTheDocument();
        // Create is disabled with no passphrase.
        expect(screen.getByRole('button', { name: /create backup/i })).toBeDisabled();
        // Empty-state hint points at the passphrase (await the backups query).
        expect(
            await screen.findByText(/set a backup passphrase to get started/i),
        ).toBeInTheDocument();
    });

    it('enables create and lists artifacts once a passphrase exists', async () => {
        const backups: BackupSummary[] = [
            { id: 'coffer-20260623T031500000Z-0a1b2c3d', sizeBytes: 2048, createdAtUtc: '2026-06-23T03:15:00Z', pinned: false },
        ];
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue(backups);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue(backups);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });

        renderPanel();

        // Passphrase set → "Change", and Create is enabled.
        expect(await screen.findByRole('button', { name: /change/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /create backup/i })).toBeEnabled();

        // The artifact row renders with a Download action and its size. Scoped
        // to the row: the restore picker below lists the same backup with the
        // same size, so an unscoped query now matches twice.
        const download = await screen.findByRole('button', { name: /download/i });
        const row = download.closest('li');
        expect(row).not.toBeNull();
        expect(within(row!).getByText(/2\.0 KB/)).toBeInTheDocument();

        // Restore-from-backup is now an in-app admin action (ADR-0071 D3).
        expect(screen.getByRole('button', { name: /restore database/i })).toBeInTheDocument();
    });

    it('offers Show only once a passphrase exists (ADR-0092 D7)', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue(SCHEDULE_OFF);

        renderPanel();

        await screen.findByRole('button', { name: /set passphrase/i });
        // Nothing to show yet — offering it would only produce a dead ceremony.
        expect(screen.queryByRole('button', { name: /^show$/i })).not.toBeInTheDocument();
    });

    it('reveals the stored passphrase behind the ceremony, and hides it again', async () => {
        // Why this exists at all: the server unseals the passphrase on every
        // scheduled backup, so it was always recoverable in principle — but with no
        // way to look it up, a forgotten one meant every backup silently became
        // unrestorable.
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        const reveal = vi
            .spyOn(apiModule, 'revealBackupPassphrase')
            .mockResolvedValue('correct-horse-battery');
        const user = userEvent.setup();

        renderPanel();

        const show = await screen.findByRole('button', { name: /^show$/i });
        expect(screen.queryByText('correct-horse-battery')).not.toBeInTheDocument();

        await user.click(show);

        expect(await screen.findByText('correct-horse-battery')).toBeInTheDocument();
        expect(reveal).toHaveBeenCalledTimes(1);

        await user.click(screen.getByRole('button', { name: /^hide$/i }));
        expect(screen.queryByText('correct-horse-battery')).not.toBeInTheDocument();
    });

    it('drops a revealed passphrase once it has been changed', async () => {
        // Leaving the OLD value on screen after a change is actively misleading — the
        // operator would copy it straight into their password manager.
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        vi.spyOn(apiModule, 'revealBackupPassphrase').mockResolvedValue('old-passphrase');
        vi.spyOn(apiModule, 'setBackupPassphrase').mockResolvedValue(undefined);
        const user = userEvent.setup();

        renderPanel();

        await user.click(await screen.findByRole('button', { name: /^show$/i }));
        expect(await screen.findByText('old-passphrase')).toBeInTheDocument();

        // Change it through the dialog. Queries are scoped to the dialog: the panel
        // renders the master-key card alongside, so "passphrase" is ambiguous page-wide.
        await user.click(screen.getByRole('button', { name: /^change$/i }));
        const dialog = within(await screen.findByRole('dialog'));
        await user.type(dialog.getByLabelText(/^passphrase$/i), 'brand-new-one');
        await user.type(dialog.getByLabelText(/confirm passphrase/i), 'brand-new-one');
        await user.click(dialog.getByRole('button', { name: /^change$/i }));

        await waitFor(() => {
            expect(screen.queryByText('old-passphrase')).not.toBeInTheDocument();
        });
    });

    it('surfaces a refused reveal without showing anything', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        vi.spyOn(apiModule, 'revealBackupPassphrase').mockRejectedValue(
            new Error('The operation either timed out or was not allowed.'),
        );
        const user = userEvent.setup();

        renderPanel();

        await user.click(await screen.findByRole('button', { name: /^show$/i }));

        expect(await screen.findByText(/timed out or was not allowed/i)).toBeInTheDocument();
    });

    it('confirms a created backup, naming its size', async () => {
        // Creating one used to announce nothing: a row appeared somewhere in a
        // list that already had rows, after a pause long enough to wonder
        // whether the click registered. Failure was announced and success was
        // not, leaving the outcome you most want to be sure of as the one you
        // had to infer.
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        vi.spyOn(apiModule, 'createBackup').mockResolvedValue({
            id: 'coffer-20260929T130600000Z-0a1b2c3d',
            sizeBytes: 22.2 * 1024 * 1024,
            createdAtUtc: '2026-09-29T13:06:00Z',
            pinned: false,
        });
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /create backup/i }));

        // The size is part of the confirmation: it says real bytes were
        // written, which an empty or truncated dump would not.
        const notice = await screen.findByRole('status');
        expect(notice).toHaveTextContent(/backup created/i);
        expect(notice).toHaveTextContent(/22\.2 MB/);
    });
});
