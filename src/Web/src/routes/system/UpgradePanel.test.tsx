import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import type { UpgradeAttempt, UpgradeStatus } from '@/lib/types';

import { stepUpHeaders } from '@/lib/api/stepUp';

import { UpgradePanel } from './UpgradePanel';

// The passkey ceremony needs a real authenticator, so the one seam mocked is the tap itself.
// Everything else goes through the real client and a stubbed `fetch`, so a request that is never
// sent, or sent to the wrong route or with the wrong body, fails here instead of passing against a
// mocked client (ADR-0110).
vi.mock('@/lib/api/stepUp', async (importOriginal) => ({
    ...(await importOriginal<typeof import('@/lib/api/stepUp')>()),
    stepUpHeaders: vi.fn(async () => ({ 'X-Coffer-Step-Up': 'tap-proof' })),
}));

const INSTALL_DIR = '/srv/coffer';

function status(over: Partial<UpgradeStatus> = {}): UpgradeStatus {
    return {
        installed: '0.128.1',
        inAppEnabled: true,
        autoCheck: false,
        privateRepo: false,
        tokenStored: false,
        repository: 'example/coffer',
        installDir: INSTALL_DIR,
        hostNote: '',
        host: { state: 'running', at: '2026-10-31T12:00:00Z' },
        latest: { version: '0.129.0', notes: 'Example note.', url: 'https://github.com/example/coffer/releases/tag/v0.129.0', publishedAt: '2026-10-28T09:00:00Z' },
        checkedAt: '2026-10-31T10:00:00Z',
        checkError: null,
        updateAvailable: true,
        canStart: true,
        cannotStartReason: null,
        attempt: null,
        ...over,
    };
}

function attempt(over: Partial<UpgradeAttempt> = {}): UpgradeAttempt {
    return { from: '0.128.1', to: '0.129.0', phase: 'started', startedAtUtc: '2026-10-31T11:55:00Z', message: null, changed: false, ...over };
}

interface Call { method: string; path: string; body: unknown; headers: Record<string, string> }

/** Stub `fetch`: each route answers with a status and body, or a function of the call. Records every call. */
function stubFetch(routes: Record<string, (call: Call) => { status?: number; body?: unknown } | Promise<never>>) {
    const calls: Call[] = [];
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit = {}) => {
        const call: Call = {
            method: init.method ?? 'GET',
            path,
            body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
            headers: (init.headers ?? {}) as Record<string, string>,
        };
        calls.push(call);
        const route = routes[`${call.method} ${path}`];
        if (!route) throw new Error(`unexpected request ${call.method} ${path}`);
        const result = await route(call);
        const code = result.status ?? 200;
        return new Response(code === 204 ? null : JSON.stringify(result.body ?? {}), {
            status: code,
            headers: { 'Content-Type': 'application/json' },
        });
    }));
    return calls;
}

function renderPanel(onOpenBackups = vi.fn()) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <UpgradePanel onOpenBackups={onOpenBackups} />
        </QueryClientProvider>,
    );
    return { onOpenBackups, queryClient };
}

