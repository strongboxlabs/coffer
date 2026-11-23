import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { McpConnectedAppsPanel } from './McpConnectedAppsPanel';
import * as apiModule from '@/lib/api';
import type { McpClient, McpConnection, McpSetting } from '@/lib/api';

// Connected apps (ADR-0113): one list, each app with the live sign-ins made through it, and
// personal tokens beside them. It replaced two lists that described the same thing from two
// sides with nothing joining them: a client said "1 connection(s)" that no connection row
// matched, and two apps of one name could not be told apart.

const FAR = '2099-01-01T00:00:00Z';

const CLAUDE: McpClient = {
    clientId: 'c-claude', displayName: 'Claude', clientType: 'public',
    redirectUris: ['http://localhost:3118/callback'], liveConnections: 1,
    registeredAt: '2026-10-01T12:00:00Z',
};
const HOSTED: McpClient = {
    clientId: 'c-hosted', displayName: 'Hosted', clientType: 'confidential',
    redirectUris: ['https://connector.example.com/cb'], liveConnections: 1, registeredAt: null,
};
const IDLE: McpClient = {
    clientId: 'c-idle', displayName: 'Idle app', clientType: 'public',
    redirectUris: ['http://127.0.0.1:5525/oauth/callback'], liveConnections: 0, registeredAt: null,
};

const TOKEN: McpConnection = {
    kind: 'token', id: 't-1', userId: 'u-alice', user: 'Alice Example',
    name: 'Desk token', label: null, appName: null, clientId: null, canWrite: false,
    connectedAt: '2026-10-01T00:00:00Z', lastUsedAt: null, expiresAt: FAR,
};
const GRANT: McpConnection = {
    kind: 'oauth', id: 'g-1', userId: 'u-bob', user: 'Bob Example',
    name: 'Claude', label: null, appName: 'Claude', clientId: 'c-claude', canWrite: true,
    connectedAt: '2026-10-02T00:00:00Z', lastUsedAt: '2026-10-20T15:30:00Z', expiresAt: FAR,
};
const HOSTED_GRANT: McpConnection = {
    ...GRANT, id: 'g-2', name: 'Hosted', appName: 'Hosted', clientId: 'c-hosted',
    lastUsedAt: null,
};

const SETTING = (writesActive: boolean): McpSetting => ({
    enabled: true, active: true, configForced: false,
    writesEnabled: writesActive, writesActive, writesConfigForced: false,
    publicUrl: 'https://mcp.example.com/mcp',
});

function mock(clients: McpClient[], connections: McpConnection[], writesActive = true) {
    vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue(clients);
    vi.spyOn(apiModule, 'fetchMcpConnections').mockResolvedValue(connections);
    vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue(SETTING(writesActive));
}

function renderPanel() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
        <QueryClientProvider client={qc}>
            <McpConnectedAppsPanel />
        </QueryClientProvider>,
    );
    return qc;
}

/** The list item for an app, found by its name. */
const appRow = async (name: string) => (await screen.findByText(name, { selector: 'span' })).closest('li')!;

