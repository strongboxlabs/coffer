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
import type { BackupSchedule, SystemAttention } from '@/lib/types';

// System settings page (ADR-0060): About is visible to everyone; the Backups
// tab only appears for admins (and the API is RequireAdmin regardless).

const VERSION = {
    api: { version: '1.0.0', build: 42, commit: 'abc1234', commitDate: '2026-06-23' },
    db: { schemaVersion: 139, script: '139_global_scheduled_jobs.sql' },
};

const SCHEDULE_OFF: BackupSchedule = {
    enabled: false, hourLocal: 3, minuteLocal: 0, timezone: null,
    lastRunAt: null, nextRunAt: null, passphraseConfigured: false,
    consecutiveFailures: 0, lastError: null, lastFailureAt: null, disabledReason: null,
};

const NOTHING_OUTSTANDING: SystemAttention = { worst: null, subjects: [], upgradeVersion: null };

function renderSystem(isAdmin: boolean, attention: SystemAttention = NOTHING_OUTSTANDING) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['me'], {
        id: '00000000-0000-0000-0000-000000000001',
        username: 'alice',
        displayName: 'Alice',
        isAdmin,
    });
    // Seeded, so a test that is not about the dots makes no request for them.
    queryClient.setQueryData(['system-attention'], attention);

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

    it('gives a non-admin the Appearance tab too', async () => {
        // Appearance is a personal display preference, not an admin control, so
        // it joins About in the open set. This is the case the old
        // `tab !== 'about' && !isAdmin` fallback would have bounced.
        const user = userEvent.setup();
        renderSystem(false);

        await user.click(await screen.findByRole('tab', { name: /appearance/i }));
        expect(await screen.findByRole('radio', { name: /dark high contrast/i }))
            .toBeInTheDocument();
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

    it('hides the Encryption tab from a non-admin', async () => {
        renderSystem(false);
        // AWAITED, and anchored on a tab a non-admin does get. The tabs render
        // asynchronously, so the original — a bare queryByRole straight after
        // render — ran against a DOM with no tabs in it at all and would have
        // passed even if Encryption were shown to everyone.
        expect(await screen.findByRole('tab', { name: /appearance/i })).toBeInTheDocument();
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

    it('puts the Upgrade tab next to Backups for an admin and opens the panel (ADR-0110)', async () => {
        vi.spyOn(apiModule, 'fetchUpgradeStatus').mockResolvedValue({
            installed: '0.128.1', inAppEnabled: true, autoCheck: false, privateRepo: false, tokenStored: false,
            repository: 'example/coffer', installDir: '/srv/coffer', hostNote: '',
            host: { state: 'running', at: null }, latest: null, checkedAt: null, checkError: null,
            updateAvailable: false, canStart: false, cannotStartReason: 'There is no newer release.', attempt: null,
        });

        renderSystem(true);
        const user = userEvent.setup();

        const tabs = (await screen.findAllByRole('tab')).map((t) => t.textContent);
        expect(tabs.indexOf('Upgrade')).toBe(tabs.indexOf('Backups') + 1);

        await user.click(screen.getByRole('tab', { name: /^upgrade$/i }));
        expect(await screen.findByText('Not checked yet')).toBeInTheDocument();
    });

    // The trail the gear starts, followed down to the tab that holds the thing (ADR-0102, amended
    // for ADR-0110): Upgrade lights for the upgrade alone, Notifications for everything else, so
    // one subject never lights two tabs.
    const SINCE = '2026-11-02T12:00:00Z';

    it('lights the Upgrade tab for an available release, and not Notifications', async () => {
        renderSystem(true, {
            worst: 'warning',
            subjects: [{ subject: 'upgrade', severity: 'warning', since: SINCE }],
            upgradeVersion: '0.131.0',
        });

        expect(await screen.findByRole('tab', { name: 'Upgrade Update available: 0.131.0' }))
            .toBeInTheDocument();
        // The simpler rule: the upgrade is the Upgrade tab's alone, so Notifications stays plain.
        expect(screen.getByRole('tab', { name: 'Notifications' })).toBeInTheDocument();
    });

    it('lights the Notifications tab for a problem, and leaves Upgrade plain', async () => {
        renderSystem(true, {
            worst: 'critical',
            subjects: [{ subject: 'backup', severity: 'critical', since: SINCE }],
            upgradeVersion: null,
        });

        expect(await screen.findByRole('tab', { name: 'Notifications 1 problem outstanding on this install' }))
            .toBeInTheDocument();
        expect(screen.getByRole('tab', { name: 'Upgrade' })).toBeInTheDocument();
    });

    it('lights each tab for its own subject when both are outstanding', async () => {
        renderSystem(true, {
            worst: 'critical',
            subjects: [
                { subject: 'backup', severity: 'critical', since: SINCE },
                { subject: 'upgrade', severity: 'warning', since: SINCE },
            ],
            upgradeVersion: '0.131.0',
        });

        expect(await screen.findByRole('tab', { name: 'Upgrade Update available: 0.131.0' }))
            .toBeInTheDocument();
        // One problem, not two: the upgrade is not counted among what Notifications lists.
        expect(screen.getByRole('tab', { name: 'Notifications 1 problem outstanding on this install' }))
            .toBeInTheDocument();
    });

    it('draws the same dot a ledger problem does: an outline for a warning, a fill for critical', async () => {
        renderSystem(true, {
            worst: 'critical',
            subjects: [
                { subject: 'backup', severity: 'critical', since: SINCE },
                { subject: 'upgrade', severity: 'warning', since: SINCE },
            ],
            upgradeVersion: '0.131.0',
        });

        const upgradeTab = await screen.findByRole('tab', { name: /^Upgrade Update available/ });
        const notificationsTab = screen.getByRole('tab', { name: /^Notifications 1 problem/ });
        // The weight difference ADR-0102 D4 settled on, not a second visual language for upgrades.
        expect(upgradeTab.querySelector('.border-mark-warning')).not.toBeNull();
        expect(notificationsTab.querySelector('.bg-mark-danger')).not.toBeNull();
    });

    it('asks nothing about attention for a non-admin, and shows no dot', async () => {
        const spy = vi.spyOn(apiModule, 'fetchSystemAttention');
        renderSystem(false, {
            worst: 'warning',
            subjects: [{ subject: 'upgrade', severity: 'warning', since: SINCE }],
            upgradeVersion: '0.131.0',
        });

        expect(await screen.findByRole('tab', { name: /appearance/i })).toBeInTheDocument();
        expect(spy).not.toHaveBeenCalled();
        expect(screen.queryByText(/update available/i)).not.toBeInTheDocument();
    });

    it('hides the Upgrade tab from a non-admin', async () => {
        renderSystem(false);
        expect(await screen.findByRole('tab', { name: /appearance/i })).toBeInTheDocument();
        expect(screen.queryByRole('tab', { name: /^upgrade$/i })).not.toBeInTheDocument();
    });

    it('hides the MCP tab from a non-admin', async () => {
        renderSystem(false);
        expect(await screen.findByRole('tab', { name: /appearance/i })).toBeInTheDocument();
        expect(screen.queryByRole('tab', { name: /^mcp$/i })).not.toBeInTheDocument();
    });
});
