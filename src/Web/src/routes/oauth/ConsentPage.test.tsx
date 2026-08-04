import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ConsentPage } from './ConsentPage';
import * as authModule from '@/lib/api/auth';

// ConsentPage (ADR-0063): renders the requesting client + the granted scope
// (read-only vs full read+write, ADR-0081) and, on a decision, replays the OAuth
// request as a POST to /oauth/authorize.

function renderConsent(query: string) {
    window.history.replaceState({}, '', `/oauth/consent${query}`);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <ConsentPage />
        </QueryClientProvider>,
    );
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

    it('shows the client name, read-only scope, and where access goes', async () => {
        renderConsent('?client_name=Claude&client_id=abc123&scope=coffer.read&redirect_uri=http%3A%2F%2Flocalhost%3A35535%2Fcb&state=s');

        expect(await screen.findByText(/Connect Claude to Coffer\?/i)).toBeInTheDocument();
        expect(screen.getByText(/read-only/i)).toBeInTheDocument();
        expect(screen.getByText(/can't create, edit, or delete/i)).toBeInTheDocument();
        // Transparency: where the authorization is delivered + the client id.
        expect(screen.getByText('localhost:35535')).toBeInTheDocument();
        expect(screen.getByText('abc123')).toBeInTheDocument();
    });

    it('shows full-access copy and the global-switch caveat when the write scope is present', async () => {
        renderConsent('?client_name=Claude&client_id=abc123&scope=coffer.read+coffer.write+offline_access&redirect_uri=http%3A%2F%2Flocalhost%3A5525%2Fcb&state=s');

        expect(await screen.findByText(/Connect Claude to Coffer\?/i)).toBeInTheDocument();
        // Full access, not read-only.
        expect(screen.getByText(/full access/i)).toBeInTheDocument();
        expect(screen.queryByText(/read-only/i)).not.toBeInTheDocument();
        // Writes disclosed AND gated on the global admin switch.
        expect(screen.getByText(/Create, edit, and delete/i)).toBeInTheDocument();
        expect(screen.getByText(/global admin switch/i)).toBeInTheDocument();
        expect(screen.queryByText(/can't create, edit, or delete/i)).not.toBeInTheDocument();
        // The scope line still lists everything granted.
        expect(screen.getByText(/coffer\.write/i)).toBeInTheDocument();
    });

    it('falls back to a neutral label when no client name is provided', async () => {
        renderConsent('?client_id=abc123&scope=coffer.read');

        expect(await screen.findByText(/Connect An application to Coffer\?/i)).toBeInTheDocument();
    });

    // decide() builds a form, appends it to document.body, and submits it. Stub
    // submit() (jsdom can't navigate) and read the appended form back from the
    // DOM — avoids aliasing `this` in the spy (no-this-alias).
    function readSubmittedForm(): Record<string, string> {
        // The decide() form is appended to document.body (outside RTL's container);
        // take the most recently appended one.
        const form = Array.from(document.body.querySelectorAll('form')).at(-1);
        expect(form).toBeTruthy();
        expect(form!.method).toBe('post');
        expect(form!.action).toContain('/oauth/authorize');
        return Object.fromEntries(
            Array.from(form!.querySelectorAll('input')).map((i) => [i.name, i.value]),
        );
    }

    it('posts the OAuth request back to /oauth/authorize on Allow', async () => {
        vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});

        renderConsent('?client_name=Claude&client_id=abc&scope=coffer.read&redirect_uri=https%3A%2F%2Fx&state=s');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^allow$/i }));

        const fields = readSubmittedForm();
        expect(fields.decision).toBe('allow');
        expect(fields.client_id).toBe('abc');
        expect(fields.scope).toBe('coffer.read');
        // client_name is display-only and must NOT be replayed as an OAuth param.
        expect(fields.client_name).toBeUndefined();
    });

    it('posts a deny decision on Deny', async () => {
        vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});

        renderConsent('?client_name=Claude&client_id=abc&scope=coffer.read');
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /^deny$/i }));

        const fields = readSubmittedForm();
        expect(fields.decision).toBe('deny');
        // Guard the gotcha: the field must never be named "submit" (it would
        // shadow HTMLFormElement.submit() and break the post in a real browser).
        expect(fields.submit).toBeUndefined();
    });
});