describe('McpConnectedAppsPanel', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('puts each sign-in under the app it was made through, and tokens on their own', async () => {
        mock([CLAUDE, HOSTED], [TOKEN, GRANT, HOSTED_GRANT]);
        renderPanel();

        const claude = await appRow('Claude');
        const claudeSignIns = within(claude).getByRole('list', { name: 'Sign-ins through Claude' });
        expect(within(claudeSignIns).getAllByRole('listitem')).toHaveLength(1);
        expect(within(claudeSignIns).getByText('Bob Example')).toBeInTheDocument();

        const tokens = screen.getByRole('list', { name: 'Personal tokens' });
        expect(within(tokens).getByText('Alice Example')).toBeInTheDocument();
        expect(within(tokens).getByText('Desk token')).toBeInTheDocument();
    });

    it('always names the person who holds a sign-in, and the sign-in’s own name beside it', async () => {
        mock([CLAUDE], [{ ...GRANT, name: 'Bob laptop', label: 'Bob laptop' }]);
        renderPanel();

        const row = (await screen.findByText('Bob laptop')).closest('li')!;
        expect(within(row).getByText('Bob Example')).toBeInTheDocument();
    });

    it('tells two apps apart by where they run, the callback host and when they registered', async () => {
        mock([CLAUDE, HOSTED], [GRANT, HOSTED_GRANT]);
        renderPanel();

        const claude = await appRow('Claude');
        expect(within(claude).getByText(/App on a person’s device · callback on localhost:3118/)).toBeInTheDocument();
        expect(within(claude).getByText(/registered/)).toBeInTheDocument();
        const hosted = await appRow('Hosted');
        expect(within(hosted).getByText(/Hosted service · connector\.example\.com/)).toBeInTheDocument();
        // Registered before the time was recorded: nothing, rather than a made-up date.
        expect(within(hosted).queryByText(/registered/)).toBeNull();
    });

    it('gives each app one status: active with its last use, no use recorded, or no live sign-in', async () => {
        mock([CLAUDE, HOSTED, IDLE], [GRANT, HOSTED_GRANT]);
        renderPanel();

        expect(within(await appRow('Claude')).getByText(/^Active · last used/)).toBeInTheDocument();
        expect(within(await appRow('Hosted')).getByText('No use recorded')).toBeInTheDocument();
        const idle = await appRow('Idle app');
        expect(within(idle).getByText('No live sign-in')).toBeInTheDocument();
        expect(within(idle).getByText(/0 sign-ins/)).toBeInTheDocument();
    });

    it('lists the most recently used app first and apps with no live sign-in last', async () => {
        mock([IDLE, HOSTED, CLAUDE], [GRANT, HOSTED_GRANT]);
        renderPanel();

        await appRow('Claude');
        const names = screen.getAllByText(/^(Claude|Hosted|Idle app)$/, { selector: 'span.font-medium' })
            .map((e) => e.textContent);
        expect(names).toEqual(['Claude', 'Hosted', 'Idle app']);
    });

    it('says read-only only where it differs from the deployment: with AI writes on', async () => {
        mock([CLAUDE], [TOKEN, GRANT], true);
        renderPanel();

        const token = (await screen.findByText('Desk token')).closest('li')!;
        expect(within(token).getByText(/read-only/)).toBeInTheDocument();
        const grant = (await screen.findByText('Bob Example')).closest('li')!;
        expect(within(grant).queryByText(/read-only|can make changes/)).toBeNull();
    });

    it('says nothing about write access with AI writes off, since nothing can write', async () => {
        mock([CLAUDE], [TOKEN, GRANT], false);
        const qc = renderPanel();

        const token = (await screen.findByText('Desk token')).closest('li')!;
        // Anchor on the settled row and the settled setting, then show the mark is absent:
        // before the setting lands the mark is absent whatever the setting says.
        expect(within(token).getByText(/never used/)).toBeInTheDocument();
        await waitFor(() => expect(qc.getQueryState(['admin-mcp-setting'])?.status).toBe('success'));
        expect(within(token).queryByText(/read-only/)).toBeNull();
    });

    it('does not call an unrecorded OAuth use "never", but does call an unused token that', async () => {
        mock([CLAUDE], [TOKEN, { ...GRANT, lastUsedAt: null }]);
        renderPanel();

        const token = (await screen.findByText('Desk token')).closest('li')!;
        expect(within(token).getByText(/never used/)).toBeInTheDocument();
        const grant = screen.getByText('Bob Example').closest('li')!;
        expect(within(grant).getByText(/no use recorded/)).toBeInTheDocument();
        expect(within(grant).queryByText(/never used/)).toBeNull();
    });

    it('flags a sign-in about to expire, and an app about to lose all its access', async () => {
        const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000 + 60_000).toISOString();
        mock([CLAUDE], [{ ...GRANT, expiresAt: soon }]);
        renderPanel();

        const app = await appRow('Claude');
        expect(within(app).getByText(/access ends in 3 days/)).toBeInTheDocument();
        expect(within(app).getByText(/expires in 3 days/)).toBeInTheDocument();
    });

    // --- revoke one sign-in, disconnect an app, remove the unused ---------------------

    it('names whose sign-in is ending before it revokes, and revokes only that one', async () => {
        mock([CLAUDE], [TOKEN, GRANT]);
        const revoke = vi.spyOn(apiModule, 'revokeMcpConnection').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        const row = (await screen.findByText('Bob Example')).closest('li')!;
        await user.click(within(row).getByRole('button', { name: /^revoke$/i }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/Bob Example/)).toBeInTheDocument();
        expect(revoke).not.toHaveBeenCalled();

        await user.click(within(dialog).getByRole('button', { name: /^revoke$/i }));
        expect(revoke).toHaveBeenCalledTimes(1);
        expect(revoke).toHaveBeenCalledWith('oauth', 'g-1');
    });

    it('says how many sign-ins and whose before it disconnects an app', async () => {
        mock([CLAUDE], [GRANT, { ...GRANT, id: 'g-3', userId: 'u-carol', user: 'Carol Example' }]);
        const disconnect = vi.spyOn(apiModule, 'revokeMcpClient').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(within(await appRow('Claude')).getByRole('button', { name: 'Disconnect app…' }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/Ends 2 sign-ins through this app/)).toBeInTheDocument();
        expect(within(dialog).getByText(/Bob Example, Carol Example/)).toBeInTheDocument();
        expect(disconnect).not.toHaveBeenCalled();

        await user.click(within(dialog).getByRole('button', { name: 'Disconnect' }));
        expect(disconnect).toHaveBeenCalledWith('c-claude');
    });

    it('shows why a disconnect failed', async () => {
        mock([CLAUDE], [GRANT]);
        vi.spyOn(apiModule, 'revokeMcpClient').mockRejectedValue(
            new apiModule.ApiError(500, 'The server could not do that.'),
        );
        const user = userEvent.setup();
        renderPanel();

        await user.click(within(await appRow('Claude')).getByRole('button', { name: 'Disconnect app…' }));
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Disconnect' }));

        expect(await screen.findByRole('alert')).toHaveTextContent(/could not do that/i);
    });

    it('offers to remove never-used apps only when there is one, and names each before it does', async () => {
        mock([CLAUDE, IDLE], [GRANT]);
        const prune = vi.spyOn(apiModule, 'pruneMcpClients').mockResolvedValue({ pruned: 1 });
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: 'Remove never-used apps' }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText('Idle app')).toBeInTheDocument();
        expect(within(dialog).getByText(/127\.0\.0\.1/)).toBeInTheDocument();
        expect(within(dialog).queryByText('Claude')).toBeNull();
        expect(prune).not.toHaveBeenCalled();

        await user.click(within(dialog).getByRole('button', { name: 'Remove 1 app' }));
        expect(prune).toHaveBeenCalledTimes(1);
    });

    it('has no remove button when every app has a live sign-in', async () => {
        mock([CLAUDE], [GRANT]);
        renderPanel();

        expect(within(await appRow('Claude')).getByText(/^Active/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Remove never-used apps' })).toBeNull();
    });

    // --- rename -------------------------------------------------------------------------

    it('renames an OAuth sign-in, addressed by kind and id', async () => {
        mock([CLAUDE], [GRANT]);
        const rename = vi.spyOn(apiModule, 'renameMcpConnection').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.type(screen.getByLabelText(/name for claude/i), 'Phone');
        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(rename).toHaveBeenCalledWith('oauth', 'g-1', 'Phone');
    });

    it('clears an OAuth sign-in’s name when it is saved blank', async () => {
        mock([CLAUDE], [{ ...GRANT, name: 'Phone', label: 'Phone' }]);
        const rename = vi.spyOn(apiModule, 'renameMcpConnection').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.clear(screen.getByLabelText(/name for phone/i));
        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(rename).toHaveBeenCalledWith('oauth', 'g-1', '');
    });

    it('will not save a token blank: it has no other name to fall back to', async () => {
        mock([], [TOKEN]);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.clear(screen.getByLabelText(/name for desk token/i));

        expect(screen.getByRole('button', { name: /^save$/i })).toBeDisabled();
    });

    it('shows why a rename failed, and keeps what was typed', async () => {
        mock([CLAUDE], [GRANT]);
        vi.spyOn(apiModule, 'renameMcpConnection').mockRejectedValue(
            new apiModule.ApiError(422, 'No such connection.', 'mcp-connection-not-found'),
        );
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.type(screen.getByLabelText(/name for claude/i), 'Phone');
        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(/no such connection/i);
        expect(screen.getByLabelText(/name for claude/i)).toHaveValue('Phone');
    });

    it('does not save on cancel', async () => {
        mock([CLAUDE], [GRANT]);
        const rename = vi.spyOn(apiModule, 'renameMcpConnection').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.type(screen.getByLabelText(/name for claude/i), 'Discarded');
        await user.click(screen.getByRole('button', { name: /^cancel$/i }));

        expect(rename).not.toHaveBeenCalled();
        expect(screen.getByText('Bob Example')).toBeInTheDocument();
    });

    // ADR-0023 §L: the rename form goes on a save that lands, Cancel or Escape,
    // and the row's Rename takes its place and its focus, never the page. Each
    // waits for the form to be gone before it looks at focus.
    describe('focus when the rename form goes', () => {
        const renameOf = (text: string) =>
            within(screen.getByText(text).closest('li')!).getByRole('button', { name: /^rename$/i });

        it('after a save that lands', async () => {
            mock([CLAUDE], [TOKEN, GRANT]);
            vi.spyOn(apiModule, 'renameMcpConnection').mockResolvedValue(undefined);
            const user = userEvent.setup();
            renderPanel();

            await user.click(within((await screen.findByText('Bob Example')).closest('li')!).getByRole('button', { name: /^rename$/i }));
            await user.type(screen.getByLabelText(/name for claude/i), '{Enter}');

            await waitFor(() => expect(screen.queryByLabelText(/name for claude/i)).toBeNull());
            expect(renameOf('Bob Example')).toHaveFocus();
        });

        it('after Cancel', async () => {
            mock([CLAUDE], [TOKEN, GRANT]);
            const user = userEvent.setup();
            renderPanel();

            await user.click(within((await screen.findByText('Desk token')).closest('li')!).getByRole('button', { name: /^rename$/i }));
            await user.click(screen.getByRole('button', { name: /^cancel$/i }));

            expect(screen.queryByLabelText(/name for desk token/i)).toBeNull();
            expect(renameOf('Desk token')).toHaveFocus();
        });

        it('after Escape in the name field', async () => {
            mock([CLAUDE], [TOKEN, GRANT]);
            const user = userEvent.setup();
            renderPanel();

            await user.click(within((await screen.findByText('Bob Example')).closest('li')!).getByRole('button', { name: /^rename$/i }));
            await user.keyboard('{Escape}');

            expect(screen.queryByLabelText(/name for claude/i)).toBeNull();
            expect(renameOf('Bob Example')).toHaveFocus();
        });
    });

    // ADR-0023 §L: while the rename is in flight the form is pending: its
    // Cancel and Escape are refused and the field is read-only, so nothing typed
    // is dropped by the save's success and a refusal still has its form.
    it('holds the whole form while the rename is in flight, and keeps it open on a refusal', async () => {
        mock([CLAUDE], [GRANT]);
        let refuse!: () => void;
        vi.spyOn(apiModule, 'renameMcpConnection').mockImplementation(() => new Promise((_, reject) => {
            refuse = () => reject(new apiModule.ApiError(422, 'No such connection.', 'mcp-connection-not-found'));
        }));
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /^rename$/i }));
        const field = screen.getByLabelText(/name for claude/i);
        await user.type(field, 'Phone{Enter}');
        const cancel = screen.getByRole('button', { name: /^cancel$/i });
        await waitFor(() => expect(cancel).toHaveAttribute('aria-disabled', 'true'));
        expect(field).toHaveAttribute('readonly');

        await user.click(cancel);
        await user.type(field, 'X{Escape}');
        expect(screen.getByLabelText(/name for claude/i)).toHaveValue('Phone');

        refuse();
        expect(await screen.findByRole('alert')).toHaveTextContent(/no such connection/i);
        expect(screen.getByLabelText(/name for claude/i)).toHaveValue('Phone');
    });

    it('says so when nothing is connected', async () => {
        mock([], []);
        renderPanel();

        expect(await screen.findByText(/nothing is connected yet/i)).toBeInTheDocument();
    });

    // --- focus when a row goes (ADR-0023 §L) -------------------------------------------

    it('gives focus to the list once the last revoked row has gone', async () => {
        mock([], [TOKEN]);
        vi.spyOn(apiModule, 'revokeMcpConnection').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /^revoke$/i }));
        vi.spyOn(apiModule, 'fetchMcpConnections').mockResolvedValue([]);
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /^revoke$/i }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect(screen.getByText(/nothing is connected yet/i)).toBeInTheDocument();
        const list = screen.getByRole('group', { name: 'Connected apps' });
        expect(list).toHaveAttribute('tabindex', '-1');
        expect(list).toHaveFocus();
    });

    it('gives focus to the next app’s Disconnect once the disconnected app has gone', async () => {
        mock([CLAUDE, HOSTED], [GRANT, HOSTED_GRANT]);
        vi.spyOn(apiModule, 'revokeMcpClient').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(within(await appRow('Claude')).getByRole('button', { name: 'Disconnect app…' }));
        vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue([HOSTED]);
        vi.spyOn(apiModule, 'fetchMcpConnections').mockResolvedValue([HOSTED_GRANT]);
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Disconnect' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        await waitFor(() => expect(screen.queryByText('Claude', { selector: 'span' })).toBeNull());
        expect(within(await appRow('Hosted')).getByRole('button', { name: 'Disconnect app…' })).toHaveFocus();
    });

    it('gives focus to the list when removing leaves no unused app, taking its button with it', async () => {
        mock([IDLE], []);
        vi.spyOn(apiModule, 'pruneMcpClients').mockResolvedValue({ pruned: 1 });
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: 'Remove never-used apps' }));
        vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue([]);
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove 1 app' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect(screen.getByText(/nothing is connected yet/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Remove never-used apps' })).toBeNull();
        expect(screen.getByRole('group', { name: 'Connected apps' })).toHaveFocus();
    });
});
