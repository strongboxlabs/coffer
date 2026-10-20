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

    it('lists the apps connected through OAuth and disconnects one', async () => {
        vi.spyOn(authModule, 'fetchCredentials').mockResolvedValue([]);
        vi.spyOn(mcpModule, 'fetchOAuthGrants').mockResolvedValue([
            {
                id: 'g-1', appName: 'Claude', canWrite: true,
                connectedAt: '2026-10-01T00:00:00Z', expiresAt: '2026-12-30T00:00:00Z',
            },
        ]);
        const revoke = vi.spyOn(mcpModule, 'revokeOAuthGrant').mockResolvedValue(undefined);

        renderSecurity();

        const row = (await screen.findByText('Claude')).closest('li')!;
        expect(within(row).getByText(/can make changes/)).toBeInTheDocument();
        await userEvent.click(within(row).getByRole('button', { name: /disconnect/i }));
        await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /disconnect/i }));
        expect(revoke).toHaveBeenCalledWith('g-1');
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
