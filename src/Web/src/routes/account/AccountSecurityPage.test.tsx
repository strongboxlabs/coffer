import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { AccountSecurityPage } from './AccountSecurityPage';
import * as authModule from '@/lib/auth';
import type { CredentialSummary } from '@/lib/auth';
import * as mcpModule from '@/lib/api/mcp';
import { ApiError } from '@/lib/api';

// AccountSecurityPage tests (ADR-0013). Locked down:
//   * passkeys list renders; the last one's Remove is disabled
//   * a second passkey makes Remove available
//   * recovery-code count renders; regenerate shows the fresh codes once

function makeCred(id: string, nickname: string): CredentialSummary {
    return { id, nickname, createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null };
}

function renderSecurity(search = '') {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const root = createRootRoute();
    const securityRoute = createRoute({
        getParentRoute: () => root,
        path: '/account/security',
        component: AccountSecurityPage,
        validateSearch: (s: Record<string, unknown>): { recovered?: boolean } =>
            s.recovered === true || s.recovered === 'true' ? { recovered: true } : {},
    });
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <main>home</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([securityRoute, landingRoute]),
        history: createMemoryHistory({ initialEntries: [`/account/security${search}`] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('AccountSecurityPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(authModule, 'fetchRecoveryCodesStatus').mockResolvedValue({
            remaining: 8,
            total: 10,
        });
        // Connected apps: default to an empty list so the section renders
        // without hitting the network. Per-test overrides below.
        vi.spyOn(mcpModule, 'fetchMcpTokens').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([]);
        vi.spyOn(authModule, 'fetchSessions').mockResolvedValue([]);
    });

    // --- sessions and OAuth grants (FU-087) ---------------------------------

    it('lists the sessions, marks this one, and signs out another', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(authModule, 'fetchSessions').mockResolvedValue([
            {
                id: 's-here', createdAt: '2026-10-01T09:00:00Z', lastSeenAt: '2026-10-19T09:00:00Z',
                userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36',
                authMethod: 'passkey', current: true,
            },
            {
                id: 's-phone', createdAt: '2026-10-05T09:00:00Z', lastSeenAt: '2026-10-18T09:00:00Z',
                userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1',
                authMethod: 'passkey', current: false,
            },
        ]);
        const signOut = vi.spyOn(authModule, 'signOutSession').mockResolvedValue(undefined);

        renderSecurity();

        const here = (await screen.findByText('Chrome on Windows')).closest('li')!;
        expect(within(here).getByText('This device')).toBeInTheDocument();
        expect(within(here).queryByRole('button', { name: /sign out/i })).toBeNull();

        const phone = screen.getByText('Safari on iOS').closest('li')!;
        await userEvent.click(within(phone).getByRole('button', { name: /sign out/i }));
        expect(signOut).toHaveBeenCalledWith('s-phone');
    });

    it('gives focus to the next session\'s Sign out when a signed-out session leaves the list', async () => {
        // ADR-0023 §L: a row removed by its own button hands focus to the same
        // control on the next row, else the previous one; never to the page.
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        const session = (id: string, userAgent: string, current = false) => ({
            id, createdAt: '2026-10-01T09:00:00Z', lastSeenAt: '2026-10-19T09:00:00Z',
            userAgent, authMethod: 'passkey' as const, current,
        });
        let sessions = [
            session('s-here', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36', true),
            session('s-phone', 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1'),
            session('s-mac', 'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) Gecko/20100101 Firefox/131.0'),
        ];
        vi.spyOn(authModule, 'fetchSessions').mockImplementation(async () => sessions);
        vi.spyOn(authModule, 'signOutSession').mockImplementation(async (id: string) => {
            sessions = sessions.filter((s) => s.id !== id);
        });
        const user = userEvent.setup();
        renderSecurity();

        const phone = (await screen.findByText('Safari on iOS')).closest('li')!;
        const mac = screen.getByText(/on macOS/).closest('li')!;
        within(phone).getByRole('button', { name: /sign out/i }).focus();
        await user.keyboard('{Enter}');

        await vi.waitFor(() => expect(screen.queryByText('Safari on iOS')).toBeNull());
        expect(within(mac).getByRole('button', { name: /sign out/i })).toHaveFocus();
    });

    it('gives focus to the sessions list when the last other session is signed out', async () => {
        // ADR-0023 §L: the list has no add control, and this device's row has no
        // Sign out, so the last other session's Sign out hands focus to the list
        // itself: named, and no Tab stop of its own.
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        const session = (id: string, userAgent: string, current = false) => ({
            id, createdAt: '2026-10-01T09:00:00Z', lastSeenAt: '2026-10-19T09:00:00Z',
            userAgent, authMethod: 'passkey' as const, current,
        });
        let sessions = [
            session('s-here', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36', true),
            session('s-phone', 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1'),
        ];
        vi.spyOn(authModule, 'fetchSessions').mockImplementation(async () => sessions);
        vi.spyOn(authModule, 'signOutSession').mockImplementation(async (id: string) => {
            sessions = sessions.filter((s) => s.id !== id);
        });
        const user = userEvent.setup();
        renderSecurity();

        const phone = (await screen.findByText('Safari on iOS')).closest('li')!;
        within(phone).getByRole('button', { name: /^sign out$/i }).focus();
        await user.keyboard('{Enter}');

        await vi.waitFor(() => expect(screen.queryByText('Safari on iOS')).toBeNull());
        const list = screen.getByRole('list', { name: 'Sessions' });
        expect(within(list).getByText('This device')).toBeInTheDocument();
        expect(list).toHaveAttribute('tabindex', '-1');
        expect(list).toHaveFocus();
    });

    function grant(over: Partial<mcpModule.OAuthGrantSummary> = {}): mcpModule.OAuthGrantSummary {
        return {
            id: 'g-1', appName: 'Claude', canWrite: true,
            connectedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-12-30T00:00:00Z',
            label: null, lastUsedAt: null,
            ...over,
        };
    }

    it('lists the apps connected through OAuth and revokes one', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant()]);
        const revoke = vi.spyOn(mcpModule, 'revokeOAuthGrant').mockResolvedValue(undefined);

        renderSecurity();

        const row = (await screen.findByText('Claude')).closest('li')!;
        expect(within(row).getByText(/can make changes/)).toBeInTheDocument();
        await userEvent.click(within(row).getByRole('button', { name: /revoke/i }));
        await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /revoke/i }));
        expect(revoke).toHaveBeenCalledWith('g-1');
        // One word for ending a connection, tokens and sign-ins alike.
        expect(screen.queryByRole('button', { name: /disconnect/i })).toBeNull();
    });

    it('says when an OAuth connection was last used, and says so honestly when no use is recorded', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([
            grant({ id: 'g-used', appName: 'Used app', lastUsedAt: '2026-10-20T15:30:00Z' }),
            grant({ id: 'g-fresh', appName: 'Fresh app', lastUsedAt: null }),
        ]);

        renderSecurity();

        const used = (await screen.findByText('Used app')).closest('li')!;
        expect(within(used).getByText(/last used/)).toBeInTheDocument();
        // Not "never used": a connection from before use was recorded would be wrongly
        // called unused.
        const fresh = screen.getByText('Fresh app').closest('li')!;
        expect(within(fresh).getByText(/no use recorded/)).toBeInTheDocument();
        expect(within(fresh).queryByText(/never used/)).toBeNull();
    });

    it('renames an OAuth connection, keeping the app’s own name beside it', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant({ label: 'Laptop' })]);
        const rename = vi.spyOn(mcpModule, 'renameOAuthGrant').mockResolvedValue(undefined);

        renderSecurity();

        const row = (await screen.findByText('Laptop')).closest('li')!;
        expect(within(row).getByText('(Claude)')).toBeInTheDocument();
        await userEvent.click(within(row).getByRole('button', { name: /rename/i }));
        const field = within(row).getByRole('textbox');
        await userEvent.clear(field);
        await userEvent.type(field, 'Phone');
        await userEvent.click(within(row).getByRole('button', { name: /save/i }));

        expect(rename).toHaveBeenCalledWith('g-1', 'Phone');
    });

    it('clears an OAuth connection’s name when it is saved blank', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant({ label: 'Laptop' })]);
        const rename = vi.spyOn(mcpModule, 'renameOAuthGrant').mockResolvedValue(undefined);

        renderSecurity();

        const row = (await screen.findByText('Laptop')).closest('li')!;
        await userEvent.click(within(row).getByRole('button', { name: /rename/i }));
        await userEvent.clear(within(row).getByRole('textbox'));
        await userEvent.click(within(row).getByRole('button', { name: /save/i }));

        expect(rename).toHaveBeenCalledWith('g-1', '');
    });

    it('shows why a rename failed and keeps what was typed', async () => {
        // A failed save used to be swallowed: the button blinked and nothing said why.
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant()]);
        vi.spyOn(mcpModule, 'renameOAuthGrant').mockRejectedValue(
            new ApiError(422, 'No connected app with that id for this user.', 'oauth-grant-not-found'),
        );

        renderSecurity();

        const row = (await screen.findByText('Claude')).closest('li')!;
        await userEvent.click(within(row).getByRole('button', { name: /rename/i }));
        await userEvent.type(within(row).getByRole('textbox'), 'Phone');
        await userEvent.click(within(row).getByRole('button', { name: /save/i }));

        expect(await within(row).findByRole('alert')).toHaveTextContent(/no connected app/i);
        expect(within(row).getByRole('textbox')).toHaveValue('Phone');
    });

    it('renames a token, and will not save it blank', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchMcpTokens').mockResolvedValue([
            {
                id: 't-1', name: 'Desk', scopes: 'coffer.read',
                createdAt: '2026-10-01T00:00:00Z', lastUsedAt: null, expiresAt: null,
            },
        ]);
        const rename = vi.spyOn(mcpModule, 'renameMcpToken').mockResolvedValue(undefined);

        renderSecurity();

        const row = (await screen.findByText('Desk')).closest('li')!;
        expect(within(row).getByText(/never used/)).toBeInTheDocument();
        await userEvent.click(within(row).getByRole('button', { name: /rename/i }));
        const field = within(row).getByRole('textbox');
        await userEvent.clear(field);
        // A token has no name to fall back to, so blank cannot be saved.
        expect(within(row).getByRole('button', { name: /save/i })).toBeDisabled();
        await userEvent.type(field, 'Laptop');
        await userEvent.click(within(row).getByRole('button', { name: /save/i }));

        expect(rename).toHaveBeenCalledWith('t-1', 'Laptop');
    });

    // ADR-0023 §L: the rename form goes on a save that lands, Cancel or Escape,
    // and the row's Rename takes its place and its focus, in either list. Each
    // waits for the form to be gone before it looks at focus.
    describe('focus when a rename form goes', () => {
        const TOKEN = {
            id: 't-1', name: 'Desk', scopes: 'coffer.read',
            createdAt: '2026-10-01T00:00:00Z', lastUsedAt: null, expiresAt: null,
        };
        const renameIn = (row: HTMLElement) => within(row).getByRole('button', { name: /^rename$/i });

        it('a token\'s, after a save that lands', async () => {
            vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
            vi.spyOn(mcpModule, 'fetchMcpTokens').mockResolvedValue([TOKEN]);
            vi.spyOn(mcpModule, 'renameMcpToken').mockResolvedValue(undefined);
            renderSecurity();

            const row = (await screen.findByText('Desk')).closest('li')!;
            await userEvent.click(renameIn(row));
            await userEvent.type(within(row).getByRole('textbox'), ' A{Enter}');

            await vi.waitFor(() => expect(within(row).queryByRole('textbox')).toBeNull());
            expect(renameIn(row)).toHaveFocus();
        });

        it('a token\'s, after Escape', async () => {
            vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
            vi.spyOn(mcpModule, 'fetchMcpTokens').mockResolvedValue([TOKEN]);
            renderSecurity();

            const row = (await screen.findByText('Desk')).closest('li')!;
            await userEvent.click(renameIn(row));
            await userEvent.keyboard('{Escape}');

            expect(within(row).queryByRole('textbox')).toBeNull();
            expect(renameIn(row)).toHaveFocus();
        });

        it('a sign-in\'s, after its Save lands', async () => {
            vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
            vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant()]);
            vi.spyOn(mcpModule, 'renameOAuthGrant').mockResolvedValue(undefined);
            renderSecurity();

            const row = (await screen.findByText('Claude')).closest('li')!;
            await userEvent.click(renameIn(row));
            await userEvent.type(within(row).getByRole('textbox'), 'Phone');
            await userEvent.click(within(row).getByRole('button', { name: /^save$/i }));

            await vi.waitFor(() => expect(within(row).queryByRole('textbox')).toBeNull());
            expect(renameIn(row)).toHaveFocus();
        });

        it('a sign-in\'s, after Cancel', async () => {
            vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
            vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([grant()]);
            renderSecurity();

            const row = (await screen.findByText('Claude')).closest('li')!;
            await userEvent.click(renameIn(row));
            await userEvent.click(within(row).getByRole('button', { name: /^cancel$/i }));

            expect(within(row).queryByRole('textbox')).toBeNull();
            expect(renameIn(row)).toHaveFocus();
        });
    });

    it('disables Remove for the only passkey when there are no recovery codes', async () => {
        // No fallback login → removing the last passkey would lock the user out.
        vi.spyOn(authModule, 'fetchRecoveryCodesStatus').mockResolvedValue({ remaining: 0, total: 10 });
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([
            makeCred('c1', 'only-key'),
        ]);

        renderSecurity();

        const row = (await screen.findByText('only-key')).closest('li')!;
        expect(within(row).getByRole('button', { name: /remove/i })).toBeDisabled();
    });

    it('allows removing the only passkey when recovery codes exist', async () => {
        // beforeEach mocks 8 unused recovery codes → a fallback login exists, so a
        // dead last passkey (e.g. left over after an address change) can be cleared.
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([
            makeCred('c1', 'only-key'),
        ]);

        renderSecurity();

        const row = (await screen.findByText('only-key')).closest('li')!;
        expect(within(row).getByRole('button', { name: /remove/i })).toBeEnabled();
    });

    it('allows Remove when more than one passkey exists', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([
            makeCred('c1', 'key-one'),
            makeCred('c2', 'key-two'),
        ]);

        renderSecurity();

        const row = (await screen.findByText('key-two')).closest('li')!;
        expect(within(row).getByRole('button', { name: /remove/i })).toBeEnabled();
    });

    it('shows the remaining recovery-code count', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);

        renderSecurity();

        expect(await screen.findByText(/8 of 10 remaining/i)).toBeInTheDocument();
    });

    it('shows the fresh codes after regenerating', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);
        vi.spyOn(authModule, 'regenerateRecoveryCodes').mockResolvedValue([
            'NEW01-AAAAA',
            'NEW02-BBBBB',
        ]);

        renderSecurity();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /regenerate codes/i }));
        // ConfirmDialog → affirmative
        await user.click(await screen.findByRole('button', { name: /^regenerate$/i }));

        expect(await screen.findByText('NEW01-AAAAA')).toBeInTheDocument();
        expect(screen.getByText('NEW02-BBBBB')).toBeInTheDocument();
    });

    it('shows the recovery banner when arriving with ?recovered', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);

        renderSecurity('?recovered=true');

        expect(
            await screen.findByText(/signed in with a recovery code/i),
        ).toBeInTheDocument();
    });

    it('lists connected-app tokens with a revoke action', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);
        vi.spyOn(mcpModule, 'fetchMcpTokens').mockResolvedValue([
            {
                id: 't1',
                name: 'Claude Desktop',
                scopes: 'coffer.read',
                createdAt: '2026-06-01T00:00:00Z',
                lastUsedAt: null,
                expiresAt: null,
            },
        ]);

        renderSecurity();

        const row = (await screen.findByText('Claude Desktop')).closest('li')!;
        expect(within(row).getByRole('button', { name: /revoke/i })).toBeEnabled();
    });

    it('shows the new token exactly once after generating', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);
        vi.spyOn(mcpModule, 'createMcpToken').mockResolvedValue({
            id: 't1',
            name: 'Claude Desktop',
            scopes: 'coffer.read',
            expiresAt: null,
            token: 'coffer_mcp_SECRETVALUE',
        });

        renderSecurity();
        const user = userEvent.setup();

        await user.type(await screen.findByPlaceholderText(/claude desktop/i), 'Claude Desktop');
        await user.click(screen.getByRole('button', { name: /^generate$/i }));

        expect(await screen.findByText(/copy your token now/i)).toBeInTheDocument();
        expect(screen.getByDisplayValue('coffer_mcp_SECRETVALUE')).toBeInTheDocument();
    });

    it('shows the turned-off state when MCP is disabled (404)', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([makeCred('c1', 'k')]);
        vi.spyOn(mcpModule, 'fetchMcpTokens').mockRejectedValue(new ApiError(404, 'Not Found'));

        renderSecurity();

        expect(
            await screen.findByText(/turned off for this deployment/i),
        ).toBeInTheDocument();
    });
});
