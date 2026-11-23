import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
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

        // Anchored on the status the schedule's answer writes: before it, nothing
        // is known about the passphrase.
        expect(await screen.findByText(/^Not set\. Required before/)).toBeInTheDocument();
        // The set-passphrase CTA (not "Change") is shown.
        expect(screen.getByRole('button', { name: /set passphrase/i })).toBeInTheDocument();
        // Create is disabled with no passphrase, and says why.
        const create = screen.getByRole('button', { name: /create backup/i });
        expect(create).toBeDisabled();
        expect(create).toHaveAttribute('title', 'Set a backup passphrase first.');
        // Empty-state hint points at the passphrase (await the backups query).
        expect(
            await screen.findByText(/set a backup passphrase to get started/i),
        ).toBeInTheDocument();
    });

    // ADR-0023 §L: the passphrase dialog's fields are pending while its save is in
    // flight; the save sends what was typed at the press and its success closes it.
    it('refuses typing in the passphrase dialog while it saves', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue(SCHEDULE_OFF);
        vi.spyOn(apiModule, 'setBackupPassphrase').mockImplementation(() => new Promise(() => {}));
        const user = userEvent.setup();

        renderPanel();
        await user.click(await screen.findByRole('button', { name: /set passphrase/i }));
        const dialog = await screen.findByRole('dialog');
        const passphrase = within(dialog).getByLabelText('Passphrase');
        const confirm = within(dialog).getByLabelText('Confirm passphrase');
        await user.type(passphrase, 'longenough');
        await user.type(confirm, 'longenough');
        await user.click(within(dialog).getByRole('button', { name: /set passphrase/i }));
        await within(dialog).findByRole('button', { name: /saving/i });

        await user.type(passphrase, 'x');
        await user.type(confirm, 'x');
        expect(passphrase).toHaveValue('longenough');
        expect(confirm).toHaveValue('longenough');
        expect(passphrase).toHaveAttribute('aria-disabled', 'true');
        expect(confirm).toHaveAttribute('aria-disabled', 'true');
    });

    it('holds Create backup pending, not disabled, while the schedule says whether a passphrase is set', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        let answer: (s: BackupSchedule) => void = () => {};
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockImplementation(
            () => new Promise((resolve) => { answer = resolve; }));
        const create = vi.spyOn(apiModule, 'createBackup');

        renderPanel();

        const button = await screen.findByRole('button', { name: /create backup/i });
        const status = screen.getByText('Backup passphrase').parentElement!;
        expect(within(status).getByText('Loading…')).toBeInTheDocument();
        expect(button).toBeEnabled();
        expect(button).toHaveAttribute('aria-busy', 'true');
        expect(button).not.toHaveAttribute('title');
        expect(screen.queryByText('Set a backup passphrase first.')).toBeNull();
        await userEvent.click(button);
        expect(create).not.toHaveBeenCalled();

        await act(async () => { answer(SCHEDULE_OFF); });
        expect(await screen.findByText(/^Not set\. Required before/)).toBeInTheDocument();
        expect(button).toBeDisabled();
        expect(button).not.toHaveAttribute('aria-busy');
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

    it('names a backup held for an upgrade, and does not call it pinned (ADR-0110)', async () => {
        const backups: BackupSummary[] = [
            { id: 'coffer-20260623T031500000Z-0a1b2c3d', sizeBytes: 2048, createdAtUtc: '2026-06-23T03:15:00Z', pinned: false, upgradeLabel: 'before upgrade 0.128.1 to 0.129.0' },
            { id: 'coffer-20260622T031500000Z-0a1b2c3e', sizeBytes: 2048, createdAtUtc: '2026-06-22T03:15:00Z', pinned: false },
        ];
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue(backups);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue(backups);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });

        renderPanel();

        const label = (await screen.findAllByText('before upgrade 0.128.1 to 0.129.0'))[0];
        const rows = screen.getAllByRole('listitem').filter((li) => li.textContent?.includes('Download'));
        const labelled = label.closest('li');
        expect(labelled).not.toBeNull();
        expect(within(labelled!).queryByText(/pinned/i)).toBeNull();
        // Only the labelled row carries it.
        expect(rows.filter((li) => li.textContent?.includes('before upgrade'))).toHaveLength(1);
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
        // ADR-0023 §L: Hide took itself away with focus on it; focus went to Show.
        expect(screen.getByRole('button', { name: /^show$/i })).toHaveFocus();
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
        // Without an announcement a row just appears somewhere in a list that
        // already has rows, after a pause long enough to wonder whether the
        // click registered. Announcing failure but not success would leave the
        // outcome you most want to be sure of as the one you have to infer.
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

    // ADR-0023 §L: a retention save that lands leaves nothing changed, so
    // Save, holding focus, becomes disabled; focus goes to the first field,
    // never to the page.
    it('gives focus to Daily when its own save disables Save', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        vi.spyOn(apiModule, 'fetchBackupRetention')
            .mockResolvedValueOnce({ retentionDaily: 7, retentionWeekly: 4, retentionMonthly: 6 })
            .mockResolvedValue({ retentionDaily: 8, retentionWeekly: 4, retentionMonthly: 6 });
        const save = vi.spyOn(apiModule, 'setBackupRetention').mockResolvedValue(undefined as never);
        const user = userEvent.setup();
        renderPanel();

        const daily = await screen.findByRole('spinbutton', { name: /daily/i });
        await user.tripleClick(daily);
        await user.keyboard('8');
        const saveButton = screen.getByRole('button', { name: 'Save' });
        await user.click(saveButton);

        expect(save).toHaveBeenCalledTimes(1);
        await waitFor(() => expect(saveButton).toBeDisabled());
        await waitFor(() => expect(daily).toHaveFocus());
        expect(daily).toHaveValue(8);
    });

    // A pending field looks as the same field disabled does (ADR-0023 §L):
    // the retention fields dim when disabled, with the arrow cursor a disabled
    // field shows, so pending they show that cursor too, not the text caret
    // their readOnly would.
    it('shows a pending retention field with the cursor and dim of a disabled one', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(backupModule, 'fetchRestoreLimits')
            .mockResolvedValue({ partSizeBytes: 49 * 1024 * 1024 });
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });
        vi.spyOn(apiModule, 'fetchBackupRetention')
            .mockResolvedValue({ retentionDaily: 7, retentionWeekly: 4, retentionMonthly: 6 });
        vi.spyOn(apiModule, 'setBackupRetention').mockReturnValue(new Promise<never>(() => {}));
        const user = userEvent.setup();
        renderPanel();

        const daily = await screen.findByRole('spinbutton', { name: /daily/i });
        await user.tripleClick(daily);
        await user.keyboard('8');
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(daily).toHaveAttribute('aria-disabled', 'true'));
        expect(daily).toHaveClass('aria-disabled:cursor-default', 'aria-disabled:opacity-50', 'disabled:opacity-50');
    });
});