describe('UpgradePanel', () => {
    beforeEach(() => vi.useRealTimers());
    afterEach(() => {
        vi.unstubAllGlobals();
        vi.useRealTimers();
    });

    it('shows the new version and its notes as plain text, never as markup', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({ latest: { ...status().latest!, notes: '<img src=x onerror=alert(1)> **bold**' } }),
            }),
        });
        renderPanel();

        expect(await screen.findByText('Update available')).toBeInTheDocument();
        expect(screen.getByText('0.129.0')).toBeInTheDocument();
        // Present as text, and no element was made from it.
        expect(screen.getByText(/<img src=x onerror=alert\(1\)> \*\*bold\*\*/)).toBeInTheDocument();
        expect(document.querySelector('img')).toBeNull();
    });

    it('keeps the button off until the phrase matches, then sends the tap, version and phrase', async () => {
        const calls = stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status() }),
            'POST /api/admin/upgrade/preflight': () => ({ status: 204 }),
            'POST /api/admin/upgrade': () => ({ status: 202, body: status({ attempt: attempt({ phase: 'backing-up' }) }) }),
        });
        renderPanel();
        const user = userEvent.setup();

        const button = await screen.findByRole('button', { name: /upgrade to 0\.129\.0/i });
        expect(button).toBeDisabled();
        await user.type(screen.getByLabelText(/type upgrade to confirm/i), 'nope');
        expect(button).toBeDisabled();

        await user.clear(screen.getByLabelText(/type upgrade to confirm/i));
        await user.type(screen.getByLabelText(/type upgrade to confirm/i), '  Upgrade ');
        expect(button).toBeEnabled();
        await user.click(button);

        // The progress card replaces the form, and the request carried what the server checks.
        expect(await screen.findByRole('list', { name: /upgrade progress/i })).toBeInTheDocument();
        const start = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/upgrade');
        expect(start?.body).toEqual({ version: '0.129.0', phrase: '  Upgrade ' });
        expect(start?.headers['X-Coffer-Step-Up']).toBe('tap-proof');
        // The server was asked first, with the same body and no tap, and the tap came after it.
        const order = calls.filter((c) => c.method === 'POST').map((c) => c.path);
        expect(order).toEqual(['/api/admin/upgrade/preflight', '/api/admin/upgrade']);
        const preflight = calls.find((c) => c.path === '/api/admin/upgrade/preflight');
        expect(preflight?.body).toEqual({ version: '0.129.0', phrase: '  Upgrade ' });
        expect(preflight?.headers['X-Coffer-Step-Up']).toBeUndefined();
    });

    // ADR-0023 §L: the phrase field is pending while the start is in flight, since
    // the start's success clears it and would drop anything typed meanwhile.
    it('refuses typing in the phrase while the start is in flight', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status() }),
            'POST /api/admin/upgrade/preflight': () => new Promise<never>(() => {}),
        });
        renderPanel();
        const user = userEvent.setup();

        const phrase = await screen.findByLabelText(/type upgrade to confirm/i);
        await user.type(phrase, 'upgrade');
        await user.click(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i }));
        await screen.findByRole('button', { name: /confirm on your passkey/i });

        await user.type(phrase, ' more');
        expect(phrase).toHaveValue('upgrade');
        expect(phrase).toHaveAttribute('aria-disabled', 'true');
    });

    it('asks before the passkey, and a refusal for a stale release shows the newer one without a tap', async () => {
        vi.mocked(stepUpHeaders).mockClear();
        let reads = 0;
        const newer = status({ latest: { ...status().latest!, version: '0.130.0' } });
        const calls = stubFetch({
            // The screen first holds 0.129.0; once the server has refused, it reads 0.130.0.
            'GET /api/admin/upgrade': () => ({ body: ++reads === 1 ? status() : newer }),
            'POST /api/admin/upgrade/preflight': () => ({
                status: 422,
                body: { code: 'upgrade-nothing-newer', detail: '0.128.1 is installed and 0.130.0 is the latest release; check again.' },
            }),
        });
        renderPanel();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/type upgrade to confirm/i), 'upgrade');
        await user.click(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i }));

        // The page now offers 0.130.0, and says so.
        expect(await screen.findByRole('button', { name: /upgrade to 0\.130\.0/i })).toBeInTheDocument();
        expect(screen.getByRole('alert')).toHaveTextContent(/0\.130\.0 is now the latest release/);
        // No passkey was asked for, and no start was sent.
        expect(stepUpHeaders).not.toHaveBeenCalled();
        expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/upgrade')).toBe(false);
    });

    it('names a missing backup passphrase before any passkey, in the server\'s words', async () => {
        vi.mocked(stepUpHeaders).mockClear();
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status() }),
            'POST /api/admin/upgrade/preflight': () => ({
                status: 422,
                body: { code: 'backup-passphrase-not-set', detail: 'Set a backup passphrase under Backups first.' },
            }),
        });
        renderPanel();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/type upgrade to confirm/i), 'upgrade');
        await user.click(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(/Set a backup passphrase under Backups first/);
        expect(stepUpHeaders).not.toHaveBeenCalled();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeInTheDocument();
    });

    it('still shows a refusal that only the start itself can give', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status() }),
            'POST /api/admin/upgrade/preflight': () => ({ status: 204 }),
            'POST /api/admin/upgrade': () => ({
                status: 422,
                body: { code: 'backup-in-progress', detail: 'A backup is running. Wait for it to finish, then start the upgrade.' },
            }),
        });
        renderPanel();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/type upgrade to confirm/i), 'upgrade');
        await user.click(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(/A backup is running/);
    });

    it('says why it cannot start and shows the command that fixes it, with the real install path', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({
                    host: { state: 'disabled', at: '2026-10-31T12:00:00Z' },
                    canStart: false,
                    cannotStartReason: 'The host updater is disabled on the host.',
                }),
            }),
        });
        renderPanel();

        expect(await screen.findByText('The host updater is disabled on the host.')).toBeInTheDocument();
        expect(screen.getByText(`bash ${INSTALL_DIR}/app-upgrades.sh enable`)).toBeInTheDocument();
        expect(screen.getByText(`bash ${INSTALL_DIR}/upgrade.sh`)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeDisabled();
        expect(screen.queryByLabelText(/type upgrade to confirm/i)).toBeNull();
    });

    it('quotes an install path that has a space, so the command pastes as shown', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({ installDir: '/srv/my coffer', canStart: false, cannotStartReason: 'x', hostNote: 'No cron daemon was found.', host: { state: 'absent', at: null } }),
            }),
        });
        renderPanel();

        expect(await screen.findByText(`bash '/srv/my coffer/upgrade.sh'`)).toBeInTheDocument();
        expect(screen.getByText('No cron daemon was found.')).toBeInTheDocument();
    });

    it('sends one save for two settings changed back to back, so the second never puts the first back', async () => {
        // Each setting saves all three, built from the status last read: one
        // group (pending.ts), so the first change holds every setting until its
        // save has landed.
        const calls = stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ inAppEnabled: true, autoCheck: false }) }),
            'PUT /api/admin/upgrade/settings': () => new Promise<never>(() => {}),
        });
        renderPanel();

        const inApp = await screen.findByRole('checkbox', { name: /allow upgrades from this app/i });
        act(() => { inApp.click(); });
        act(() => { screen.getByRole('checkbox', { name: /check for new releases automatically/i }).click(); });
        await new Promise((resolve) => { setTimeout(resolve, 20); });

        const saves = calls.filter((c) => c.method === 'PUT' && c.path === '/api/admin/upgrade/settings');
        expect(saves).toHaveLength(1);
        expect(saves[0]!.body).toMatchObject({ inAppEnabled: false, autoCheck: false });
    });

    it('shows up to date with no form when there is nothing newer', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ updateAvailable: false, canStart: false, cannotStartReason: 'There is no newer release.', latest: { ...status().latest!, version: '0.128.1' } }) }),
        });
        renderPanel();

        expect(await screen.findByText('Up to date')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^upgrade to/i })).toBeNull();
    });

    it('shows the last good result with the failure when the check fails', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ checkError: 'GitHub did not answer.' }) }),
        });
        renderPanel();

        expect(await screen.findByText(/The last check failed: GitHub did not answer\./)).toBeInTheDocument();
        expect(screen.getByText('0.129.0')).toBeInTheDocument();
    });

    it('runs Check now through the check route and shows what it found', async () => {
        const calls = stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ updateAvailable: false, canStart: false, latest: null, checkedAt: null }) }),
            'POST /api/admin/upgrade/check': () => ({ body: status() }),
        });
        renderPanel();
        const user = userEvent.setup();

        expect(await screen.findByText('Not checked yet')).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: /check now/i }));

        expect(await screen.findByText('Update available')).toBeInTheDocument();
        expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/upgrade/check')).toBe(true);
    });

    it('asks the gear and the tabs again after a check, so a found release lights the dot at once', async () => {
        // A release found by Check now must show on the System gear exactly as one found by the
        // automatic check does. The page's own answer is not enough: the dot reads a different
        // query, and left alone it would stay dark until the next focus or the next minute.
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ updateAvailable: false, canStart: false, latest: null, checkedAt: null }) }),
            'POST /api/admin/upgrade/check': () => ({ body: status() }),
        });
        const { queryClient } = renderPanel();
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
        const user = userEvent.setup();

        await screen.findByText('Not checked yet');
        await user.click(screen.getByRole('button', { name: /check now/i }));
        await screen.findByText('Update available');

        expect(invalidate).toHaveBeenCalledWith({ queryKey: ['system-attention'] });
    });

    it('keeps polling through the restart, says it is waiting, and lands on done', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                // The API is down while the host swaps it.
                if (poll === 2) return Promise.reject(new TypeError('Failed to fetch'));
                return { body: status({ installed: '0.129.0', updateAvailable: false, attempt: attempt({ phase: 'done', message: 'Coffer is now on 0.129.0.' }) }) };
            },
        });
        renderPanel();

        expect(await screen.findByRole('list', { name: /upgrade progress/i })).toBeInTheDocument();
        expect(screen.queryByText(/waiting for coffer to restart/i)).toBeNull();

        await act(() => vi.advanceTimersByTimeAsync(3100));
        // The last good answer stays on screen, with the note that the page is waiting.
        expect(await screen.findByText(/waiting for coffer to restart/i)).toBeInTheDocument();
        expect(screen.getByRole('list', { name: /upgrade progress/i })).toBeInTheDocument();
        expect(screen.getByText(`bash ${INSTALL_DIR}/app-upgrades.sh status`)).toBeInTheDocument();

        await act(() => vi.advanceTimersByTimeAsync(3100));
        expect(await screen.findByText('Coffer is now on 0.129.0.')).toBeInTheDocument();
        expect(screen.queryByText(/waiting for coffer to restart/i)).toBeNull();
        expect(poll).toBe(3);
        // The release card is still there under the result, with Check now.
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
    });

    it('offers a reload with the result, because the page that watched it is the old version', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        const reload = vi.fn();
        const original = window.location;
        Object.defineProperty(window, 'location', { configurable: true, value: { ...original, reload } });
        try {
            let poll = 0;
            stubFetch({
                'GET /api/admin/upgrade': () => {
                    poll += 1;
                    if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                    return { body: status({ installed: '0.129.0', updateAvailable: false, attempt: attempt({ phase: 'done', message: 'Coffer is now on 0.129.0.' }) }) };
                },
            });
            renderPanel();
            const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

            await screen.findByRole('list', { name: /upgrade progress/i });
            await act(() => vi.advanceTimersByTimeAsync(3100));

            expect(await screen.findByText(/This page is still the old version/)).toBeInTheDocument();
            await user.click(screen.getByRole('button', { name: /^reload$/i }));
            expect(reload).toHaveBeenCalledTimes(1);
        } finally {
            Object.defineProperty(window, 'location', { configurable: true, value: original });
        }
    });

    it('asks again for the gear and the tab dots when polling sees an upgrade finish', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                return { body: status({ installed: '0.129.0', updateAvailable: false, attempt: attempt({ phase: 'done', message: 'Coffer is now on 0.129.0.' }) }) };
            },
        });
        const { queryClient } = renderPanel();
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

        await screen.findByRole('list', { name: /upgrade progress/i });
        expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['system-attention'] });
        await act(() => vi.advanceTimersByTimeAsync(3100));
        await screen.findByText('Coffer is now on 0.129.0.');

        expect(invalidate).toHaveBeenCalledWith({ queryKey: ['system-attention'] });
    });

    it('shows no result card for an upgrade that finished before this page was opened', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({ updateAvailable: false, attempt: attempt({ phase: 'done', message: 'Coffer is now on 0.129.0.' }) }),
            }),
        });
        renderPanel();

        // What the page shows instead is the release card, with the way to look again.
        expect(await screen.findByRole('button', { name: /check now/i })).toBeInTheDocument();
        expect(screen.queryByText('Coffer is now on 0.129.0.')).toBeNull();
        expect(screen.queryByRole('button', { name: /open backups/i })).toBeNull();
    });

    it('does not poll when no upgrade is running', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        const calls = stubFetch({ 'GET /api/admin/upgrade': () => ({ body: status() }) });
        renderPanel();

        await screen.findByText('Update available');
        await act(() => vi.advanceTimersByTimeAsync(10_000));
        expect(calls.filter((c) => c.path === '/api/admin/upgrade')).toHaveLength(1);
    });

    it('opens Backups from the done screen', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                return { body: status({ updateAvailable: false, attempt: attempt({ phase: 'done', message: 'Coffer is now on 0.129.0.' }) }) };
            },
        });
        const { onOpenBackups } = renderPanel();
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

        await screen.findByRole('list', { name: /upgrade progress/i });
        await act(() => vi.advanceTimersByTimeAsync(3100));
        await user.click(await screen.findByRole('button', { name: /open backups/i }));
        expect(onOpenBackups).toHaveBeenCalledTimes(1);
    });

    it('shows a failure that changed nothing to the page that watched it, beside the form, and it can be dismissed', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'requested' }) }) };
                return { body: status({ attempt: attempt({ phase: 'failed', changed: false, message: 'A check before any change failed, so nothing was changed.' }) }) };
            },
        });
        renderPanel();
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

        await screen.findByRole('list', { name: /upgrade progress/i });
        await act(() => vi.advanceTimersByTimeAsync(3100));

        expect(await screen.findByText('Nothing was changed')).toBeInTheDocument();
        // The way forward is not hidden behind it.
        expect(screen.getByLabelText(/type upgrade to confirm/i)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: /dismiss/i }));
        expect(screen.queryByText('Nothing was changed')).toBeNull();
        expect(screen.getByLabelText(/type upgrade to confirm/i)).toBeInTheDocument();
    });

    it('shows nothing about a failure that changed nothing on another visit, only the form', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({ attempt: attempt({ phase: 'failed', changed: false, message: 'A check before any change failed, so nothing was changed.' }) }),
            }),
        });
        renderPanel();

        expect(await screen.findByLabelText(/type upgrade to confirm/i)).toBeInTheDocument();
        expect(screen.queryByText('Nothing was changed')).toBeNull();
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
    });

    it('while the host is blocked after a failure that changed the install, names the host commands and keeps Check now', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({
                    host: { state: 'blocked', at: '2026-10-31T12:00:00Z' },
                    canStart: false,
                    cannotStartReason: 'An earlier upgrade failed after it began changing this install, so the host refuses new ones. Run ack on the host once you have looked.',
                    attempt: attempt({ phase: 'failed', changed: true, message: 'The upgrade failed after it began changing this install.' }),
                }),
            }),
        });
        renderPanel();

        expect(await screen.findByText('This install was changed')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /try again/i })).toBeNull();
        expect(screen.getByText(`bash ${INSTALL_DIR}/app-upgrades.sh status`)).toBeInTheDocument();
        // The ack command is shown once, in the card, and not again beside the disabled button.
        expect(screen.getAllByText(`bash ${INSTALL_DIR}/app-upgrades.sh ack`)).toHaveLength(1);
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeDisabled();
        expect(screen.queryByLabelText(/type upgrade to confirm/i)).toBeNull();
    });

    it('stops showing that failure once the host is no longer blocked, on another visit', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({
                    attempt: attempt({ phase: 'failed', changed: true, message: 'The upgrade failed after it began changing this install.' }),
                }),
            }),
        });
        renderPanel();

        // ack was run on the host: the heartbeat is running again, so the failure is the host's old news.
        expect(await screen.findByLabelText(/type upgrade to confirm/i)).toBeInTheDocument();
        expect(screen.queryByText('This install was changed')).toBeNull();
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
    });

    it('shows the ack command beside the disabled button when the host is blocked and no failure is on screen', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({
                    host: { state: 'blocked', at: '2026-10-31T12:00:00Z' },
                    canStart: false,
                    cannotStartReason: 'An earlier upgrade failed after it began changing this install, so the host refuses new ones. Run ack on the host once you have looked.',
                }),
            }),
        });
        renderPanel();

        expect(await screen.findByText(/the host refuses new ones/)).toBeInTheDocument();
        expect(screen.getByText(`bash ${INSTALL_DIR}/app-upgrades.sh ack`)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeDisabled();
    });

    it('keeps a failure the page watched beside a form that cannot start, even while the heartbeat still says running', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                // The tick that reported the failure has not written its heartbeat yet.
                return { body: status({ attempt: attempt({ phase: 'failed', changed: true, message: 'The upgrade failed after it began changing this install.' }) }) };
            },
        });
        renderPanel();

        await screen.findByRole('list', { name: /upgrade progress/i });
        await act(() => vi.advanceTimersByTimeAsync(3100));

        expect(await screen.findByText('This install was changed')).toBeInTheDocument();
        // The server says it can start; the page must not offer it beside the failure.
        expect(screen.queryByLabelText(/type upgrade to confirm/i)).toBeNull();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeDisabled();
        expect(screen.getByRole('button', { name: /check now/i })).toBeInTheDocument();
    });

    it('drops the failure it watched once the host has said blocked and then stopped, which is ack', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        const failed = attempt({ phase: 'failed', changed: true, message: 'The upgrade failed after it began changing this install.' });
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started' }) }) };
                return { body: status({ host: { state: 'blocked', at: '2026-10-31T12:00:00Z' }, canStart: false, cannotStartReason: 'blocked', attempt: failed }) };
            },
            // After ack the heartbeat is running again; the failure is still the host's last word.
            'POST /api/admin/upgrade/check': () => ({ body: status({ attempt: failed }) }),
        });
        renderPanel();
        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

        await screen.findByRole('list', { name: /upgrade progress/i });
        await act(() => vi.advanceTimersByTimeAsync(3100));
        expect(await screen.findByText('This install was changed')).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: /check now/i }));

        await waitFor(() => expect(screen.queryByText('This install was changed')).toBeNull());
        expect(screen.getByLabelText(/type upgrade to confirm/i)).toBeInTheDocument();
    });

    it('keeps the server\'s own words when a refusal finds nothing newer to offer', async () => {
        let reads = 0;
        stubFetch({
            // The install was upgraded to the very release the page showed, from elsewhere.
            'GET /api/admin/upgrade': () => ({
                body: ++reads === 1
                    ? status()
                    : status({ installed: '0.129.0', updateAvailable: false, canStart: false, cannotStartReason: 'There is no newer release.' }),
            }),
            'POST /api/admin/upgrade/preflight': () => ({
                status: 422,
                body: { code: 'upgrade-nothing-newer', detail: '0.129.0 is installed and 0.129.0 is the latest release; check again.' },
            }),
        });
        renderPanel();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText(/type upgrade to confirm/i), 'upgrade');
        await user.click(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i }));

        expect(await screen.findByText('Up to date')).toBeInTheDocument();
        // No "press Upgrade again": there is no such button now.
        expect(screen.queryByText(/press upgrade again/i)).toBeNull();
        expect(screen.getByRole('alert')).toHaveTextContent(/0\.129\.0 is installed/);
    });

    it('offers no reload when the page is already the version the upgrade installed', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        let poll = 0;
        stubFetch({
            'GET /api/admin/upgrade': () => {
                poll += 1;
                if (poll === 1) return { body: status({ attempt: attempt({ phase: 'started', to: __APP_VERSION__ }) }) };
                return { body: status({ installed: __APP_VERSION__, updateAvailable: false, attempt: attempt({ phase: 'done', to: __APP_VERSION__, message: `Coffer is now on ${__APP_VERSION__}.` }) }) };
            },
        });
        renderPanel();

        await screen.findByRole('list', { name: /upgrade progress/i });
        await act(() => vi.advanceTimersByTimeAsync(3100));

        expect(await screen.findByText(`Coffer is now on ${__APP_VERSION__}.`)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^reload$/i })).toBeNull();
        expect(screen.queryByText(/still the old version/)).toBeNull();
    });
});

