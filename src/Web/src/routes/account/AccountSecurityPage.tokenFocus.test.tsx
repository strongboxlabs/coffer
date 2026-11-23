import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';

import { AccountSecurityPage } from './AccountSecurityPage';

// Connected apps: Generate makes a token and opens "Copy your token now".
// Making it clears the name, so Generate is disabled until a new name is typed
// and cannot take focus back when the dialog closes. Focus goes to the token
// name field instead, never to the page (ADR-0023 §L).
//
// The API is stubbed at fetch, so the page's own mutation runs as it does in
// the browser.

// The passkey tap a token needs (ADR-0105 D1); jsdom has no WebAuthn.
vi.mock('@simplewebauthn/browser', async (importOriginal) => ({
    ...(await importOriginal<typeof import('@simplewebauthn/browser')>()),
    startAuthentication: vi.fn(async () => ({ id: 'cred-1', rawId: 'cred-1', type: 'public-key', response: {} })),
    // The authenticator's answer to adding a passkey.
    startRegistration: vi.fn(async () => ({ id: 'cred-2', rawId: 'cred-2', type: 'public-key', response: {} })),
}));

function renderSecurity() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const securityRoute = createRoute({
        getParentRoute: () => root,
        path: '/account/security',
        component: AccountSecurityPage,
    });
    const router = createRouter({
        routeTree: root.addChildren([securityRoute]),
        history: createMemoryHistory({ initialEntries: ['/account/security'] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('AccountSecurityPage: generating a token, where focus goes', () => {
    let stub: FetchStub;

    beforeEach(() => {
        vi.restoreAllMocks();
        stub = stubFetch()
            .on('GET', /^\/api\/auth\/credentials$/, [
                { id: 'c1', nickname: 'Key', createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null },
            ])
            .on('GET', /^\/api\/auth\/recovery-codes$/, { remaining: 8, total: 10 })
            .on('GET', /^\/api\/auth\/sessions$/, [])
            .on('GET', /^\/api\/account\/mcp-tokens$/, () => (
                stub.callsTo('POST', /^\/api\/account\/mcp-tokens$/).length > 0
                    ? [{ id: 't1', name: 'Desktop app', scopes: 'read', createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null, expiresAt: null }]
                    : []
            ))
            .on('GET', /^\/api\/account\/oauth-grants$/, [])
            .on('POST', /^\/api\/auth\/step-up\/begin$/, {
                challengeId: 'challenge-1',
                assertionOptions: { challenge: 'Y2hhbGxlbmdl', allowCredentials: [] },
            })
            .on('POST', /^\/api\/account\/mcp-tokens$/, {
                id: 't1', name: 'Desktop app', scopes: 'read', expiresAt: null, token: 'synthetic-token-value',
            });
    });

    afterEach(() => vi.unstubAllGlobals());

    it('gives focus to the token name field when the token dialog closes', async () => {
        renderSecurity();
        const user = userEvent.setup();
        const name = await screen.findByLabelText('Generate a token');

        await user.type(name, 'Desktop app');
        await user.click(screen.getByRole('button', { name: 'Generate' }));
        const issued = await screen.findByRole('dialog', { name: 'Copy your token now' });
        expect(within(issued).getByDisplayValue('synthetic-token-value')).toBeInTheDocument();

        // The name was cleared, so Generate cannot take focus back.
        expect(screen.getByRole('button', { name: 'Generate' })).toBeDisabled();
        await user.click(within(issued).getByRole('button', { name: 'Done' }));

        expect(screen.queryByRole('dialog', { name: 'Copy your token now' })).toBeNull();
        expect(name).toHaveValue('');
        expect(name).toHaveFocus();
    });

    // The token list is read again once the token is made. The dialog can close
    // before that read answers, and the name field must take focus then too: it
    // is not held disabled by the read.
    it.each(['Done', 'Escape'])('gives focus to the name field when %s closes the dialog before the list is read again',
        async (how) => {
            const answer = globalThis.fetch;
            vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
                const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
                const method = (init?.method ?? 'GET').toUpperCase();
                const reread = method === 'GET' && new URL(raw, 'http://localhost').pathname === '/api/account/mcp-tokens'
                    && stub.callsTo('POST', /^\/api\/account\/mcp-tokens$/).length > 0;
                if (reread) await new Promise(() => {});
                return answer(input, init);
            });
            renderSecurity();
            const user = userEvent.setup();
            const name = await screen.findByLabelText('Generate a token');

            await user.type(name, 'Desktop app');
            await user.click(screen.getByRole('button', { name: 'Generate' }));
            const issued = await screen.findByRole('dialog', { name: 'Copy your token now' });
            expect(name).toBeEnabled();

            if (how === 'Done') await user.click(within(issued).getByRole('button', { name: 'Done' }));
            else await user.keyboard('{Escape}');

            expect(screen.queryByRole('dialog', { name: 'Copy your token now' })).toBeNull();
            expect(name).toHaveFocus();
        });
});

// Passkeys: an added passkey clears the nickname, which leaves Add disabled with
// focus on it. Focus goes on to the nickname field, which is disabled while the
// add is in flight and takes focus once it is enabled again.
describe('AccountSecurityPage: adding a passkey, where focus goes', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        const stub: FetchStub = stubFetch()
            .on('GET', /^\/api\/auth\/credentials$/, () => (
                stub.callsTo('POST', /^\/api\/auth\/register\/complete$/).length > 0
                    ? [
                        { id: 'c1', nickname: 'Key', createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null },
                        { id: 'c2', nickname: 'Second key', createdAt: '2026-06-02T00:00:00Z', lastUsedAt: null },
                    ]
                    : [{ id: 'c1', nickname: 'Key', createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null }]
            ))
            .on('GET', /^\/api\/auth\/recovery-codes$/, { remaining: 8, total: 10 })
            .on('GET', /^\/api\/auth\/sessions$/, [])
            .on('GET', /^\/api\/account\/mcp-tokens$/, [])
            .on('GET', /^\/api\/account\/oauth-grants$/, [])
            .on('POST', /^\/api\/auth\/register\/begin$/, {
                challengeId: 'challenge-2',
                options: { challenge: 'Y2hhbGxlbmdl', rp: { name: 'Coffer' }, user: { id: 'dQ', name: 'user', displayName: 'User' }, pubKeyCredParams: [] },
            })
            .on('POST', /^\/api\/auth\/register\/complete$/, {
                id: 'c2', nickname: 'Second key', createdAt: '2026-06-02T00:00:00Z', lastUsedAt: null,
            });
    });

    afterEach(() => vi.unstubAllGlobals());

    it('gives focus to the nickname field when the added passkey clears it and leaves Add disabled', async () => {
        renderSecurity();
        const user = userEvent.setup();
        const nickname = await screen.findByLabelText('Add a passkey');

        await user.type(nickname, 'Second key');
        const add = screen.getByRole('button', { name: 'Add' });
        await user.click(add);

        expect(await screen.findByText('Second key')).toBeInTheDocument();
        await waitFor(() => expect(nickname).toBeEnabled());
        expect(nickname).toHaveValue('');
        expect(add).toBeDisabled();
        expect(nickname).toHaveFocus();
    });

    it('gives the nickname field focus before the passkey list is read again', async () => {
        const answer = globalThis.fetch;
        let added = false;
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            const path = new URL(raw, 'http://localhost').pathname;
            if (path === '/api/auth/register/complete') added = true;
            else if (added && path === '/api/auth/credentials') await new Promise(() => {});
            return answer(input, init);
        });
        renderSecurity();
        const user = userEvent.setup();
        const nickname = await screen.findByLabelText('Add a passkey');

        await user.type(nickname, 'Second key');
        const add = screen.getByRole('button', { name: 'Add' });
        await user.click(add);

        await waitFor(() => expect(nickname).toHaveValue(''));
        await waitFor(() => expect(nickname).toHaveFocus());
        expect(add).toBeDisabled();
        expect(add).not.toHaveAttribute('aria-busy');
    });
});
