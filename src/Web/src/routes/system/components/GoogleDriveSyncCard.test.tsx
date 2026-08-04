import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { GoogleDriveSyncCard } from './GoogleDriveSyncCard';
import * as apiModule from '@/lib/api';
import type { DriveSyncStatus } from '@/lib/types';

// Smoke tests for the Google Drive sync card (ADR-0062 §④a):
//   * not connected → "Connect Google Drive" CTA, no Sync now / Disconnect
//   * connected → account + folder shown, Sync now + Disconnect actions

const NOT_CONNECTED: DriveSyncStatus = {
    enabled: false, connected: false, connectedEmail: null, folderName: null,
    installId: null,
    lastSyncAt: null, lastSyncStatus: null, lastSyncError: null,
};

const CONNECTED: DriveSyncStatus = {
    ...NOT_CONNECTED,
    enabled: true, connected: true,
    connectedEmail: 'user@example.com', folderName: 'Coffer Backups [a1b2c3]',
    installId: 'a1b2c3',
    lastSyncAt: '2026-06-24T03:15:00Z', lastSyncStatus: 'ok',
};

function renderCard() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <GoogleDriveSyncCard />
        </QueryClientProvider>,
    );
}

describe('GoogleDriveSyncCard', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('shows the connect CTA when not connected', async () => {
        vi.spyOn(apiModule, 'fetchDriveSyncStatus').mockResolvedValue(NOT_CONNECTED);

        renderCard();

        expect(await screen.findByRole('button', { name: /connect google drive/i })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /sync now/i })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /disconnect/i })).not.toBeInTheDocument();
    });

    it('shows the connected account with sync + disconnect actions', async () => {
        vi.spyOn(apiModule, 'fetchDriveSyncStatus').mockResolvedValue(CONNECTED);

        renderCard();

        expect(await screen.findByText('user@example.com')).toBeInTheDocument();
        expect(screen.getByText('Coffer Backups [a1b2c3]')).toBeInTheDocument();
        // The install id is surfaced (per-install folder namespacing).
        expect(screen.getByText('a1b2c3')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /sync to drive now/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /disconnect/i })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /connect google drive/i })).not.toBeInTheDocument();
    });
});