describe('UpgradePanel settings', () => {
    afterEach(() => vi.unstubAllGlobals());

    it('saves a switch with the other two unchanged, and the switch is the confirmation', async () => {
        let autoCheck = false;
        const calls = stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ inAppEnabled: true, autoCheck, privateRepo: false }) }),
            'PUT /api/admin/upgrade/settings': () => { autoCheck = true; return { status: 204 }; },
        });
        renderPanel();
        const user = userEvent.setup();

        const box = await screen.findByRole('checkbox', { name: /check for new releases automatically/i });
        await user.click(box);

        await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
        expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ inAppEnabled: true, autoCheck: true, privateRepo: false });
        // The switch stays on once the save has gone through; that, not a message, is what says so. The
        // settled switch is also the anchor for the absence: the message would have rendered with it.
        await waitFor(() => expect(box).toBeChecked());
        expect(screen.queryByText('Saved.')).toBeNull();
        expect(screen.queryByRole('status')).toBeNull();
    });

    it('shows the token field only for a private fork, and never shows the token back', async () => {
        let stored = false;
        const calls = stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ privateRepo: true, tokenStored: stored }) }),
            'PUT /api/admin/upgrade/token': () => { stored = true; return { status: 204 }; },
        });
        renderPanel();
        const user = userEvent.setup();

        const field = await screen.findByLabelText('New token');
        expect(field).toHaveAttribute('type', 'password');
        expect(screen.getByText(/not stored/i)).toBeInTheDocument();

        await user.type(field, 'ghp_exampletoken');
        await user.click(screen.getByRole('button', { name: /save token/i }));

        // The token's status is the confirmation: it reads Stored, with no message besides.
        expect(await screen.findByText(/sealed with the master key/i)).toBeInTheDocument();
        expect(screen.queryByText('Token stored.')).toBeNull();
        expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/admin/upgrade/token')?.body).toEqual({ token: 'ghp_exampletoken' });
        // Cleared from the field, and nothing on the page echoes it.
        expect(screen.getByLabelText('New token')).toHaveValue('');
        expect(document.body.textContent).not.toContain('ghp_exampletoken');
        // The cleared field leaves Save token disabled; focus went on to the field.
        expect(screen.getByRole('button', { name: /save token/i })).toBeDisabled();
        expect(screen.getByLabelText('New token')).toHaveFocus();
    });

    // ADR-0023 §L: the token field is pending while the token is stored, since
    // the save's success clears it and would drop anything typed meanwhile.
    it('refuses typing in the token field while the token is stored', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ privateRepo: true }) }),
            'PUT /api/admin/upgrade/token': () => new Promise<never>(() => {}),
        });
        renderPanel();
        const user = userEvent.setup();

        const field = await screen.findByLabelText('New token');
        await user.type(field, 'ghp_exampletoken');
        await user.click(screen.getByRole('button', { name: /save token/i }));
        await screen.findByRole('button', { name: /saving/i });

        await user.type(field, 'more');
        expect(field).toHaveValue('ghp_exampletoken');
        expect(field).toHaveAttribute('aria-disabled', 'true');
    });

    // ADR-0023 §L: Remove holds focus while pending, and the status read after
    // the clear takes it away; focus goes to the token field, never the page.
    it('hands focus to the token field once a removed token takes Remove away', async () => {
        let stored = true;
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ privateRepo: true, tokenStored: stored }) }),
            'DELETE /api/admin/upgrade/token': () => { stored = false; return { status: 204 }; },
        });
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^remove$/i }));

        // Settled: the status reads Not stored and Remove is gone.
        expect(await screen.findByText(/not stored/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^remove$/i })).toBeNull();
        expect(screen.getByLabelText('New token')).toHaveFocus();
    });

    it('hides the token field when the repository is public', async () => {
        stubFetch({ 'GET /api/admin/upgrade': () => ({ body: status({ privateRepo: false }) }) });
        renderPanel();

        await screen.findByRole('checkbox', { name: /private fork/i });
        expect(screen.queryByLabelText('New token')).toBeNull();
    });

    it('shows a rejected token in the server\'s words', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({ body: status({ privateRepo: true }) }),
            'PUT /api/admin/upgrade/token': () => ({ status: 422, body: { code: 'upgrade-token-invalid', detail: 'Enter the token as one value, without spaces, up to 500 characters.' } }),
        });
        renderPanel();
        const user = userEvent.setup();

        await user.type(await screen.findByLabelText('New token'), 'two words');
        await user.click(screen.getByRole('button', { name: /save token/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(/without spaces/);
    });

    it('reports the host updater state', async () => {
        stubFetch({ 'GET /api/admin/upgrade': () => ({ body: status({ host: { state: 'stale', at: '2026-10-31T11:40:00Z' } }) }) });
        renderPanel();

        expect(await screen.findByText(/not responding/i)).toBeInTheDocument();
    });

    it('says when the host is blocked or cannot tell the installed version, and does not call it never answered', async () => {
        stubFetch({ 'GET /api/admin/upgrade': () => ({ body: status({ host: { state: 'blocked', at: '2026-10-31T12:00:00Z' }, canStart: false, cannotStartReason: 'blocked' }) }) });
        renderPanel();
        expect(await screen.findByText(/blocked after a failed upgrade/i)).toBeInTheDocument();
        expect(screen.queryByText(/never answered/i)).toBeNull();
    });

    it('tells an install that tracks the latest tag to upgrade by hand', async () => {
        stubFetch({
            'GET /api/admin/upgrade': () => ({
                body: status({
                    host: { state: 'unpinned', at: '2026-10-31T12:00:00Z' },
                    canStart: false,
                    cannotStartReason: 'This install tracks the latest tag, so the host cannot tell which version it runs. Upgrade it by hand on the host.',
                }),
            }),
        });
        renderPanel();

        expect(await screen.findByText(/tracks the latest tag/)).toBeInTheDocument();
        expect(screen.getByText(`bash ${INSTALL_DIR}/upgrade.sh`)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /upgrade to 0\.129\.0/i })).toBeDisabled();
        expect(screen.getByText(/cannot tell the installed version/i)).toBeInTheDocument();
    });
});
