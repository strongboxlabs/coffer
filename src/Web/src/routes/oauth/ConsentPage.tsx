import { useQuery } from '@tanstack/react-query';
import { LineChart } from 'lucide-react';

import { fetchCurrentUser } from '@/lib/api';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * `/oauth/consent` — the OAuth authorization consent screen (ADR-0063 §D2).
 * Reached when /oauth/authorize finds an authenticated user but no prior consent
 * for the requesting client; the server redirects here preserving the OAuth
 * request in the query string (plus a display-only `client_name`).
 *
 * Allow / Deny each POST the original OAuth parameters back to /oauth/authorize
 * (a server endpoint) with a `submit` decision; the cookie rides the same-origin
 * POST. On allow the server mints the code and redirects to the client; on deny
 * it returns access_denied. This is an authed route, so an anonymous visitor is
 * already bounced to /login by the route guard.
 */
export function ConsentPage() {
    const userQuery = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });
    const params = new URLSearchParams(window.location.search);
    // The server forwards the client's registered display name; fall back to a
    // neutral label rather than the opaque client id.
    const clientName = params.get('client_name')?.trim() || 'An application';
    const clientId = params.get('client_id') ?? '';
    const scopes = (params.get('scope') ?? '').split(' ').filter(Boolean);
    // DCR clients (mcp-remote) are granted the full read+write scope; the admin
    // "MCP writes" kill-switch (McpWriteGuard, ADR-0081) is what actually governs
    // whether writes execute — not the token's scope. So the copy reflects the
    // granted scope honestly (full vs read-only) instead of always claiming
    // read-only.
    const canWrite = scopes.includes('coffer.write');
    // Where the authorization is delivered — shows the user this is their own
    // machine (localhost) vs. somewhere remote.
    const redirectHost = (() => {
        const uri = params.get('redirect_uri');
        if (!uri) return '';
        try {
            return new URL(uri).host;
        } catch {
            return '';
        }
    })();
    const who = userQuery.data?.displayName ?? userQuery.data?.username ?? null;

    function decide(decision: 'allow' | 'deny') {
        // Replay the original OAuth request as a form POST to the authorization
        // endpoint. Drop client_name (our display-only addition, not an OAuth
        // parameter). The browser sends the session cookie (same-origin POST).
        const form = document.createElement('form');
        form.method = 'POST';
        form.action = '/oauth/authorize';
        const replay = new URLSearchParams(window.location.search);
        replay.delete('client_name');
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

    return (
        <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center px-6 py-12">
            <div className="mb-6 flex items-center gap-2">
                <LineChart className="h-5 w-5 text-accent" strokeWidth={2.25} aria-hidden />
                <span className="text-base font-bold tracking-tight">Coffer</span>
            </div>

            <Panel>
                <PanelBody className="space-y-5">
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
                        {scopes.length > 0 ? (
                            <p className="pt-1 text-[0.6875rem] text-text-subtle">
                                Scope: {scopes.join(', ')}
                            </p>
                        ) : null}
                    </div>

                    <dl className="space-y-1 text-[0.6875rem] text-text-muted">
                        {redirectHost ? (
                            <div className="flex justify-between gap-3">
                                <dt>Sends results to</dt>
                                <dd className="truncate font-mono text-text">{redirectHost}</dd>
                            </div>
                        ) : null}
                        {clientId ? (
                            <div className="flex justify-between gap-3">
                                <dt>Client ID</dt>
                                <dd className="truncate font-mono">{clientId}</dd>
                            </div>
                        ) : null}
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
                </PanelBody>
            </Panel>
        </main>
    );
}
