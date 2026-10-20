import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { LineChart } from 'lucide-react';

import { fetchCurrentUser, fetchOAuthConsentInfo } from '@/lib/api';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * `/oauth/consent` — the OAuth authorization consent screen (ADR-0063 §D2).
 * Reached when /oauth/authorize finds an authenticated user but no prior consent
 * for the requesting client; the server redirects here preserving the OAuth
 * request in the query string, plus a single-use `consent_ticket`.
 *
 * What the page SHOWS comes from the server, looked up by that ticket: the name
 * the app registered with and the redirect it registered (2026-10-12 security
 * audit, H1). It used to read both from the link, so whoever wrote the link chose
 * what the user saw — "Connect Claude… sends results to localhost".
 *
 * Allow / Deny each POST the original OAuth parameters back to /oauth/authorize
 * (a server endpoint) with a `decision` and the ticket; the cookie rides the
 * same-origin POST. The server honours Allow only with the ticket it minted for
 * this user and this exact request (M1). This is an authed route, so an anonymous
 * visitor is already bounced to /login by the route guard.
 */
export function ConsentPage() {
    const userQuery = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });
    const params = new URLSearchParams(window.location.search);
    // The server strips any ticket the link carried and appends its own, so there
    // is one; take the last all the same, since it is the server's.
    const ticket = params.getAll('consent_ticket').at(-1) ?? '';
    const infoQuery = useQuery({
        queryKey: ['oauth-consent', ticket],
        queryFn: () => fetchOAuthConsentInfo(ticket),
        enabled: ticket.length > 0,
        retry: false,
    });
    const info = infoQuery.data;
    const who = userQuery.data?.displayName ?? userQuery.data?.username ?? null;

    function decide(decision: 'allow' | 'deny') {
        // Replay the original OAuth request as a form POST to the authorization
        // endpoint, with exactly one consent_ticket. The browser sends the session
        // cookie (same-origin POST).
        const form = document.createElement('form');
        form.method = 'POST';
        form.action = '/oauth/authorize';
        const replay = new URLSearchParams(window.location.search);
        replay.delete('client_name');
        replay.set('consent_ticket', ticket);
        // NB: must NOT be named "submit" — a form control named "submit" shadows
        // HTMLFormElement.submit(), so form.submit() would throw instead of post.
        replay.set('decision', decision);
        for (const [key, value] of replay) {
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = key;
            input.value = value;
            form.appendChild(input);
        }
        document.body.appendChild(form);
        form.submit();
    }

    if (!ticket || infoQuery.isError) {
        return (
            <ConsentShell>
                <header className="space-y-1">
                    <h1 className="text-lg font-semibold tracking-tight">This approval has expired</h1>
                    <p className="text-sm text-text-muted">
                        Approving an app only works from the screen Coffer shows you, for a few
                        minutes, once. Start connecting the app again from the app itself.
                    </p>
                </header>
            </ConsentShell>
        );
    }

    if (!info) {
        return (
            <ConsentShell>
                <p className="text-sm text-text-muted">Loading…</p>
            </ConsentShell>
        );
    }

    const clientName = info.clientName?.trim() || 'An unnamed app';
    // DCR clients (mcp-remote) are granted the full read+write scope; the admin
    // "MCP writes" kill-switch (McpWriteGuard, ADR-0081) is what actually governs
    // whether writes execute — not the token's scope. So the copy reflects the
    // granted scope honestly (full vs read-only) instead of always claiming
    // read-only.
    const canWrite = info.scopes.includes('coffer.write');

    return (
        <ConsentShell>
            <header className="space-y-1">
                <h1 className="text-lg font-semibold tracking-tight">
                    Connect {clientName} to Coffer?
                </h1>
                <p className="text-sm text-text-muted">
                    {canWrite ? (
                        <>
                            <span className="font-medium text-text">{clientName}</span> is
                            requesting{' '}
                            <span className="font-medium text-text">full access</span> —
                            reading your data and, when writes are enabled, making changes
                            across your ledgers.
                        </>
                    ) : (
                        <>
                            <span className="font-medium text-text">{clientName}</span> is
                            requesting{' '}
                            <span className="font-medium text-text">read-only</span> access
                            to build reports across your ledgers.
                        </>
                    )}
                </p>
                <p className="text-[0.6875rem] text-text-subtle">
                    The name is the one the app gave itself. Check where it sends results
                    below before you allow it.
                </p>
            </header>

            <div className="space-y-1.5 rounded border border-border bg-surface p-3 text-sm">
                <p className="text-text">
                    ✓ Read ledgers, transactions, categories, and investments.
                </p>
                {canWrite ? (
                    <p className="text-text">
                        ✓ Create, edit, and delete — subject to a global admin switch
                        for MCP writes, which is off by default.
                    </p>
                ) : (
                    <p className="text-text-muted">
                        ✗ No changes — it can't create, edit, or delete anything.
                    </p>
                )}
                {info.scopes.length > 0 ? (
                    <p className="pt-1 text-[0.6875rem] text-text-subtle">
                        Scope: {info.scopes.join(', ')}
                    </p>
                ) : null}
            </div>

            <dl className="space-y-1 text-[0.6875rem] text-text-muted">
                <div className="flex justify-between gap-3">
                    <dt>Sends results to</dt>
                    <dd className="truncate font-mono text-text">{info.redirectHost}</dd>
                </div>
                <div className="flex justify-between gap-3">
                    <dt>Client ID</dt>
                    <dd className="truncate font-mono">{info.clientId}</dd>
                </div>
                {who ? (
                    <div className="flex justify-between gap-3">
                        <dt>Signed in as</dt>
                        <dd className="text-text">{who}</dd>
                    </div>
                ) : null}
            </dl>

            <div className="flex justify-end gap-2">
                <Button type="button" variant="secondary" onClick={() => decide('deny')}>
                    Deny
                </Button>
                <Button type="button" onClick={() => decide('allow')}>
                    Allow
                </Button>
            </div>
        </ConsentShell>
    );
}

function ConsentShell({ children }: { children: ReactNode }) {
    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <div className="mb-6 flex items-center gap-2">
                <LineChart className="size-icon-lg text-accent" strokeWidth={2.25} aria-hidden />
                <span className="text-base font-bold tracking-tight">Coffer</span>
            </div>

            <Panel>
                <PanelBody className="space-y-5">{children}</PanelBody>
            </Panel>
        </main>
    );
}
