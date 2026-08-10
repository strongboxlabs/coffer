import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { SystemSettingsPage } from './SystemSettingsPage';
import * as apiModule from '@/lib/api';
import type { BackupSchedule } from '@/lib/types';

// System settings page (ADR-0060): About is visible to everyone; the Backups
// tab only appears for admins (and the API is RequireAdmin regardless).

const VERSION = {
    api: { version: '1.0.0', build: 42, commit: 'abc1234', commitDate: '2026-06-23' },
    db: { schemaVersion: 139, script: '139_global_scheduled_jobs.sql' },
};

const SCHEDULE_OFF: BackupSchedule = {
    enabled: false, hourLocal: 3, minuteLocal: 0, timezone: null,
    lastRunAt: null, nextRunAt: null, passphraseConfigured: false,
};

function renderSystem(isAdmin: boolean) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['me'], {
        id: '00000000-0000-0000-0000-000000000001',
        username: 'alice',
        displayName: 'Alice',
        isAdmin,
    });

    const root = createRootRoute();
    const systemRoute = createRoute({
        getParentRoute: () => root, path: '/system', component: SystemSettingsPage,
    });
    const landingRoute = createRoute({
        getParentRoute: () => root, path: '/', component: () => <main>landing</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([systemRoute, landingRoute]),
        history: createMemoryHistory({ initialEntries: ['/system'] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('SystemSettingsPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchVersion').mockResolvedValue(VERSION);
    });

    it('shows About to everyone and renders the version rows', async () => {
        renderSystem(false);
        // About tab is the default; the DB schema row proves the panel loaded.
        expect(await screen.findByText(/schema 139/i)).toBeInTheDocument();
        // No Backups tab for a non-admin.
        expect(screen.queryByRole('tab', { name: /backups/i })).not.toBeInTheDocument();
    });

    it('shows the Backups tab to an admin and opens the panel', async () => {
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue(SCHEDULE_OFF);

        renderSystem(true);

        const backupsTab = await screen.findByRole('tab', { name: /backups/i });
        const user = userEvent.setup();
        await user.click(backupsTab);

        // The Backups panel surface renders: the passphrase card (exact title)
        // and the retention control (header note + the editable Retention card).
        expect(await screen.findByText('Backup passphrase')).toBeInTheDocument();
        expect(screen.getAllByText(/retention/i).length).toBeGreaterThan(0);
    });

    it('gives the master key its own Encryption tab, not a card under Backups', async () => {
        // ADR-0092: the key wraps bank-feed tokens, the backup passphrase AND the
        // Drive connection, so filing it under one of the three made it a hunt.
        vi.spyOn(apiModule, 'fetchBackups').mockResolvedValue([]);
        vi.spyOn(apiModule, 'fetchBackupSchedule').mockResolvedValue(SCHEDULE_OFF);
        vi.spyOn(apiModule, 'fetchMasterKeyStatus').mockResolvedValue({
            kekId: 'v1', path: '/app/data/master.key', fingerprint: 'ABCD',
        });

        renderSystem(true);
        const user = userEvent.setup();

        // Not on the Backups tab.
        await user.click(await screen.findByRole('tab', { name: /backups/i }));
        expect(await screen.findByText('Backup passphrase')).toBeInTheDocument();
        expect(screen.queryByRole('heading', { name: /^master key$/i })).not.toBeInTheDocument();

        // On its own tab instead.
        await user.click(screen.getByRole('tab', { name: /encryption/i }));
        expect(await screen.findByRole('heading', { name: /^master key$/i })).toBeInTheDocument();
    });

    it('hides the Encryption tab from a non-admin', () => {
        renderSystem(false);
        expect(screen.queryByRole('tab', { name: /encryption/i })).not.toBeInTheDocument();
    });

    it('shows the MCP tab to an admin and renders the toggle', async () => {
        vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue({
            enabled: false, active: false, configForced: false,
            writesEnabled: false, writesActive: false, writesConfigForced: false,
            publicUrl: 'https://mcp.example.test',
        });

        renderSystem(true);

        const mcpTab = await screen.findByRole('tab', { name: /^mcp$/i });
        const user = userEvent.setup();
        await user.click(mcpTab);

        // The panel surface renders: live-state line + the enable action.
        expect(await screen.findByText(/MCP is stopped/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /enable mcp/i })).toBeInTheDocument();
    });

    it('hides the MCP tab from a non-admin', () => {
        renderSystem(false);
        expect(screen.queryByRole('tab', { name: /^mcp$/i })).not.toBeInTheDocument();
    });
});
