import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ConsentPage } from './ConsentPage';
import * as authModule from '@/lib/api/auth';
import * as mcpModule from '@/lib/api/mcp';
import { ApiError } from '@/lib/api/_request';

// ConsentPage (ADR-0063): shows the requesting client + the granted scope (read-only
// vs full read+write, ADR-0081) — as the SERVER registered them, fetched by the
// consent ticket (2026-10-12 security audit, H1) — and, on a decision, replays the
// OAuth request with the ticket as a POST to /oauth/authorize (M1).

function renderConsent(query: string) {
    window.history.replaceState({}, '', `/oauth/consent${query}`);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <ConsentPage />
        </QueryClientProvider>,
    );
}

function serverInfo(overrides: Partial<mcpModule.OAuthConsentInfo> = {}): mcpModule.OAuthConsentInfo {
    return {
        clientId: 'abc123',
        clientName: 'Claude Desktop',
        redirectUri: 'http://localhost:35535/cb',
        redirectHost: 'localhost:35535',
        scopes: ['coffer.read'],
        ...overrides,
    };
}

describe('ConsentPage', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(authModule, 'fetchCurrentUser').mockResolvedValue({
            id: 'u1',
            username: 'alice',
            displayName: 'Alice',
            isAdmin: false,
        });
    });

    afterEach(() => {
        window.history.replaceState({}, '', '/');
        // decide() appends a form to document.body; clear it so a later test
        // doesn't read a stale one.
        document.body.querySelectorAll('form').forEach((f) => f.remove());
    });

    it('shows what the server registered, not what the link says', async () => {
        const fetchInfo = vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockResolvedValue(serverInfo());

        // The link claims a different name and a different redirect.
        renderConsent(
            '?client_name=Evil&client_id=abc123&scope=coffer.read'
            + '&redirect_uri=https%3A%2F%2Fattacker.example%2Fcb&state=s&consent_ticket=t1',
        );

        expect(await screen.findByText(/Connect Claude Desktop to Coffer\?/i)).toBeInTheDocument();
        expect(screen.queryByText(/Evil/)).not.toBeInTheDocument();
        expect(screen.getByText('localhost:35535')).toBeInTheDocument();
        expect(screen.queryByText(/attacker\.example/)).not.toBeInTheDocument();
        expect(screen.getByText(/read-only/i)).toBeInTheDocument();
        expect(screen.getByText(/can't create, edit, or delete/i)).toBeInTheDocument();
        expect(screen.getByText('abc123')).toBeInTheDocument();
        expect(fetchInfo).toHaveBeenCalledWith('t1');
    });

    it('shows full-access copy and the global-switch caveat when the write scope is granted', async () => {
        vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockResolvedValue(
            serverInfo({ scopes: ['coffer.read', 'coffer.write', 'offline_access'] }),
        );

        renderConsent('?client_id=abc123&consent_ticket=t1');

        expect(await screen.findByText(/full access/i)).toBeInTheDocument();
        expect(screen.queryByText(/read-only/i)).not.toBeInTheDocument();
        expect(screen.getByText(/Create, edit, and delete/i)).toBeInTheDocument();
        expect(screen.getByText(/global admin switch/i)).toBeInTheDocument();
        expect(screen.getByText(/coffer\.write/i)).toBeInTheDocument();
    });

    it('names an app that registered without a name as unnamed', async () => {
        vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockResolvedValue(serverInfo({ clientName: null }));

        renderConsent('?client_id=abc123&consent_ticket=t1');

        expect(await screen.findByText(/Connect An unnamed app to Coffer\?/i)).toBeInTheDocument();
    });

    it('says the approval expired when the server no longer knows the ticket', async () => {
        vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockRejectedValue(new ApiError(404, 'Not Found'));

        renderConsent('?client_id=abc123&consent_ticket=stale');

        expect(await screen.findByText(/This approval has expired/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^allow$/i })).not.toBeInTheDocument();
    });

    it('says the approval expired when the link carries no ticket at all', async () => {
        const fetchInfo = vi.spyOn(mcpModule, 'fetchOAuthConsentInfo');

        renderConsent('?client_name=Claude&client_id=abc123&scope=coffer.read');

        expect(await screen.findByText(/This approval has expired/i)).toBeInTheDocument();
        expect(fetchInfo).not.toHaveBeenCalled();
    });

    // decide() builds a form, appends it to document.body, and submits it. Stub
    // submit() (jsdom can't navigate) and read the appended form back from the
    // DOM — avoids aliasing `this` in the spy (no-this-alias).
    function readSubmittedForm(): Record<string, string> {
        const form = Array.from(document.body.querySelectorAll('form')).at(-1);
        expect(form).toBeTruthy();
        expect(form!.method).toBe('post');
        expect(form!.action).toContain('/oauth/authorize');
        return Object.fromEntries(
            Array.from(form!.querySelectorAll('input')).map((i) => [i.name, i.value]),
        );
    }

    it('posts the OAuth request back with the ticket on Allow', async () => {
        vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
        vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockResolvedValue(serverInfo());

        renderConsent('?client_name=Claude&client_id=abc&scope=coffer.read&redirect_uri=https%3A%2F%2Fx&state=s&consent_ticket=t1');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^allow$/i }));

        const fields = readSubmittedForm();
        expect(fields.decision).toBe('allow');
        expect(fields.consent_ticket).toBe('t1');
        expect(fields.client_id).toBe('abc');
        expect(fields.scope).toBe('coffer.read');
        // client_name is not an OAuth parameter and must not be replayed as one.
        expect(fields.client_name).toBeUndefined();
    });

    it('posts a deny decision on Deny', async () => {
        vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
        vi.spyOn(mcpModule, 'fetchOAuthConsentInfo').mockResolvedValue(serverInfo());

        renderConsent('?client_id=abc&scope=coffer.read&consent_ticket=t1');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^deny$/i }));

        const fields = readSubmittedForm();
        expect(fields.decision).toBe('deny');
        // Guard the gotcha: the field must never be named "submit" (it would
        // shadow HTMLFormElement.submit() and break the post in a real browser).
        expect(fields.submit).toBeUndefined();
    });
});
