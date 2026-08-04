import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { BackupsPanel } from './BackupsPanel';
import * as apiModule from '@/lib/api';
import type { BackupSchedule, BackupSummary } from '@/lib/types';

// Smoke tests for the admin Backups panel (ADR-0060). Behaviour locked down:
//   * passphrase-not-set → "Set passphrase" CTA, Create disabled, gated empty state
//   * passphrase-set + artifacts → "Change", Create enabled, rows with Download
//   * no Restore affordance (restore is the operator CLI, not the UI)

const SCHEDULE_OFF: BackupSchedule = {
    enabled: false, hourLocal: 3, minuteLocal: 0, timezone: null,
    lastRunAt: null, nextRunAt: null, passphraseConfigured: false,
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
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue({
            ...SCHEDULE_OFF, passphraseConfigured: true,
        });

        renderPanel();

        // Passphrase set → "Change", and Create is enabled.
        expect(await screen.findByRole('button', { name: /change/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /create backup/i })).toBeEnabled();

        // The artifact row renders with a Download action and its size.
        expect(await screen.findByRole('button', { name: /download/i })).toBeInTheDocument();
        expect(screen.getByText(/2\.0 KB/)).toBeInTheDocument();

        // Restore-from-backup is now an in-app admin action (ADR-0071 D3).
        expect(screen.getByRole('button', { name: /restore database/i })).toBeInTheDocument();
    });
});
