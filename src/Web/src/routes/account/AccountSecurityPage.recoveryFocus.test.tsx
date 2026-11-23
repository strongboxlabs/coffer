import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';

import { AccountSecurityPage } from './AccountSecurityPage';

// Regenerate codes opens a confirm, and the confirm hands over to the fresh
// codes. Closing the codes gives focus back to Regenerate codes (ADR-0023 §L).
//
// The API is stubbed at fetch, so the page's own mutation runs as it does in the
// browser. The confirm hands over to the codes while the regenerate is still
// pending, when Regenerate codes is pending too, not disabled: the confirm can
// give focus back to it, and the codes give it back on close. A regenerate's
// success refreshes the code count; that read is held here until the codes are
// on screen.

// The passkey tap a regenerate needs (ADR-0105 D1); jsdom has no WebAuthn.
vi.mock('@simplewebauthn/browser', async (importOriginal) => ({
    ...(await importOriginal<typeof import('@simplewebauthn/browser')>()),
    startAuthentication: vi.fn(async () => ({ id: 'cred-1', rawId: 'cred-1', type: 'public-key', response: {} })),
}));

const CODES = ['NEW01-AAAAA', 'NEW02-BBBBB'];

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

/** Holds every read of the code count made after a regenerate until `release`. */
function holdCountAfterRegenerate(stub: FetchStub) {
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
        release = resolve;
    });
    const routed = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const answer = routed(input, init);
        const path = new URL(String(input), 'http://localhost').pathname;
        const regenerated = stub.callsTo('POST', /\/recovery-codes\/regenerate$/).length > 0;
        if (regenerated && path === '/api/auth/recovery-codes') await held;
        return answer;
    });
    return release;
}

describe('AccountSecurityPage: regenerating recovery codes, where focus goes', () => {
    let stub: FetchStub;

    beforeEach(() => {
        vi.restoreAllMocks();
        stub = stubFetch()
            .on('GET', /^\/api\/auth\/credentials$/, [
                { id: 'c1', nickname: 'Key', createdAt: '2026-06-01T00:00:00Z', lastUsedAt: null },
            ])
            .on('GET', /^\/api\/auth\/recovery-codes$/, () => (
                stub.callsTo('POST', /\/recovery-codes\/regenerate$/).length > 0
                    ? { remaining: 10, total: 10 }
                    : { remaining: 8, total: 10 }
            ))
            .on('GET', /^\/api\/auth\/sessions$/, [])
            .on('GET', /^\/api\/account\/mcp-tokens$/, [])
            .on('GET', /^\/api\/account\/oauth-grants$/, [])
            .on('POST', /^\/api\/auth\/step-up\/begin$/, {
                challengeId: 'challenge-1',
                assertionOptions: { challenge: 'Y2hhbGxlbmdl', allowCredentials: [] },
            })
            .on('POST', /^\/api\/auth\/recovery-codes\/regenerate$/, { recoveryCodes: CODES });
    });

    afterEach(() => vi.unstubAllGlobals());

    it('gives focus back to Regenerate codes when the fresh codes close', async () => {
        const release = holdCountAfterRegenerate(stub);
        renderSecurity();
        const user = userEvent.setup();
        expect(await screen.findByText(/8 of 10 remaining/)).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Regenerate codes' }));
        const confirm = await screen.findByRole('dialog', { name: 'Regenerate recovery codes?' });
        await user.click(within(confirm).getByRole('button', { name: 'Regenerate' }));

        // The codes are up, in place of the confirm, while the count is still
        // being read again.
        expect(await screen.findByText(CODES[0])).toBeInTheDocument();
        const codes = screen.getByRole('dialog', { name: 'Save your recovery codes' });
        expect(within(codes).getByText(CODES[1])).toBeInTheDocument();
        expect(screen.getByText(/8 of 10 remaining/)).toBeInTheDocument();

        // Regenerate codes was never disabled on the way (jsdom keeps focus on a
        // control that becomes disabled, where a browser drops it to the page).
        expect(screen.getByRole('button', { name: 'Regenerate codes' })).toBeEnabled();
        release();
        expect(await screen.findByText(/10 of 10 remaining/)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Regenerate codes' })).toBeEnabled();

        await user.click(within(codes).getByRole('checkbox', { name: /I have saved my recovery codes/ }));
        await user.click(within(codes).getByRole('button', { name: 'Continue' }));

        expect(screen.queryByRole('dialog')).toBeNull();
        expect(screen.getByRole('button', { name: 'Regenerate codes' })).toHaveFocus();
    });
});
